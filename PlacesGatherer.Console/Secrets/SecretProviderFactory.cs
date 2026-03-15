using PlacesGatherer.Console.Models;

namespace PlacesGatherer.Console.Secrets;

public static class SecretProviderFactory
{
    public static ISecretProvider Create(SecretSettings settings)
    {
        if (settings.Provider.Equals("Local", StringComparison.OrdinalIgnoreCase))
        {
            return new LocalConfigurationSecretProvider(settings);
        }

        throw new NotSupportedException(
            $"Secret provider '{settings.Provider}' is not implemented yet. Use 'Local' for now.");
    }
}
