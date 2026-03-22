namespace KmlGenerator.Core.Models;

public sealed class CoverageDiagnosticResult
{
    public double Latitude { get; init; }

    public double Longitude { get; init; }

    public double RadiusMiles { get; init; }

    public IReadOnlyList<CategoryCoverageDiagnostic> Categories { get; init; } = Array.Empty<CategoryCoverageDiagnostic>();

    public IReadOnlyList<string> MissingCategories { get; init; } = Array.Empty<string>();
}

public sealed class CategoryCoverageDiagnostic
{
    public string Category { get; init; } = string.Empty;

    public bool HasMatchWithinRadius { get; init; }

    public IReadOnlyList<CoverageDiagnosticLocation> NearestLocations { get; init; } = Array.Empty<CoverageDiagnosticLocation>();
}

public sealed class CoverageDiagnosticLocation
{
    public string Label { get; init; } = string.Empty;

    public double Latitude { get; init; }

    public double Longitude { get; init; }

    public double DistanceMiles { get; init; }
}
