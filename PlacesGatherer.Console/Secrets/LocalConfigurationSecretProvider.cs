using PlacesGatherer.Console.Models;

namespace PlacesGatherer.Console.Secrets;

/// <summary>
/// Current production of the secret-provider abstraction.
/// Future cloud secret managers should plug in behind the same interface.
/// </summary>
public sealed class LocalConfigurationSecretProvider : ISecretProvider
{
    private readonly SecretSettings _settings;

    public LocalConfigurationSecretProvider(SecretSettings settings)
    {
        _settings = settings;
    }

    public string GetGoogleMapsApiKey()
    {
        var apiKey = Environment.GetEnvironmentVariable(_settings.EnvironmentVariableName);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            return apiKey;
        }

        throw new InvalidOperationException(
            $"Missing Google Places API key. Set the {_settings.EnvironmentVariableName} environment variable.");
    }
}
