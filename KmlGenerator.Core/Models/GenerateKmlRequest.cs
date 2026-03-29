namespace KmlGenerator.Core.Models;

/// <summary>
/// Shared request contract for the API and the file-based console host.
/// </summary>
public sealed class GenerateKmlRequest
{
    public IReadOnlyList<LocationInput> Locations { get; init; } = Array.Empty<LocationInput>();

    public IReadOnlyList<GeometryFeatureInput> Features { get; init; } = Array.Empty<GeometryFeatureInput>();

    public double Step { get; init; } = 0.0001d;

    public double RadiusMiles { get; init; } = 0.5d;

    public IReadOnlyDictionary<string, double> CategoryRadiusMiles { get; init; } = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

    public double PaddingDegrees { get; init; } = 0.01d;
}
