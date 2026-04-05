namespace PlacesGatherer.Console.Models;

public sealed class SecretSettings
{
    public string Provider { get; init; } = "Local";

    public string EnvironmentVariableName { get; init; } = "GoogleMaps__ApiKey";
}
