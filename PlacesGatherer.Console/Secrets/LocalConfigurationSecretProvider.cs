using KmlSuite.Shared.Diagnostics;
using Microsoft.Extensions.Logging;
using PlacesGatherer.Console.Models;

namespace PlacesGatherer.Console.Secrets;

public sealed class LocalConfigurationSecretProvider : ISecretProvider
{
    private readonly SecretSettings _settings;
    private readonly ILogger<LocalConfigurationSecretProvider> _logger;

    public LocalConfigurationSecretProvider(SecretSettings settings, ILogger<LocalConfigurationSecretProvider> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public string GetGoogleMapsApiKey()
    {
        using var _ = MethodTrace.Enter(
            _logger,
            nameof(LocalConfigurationSecretProvider),
            new Dictionary<string, object?> { ["EnvironmentVariableName"] = _settings.EnvironmentVariableName });

        var apiKey = Environment.GetEnvironmentVariable(_settings.EnvironmentVariableName);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogDebug("Resolved Google Maps API key from environment variable {EnvironmentVariableName}", _settings.EnvironmentVariableName);
            return apiKey;
        }

        throw new InvalidOperationException($"Missing Google Places API key. Set the {_settings.EnvironmentVariableName} environment variable.");
    }
}
