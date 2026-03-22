using System.Text.Json;
using KmlSuite.Shared.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PlacesGatherer.Console;
using PlacesGatherer.Console.Models;
using PlacesGatherer.Console.Secrets;
using PlacesGatherer.Console.Services;

return await PlacesGathererProgram.RunAsync(args, Console.Out, Console.Error);

public static class PlacesGathererProgram
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
        services.AddTracedSingleton<IPlacesGathererApp, PlacesGathererRunner>();

        await using var serviceProvider = services.BuildServiceProvider();
        return await serviceProvider.GetRequiredService<IPlacesGathererApp>().RunAsync(args, output, error);
    }
}

/// <summary>
/// Console signpost: this host owns config loading, secret resolution, and local jsonl writing.
/// </summary>
public sealed class PlacesGathererRunner : IPlacesGathererApp
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly ILogger<PlacesGathererRunner> _logger;
    private readonly ISecretProviderFactory _secretProviderFactory;
    private readonly IPlacesSearchExpander _searchExpander;
    private readonly IPlaceNameNormalizer _placeNameNormalizer;
    private readonly IGooglePlacesClient _client;

    public PlacesGathererRunner(
        ILogger<PlacesGathererRunner> logger,
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
            _logger.LogInformation(
                "Loaded gatherer config {ConfigPath} with {SearchCount} searches",
                parsed.Value.ConfigPath,
                config.Searches.Count);

            var secretProvider = _secretProviderFactory.Create(config.Secrets);
            var apiKey = secretProvider.GetGoogleMapsApiKey();
            _logger.LogDebug("Resolved Google Maps API key using provider {Provider}", config.Secrets.Provider);

            var gatheredRecords = new List<NormalizedPlaceRecord>();

            foreach (var search in config.Searches)
            {
                foreach (var expandedSearch in _searchExpander.Expand(search))
                {
                    var results = await _client.SearchAsync(expandedSearch, config.Bounds, apiKey);
                    _logger.LogInformation(
                        "Search {Query} ({SourceQueryType}) returned {ResultCount} normalized records",
                        expandedSearch.Query,
                        expandedSearch.SourceQueryType,
                        results.Count);
                    gatheredRecords.AddRange(results);
                }
            }

            _logger.LogInformation("Collected {GatheredRecordCount} raw records before normalization", gatheredRecords.Count);
            var normalizedRecords = _placeNameNormalizer.Normalize(gatheredRecords);
            _logger.LogInformation("Normalized to {NormalizedRecordCount} output records", normalizedRecords.Count);
            var lines = normalizedRecords.Select(record => JsonSerializer.Serialize(record, JsonOptions));
            await File.WriteAllLinesAsync(parsed.Value.OutputPath, lines);
            await output.WriteLineAsync($"Saved {normalizedRecords.Count} places to {parsed.Value.OutputPath}");
            return 0;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Places gatherer failed");
            await error.WriteLineAsync(exception.Message);
            return 1;
        }
    }

    private (string ConfigPath, string OutputPath)? ParseArguments(IReadOnlyList<string> args)
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
}


