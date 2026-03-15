namespace PlacesGatherer.Console.Models;

public sealed class PlacesSearchDefinition
{
    public string Query { get; init; } = string.Empty;

    public string Category { get; init; } = string.Empty;
}
