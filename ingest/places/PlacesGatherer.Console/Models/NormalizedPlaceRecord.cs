namespace PlacesGatherer.Console.Models;

public sealed record class NormalizedPlaceRecord
{
    public required string Query { get; init; }

    public required string Category { get; init; }

    public required string PlaceId { get; init; }

    public required string Name { get; init; }

    public required string FormattedAddress { get; init; }

    public double Latitude { get; init; }

    public double Longitude { get; init; }

    public required IReadOnlyList<string> Types { get; init; }

    public required string SourceQueryType { get; init; }

    public required IReadOnlyList<string> SearchNames { get; init; }
}
