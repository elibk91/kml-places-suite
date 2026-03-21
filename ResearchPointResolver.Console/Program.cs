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

                var match = matches.FirstOrDefault();
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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };
}
