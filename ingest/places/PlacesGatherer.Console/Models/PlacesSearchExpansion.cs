namespace PlacesGatherer.Console.Models;

/// <summary>
/// Query-expansion rules let the gatherer ask Google for additional real points without inventing coordinates.
/// </summary>
public sealed class PlacesSearchExpansion
{
    public bool Enabled { get; init; }

    public IReadOnlyList<string> Templates { get; init; } = Array.Empty<string>();
}
