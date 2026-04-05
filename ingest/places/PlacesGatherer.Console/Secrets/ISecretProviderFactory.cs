using PlacesGatherer.Console.Models;

namespace PlacesGatherer.Console.Secrets;

public interface ISecretProviderFactory
{
    ISecretProvider Create(SecretSettings settings);
}
