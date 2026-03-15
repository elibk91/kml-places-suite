using System.Text.Json;
using PlacesGatherer.Console.Models;
using PlacesGatherer.Console.Secrets;
using PlacesGatherer.Console.Services;

return await PlacesGathererRunner.RunAsync(args, Console.Out, Console.Error);

/// <summary>
/// Console signpost: this host owns config loading, secret resolution, and local jsonl writing.
/// </summary>
public static class PlacesGathererRunner
{
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, HttpClient? httpClient = null)
    {
        var parsed = ParseArguments(args);
        if (parsed is null)
        {
            await error.WriteLineAsync("Usage: places-gatherer --config search-config.json --output places.jsonl");
            return 1;
        }

        try
        {
            var configText = await File.ReadAllTextAsync(parsed.Value.ConfigPath);
            var config = JsonSerializer.Deserialize<PlacesGathererConfig>(configText, JsonOptions);
            if (config is null)
            {
                throw new InvalidOperationException("The search config file did not contain a valid JSON payload.");
            }

            GooglePlacesClient.ValidateConfig(config);

            var secretProvider = SecretProviderFactory.Create(config.Secrets);
            var apiKey = secretProvider.GetGoogleMapsApiKey();

            using var ownedClient = httpClient is null ? new HttpClient() : null;
            var client = new GooglePlacesClient(httpClient ?? ownedClient!);

            var lines = new List<string>();

            // Each query stays independent so the output file reflects exactly what was asked for.
            foreach (var search in config.Searches)
            {
                var results = await client.SearchAsync(search, config.Bounds, apiKey);
                lines.AddRange(results.Select(record => JsonSerializer.Serialize(record, JsonOptions)));
            }

            await File.WriteAllLinesAsync(parsed.Value.OutputPath, lines);
            await output.WriteLineAsync($"Saved {lines.Count} places to {parsed.Value.OutputPath}");
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

        if (string.IsNullOrWhiteSpace(configPath) || string.IsNullOrWhiteSpace(outputPath))
        {
            return null;
        }

        return (configPath, outputPath);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };
}
