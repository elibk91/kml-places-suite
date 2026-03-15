using PlacesGatherer.Console.Models;
using PlacesGatherer.Console.Secrets;

namespace PlacesGatherer.Console.Tests;

public sealed class LocalConfigurationSecretProviderTests
{
    [Fact]
    public void GetGoogleMapsApiKey_ReturnsEnvironmentValue()
    {
        const string variableName = "PLACES_GATHERER_TEST_KEY";
        Environment.SetEnvironmentVariable(variableName, "test-key");

        var provider = new LocalConfigurationSecretProvider(new SecretSettings
        {
            EnvironmentVariableName = variableName
        });

        var result = provider.GetGoogleMapsApiKey();

        Assert.Equal("test-key", result);
        Environment.SetEnvironmentVariable(variableName, null);
    }
}
