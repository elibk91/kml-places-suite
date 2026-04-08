using System.Text.Json;
using KmlGenerator.Core.Models;
using KmlGenerator.Core.Services;
using KmlSuite.Shared.DependencyInjection;
using KmlSuite.Shared.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

return await KmlTilerProgram.RunAsync(args, Console.Out, Console.Error);

public static class KmlTilerProgram
{
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        var services = new ServiceCollection();
        services.AddKmlSuiteHostDiagnostics("kml-tiler");
        services.AddKmlSuiteTracing();
        services.AddTracedSingleton<IKmlGenerationService, KmlGenerationService>();
        services.AddTracedSingleton<IKmlTilerApp, KmlTilerRunner>();

        await using var serviceProvider = services.BuildServiceProvider();
        return await serviceProvider.GetRequiredService<IKmlTilerApp>().RunAsync(args, output, error);
    }
}

/// <summary>
/// Console signpost: this host walks a fixed lat/lon grid and runs the existing KML generator tile by tile.
/// </summary>
public sealed class KmlTilerRunner : IKmlTilerApp
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly ILogger<KmlTilerRunner> _logger;
    private readonly IKmlGenerationService _service;

    public KmlTilerRunner(ILogger<KmlTilerRunner> logger, IKmlGenerationService service)
    {
        _logger = logger;
        _service = service;
    }

    public async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        var parsed = ParseArguments(args);
        if (parsed is null)
        {
            await error.WriteLineAsync(
                "Usage: kml-tiler --input request.json --output-dir out\\tiles --north 33.95 --south 33.69 --west -84.55 --east -84.09 --lat-step 0.07 --lon-step 0.09");
            return 1;
        }

        try
        {
            Directory.CreateDirectory(parsed.OutputDirectory);

            await using var requestStream = File.OpenRead(parsed.InputPath);
            var request = await JsonSerializer.DeserializeAsync<GenerateKmlRequest>(requestStream, JsonOptions);
            if (request is null)
            {
                throw new InvalidOperationException("The request file did not contain a valid JSON payload.");
            }

            var requestFeatures = MaterializeFeatures(request);
            var requiredCategories = requestFeatures
                .Select(feature => feature.Category.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            _logger.LogInformation("Loaded tile request with {FeatureCount} features across {CategoryCount} categories", requestFeatures.Count, requiredCategories.Length);

            var tileRows = BuildTileRows(parsed.North, parsed.South, parsed.LatitudeStep);
            var tileColumns = BuildTileColumns(parsed.West, parsed.East, parsed.LongitudeStep);
            var summaries = new List<TileSummary>();

            for (var row = 0; row < tileRows.Count; row++)
            {
                var tileNorth = tileRows[row].North;
                var tileSouth = tileRows[row].South;

                for (var column = 0; column < tileColumns.Count; column++)
                {
                    var tileWest = tileColumns[column].West;
                    var tileEast = tileColumns[column].East;
                    var tileFeatures = SelectTileFeatures(requestFeatures, request, tileNorth, tileSouth, tileWest, tileEast);
                    var categoriesPresent = tileFeatures
                        .Select(feature => feature.Category.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();

                    var tileName = $"tile_r{row:D2}_c{column:D2}";
                    var summary = new TileSummary
                    {
                        Name = tileName,
                        Row = row,
                        Column = column,
                        North = tileNorth,
                        South = tileSouth,
                        West = tileWest,
                        East = tileEast,
                        PointCount = tileFeatures.Count,
                        CategoriesPresent = categoriesPresent
                    };

                    if (!requiredCategories.All(required => categoriesPresent.Contains(required, StringComparer.OrdinalIgnoreCase)))
                    {
                        summary.Status = "missing_categories";
                        summaries.Add(summary);
                        continue;
                    }

                    var tileRequest = new GenerateKmlRequest
                    {
                        Features = tileFeatures,
                        Step = request.Step,
                        RadiusMiles = request.RadiusMiles,
                        PaddingDegrees = request.PaddingDegrees,
                        CategoryRadiusMiles = request.CategoryRadiusMiles
                    };

                    var result = _service.Generate(tileRequest);
                    summary.BoundaryPointCount = result.IntersectionPolygonCount;

                    if (result.IntersectionPolygonCount <= 0)
                    {
                        summary.Status = "empty_outline";
                        summaries.Add(summary);
                        continue;
                    }

                    var requestPath = Path.Combine(parsed.OutputDirectory, $"{tileName}.request.json");
                    var kmlPath = Path.Combine(parsed.OutputDirectory, $"{tileName}.kml");

                    await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(tileRequest, JsonOptions));
                    await File.WriteAllTextAsync(kmlPath, result.Kml);

                    summary.Status = "written";
                    summary.RequestPath = requestPath;
                    summary.KmlPath = kmlPath;
                    summaries.Add(summary);
                }
            }

            var summaryPath = Path.Combine(parsed.OutputDirectory, "tiles-summary.json");
            await File.WriteAllTextAsync(summaryPath, JsonSerializer.Serialize(summaries, JsonOptions));

            var writtenCount = summaries.Count(summary => summary.Status == "written");
            _logger.LogInformation("Processed {TileCount} tiles and wrote {WrittenCount} tile KML files", summaries.Count, writtenCount);
            await output.WriteLineAsync($"Processed {summaries.Count} tiles and wrote {writtenCount} non-empty tile KML files to {parsed.OutputDirectory}");
            return 0;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "KML tiler failed");
            await error.WriteLineAsync(exception.Message);
            return 1;
        }
    }

    private static IReadOnlyList<GeometryFeatureInput> MaterializeFeatures(GenerateKmlRequest request)
    {
        if (request.Features.Count > 0)
        {
            return request.Features;
        }

        return request.Locations.Select(location => new GeometryFeatureInput
        {
            Category = location.Category,
            Label = location.Label,
            GeometryType = "point",
            Points =
            [
                new CoordinateInput { Latitude = location.Latitude, Longitude = location.Longitude }
            ]
        }).ToArray();
    }

    private static List<TileRow> BuildTileRows(double north, double south, double latitudeStep)
    {
        var rows = new List<TileRow>();
        for (var tileNorth = north; tileNorth > south; tileNorth -= latitudeStep)
        {
            rows.Add(new TileRow(tileNorth, Math.Max(tileNorth - latitudeStep, south)));
        }

        return rows;
    }

    private static List<TileColumn> BuildTileColumns(double west, double east, double longitudeStep)
    {
        var columns = new List<TileColumn>();
        for (var tileWest = west; tileWest < east; tileWest += longitudeStep)
        {
            columns.Add(new TileColumn(tileWest, Math.Min(tileWest + longitudeStep, east)));
        }

        return columns;
    }

    private static IReadOnlyList<GeometryFeatureInput> SelectTileFeatures(
        IReadOnlyList<GeometryFeatureInput> features,
        GenerateKmlRequest request,
        double tileNorth,
        double tileSouth,
        double tileWest,
        double tileEast)
    {
        var selected = new List<GeometryFeatureInput>();
        foreach (var feature in features)
        {
            var radiusMiles = request.CategoryRadiusMiles.TryGetValue(feature.Category, out var configuredRadius)
                ? configuredRadius
                : request.RadiusMiles;
            var padding = radiusMiles / 69d;
            var bounds = ComputeFeatureBounds(feature, padding);
            if (bounds.MaxLatitude < tileSouth
                || bounds.MinLatitude > tileNorth
                || bounds.MaxLongitude < tileWest
                || bounds.MinLongitude > tileEast)
            {
                continue;
            }

            selected.Add(feature);
        }

        return selected;
    }

    private static TileFeatureBounds ComputeFeatureBounds(GeometryFeatureInput feature, double padding)
    {
        var coordinates = EnumerateCoordinates(feature).ToArray();
        return new TileFeatureBounds(
            coordinates.Min(point => point.Latitude) - padding,
            coordinates.Max(point => point.Latitude) + padding,
            coordinates.Min(point => point.Longitude) - padding,
            coordinates.Max(point => point.Longitude) + padding);
    }

    private static IEnumerable<CoordinateInput> EnumerateCoordinates(GeometryFeatureInput feature)
    {
        foreach (var point in feature.Points)
        {
            yield return point;
        }

        foreach (var line in feature.Lines)
        {
            foreach (var coordinate in line.Coordinates)
            {
                yield return coordinate;
            }
        }

        foreach (var polygon in feature.Polygons)
        {
            foreach (var coordinate in polygon.OuterRing)
            {
                yield return coordinate;
            }

            foreach (var ring in polygon.InnerRings)
            {
                foreach (var coordinate in ring)
                {
                    yield return coordinate;
                }
            }
        }
    }

    private static bool TryGetTileRow(
        double latitude,
        double overallNorth,
        double overallSouth,
        double latitudeStep,
        int rowCount,
        out int row)
    {
        if (latitude > overallNorth || latitude < overallSouth)
        {
            row = -1;
            return false;
        }

        row = (int)Math.Floor((overallNorth - latitude) / latitudeStep);
        row = Math.Clamp(row, 0, rowCount - 1);
        return true;
    }

    private static bool TryGetTileColumn(
        double longitude,
        double overallWest,
        double overallEast,
        double longitudeStep,
        int columnCount,
        out int column)
    {
        if (longitude < overallWest || longitude > overallEast)
        {
            column = -1;
            return false;
        }

        column = (int)Math.Floor((longitude - overallWest) / longitudeStep);
        column = Math.Clamp(column, 0, columnCount - 1);
        return true;
    }

    private TilerArguments? ParseArguments(IReadOnlyList<string> args)
    {
        string? inputPath = null;
        string? outputDirectory = null;
        double? north = null;
        double? south = null;
        double? west = null;
        double? east = null;
        double? latitudeStep = null;
        double? longitudeStep = null;

        for (var index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--input" when index + 1 < args.Count:
                    inputPath = args[++index];
                    break;
                case "--output-dir" when index + 1 < args.Count:
                    outputDirectory = args[++index];
                    break;
                case "--north" when index + 1 < args.Count && double.TryParse(args[++index], out var parsedNorth):
                    north = parsedNorth;
                    break;
                case "--south" when index + 1 < args.Count && double.TryParse(args[++index], out var parsedSouth):
                    south = parsedSouth;
                    break;
                case "--west" when index + 1 < args.Count && double.TryParse(args[++index], out var parsedWest):
                    west = parsedWest;
                    break;
                case "--east" when index + 1 < args.Count && double.TryParse(args[++index], out var parsedEast):
                    east = parsedEast;
                    break;
                case "--lat-step" when index + 1 < args.Count && double.TryParse(args[++index], out var parsedLatStep):
                    latitudeStep = parsedLatStep;
                    break;
                case "--lon-step" when index + 1 < args.Count && double.TryParse(args[++index], out var parsedLonStep):
                    longitudeStep = parsedLonStep;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(inputPath) ||
            string.IsNullOrWhiteSpace(outputDirectory) ||
            north is null ||
            south is null ||
            west is null ||
            east is null ||
            latitudeStep is null ||
            longitudeStep is null)
        {
            return null;
        }

        if (north <= south)
        {
            throw new InvalidOperationException("North must be greater than south.");
        }

        if (east <= west)
        {
            throw new InvalidOperationException("East must be greater than west.");
        }

        if (latitudeStep <= 0d || longitudeStep <= 0d)
        {
            throw new InvalidOperationException("Tile steps must be greater than zero.");
        }

        return new TilerArguments(
            inputPath,
            outputDirectory,
            north.Value,
            south.Value,
            west.Value,
            east.Value,
            latitudeStep.Value,
            longitudeStep.Value);
    }

    private sealed record TilerArguments(
        string InputPath,
        string OutputDirectory,
        double North,
        double South,
        double West,
        double East,
        double LatitudeStep,
        double LongitudeStep);

    private sealed record TileRow(double North, double South);

    private sealed record TileColumn(double West, double East);

    private sealed record TileFeatureBounds(double MinLatitude, double MaxLatitude, double MinLongitude, double MaxLongitude);

    private sealed class TileSummary
    {
        public string Name { get; init; } = string.Empty;

        public int Row { get; init; }

        public int Column { get; init; }

        public double North { get; init; }

        public double South { get; init; }

        public double West { get; init; }

        public double East { get; init; }

        public int PointCount { get; set; }

        public int BoundaryPointCount { get; set; }

        public IReadOnlyList<string> CategoriesPresent { get; init; } = Array.Empty<string>();

        public string Status { get; set; } = "pending";

        public string? RequestPath { get; set; }

        public string? KmlPath { get; set; }
    }
}

