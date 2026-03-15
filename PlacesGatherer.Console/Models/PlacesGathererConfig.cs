namespace PlacesGatherer.Console.Models;

public sealed class PlacesGathererConfig
{
    public RectangleBounds Bounds { get; init; } = new();

    public SecretSettings Secrets { get; init; } = new();

    public IReadOnlyList<PlacesSearchDefinition> Searches { get; init; } = Array.Empty<PlacesSearchDefinition>();
}
