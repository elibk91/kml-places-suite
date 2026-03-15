namespace PlacesGatherer.Console.Models;

public sealed class NormalizedPlaceRecord
{
    public string Query { get; init; } = string.Empty;

    public string Category { get; init; } = string.Empty;

    public string PlaceId { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string? FormattedAddress { get; init; }

    public double Latitude { get; init; }

    public double Longitude { get; init; }

    public IReadOnlyList<string> Types { get; init; } = Array.Empty<string>();
}
