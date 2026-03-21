using System.Text.Json;
using MasterListBuilder.Console.Models;
using PlacesGatherer.Console.Models;
using PlacesGatherer.Console.Secrets;
using PlacesGatherer.Console.Services;

return await MasterListBuilderRunner.RunAsync(args, Console.Out, Console.Error);

/// <summary>
/// Builds category-specific master lists by querying small fixed-degree tiles for chain categories and direct queries for curated categories.
/// </summary>
public static class MasterListBuilderRunner
{
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, HttpClient? httpClient = null)
    {
        var parsed = ParseArguments(args);
        if (parsed is null)
        {
            await error.WriteLineAsync("Usage: master-list-builder --config master-list-config.json --output-dir out\\master-lists");
            return 1;
        }

        try
        {
            Directory.CreateDirectory(parsed.Value.OutputDirectory);

            var configText = await File.ReadAllTextAsync(parsed.Value.ConfigPath);
            var config = JsonSerializer.Deserialize<MasterListConfig>(configText, JsonOptions);
            if (config is null)
            {
                throw new InvalidOperationException("The master-list config file did not contain a valid JSON payload.");
            }

            ValidateConfig(config);

            var secretProvider = SecretProviderFactory.Create(config.Secrets);
            var apiKey = secretProvider.GetGoogleMapsApiKey();

            using var ownedClient = httpClient is null ? new HttpClient() : null;
            var client = new GooglePlacesClient(httpClient ?? ownedClient!);

            var summary = new List<MasterListSummary>();

            foreach (var group in config.Groups)
            {
                var records = await GatherGroupAsync(group, config.Bounds, config.TileLatitudeStep, config.TileLongitudeStep, client, apiKey);
                var deduped = Deduplicate(records);
                var normalized = PlaceNameNormalizer.Normalize(deduped);

                var outputPath = Path.Combine(parsed.Value.OutputDirectory, $"{group.Name}-master.jsonl");
                await File.WriteAllLinesAsync(outputPath, normalized.Select(record => JsonSerializer.Serialize(record, JsonOptions)));

                summary.Add(new MasterListSummary
                {
                    GroupName = group.Name,
                    Mode = group.Mode,
                    OutputPath = outputPath,
                    RecordCount = normalized.Count
                });
            }

            var summaryPath = Path.Combine(parsed.Value.OutputDirectory, "master-lists-summary.json");
            await File.WriteAllTextAsync(summaryPath, JsonSerializer.Serialize(summary, JsonOptions));
            await output.WriteLineAsync($"Built {summary.Count} master list files in {parsed.Value.OutputDirectory}");
            return 0;
        }
        catch (Exception exception)
        {
            await error.WriteLineAsync(exception.Message);
            return 1;
        }
    }

    private static async Task<List<NormalizedPlaceRecord>> GatherGroupAsync(
        SearchGroup group,
        RectangleBounds bounds,
        double tileLatitudeStep,
        double tileLongitudeStep,
        GooglePlacesClient client,
        string apiKey)
    {
        var records = new List<NormalizedPlaceRecord>();

        if (group.Mode.Equals("direct", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var search in group.Searches)
            {
                foreach (var expandedSearch in PlacesSearchExpander.Expand(search))
                {
                    records.AddRange(await client.SearchAsync(expandedSearch, bounds, apiKey));
                }
            }

            return records;
        }

        for (var north = bounds.North; north > bounds.South; north -= tileLatitudeStep)
        {
            var south = Math.Max(bounds.South, north - tileLatitudeStep);

            for (var west = bounds.West; west < bounds.East; west += tileLongitudeStep)
            {
                var east = Math.Min(bounds.East, west + tileLongitudeStep);
                var tileBounds = new RectangleBounds
                {
                    North = north,
                    South = south,
                    West = west,
                    East = east
                };

                foreach (var search in group.Searches)
                {
                    foreach (var expandedSearch in PlacesSearchExpander.Expand(search))
                    {
                        records.AddRange(await client.SearchAsync(expandedSearch, tileBounds, apiKey));
                    }
                }
            }
        }

        return records;
    }

    private static List<NormalizedPlaceRecord> Deduplicate(IEnumerable<NormalizedPlaceRecord> records)
    {
        var byIdentity = new Dictionary<string, NormalizedPlaceRecord>(StringComparer.OrdinalIgnoreCase);
        var byCoordinates = new Dictionary<string, NormalizedPlaceRecord>(StringComparer.OrdinalIgnoreCase);

        foreach (var record in records)
        {
            if (!string.IsNullOrWhiteSpace(record.PlaceId))
            {
                var identityKey = $"{record.Category}::{record.PlaceId}";
                byIdentity.TryAdd(identityKey, record);
                continue;
            }

            var coordinateKey = $"{record.Category}::{record.Latitude:G17}::{record.Longitude:G17}";
            byCoordinates.TryAdd(coordinateKey, record);
        }

        foreach (var record in byIdentity.Values)
        {
            var coordinateKey = $"{record.Category}::{record.Latitude:G17}::{record.Longitude:G17}";
            byCoordinates.TryAdd(coordinateKey, record);
        }

        return byCoordinates.Values.ToList();
    }

    private static void ValidateConfig(MasterListConfig config)
    {
        if (config.TileLatitudeStep <= 0d || config.TileLongitudeStep <= 0d)
        {
            throw new InvalidOperationException("TileLatitudeStep and TileLongitudeStep must be greater than zero.");
        }

        if (config.Bounds.North <= config.Bounds.South || config.Bounds.East <= config.Bounds.West)
        {
            throw new InvalidOperationException("The configured bounds are invalid.");
        }

        if (config.Groups.Count == 0)
        {
            throw new InvalidOperationException("At least one search group is required.");
        }

        foreach (var group in config.Groups)
        {
            if (string.IsNullOrWhiteSpace(group.Name))
            {
                throw new InvalidOperationException("Each search group requires a name.");
            }

            if (group.Searches.Count == 0)
            {
                throw new InvalidOperationException($"Search group '{group.Name}' must contain at least one search.");
            }
        }
    }

    private static (string ConfigPath, string OutputDirectory)? ParseArguments(IReadOnlyList<string> args)
    {
        string? configPath = null;
        string? outputDirectory = null;

        for (var index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--config" when index + 1 < args.Count:
                    configPath = args[++index];
                    break;
                case "--output-dir" when index + 1 < args.Count:
                    outputDirectory = args[++index];
                    break;
            }
        }

        return string.IsNullOrWhiteSpace(configPath) || string.IsNullOrWhiteSpace(outputDirectory)
            ? null
            : (configPath, outputDirectory);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private sealed class MasterListSummary
    {
        public string GroupName { get; init; } = string.Empty;

        public string Mode { get; init; } = string.Empty;

        public string OutputPath { get; init; } = string.Empty;

        public int RecordCount { get; init; }
    }
}
