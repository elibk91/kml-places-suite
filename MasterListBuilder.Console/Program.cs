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
                var filtered = FilterRecords(group, records);
                var deduped = Deduplicate(filtered);
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
                foreach (var expandedSearch in ExpandDirectSearches(group, search))
                {
                    var matches = await client.SearchAsync(expandedSearch, bounds, apiKey);

                    if (group.Name.Equals("marta", StringComparison.OrdinalIgnoreCase))
                    {
                        var bestMatch = SelectBestMartaMatch(expandedSearch.Query, matches);
                        if (bestMatch is not null)
                        {
                            records.Add(bestMatch);
                        }

                        continue;
                    }

                    records.AddRange(matches);
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

    private static IReadOnlyList<PlacesSearchDefinition> ExpandDirectSearches(SearchGroup group, PlacesSearchDefinition search)
    {
        if (!group.Name.Equals("marta", StringComparison.OrdinalIgnoreCase))
        {
            return PlacesSearchExpander.Expand(search);
        }

        return BuildMartaSearchVariants(search)
            .SelectMany(PlacesSearchExpander.Expand)
            .GroupBy(candidate => candidate.Query, StringComparer.OrdinalIgnoreCase)
            .Select(grouped => grouped.First())
            .ToArray();
    }

    private static IReadOnlyList<PlacesSearchDefinition> BuildMartaSearchVariants(PlacesSearchDefinition search)
    {
        var stationName = search.Query
            .Replace("MARTA", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("Station", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();

        var aliases = MartaQueryAliases.TryGetValue(search.Query, out var configuredAliases)
            ? configuredAliases
            : Array.Empty<string>();

        var queryVariants = new[]
        {
            search.Query,
            $"{stationName} MARTA Station",
            $"{stationName} Station MARTA",
            $"{search.Query} Atlanta GA",
            $"{stationName} Atlanta GA",
            $"{stationName} Georgia"
        }
            .Concat(aliases)
            .Where(query => !string.IsNullOrWhiteSpace(query))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return queryVariants
            .Select(query => new PlacesSearchDefinition
            {
                Query = query,
                Category = search.Category,
                SourceQueryType = search.SourceQueryType,
                Expansion = search.Expansion
            })
            .ToArray();
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

    private static IReadOnlyList<NormalizedPlaceRecord> FilterRecords(SearchGroup group, IReadOnlyList<NormalizedPlaceRecord> records)
    {
        if (!group.ApplyCategoryFilter)
        {
            return records;
        }

        return group.Name.ToLowerInvariant() switch
        {
            "gyms" => records.Where(IsLikelyGym).ToArray(),
            "groceries" => records.Where(IsLikelyGrocery).ToArray(),
            "marta" => records.Where(IsLikelyMartaStation).ToArray(),
            "parks-trails" => records.Where(IsLikelyParkOrTrail).ToArray(),
            _ => records
        };
    }

    private static bool IsLikelyGym(NormalizedPlaceRecord record)
    {
        if (ContainsRejectKeyword(record.Name) || ContainsRejectKeyword(record.FormattedAddress))
        {
            return false;
        }

        if (ContainsType(record, "gym"))
        {
            return true;
        }

        return ContainsNameKeyword(record.Name, "fitness")
               || ContainsNameKeyword(record.Name, "ymca")
               || ContainsNameKeyword(record.Name, "workout");
    }

    private static bool IsLikelyGrocery(NormalizedPlaceRecord record)
    {
        if (ContainsRejectKeyword(record.Name) || ContainsRejectKeyword(record.FormattedAddress))
        {
            return false;
        }

        if (ContainsType(record, "grocery_store") || ContainsType(record, "supermarket"))
        {
            return true;
        }

        return record.Query.Contains("Walmart", StringComparison.OrdinalIgnoreCase)
               || record.Query.Contains("Target", StringComparison.OrdinalIgnoreCase)
               || record.Query.Contains("Trader Joe", StringComparison.OrdinalIgnoreCase)
               || record.Query.Contains("Whole Foods", StringComparison.OrdinalIgnoreCase)
               || record.Query.Contains("Sprouts", StringComparison.OrdinalIgnoreCase)
               || record.Query.Contains("Publix", StringComparison.OrdinalIgnoreCase)
               || record.Query.Contains("Kroger", StringComparison.OrdinalIgnoreCase)
               || record.Query.Contains("ALDI", StringComparison.OrdinalIgnoreCase)
               || record.Query.Contains("Lidl", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLikelyMartaStation(NormalizedPlaceRecord record)
    {
        if (!record.Name.Contains("Station", StringComparison.OrdinalIgnoreCase)
            || !(ContainsType(record, "transit_station")
                 || ContainsType(record, "train_station")
                 || ContainsType(record, "subway_station")))
        {
            return false;
        }

        var expectedTokens = Tokenize(record.Query)
            .Where(token => !MartaIgnoredTokens.Contains(token, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        if (expectedTokens.Length == 0)
        {
            return true;
        }

        var candidateTokens = Tokenize(record.Name)
            .Select(NormalizeToken)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return expectedTokens.All(token => candidateTokens.Contains(NormalizeToken(token)));
    }

    private static NormalizedPlaceRecord? SelectBestMartaMatch(string query, IReadOnlyList<NormalizedPlaceRecord> matches)
    {
        var filtered = matches
            .Where(IsLikelyMartaStation)
            .Select(record => new
            {
                Record = record,
                Score = ScoreMartaMatch(query, record)
            })
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Record.Name.Length)
            .ToArray();

        return filtered.FirstOrDefault()?.Record;
    }

    private static int ScoreMartaMatch(string query, NormalizedPlaceRecord record)
    {
        var expectedTokens = Tokenize(query)
            .Where(token => !MartaIgnoredTokens.Contains(token, StringComparer.OrdinalIgnoreCase))
            .Select(NormalizeToken)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var candidateTokens = Tokenize(record.Name)
            .Select(NormalizeToken)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var score = expectedTokens.Count(token => candidateTokens.Contains(token)) * 10;

        if (ContainsType(record, "subway_station") || ContainsType(record, "train_station"))
        {
            score += 5;
        }

        if (ContainsType(record, "transit_station"))
        {
            score += 2;
        }

        if (ContainsType(record, "bus_stop"))
        {
            score -= 3;
        }

        if (ContainsType(record, "bus_station"))
        {
            score -= 2;
        }

        if (record.Name.StartsWith("MARTA", StringComparison.OrdinalIgnoreCase)
            || record.Name.EndsWith("Station", StringComparison.OrdinalIgnoreCase))
        {
            score += 1;
        }

        score -= Math.Max(0, candidateTokens.Count - expectedTokens.Count);
        return score;
    }

    private static bool IsLikelyParkOrTrail(NormalizedPlaceRecord record)
    {
        var name = record.Name;
        var types = record.Types;

        if (ContainsRejectKeyword(name) || ContainsRejectKeyword(record.FormattedAddress))
        {
            return false;
        }

        if (types.Any(type => ParkTrailRejectedTypes.Contains(type, StringComparer.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (types.Any(type => ParkTrailAllowedTypes.Contains(type, StringComparer.OrdinalIgnoreCase)))
        {
            return true;
        }

        var nameTokens = Tokenize(name)
            .Select(NormalizeToken)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (ParkTrailKeywords.Any(keyword => nameTokens.Contains(keyword)))
        {
            return true;
        }

        return false;
    }

    private static bool ContainsType(NormalizedPlaceRecord record, string type) =>
        record.Types.Any(candidate => candidate.Equals(type, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsNameKeyword(string? value, string keyword) =>
        !string.IsNullOrWhiteSpace(value) && value.Contains(keyword, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> Tokenize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        var parts = value.Split(
            [' ', '-', '/', '&', ',', '.', '(', ')', ':', ';', '|'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var part in parts)
        {
            yield return part;
        }
    }

    private static string NormalizeToken(string token) =>
        token.ToLowerInvariant() switch
        {
            "ft" => "fort",
            _ => token.ToLowerInvariant()
        };

    private static bool ContainsRejectKeyword(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return RejectKeywords.Any(keyword => value.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly string[] RejectKeywords =
    [
        "apartment",
        "apartments",
        "condo",
        "condominiums",
        "office",
        "restaurant",
        "bar",
        "grill",
        "parking",
        "garage",
        "llc",
        "inc",
        "medical",
        "dentist",
        "church",
        "bank",
        "salon"
    ];

    private static readonly string[] ParkTrailAllowedTypes =
    [
        "park",
        "hiking_area",
        "dog_park",
        "national_park",
        "state_park",
        "botanical_garden",
        "tourist_attraction",
        "campground"
    ];

    private static readonly string[] ParkTrailRejectedTypes =
    [
        "apartment_building",
        "accounting",
        "athletic_field",
        "bank",
        "bar",
        "brewery",
        "bus_station",
        "bus_stop",
        "cemetery",
        "discount_store",
        "funeral_home",
        "hotel",
        "lodging",
        "parking",
        "park_and_ride",
        "primary_school",
        "school",
        "sports_club",
        "store",
        "thrift_store"
    ];

    private static readonly string[] ParkTrailKeywords =
    [
        "park",
        "trail",
        "greenway",
        "preserve",
        "beltline",
        "path",
        "trailhead"
    ];

    private static readonly string[] MartaIgnoredTokens =
    [
        "marta",
        "station"
    ];

    private static readonly Dictionary<string, string[]> MartaQueryAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MARTA Brookhaven Oglethorpe Station"] =
        [
            "Brookhaven/Oglethorpe Station",
            "MARTA Brookhaven/Oglethorpe Station"
        ],
        ["MARTA Edgewood Candler Park Station"] =
        [
            "Edgewood/Candler Park Station",
            "MARTA Edgewood/Candler Park Station"
        ],
        ["MARTA Inman Park Reynoldstown Station"] =
        [
            "Inman Park/Reynoldstown Station",
            "MARTA Inman Park/Reynoldstown Station"
        ],
        ["MARTA Lakewood Ft McPherson Station"] =
        [
            "Lakewood/Fort McPherson Station",
            "Lakewood/Ft. McPherson Station",
            "MARTA Lakewood/Fort McPherson Station"
        ],
        ["MARTA SEC District Station"] =
        [
            "GWCC/CNN Center Station",
            "Dome/GWCC/Philips Arena/CNN Center Station",
            "MARTA GWCC/CNN Center Station"
        ]
    };

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
