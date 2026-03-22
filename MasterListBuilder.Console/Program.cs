using System.Text.Json;
using System.Text;
using KmlSuite.Shared.DependencyInjection;
using KmlSuite.Shared.Diagnostics;
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
        using var _ = MethodTrace.Enter(
            _logger,
            nameof(MasterListBuilderRunner),
            new Dictionary<string, object?> { ["ArgumentCount"] = args.Length });

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
                var filtered = FilterRecords(group, records);
                var deduped = Deduplicate(filtered);
                var normalized = _placeNameNormalizer.Normalize(deduped);

                var outputPath = Path.Combine(parsed.Value.OutputDirectory, $"{group.Name}-master.jsonl");
                await File.WriteAllLinesAsync(outputPath, normalized.Select(record => JsonSerializer.Serialize(record, JsonOptions)));
                _logger.LogInformation(
                    "Group {GroupName}: raw={RawCount}, filtered={FilteredCount}, deduped={DedupedCount}, normalized={NormalizedCount}",
                    group.Name,
                    records.Count,
                    filtered.Count,
                    deduped.Count,
                    normalized.Count);

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
        using var _ = MethodTrace.Enter(
            _logger,
            nameof(MasterListBuilderRunner),
            new Dictionary<string, object?> { ["GroupName"] = group.Name });

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
        using var _ = MethodTrace.Enter(_logger, nameof(MasterListBuilderRunner));
        return _searchExpander.Expand(search);
    }

    private List<NormalizedPlaceRecord> Deduplicate(IEnumerable<NormalizedPlaceRecord> records)
    {
        using var _ = MethodTrace.Enter(_logger, nameof(MasterListBuilderRunner));
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

    private IReadOnlyList<NormalizedPlaceRecord> FilterRecords(SearchGroup group, IReadOnlyList<NormalizedPlaceRecord> records)
    {
        using var _ = MethodTrace.Enter(_logger, nameof(MasterListBuilderRunner));
        if (!group.ApplyCategoryFilter)
        {
            return records;
        }

        return group.Name.ToLowerInvariant() switch
        {
            "gyms" => records.Where(IsLikelyGym).ToArray(),
            "groceries" => records.Where(IsLikelyGrocery).ToArray(),
            _ => records
        };
    }

    private bool IsLikelyGym(NormalizedPlaceRecord record)
    {
        using var _ = MethodTrace.Enter(_logger, nameof(MasterListBuilderRunner));
        if (ContainsRejectKeyword(record.Name) || ContainsRejectKeyword(record.FormattedAddress))
        {
            return false;
        }

        if (!MatchesExpectedChain(record))
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

    private bool IsLikelyGrocery(NormalizedPlaceRecord record)
    {
        using var _ = MethodTrace.Enter(_logger, nameof(MasterListBuilderRunner));
        if (ContainsRejectKeyword(record.Name) || ContainsRejectKeyword(record.FormattedAddress))
        {
            return false;
        }

        if (!MatchesExpectedChain(record))
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

    private bool MatchesExpectedChain(NormalizedPlaceRecord record)
    {
        using var _ = MethodTrace.Enter(_logger, nameof(MasterListBuilderRunner));
        if (ChainAliases.TryGetValue(record.Query, out var aliases))
        {
            return aliases.Any(alias => ContainsChainName(record.Name, alias));
        }

        return true;
    }

    private bool ContainsChainName(string? placeName, string expectedChain)
    {
        using var _ = MethodTrace.Enter(_logger, nameof(MasterListBuilderRunner));
        if (string.IsNullOrWhiteSpace(placeName) || string.IsNullOrWhiteSpace(expectedChain))
        {
            return false;
        }

        var normalizedName = NormalizeForChainMatch(placeName);
        var normalizedExpected = NormalizeForChainMatch(expectedChain);

        if (normalizedName.Contains(normalizedExpected, StringComparison.Ordinal))
        {
            return true;
        }

        var expectedTokens = normalizedExpected
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return expectedTokens.Length > 0 && expectedTokens.All(token => normalizedName.Contains(token, StringComparison.Ordinal));
    }

    private string NormalizeForChainMatch(string value)
    {
        using var _ = MethodTrace.Enter(_logger, nameof(MasterListBuilderRunner));
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.ToLowerInvariant())
        {
            builder.Append(char.IsLetterOrDigit(character) ? character : ' ');
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private bool ContainsType(NormalizedPlaceRecord record, string type) =>
        record.Types.Any(candidate => candidate.Equals(type, StringComparison.OrdinalIgnoreCase));

    private bool ContainsNameKeyword(string? value, string keyword) =>
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

    private bool ContainsRejectKeyword(string? value)
    {
        using var _ = MethodTrace.Enter(_logger, nameof(MasterListBuilderRunner));
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
        using var _ = MethodTrace.Enter(_logger, nameof(MasterListBuilderRunner));
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
        using var _ = MethodTrace.Enter(_logger, nameof(MasterListBuilderRunner));
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
    }
}


