namespace PlacesGatherer.Console.Models;

public sealed record class PlacesSearchDefinition
{
    public required string Query { get; init; }

    public required string Category { get; init; }

    public PlacesSearchExpansion Expansion { get; init; } = new();

    public required string SourceQueryType { get; init; }
}
