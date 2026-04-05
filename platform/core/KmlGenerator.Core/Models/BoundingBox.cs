namespace KmlGenerator.Core.Models;

/// <summary>
/// Describes the scan bounds used to evaluate the KML outline.
/// </summary>
public sealed class BoundingBox
{
    public double MinLatitude { get; init; }

    public double MaxLatitude { get; init; }

    public double MinLongitude { get; init; }

    public double MaxLongitude { get; init; }
}
