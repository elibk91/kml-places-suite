using System.Text.Json;
using System.Text;
using KmlSuite.Shared.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MasterListBuilder.Console;
using MasterListBuilder.Console.Models;
using PlacesGatherer.Console.Models;
using PlacesGatherer.Console.Secrets;
using PlacesGatherer.Console.Services;

return await MasterListBuilderProgram.RunAsync(args, Console.Out, Console.Error);

public static class MasterListBuilderProgram
{
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, HttpClient? httpClient = null)
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
        services.AddSingleton(provider => httpClient ?? new HttpClient());
        services.AddTracedSingleton<ISecretProviderFactory, SecretProviderFactory>();
        services.AddTracedSingleton<IPlacesSearchExpander, PlacesSearchExpander>();
        services.AddTracedSingleton<IPlaceNameNormalizer, PlaceNameNormalizer>();
        services.AddTracedSingleton<IGooglePlacesClient, GooglePlacesClient>();
        services.AddTracedSingleton<IMasterListBuilderApp, MasterListBuilderRunner>();

        await using var serviceProvider = services.BuildServiceProvider();
        return await serviceProvider.GetRequiredService<IMasterListBuilderApp>().RunAsync(args, output, error);
    }
}

/// <summary>
/// Builds category-specific master lists for the authoritative Google-backed categories.
/// </summary>
public sealed class MasterListBuilderRunner : IMasterListBuilderApp
{
    private readonly ILogger<MasterListBuilderRunner> _logger;
    private readonly ISecretProviderFactory _secretProviderFactory;
    private readonly IPlacesSearchExpander _searchExpander;
    private readonly IPlaceNameNormalizer _placeNameNormalizer;
    private readonly IGooglePlacesClient _client;
    private const string RejectReasonRejectKeyword = "reject_keyword";
    private const string RejectReasonMissingBrandTokens = "missing_required_brand_tokens";
    private const string RejectReasonTypeMismatch = "type_mismatch";

    public MasterListBuilderRunner(
        ILogger<MasterListBuilderRunner> logger,
        ISecretProviderFactory secretProviderFactory,
        IPlacesSearchExpander searchExpander,
        IPlaceNameNormalizer placeNameNormalizer,
        IGooglePlacesClient client)
    {
        _logger = logger;
        _secretProviderFactory = secretProviderFactory;
        _searchExpander = searchExpander;
        _placeNameNormalizer = placeNameNormalizer;
        _client = client;
    }

    public async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
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
            _logger.LogInformation("Loaded master-list config with {GroupCount} groups", config.Groups.Count);

            var secretProvider = _secretProviderFactory.Create(config.Secrets);
            var apiKey = secretProvider.GetGoogleMapsApiKey();

            var summary = new List<MasterListSummary>();

            foreach (var group in config.Groups)
            {
                var records = await GatherGroupAsync(group, config.Bounds, config.TileLatitudeStep, config.TileLongitudeStep, _client, apiKey);
                var filterResult = FilterRecords(group, records);
                var deduped = Deduplicate(filterResult.KeptRecords);
                var normalized = _placeNameNormalizer.Normalize(deduped);

                var outputPath = Path.Combine(parsed.Value.OutputDirectory, $"{group.Name}-master.jsonl");
                await File.WriteAllLinesAsync(outputPath, normalized.Select(record => JsonSerializer.Serialize(record, JsonOptions)));
                var auditPath = Path.Combine(parsed.Value.OutputDirectory, $"{group.Name}-audit.json");
                await File.WriteAllTextAsync(auditPath, JsonSerializer.Serialize(CreateAuditSummary(group, records, filterResult, deduped, normalized), JsonOptions));
                _logger.LogInformation(
                    "Group {GroupName}: raw={RawCount}, kept={KeptCount}, rejected={RejectedCount}, deduped={DedupedCount}, normalized={NormalizedCount}",
                    group.Name,
                    records.Count,
                    filterResult.KeptRecords.Count,
                    filterResult.Rejections.Count,
                    deduped.Count,
                    normalized.Count);

                summary.Add(new MasterListSummary
                {
                    GroupName = group.Name,
                    Mode = group.Mode,
                    OutputPath = outputPath,
                    RecordCount = normalized.Count,
                    AuditPath = auditPath
                });
            }

