namespace KmlGenerator.Core.Models;

/// <summary>
/// Captures the generated KML plus metadata that helps callers understand the scan result.
/// </summary>
public sealed class GenerateKmlResult
{
    public string Kml { get; init; } = string.Empty;

    public int IntersectionPolygonCount { get; init; }

    public int CoveredCellCount { get; init; }

    public int FeatureCount { get; init; }

    public BoundingBox Bounds { get; init; } = new();
}
