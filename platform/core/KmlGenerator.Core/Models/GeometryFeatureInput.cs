namespace KmlGenerator.Core.Models;

public sealed class GeometryFeatureInput
{
    public required string Category { get; init; }

    public required string Label { get; init; }

    public required string GeometryType { get; init; }

    public IReadOnlyList<CoordinateInput> Points { get; init; } = Array.Empty<CoordinateInput>();

    public IReadOnlyList<LineStringInput> Lines { get; init; } = Array.Empty<LineStringInput>();

    public IReadOnlyList<PolygonInput> Polygons { get; init; } = Array.Empty<PolygonInput>();
}

public sealed class CoordinateInput
{
    public double Latitude { get; init; }

    public double Longitude { get; init; }
}

public sealed class LineStringInput
{
    public IReadOnlyList<CoordinateInput> Coordinates { get; init; } = Array.Empty<CoordinateInput>();
}

public sealed class PolygonInput
{
    public IReadOnlyList<CoordinateInput> OuterRing { get; init; } = Array.Empty<CoordinateInput>();

    public IReadOnlyList<IReadOnlyList<CoordinateInput>> InnerRings { get; init; } = Array.Empty<IReadOnlyList<CoordinateInput>>();
}
