namespace KmlGenerator.Core.Models;

/// <summary>
/// Represents one business or location point used in the category-overlap scan.
/// </summary>
public sealed class LocationInput
{
    public double Latitude { get; init; }

    public double Longitude { get; init; }

    public string Category { get; init; } = string.Empty;
}