            var summaryPath = Path.Combine(parsed.Value.OutputDirectory, "master-lists-summary.json");
            await File.WriteAllTextAsync(summaryPath, JsonSerializer.Serialize(summary, JsonOptions));
            await output.WriteLineAsync($"Built {summary.Count} master list files in {parsed.Value.OutputDirectory}");
            return 0;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Master list builder failed");
            await error.WriteLineAsync(exception.Message);
            return 1;
        }
    }

    private async Task<List<NormalizedPlaceRecord>> GatherGroupAsync(
        SearchGroup group,
        RectangleBounds bounds,
        double tileLatitudeStep,
        double tileLongitudeStep,
        IGooglePlacesClient client,
        string apiKey)
    {
        var records = new List<NormalizedPlaceRecord>();

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
                    foreach (var expandedSearch in ExpandGroupSearches(search))
                    {
                        records.AddRange(await client.SearchAsync(expandedSearch, tileBounds, apiKey));
                    }
                }
            }
        }

        return records;
    }

    private IReadOnlyList<PlacesSearchDefinition> ExpandGroupSearches(PlacesSearchDefinition search)
    {
        return _searchExpander.Expand(search);
    }

    private List<NormalizedPlaceRecord> Deduplicate(IEnumerable<NormalizedPlaceRecord> records)
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

    private FilterOutcome FilterRecords(SearchGroup group, IReadOnlyList<NormalizedPlaceRecord> records)
    {
        if (!group.ApplyCategoryFilter)
        {
            return new FilterOutcome(records, Array.Empty<RejectedRecord>());
        }

        return group.Name.ToLowerInvariant() switch
        {
            "gyms" => FilterByPredicate(records, EvaluateGymRecord),
            "groceries" => FilterByPredicate(records, EvaluateGroceryRecord),
            _ => new FilterOutcome(records, Array.Empty<RejectedRecord>())
        };
    }

    private FilterOutcome FilterByPredicate(
        IReadOnlyList<NormalizedPlaceRecord> records,
        Func<NormalizedPlaceRecord, RecordEvaluation> evaluator)
    {
        var kept = new List<NormalizedPlaceRecord>(records.Count);
        var rejections = new List<RejectedRecord>();

        foreach (var record in records)
        {
            var evaluation = evaluator(record);
            if (evaluation.IsAccepted)
            {
                kept.Add(record);
                continue;
            }

            rejections.Add(new RejectedRecord
            {
                Query = record.Query,
                Name = record.Name,
                FormattedAddress = record.FormattedAddress,
                SourceQueryType = record.SourceQueryType,
                PlaceId = record.PlaceId,
                Latitude = record.Latitude,
                Longitude = record.Longitude,
                Reason = evaluation.Reason ?? "rejected",
                Types = record.Types
            });
        }

        return new FilterOutcome(kept, rejections);
    }

    private RecordEvaluation EvaluateGymRecord(NormalizedPlaceRecord record)
    {
        if (ContainsRejectKeyword(record.Name) || ContainsRejectKeyword(record.FormattedAddress))
        {
            return RecordEvaluation.Reject(RejectReasonRejectKeyword);
        }

        if (!MatchesExpectedChain(record))
        {
            return RecordEvaluation.Reject(RejectReasonMissingBrandTokens);
        }

        if (record.Query.Contains("YMCA", StringComparison.OrdinalIgnoreCase))
        {
            return HasAnyType(record, AllowedGymTypes)
                ? RecordEvaluation.Accept
                : RecordEvaluation.Reject(RejectReasonTypeMismatch);
        }

        return HasAnyType(record, AllowedGymTypes)
            ? RecordEvaluation.Accept
            : RecordEvaluation.Reject(RejectReasonTypeMismatch);
    }

    private RecordEvaluation EvaluateGroceryRecord(NormalizedPlaceRecord record)
    {
        if (ContainsRejectKeyword(record.Name) || ContainsRejectKeyword(record.FormattedAddress))
        {
            return RecordEvaluation.Reject(RejectReasonRejectKeyword);
        }

        if (!MatchesExpectedChain(record))
        {
            return RecordEvaluation.Reject(RejectReasonMissingBrandTokens);
        }

        if (HasAnyType(record, AllowedGroceryTypes))
        {
            return RecordEvaluation.Accept;
        }

        return RecordEvaluation.Reject(RejectReasonTypeMismatch);
    }

    private bool MatchesExpectedChain(NormalizedPlaceRecord record)
    {
        if (ChainAliases.TryGetValue(record.Query, out var aliases))
        {
            return aliases.Any(alias => ContainsChainName(record.Name, alias));
        }

        return true;
    }

    private bool ContainsChainName(string? placeName, string expectedChain)
    {
        if (string.IsNullOrWhiteSpace(placeName) || string.IsNullOrWhiteSpace(expectedChain))
        {
            return false;
        }

        var normalizedName = NormalizeForChainMatch(placeName);
        var normalizedExpected = NormalizeForChainMatch(expectedChain);

        if (ContainsNormalizedPhrase(normalizedName, normalizedExpected))
        {
            return true;
        }

        var actualTokens = TokenizeNormalized(normalizedName);
        var expectedTokens = TokenizeNormalized(normalizedExpected);

        return expectedTokens.Length > 0 && expectedTokens.All(actualTokens.Contains);
    }

    private string NormalizeForChainMatch(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.ToLowerInvariant())
        {
            builder.Append(char.IsLetterOrDigit(character) ? character : ' ');
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private bool ContainsType(NormalizedPlaceRecord record, string type) =>
        record.Types.Any(candidate => candidate.Equals(type, StringComparison.OrdinalIgnoreCase));

    private bool HasAnyType(NormalizedPlaceRecord record, IReadOnlySet<string> allowedTypes) =>
        record.Types.Any(candidate => allowedTypes.Contains(candidate));

    private static bool ContainsNormalizedPhrase(string normalizedValue, string normalizedPhrase)
    {
        if (string.IsNullOrWhiteSpace(normalizedValue) || string.IsNullOrWhiteSpace(normalizedPhrase))
        {
            return false;
        }

        var paddedValue = $" {normalizedValue} ";
        var paddedPhrase = $" {normalizedPhrase} ";
        return paddedValue.Contains(paddedPhrase, StringComparison.Ordinal);
    }

    private static string[] TokenizeNormalized(string value) =>
        value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private bool ContainsRejectKeyword(string? value)
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

    private static readonly HashSet<string> AllowedGymTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "gym",
        "fitness_center",
        "sports_complex",
        "sports_activity_location",
        "athletic_field",
        "swimming_pool",
        "yoga_studio"
    };

    private static readonly HashSet<string> AllowedGroceryTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "grocery_store",
        "supermarket",
        "food_store",
        "warehouse_store",
        "store"
    };

    private static readonly Dictionary<string, string[]> ChainAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Planet Fitness"] = ["Planet Fitness"],
        ["LA Fitness"] = ["LA Fitness"],
        ["Esporta Fitness"] = ["Esporta Fitness"],
        ["Crunch Fitness"] = ["Crunch", "Crunch Fitness"],
        ["Workout Anytime"] = ["Workout Anytime"],
        ["Anytime Fitness"] = ["Anytime Fitness"],
        ["Snap Fitness"] = ["Snap Fitness"],
        ["YMCA"] = ["YMCA"],
        ["Life Time"] = ["Life Time"],
        ["Onelife Fitness"] = ["Onelife Fitness", "Onelife"],
        ["Kroger"] = ["Kroger"],
        ["Publix"] = ["Publix"],
        ["Walmart"] = ["Walmart"],
        ["Walmart Neighborhood Market"] = ["Walmart Neighborhood Market", "Walmart"],
        ["Target Grocery"] = ["Target Grocery", "Target"],
        ["ALDI"] = ["ALDI"],
        ["Trader Joe's"] = ["Trader Joe's", "Trader Joes"],
        ["Lidl"] = ["Lidl"],
        ["Whole Foods Market"] = ["Whole Foods Market", "Whole Foods"],
        ["Sprouts Farmers Market"] = ["Sprouts Farmers Market", "Sprouts"],
        ["Costco"] = ["Costco"],
        ["Sam's Club"] = ["Sam's Club", "Sams Club"],
        ["The Fresh Market"] = ["The Fresh Market"]
    };

    private void ValidateConfig(MasterListConfig config)
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

            if (!group.Mode.Equals("tiled", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Search group '{group.Name}' must use tiled mode.");
            }

            if (!SupportedGroupNames.Contains(group.Name))
            {
                throw new InvalidOperationException($"Search group '{group.Name}' is not supported. Use only gyms or groceries.");
            }

            if (group.Searches.Count == 0)
            {
                throw new InvalidOperationException($"Search group '{group.Name}' must contain at least one search.");
            }
        }
    }

    private (string ConfigPath, string OutputDirectory)? ParseArguments(IReadOnlyList<string> args)
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

    private static readonly HashSet<string> SupportedGroupNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "gyms",
        "groceries"
    };

    private sealed class MasterListSummary
    {
        public string GroupName { get; init; } = string.Empty;

        public string Mode { get; init; } = string.Empty;

        public string OutputPath { get; init; } = string.Empty;

        public int RecordCount { get; init; }

        public string AuditPath { get; init; } = string.Empty;
    }

    private AuditSummary CreateAuditSummary(
        SearchGroup group,
        IReadOnlyList<NormalizedPlaceRecord> rawRecords,
        FilterOutcome filterResult,
        IReadOnlyList<NormalizedPlaceRecord> dedupedRecords,
        IReadOnlyList<NormalizedPlaceRecord> normalizedRecords)
    {
        return new AuditSummary
        {
            GroupName = group.Name,
            RawCount = rawRecords.Count,
            KeptCount = filterResult.KeptRecords.Count,
            RejectedCount = filterResult.Rejections.Count,
            DedupedCount = dedupedRecords.Count,
            NormalizedCount = normalizedRecords.Count,
            KeptCountsByQuery = normalizedRecords
                .GroupBy(record => record.Query, StringComparer.OrdinalIgnoreCase)
                .OrderBy(grouping => grouping.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(grouping => grouping.Key, grouping => grouping.Count(), StringComparer.OrdinalIgnoreCase),
            Rejections = filterResult.Rejections
        };
    }

    private sealed record FilterOutcome(
        IReadOnlyList<NormalizedPlaceRecord> KeptRecords,
        IReadOnlyList<RejectedRecord> Rejections);

    private sealed record RecordEvaluation(bool IsAccepted, string? Reason)
    {
        public static RecordEvaluation Accept { get; } = new(true, null);

        public static RecordEvaluation Reject(string reason) => new(false, reason);
    }

    private sealed class RejectedRecord
    {
        public string Query { get; init; } = string.Empty;

        public string Name { get; init; } = string.Empty;

        public string FormattedAddress { get; init; } = string.Empty;

        public string SourceQueryType { get; init; } = string.Empty;

        public string PlaceId { get; init; } = string.Empty;

        public double Latitude { get; init; }

        public double Longitude { get; init; }

        public string Reason { get; init; } = string.Empty;

        public IReadOnlyList<string> Types { get; init; } = Array.Empty<string>();
    }

    private sealed class AuditSummary
    {
        public string GroupName { get; init; } = string.Empty;

        public int RawCount { get; init; }

        public int KeptCount { get; init; }

        public int RejectedCount { get; init; }

        public int DedupedCount { get; init; }

        public int NormalizedCount { get; init; }

        public IReadOnlyDictionary<string, int> KeptCountsByQuery { get; init; } =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<RejectedRecord> Rejections { get; init; } = Array.Empty<RejectedRecord>();
    }
}


