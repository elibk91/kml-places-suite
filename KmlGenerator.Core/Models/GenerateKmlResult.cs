namespace KmlGenerator.Core.Models;

/// <summary>
/// Captures the generated KML plus metadata that helps callers understand the scan result.
/// </summary>
public sealed class GenerateKmlResult
{
    public string Kml { get; init; } = string.Empty;

    public int BoundaryPointCount { get; init; }

    public int ValidPointCount { get; init; }

    public BoundingBox Bounds { get; init; } = new();
}
