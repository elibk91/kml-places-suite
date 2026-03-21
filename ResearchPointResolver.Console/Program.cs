using System.Text.Json;
using PlacesGatherer.Console.Models;
using PlacesGatherer.Console.Secrets;
using PlacesGatherer.Console.Services;
using ResearchPointResolver.Console.Models;

return await ResearchPointResolverRunner.RunAsync(args, Console.Out, Console.Error);

/// <summary>
/// Resolves human-researched address and cross-street targets into normalized point records.
/// </summary>
public static class ResearchPointResolverRunner
{
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, HttpClient? httpClient = null)
    {
        var parsed = ParseArguments(args);
        if (parsed is null)
        {
            await error.WriteLineAsync("Usage: research-point-resolver --config research-targets.json --output resolved-points.jsonl");
            return 1;
        }

        try
        {
            var configText = await File.ReadAllTextAsync(parsed.Value.ConfigPath);
            var config = JsonSerializer.Deserialize<ResearchTargetConfig>(configText, JsonOptions);
            if (config is null)
            {
                throw new InvalidOperationException("The research target config file did not contain a valid JSON payload.");
            }

            if (config.Targets.Count == 0)
            {
                throw new InvalidOperationException("At least one research target is required.");
            }

            var secretProvider = SecretProviderFactory.Create(config.Secrets);
            var apiKey = secretProvider.GetGoogleMapsApiKey();

            using var ownedClient = httpClient is null ? new HttpClient() : null;
            var client = new GooglePlacesClient(httpClient ?? ownedClient!);
            var results = new List<NormalizedPlaceRecord>();

            foreach (var target in config.Targets)
            {
                var matches = await client.SearchAsync(
                    new PlacesSearchDefinition
                    {
                        Query = target.Query,
                        Category = target.Category,
                        SourceQueryType = "research"
                    },
                    config.Bounds,
                    apiKey);

                var match = SelectBestMatch(target, matches);
                if (match is null)
                {
                    continue;
                }

                results.Add(match with
                {
                    Name = string.IsNullOrWhiteSpace(target.Label) ? match.Name : target.Label
                });
            }

            await File.WriteAllLinesAsync(parsed.Value.OutputPath, results.Select(result => JsonSerializer.Serialize(result, JsonOptions)));
            await output.WriteLineAsync($"Resolved {results.Count} researched points to {parsed.Value.OutputPath}");
            return 0;
        }
        catch (Exception exception)
        {
            await error.WriteLineAsync(exception.Message);
            return 1;
        }
    }

    private static (string ConfigPath, string OutputPath)? ParseArguments(IReadOnlyList<string> args)
    {
        string? configPath = null;
        string? outputPath = null;

        for (var index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--config" when index + 1 < args.Count:
                    configPath = args[++index];
                    break;
                case "--output" when index + 1 < args.Count:
                    outputPath = args[++index];
                    break;
            }
        }

        return string.IsNullOrWhiteSpace(configPath) || string.IsNullOrWhiteSpace(outputPath)
            ? null
            : (configPath, outputPath);
    }

    private static NormalizedPlaceRecord? SelectBestMatch(ResearchTarget target, IReadOnlyList<NormalizedPlaceRecord> matches)
    {
        var ranked = matches
            .Select(match => new
            {
                Record = match,
                Score = ScoreMatch(target, match)
            })
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Record.Name.Length)
            .ToArray();

        return ranked.FirstOrDefault()?.Record;
    }

    private static int ScoreMatch(ResearchTarget target, NormalizedPlaceRecord record)
    {
        var score = 0;
        var nameTokens = Tokenize(record.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var addressTokens = Tokenize(record.FormattedAddress).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targetTokens = Tokenize(target.Label)
            .Where(token => !IgnoredLabelTokens.Contains(token, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        var specificTokens = targetTokens
            .Where(token => !GenericPlaceTokens.Contains(token, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        score += targetTokens.Count(token => nameTokens.Contains(token)) * 8;
        score += targetTokens.Count(token => addressTokens.Contains(token)) * 4;

        if (target.Category.Equals("park", StringComparison.OrdinalIgnoreCase)
            || target.Category.Equals("trail", StringComparison.OrdinalIgnoreCase))
        {
            var specificMatchCount = specificTokens.Count(token => nameTokens.Contains(token) || addressTokens.Contains(token));
            if (specificTokens.Length > 0 && specificMatchCount == 0)
            {
                return 0;
            }

            if (record.Types.Any(type => ParkTrailAllowedTypes.Contains(type, StringComparer.OrdinalIgnoreCase)))
            {
                score += 12;
            }

            if (record.Types.Any(type => ParkTrailRejectedTypes.Contains(type, StringComparer.OrdinalIgnoreCase)))
            {
                score -= 20;
            }

            var placeKeywordMatch = nameTokens.Overlaps(ParkTrailNameKeywords) || addressTokens.Overlaps(ParkTrailNameKeywords);
            if (!record.Types.Any(type => ParkTrailAllowedTypes.Contains(type, StringComparer.OrdinalIgnoreCase))
                && !placeKeywordMatch)
            {
                return 0;
            }

            if (placeKeywordMatch)
            {
                score += 6;
            }
        }

        return score;
    }

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
            yield return part.ToLowerInvariant() switch
            {
                "ft" => "fort",
                _ => part.ToLowerInvariant()
            };
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private static readonly string[] IgnoredLabelTokens =
    [
        "access",
        "at",
        "drive",
        "east",
        "edge",
        "entrance",
        "gate",
        "main",
        "north",
        "park",
        "road",
        "south",
        "street",
        "trail",
        "west"
    ];

    private static readonly string[] ParkTrailAllowedTypes =
    [
        "park",
        "city_park",
        "dog_park",
        "hiking_area",
        "national_park",
        "nature_preserve",
        "playground",
        "state_park",
        "tourist_attraction"
    ];

    private static readonly string[] ParkTrailRejectedTypes =
    [
        "apartment_building",
        "business_center",
        "bus_station",
        "bus_stop",
        "condominium_complex",
        "health",
        "housing_complex",
        "medical_clinic",
        "parking",
        "parking_lot",
        "shopping_mall",
        "transit_station",
        "transit_stop"
    ];

    private static readonly string[] ParkTrailNameKeywords =
    [
        "beltline",
        "greenway",
        "park",
        "path400",
        "preserve",
        "trail",
        "trailhead"
    ];

    private static readonly string[] GenericPlaceTokens =
    [
        "access",
        "avenue",
        "boulevard",
        "center",
        "crossing",
        "drive",
        "edge",
        "entrance",
        "gate",
        "marker",
        "mile",
        "north",
        "path",
        "park",
        "ramp",
        "road",
        "south",
        "station",
        "street",
        "trail",
        "west"
    ];
}
