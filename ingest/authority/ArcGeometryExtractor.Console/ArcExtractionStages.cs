using KmlGenerator.Core.Models;
using PlacesGatherer.Console.Models;

internal interface IArcSourceReader
{
    Task<IReadOnlyList<ArcSourceDocument>> ReadAsync(IReadOnlyList<string> inputPaths);
}

internal interface IArcFeatureExtractor
{
    ArcExtractionStageResult Extract(
        IReadOnlyList<ArcSourceDocument> sourceDocuments,
        double pointSpacingFeet,
        double minimumParkSquareFeet,
        double minimumTrailMiles,
        double minimumCombinedParkTrailMiles);
}

internal interface IArcOutputWriter
{
    Task WriteAsync(
        ArcGeometryExtractorApp.ExtractorArguments arguments,
        IReadOnlyList<NormalizedPlaceRecord> allPoints,
        IReadOnlyList<NormalizedPlaceRecord> parkPoints,
        IReadOnlyList<NormalizedPlaceRecord> trailPoints,
        IReadOnlyList<ArcGeometryExtractorApp.ArcFeatureRecord> features,
        IReadOnlyList<GeometryFeatureInput> geometryFeatures,
        IReadOnlyList<ArcGeometryExtractorApp.ParkPolygonRecord> parkPolygons,
        IReadOnlyList<ArcGeometryExtractorApp.TrailLineRecord> trailLines);
}

internal sealed record ArcSourcePlacemark(
    string Name,
    string Description,
    IReadOnlyDictionary<string, string> Metadata,
    IReadOnlyList<CoordinateInput> Points,
    IReadOnlyList<IReadOnlyList<CoordinateInput>> Lines,
    IReadOnlyList<IReadOnlyList<IReadOnlyList<CoordinateInput>>> Polygons);

internal sealed record ArcSourceDocument(
    string SourceFileName,
    IReadOnlyList<ArcSourcePlacemark> Placemarks);

internal sealed record ArcExtractionStageResult(
    IReadOnlyList<NormalizedPlaceRecord> PointRecords,
    IReadOnlyList<ArcGeometryExtractorApp.ArcFeatureRecord> Features,
    IReadOnlyList<GeometryFeatureInput> GeometryFeatures,
    IReadOnlyList<ArcGeometryExtractorApp.ParkPolygonRecord> ParkPolygons,
    IReadOnlyList<ArcGeometryExtractorApp.TrailLineRecord> TrailLines);
