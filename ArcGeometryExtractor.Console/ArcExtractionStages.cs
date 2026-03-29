using System.Xml.Linq;
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
        double collapseGridCellSizeFeet,
        double pointSpacingFeet,
        double minimumParkSquareFeet,
        double minimumTrailMiles,
        double minimumCombinedParkTrailMiles);
}

internal interface IArcEntityCollapser
{
    ArcCollapsedEntityResult Collapse(
        IReadOnlyList<ArcGeometryExtractorApp.CollapsibleEntity> entities,
        bool enableEntityCollapse,
        double maximumCollapseGapMiles,
        IReadOnlySet<string> collapseEligibleCategories,
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

internal sealed record ArcSourceDocument(
    string SourceFileName,
    IReadOnlyList<XElement> Placemarks);

internal sealed record ArcExtractionStageResult(
    IReadOnlyList<NormalizedPlaceRecord> DirectPointRecords,
    IReadOnlyList<ArcGeometryExtractorApp.CollapsibleEntity> CollapsibleEntities,
    IReadOnlyList<ArcGeometryExtractorApp.ArcFeatureRecord> Features,
    IReadOnlyList<GeometryFeatureInput> GeometryFeatures,
    IReadOnlyList<ArcGeometryExtractorApp.ParkPolygonRecord> ParkPolygons,
    IReadOnlyList<ArcGeometryExtractorApp.TrailLineRecord> TrailLines);

internal sealed record ArcCollapsedEntityResult(
    IReadOnlyList<NormalizedPlaceRecord> PointRecords,
    IReadOnlyList<ArcGeometryExtractorApp.ArcFeatureRecord> FeatureRecords);
