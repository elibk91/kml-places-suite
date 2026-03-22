using System.Text.Json;
using KmlGenerator.Core.Models;
using KmlGenerator.Core.Services;
using KmlSuite.Shared.DependencyInjection;
using KmlSuite.Shared.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

return await KmlTilerProgram.RunAsync(args, Console.Out, Console.Error);

public static class KmlTilerProgram
{
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.AddSimpleConsole(console =>
            {
                console.TimestampFormat = "HH:mm:ss.fff ";
                console.SingleLine = true;
            });
            builder.SetMinimumLevel(LogLevel.Trace);
        });
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
        using var _ = MethodTrace.Enter(_logger, nameof(KmlTilerRunner), new Dictionary<string, object?> { ["ArgumentCount"] = args.Length });
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

            var requestText = await File.ReadAllTextAsync(parsed.InputPath);
            var request = JsonSerializer.Deserialize<GenerateKmlRequest>(requestText, JsonOptions);
            if (request is null)
            {
                throw new InvalidOperationException("The request file did not contain a valid JSON payload.");
            }

            var requiredCategories = request.Locations
                .Select(location => location.Category.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            _logger.LogInformation("Loaded tile request with {LocationCount} locations across {CategoryCount} categories", request.Locations.Count, requiredCategories.Length);

            var summaries = new List<TileSummary>();
            var row = 0;

            for (var tileNorth = parsed.North; tileNorth > parsed.South; tileNorth -= parsed.LatitudeStep, row++)
            {
                var tileSouth = Math.Max(tileNorth - parsed.LatitudeStep, parsed.South);
                var column = 0;

                for (var tileWest = parsed.West; tileWest < parsed.East; tileWest += parsed.LongitudeStep, column++)
                {
                    var tileEast = Math.Min(tileWest + parsed.LongitudeStep, parsed.East);
                    var tileLocations = request.Locations
                        .Where(location => IsInsideTile(location, tileNorth, tileSouth, tileWest, tileEast, parsed.North, parsed.South, parsed.West, parsed.East))
                        .ToArray();

                    var categoriesPresent = tileLocations
                        .Select(location => location.Category.Trim())
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
                        PointCount = tileLocations.Length,
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
                        Locations = tileLocations,
                        Step = request.Step,
                        RadiusMiles = request.RadiusMiles,
                        PaddingDegrees = request.PaddingDegrees
                    };

                    var result = _service.Generate(tileRequest);
                    summary.BoundaryPointCount = result.BoundaryPointCount;

                    if (result.BoundaryPointCount <= 0)
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

    private bool IsInsideTile(
        LocationInput location,
        double north,
        double south,
        double west,
        double east,
        double overallNorth,
        double overallSouth,
        double overallWest,
        double overallEast)
    {
        using var _ = MethodTrace.Enter(_logger, nameof(KmlTilerRunner));
        var includeSouthEdge = Math.Abs(south - overallSouth) < double.Epsilon;
        var includeEastEdge = Math.Abs(east - overallEast) < double.Epsilon;

        var latitudeMatches = location.Latitude <= north &&
                              (includeSouthEdge ? location.Latitude >= south : location.Latitude > south);

        var longitudeMatches = location.Longitude >= west &&
                               (includeEastEdge ? location.Longitude <= east : location.Longitude < east);

        return latitudeMatches && longitudeMatches;
    }

    private TilerArguments? ParseArguments(IReadOnlyList<string> args)
    {
        using var _ = MethodTrace.Enter(_logger, nameof(KmlTilerRunner));
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

