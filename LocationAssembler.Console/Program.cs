using System.Text.Json;
using KmlGenerator.Core.Models;
using KmlSuite.Shared.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LocationAssembler.Console;
using PlacesGatherer.Console.Models;

return await LocationAssemblerProgram.RunAsync(args, Console.Out, Console.Error);

public static class LocationAssemblerProgram
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
        services.AddTracedSingleton<ILocationAssemblerApp, LocationAssemblerRunner>();

        await using var serviceProvider = services.BuildServiceProvider();
        return await serviceProvider.GetRequiredService<ILocationAssemblerApp>().RunAsync(args, output, error);
    }
}

/// <summary>
/// Console signpost: this host converts normalized gathered points into the flat KML request contract.
/// </summary>
public sealed class LocationAssemblerRunner : ILocationAssemblerApp
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly ILogger<LocationAssemblerRunner> _logger;

    public LocationAssemblerRunner(ILogger<LocationAssemblerRunner> logger)
    {
        _logger = logger;
    }

    public async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        var parsed = ParseArguments(args);
        if (parsed is null)
        {
            await error.WriteLineAsync("Usage: location-assembler --input places-1.jsonl --input places-2.jsonl [--geometry-input arc-geometry.json] --output request.json");
            return 1;
        }

        try
        {
            var categorySelection = await LoadCategorySelectionAsync(parsed.Value.CategoryConfigPath);
            var distinctLocations = new HashSet<LocationInput>(LocationInputComparer.Instance);
            var distinctFeatures = new HashSet<GeometryFeatureInput>(GeometryFeatureInputComparer.Instance);
            var inputRecordCount = 0;

            foreach (var inputPath in parsed.Value.InputPaths)
            {
                var fileRecordCount = 0;
                await foreach (var line in ReadNonEmptyLinesAsync(inputPath))
                {
                    var record = DeserializeRecord(line);
                    var feature = MapToPointFeature(record, categorySelection);
                    if (feature is not null)
                    {
                        distinctFeatures.Add(feature);
                        distinctLocations.Add(new LocationInput
                        {
                            Latitude = record.Latitude,
                            Longitude = record.Longitude,
                            Category = feature.Category,
                            Label = feature.Label
                        });
                    }

                    fileRecordCount++;
                    inputRecordCount++;
                }

                _logger.LogInformation("Read {FileRecordCount} normalized records from {InputPath}", fileRecordCount, inputPath);
            }

            foreach (var geometryInputPath in parsed.Value.GeometryInputPaths)
            {
                await using var geometryStream = File.OpenRead(geometryInputPath);
                var geometryRequest = await JsonSerializer.DeserializeAsync<GenerateKmlRequest>(geometryStream, JsonOptions)
                    ?? throw new InvalidOperationException("The geometry input file did not contain a valid JSON payload.");

                foreach (var feature in geometryRequest.Features)
                {
                    var normalizedFeature = NormalizeFeature(feature, categorySelection);
                    if (normalizedFeature is not null)
                    {
                        distinctFeatures.Add(normalizedFeature);
                    }
                }

                _logger.LogInformation("Read {FeatureCount} geometry features from {InputPath}", geometryRequest.Features.Count, geometryInputPath);
            }

            var features = distinctFeatures.ToArray();
            _logger.LogInformation("Deduplicated {InputRecordCount} point records plus geometry inputs into {FeatureCount} unique features", inputRecordCount, features.Length);

            var request = new GenerateKmlRequest
            {
                Locations = distinctLocations.ToArray(),
                Features = features,
                RadiusMiles = categorySelection.DefaultRadiusMiles,
                CategoryRadiusMiles = categorySelection.CategoryRadiusMiles
            };

            await using var outputStream = File.Create(parsed.Value.OutputPath);
            await using (var writer = new Utf8JsonWriter(outputStream, new JsonWriterOptions { Indented = true }))
            {
                WriteRequest(writer, request);
                await writer.FlushAsync();
            }
            _logger.LogInformation("Assembled geometry request with {FeatureCount} unique features at {OutputPath}", features.Length, parsed.Value.OutputPath);
            await output.WriteLineAsync($"Saved {features.Length} unique features to {parsed.Value.OutputPath}");
            return 0;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Location assembler failed");
            await error.WriteLineAsync(exception.Message);
            return 1;
        }
    }

    private static async Task<CategorySelection> LoadCategorySelectionAsync(string? categoryConfigPath)
    {
        if (string.IsNullOrWhiteSpace(categoryConfigPath))
        {
            return CategorySelection.Default;
        }

        var configText = await File.ReadAllTextAsync(categoryConfigPath);
        var config = JsonSerializer.Deserialize<CategorySelectionFile>(configText, JsonOptions)
                     ?? throw new InvalidOperationException("The category config file did not contain a valid JSON payload.");

        return CategorySelection.FromConfig(config);
    }

    private static void WriteRequest(Utf8JsonWriter writer, GenerateKmlRequest request)
    {
        JsonSerializer.Serialize(writer, request, JsonOptions);
    }

    private static async IAsyncEnumerable<string> ReadNonEmptyLinesAsync(string inputPath)
    {
        await using var stream = File.OpenRead(inputPath);
        using var reader = new StreamReader(stream);

        while (await reader.ReadLineAsync() is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                yield return line;
            }
        }
    }

    private static NormalizedPlaceRecord DeserializeRecord(string line)
    {
        try
        {
            var record = JsonSerializer.Deserialize<NormalizedPlaceRecord>(line, JsonOptions);
            if (record is null)
            {
                throw new InvalidOperationException("The jsonl input contains an invalid place record.");
            }

            return record;
        }
        catch (JsonException)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            var name = root.GetProperty("name").GetString()
                       ?? throw new InvalidOperationException("The jsonl input contains an invalid place record.");

            var searchNames = TryReadStringArray(root, "searchNames");
            if (searchNames.Count == 0)
            {
                searchNames = [name];
            }

            return new NormalizedPlaceRecord
            {
                Query = root.GetProperty("query").GetString()
                        ?? throw new InvalidOperationException("The jsonl input contains an invalid place record."),
                Category = root.GetProperty("category").GetString()
                           ?? throw new InvalidOperationException("The jsonl input contains an invalid place record."),
                PlaceId = root.GetProperty("placeId").GetString()
                          ?? throw new InvalidOperationException("The jsonl input contains an invalid place record."),
                Name = name,
                FormattedAddress = root.GetProperty("formattedAddress").GetString()
                                   ?? throw new InvalidOperationException("The jsonl input contains an invalid place record."),
                Latitude = root.GetProperty("latitude").GetDouble(),
                Longitude = root.GetProperty("longitude").GetDouble(),
                Types = TryReadStringArray(root, "types"),
                SourceQueryType = root.GetProperty("sourceQueryType").GetString()
                                  ?? throw new InvalidOperationException("The jsonl input contains an invalid place record."),
                SearchNames = searchNames,
                CollapsedEntityId = root.TryGetProperty("collapsedEntityId", out var collapsedEntityId)
                    ? collapsedEntityId.GetString()
                    : null
            };
        }
    }

    private static IReadOnlyList<string> TryReadStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var values = new List<string>();
        foreach (var element in property.EnumerateArray())
        {
            var value = element.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                values.Add(value);
            }
        }

        return values;
    }

    private static GeometryFeatureInput? MapToPointFeature(NormalizedPlaceRecord record, CategorySelection selection)
    {
        var category = selection.Normalize(record.Category);
        if (category is null)
        {
            return null;
        }

        return new GeometryFeatureInput
        {
            Category = category,
            Label = record.Name,
            GeometryType = "point",
            Points =
            [
                new CoordinateInput
                {
                    Latitude = record.Latitude,
                    Longitude = record.Longitude
                }
            ]
        };
    }

    private static GeometryFeatureInput? NormalizeFeature(GeometryFeatureInput feature, CategorySelection selection)
    {
        var category = selection.Normalize(feature.Category);
        if (category is null)
        {
            return null;
        }

        return new GeometryFeatureInput
        {
            Category = category,
            Label = feature.Label,
            GeometryType = feature.GeometryType,
            Points = feature.Points,
            Lines = feature.Lines,
            Polygons = feature.Polygons
        };
    }

    private (IReadOnlyList<string> InputPaths, IReadOnlyList<string> GeometryInputPaths, string OutputPath, string? CategoryConfigPath)? ParseArguments(IReadOnlyList<string> args)
    {
        var inputPaths = new List<string>();
        var geometryInputPaths = new List<string>();
        string? outputPath = null;
        string? categoryConfigPath = null;

        for (var index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--input" when index + 1 < args.Count:
                    inputPaths.Add(args[++index]);
                    break;
                case "--geometry-input" when index + 1 < args.Count:
                    geometryInputPaths.Add(args[++index]);
                    break;
                case "--output" when index + 1 < args.Count:
                    outputPath = args[++index];
                    break;
                case "--category-config" when index + 1 < args.Count:
                    categoryConfigPath = args[++index];
                    break;
            }
        }

        return (inputPaths.Count == 0 && geometryInputPaths.Count == 0) || string.IsNullOrWhiteSpace(outputPath)
            ? null
            : (inputPaths, geometryInputPaths, outputPath, categoryConfigPath);
    }

    private sealed class CategorySelection
    {
        public static CategorySelection Default { get; } = new([], [], 0.5d, new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase));

        private readonly Dictionary<string, string> _groupedCategories;
        private readonly HashSet<string> _includedCategories;
        public double DefaultRadiusMiles { get; }
        public IReadOnlyDictionary<string, double> CategoryRadiusMiles { get; }

        private CategorySelection(
            Dictionary<string, string> groupedCategories,
            HashSet<string> includedCategories,
            double defaultRadiusMiles,
            Dictionary<string, double> categoryRadiusMiles)
        {
            _groupedCategories = groupedCategories;
            _includedCategories = includedCategories;
            DefaultRadiusMiles = defaultRadiusMiles;
            CategoryRadiusMiles = categoryRadiusMiles;
        }

        public static CategorySelection FromConfig(CategorySelectionFile config)
        {
            var groupedCategories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in config.CategoryGroups)
            {
                if (string.IsNullOrWhiteSpace(group.TargetCategory))
                {
                    continue;
                }

                foreach (var sourceCategory in group.SourceCategories)
                {
                    if (!string.IsNullOrWhiteSpace(sourceCategory))
                    {
                        groupedCategories[sourceCategory.Trim()] = group.TargetCategory.Trim();
                    }
                }
            }

            var includedCategories = config.IncludedCategories
                .Where(static category => !string.IsNullOrWhiteSpace(category))
                .Select(static category => category.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var categoryRadiusMiles = config.CategoryRadiusMiles
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Key))
                .ToDictionary(entry => entry.Key.Trim(), entry => entry.Value, StringComparer.OrdinalIgnoreCase);

            return new CategorySelection(groupedCategories, includedCategories, config.DefaultRadiusMiles, categoryRadiusMiles);
        }

        public string? Normalize(string rawCategory)
        {
            if (string.IsNullOrWhiteSpace(rawCategory))
            {
                return null;
            }

            var normalized = rawCategory.Trim();
            if (_groupedCategories.TryGetValue(normalized, out var groupedCategory))
            {
                normalized = groupedCategory;
            }

            if (_includedCategories.Count > 0 && !_includedCategories.Contains(normalized))
            {
                return null;
            }

            return normalized;
        }
    }

    private sealed class CategorySelectionFile
    {
        public double DefaultRadiusMiles { get; init; } = 0.5d;

        public IReadOnlyList<string> IncludedCategories { get; init; } = Array.Empty<string>();

        public IReadOnlyDictionary<string, double> CategoryRadiusMiles { get; init; } = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        public double MinimumParkSquareFeet { get; init; }

        public double MinimumTrailMiles { get; init; }

        public IReadOnlyList<CategoryGroupFile> CategoryGroups { get; init; } = Array.Empty<CategoryGroupFile>();
    }

    private sealed class CategoryGroupFile
    {
        public string TargetCategory { get; init; } = string.Empty;

        public IReadOnlyList<string> SourceCategories { get; init; } = Array.Empty<string>();
    }

    private sealed class GeometryFeatureInputComparer : IEqualityComparer<GeometryFeatureInput>
    {
        public static GeometryFeatureInputComparer Instance { get; } = new();

        public bool Equals(GeometryFeatureInput? x, GeometryFeatureInput? y)
        {
            if (x is null || y is null)
            {
                return x is null && y is null;
            }

            if (!x.Category.Equals(y.Category, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(x.Label, y.Label, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(x.GeometryType, y.GeometryType, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var xJson = JsonSerializer.Serialize(x, JsonOptions);
            var yJson = JsonSerializer.Serialize(y, JsonOptions);
            return string.Equals(xJson, yJson, StringComparison.Ordinal);
        }

        public int GetHashCode(GeometryFeatureInput obj)
        {
            return HashCode.Combine(
                obj.Category.ToUpperInvariant(),
                obj.Label?.ToUpperInvariant(),
                obj.GeometryType.ToUpperInvariant(),
                JsonSerializer.Serialize(obj, JsonOptions));
        }
    }

    private sealed class LocationInputComparer : IEqualityComparer<LocationInput>
    {
        public static LocationInputComparer Instance { get; } = new();

        public bool Equals(LocationInput? x, LocationInput? y)
        {
            if (x is null || y is null)
            {
                return x is null && y is null;
            }

            return x.Category.Equals(y.Category, StringComparison.OrdinalIgnoreCase)
                   && x.Latitude.Equals(y.Latitude)
                   && x.Longitude.Equals(y.Longitude)
                   && string.Equals(x.Label, y.Label, StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode(LocationInput obj) =>
            HashCode.Combine(
                obj.Category.ToUpperInvariant(),
                obj.Latitude,
                obj.Longitude,
                obj.Label?.ToUpperInvariant());
    }
}



