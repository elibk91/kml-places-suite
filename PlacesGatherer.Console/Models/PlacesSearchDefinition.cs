namespace PlacesGatherer.Console.Models;

public sealed record class PlacesSearchDefinition
{
    public string Query { get; init; } = string.Empty;

    public string Category { get; init; } = string.Empty;

    public PlacesSearchExpansion? Expansion { get; init; }

    public string SourceQueryType { get; init; } = "base";
}
