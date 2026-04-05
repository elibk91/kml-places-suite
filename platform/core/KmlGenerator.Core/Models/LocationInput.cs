namespace KmlGenerator.Core.Models;

/// <summary>
/// Represents one business or location point used in the category-overlap scan.
/// </summary>
public sealed class LocationInput
{
    public double Latitude { get; init; }

    public double Longitude { get; init; }

    public required string Category { get; init; }

    public required string Label { get; init; }
}
