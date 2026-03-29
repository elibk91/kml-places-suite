using System.Collections.Concurrent;
using System.Globalization;
using KmlGenerator.Core.Exceptions;
using KmlGenerator.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KmlGenerator.Core.Services;

public sealed class KmlGenerationService : IKmlGenerationService
{
    private const double DegreesToRadians = Math.PI / 180d;
    private const double FeetPerDegreeLatitude = 364_000d;
    private readonly ILogger<KmlGenerationService> _logger;

    public KmlGenerationService(ILogger<KmlGenerationService>? logger = null)
    {
        _logger = logger ?? NullLogger<KmlGenerationService>.Instance;
    }

    public GenerateKmlResult Generate(GenerateKmlRequest request)
    {
        Validate(request);
        var features = MaterializeFeatures(request);
        var nativeResult = NativeGeometryLibrary.Generate(request);
        var frame = BuildFrame(features);
        var categories = BuildCategories(features, request, frame);

        _logger.LogInformation(
            "Generated native geometry KML with {CategoryCount} categories, {FeatureCount} features, and {PolygonCount} output polygons",
            categories.Count,
            features.Count,
            nativeResult.IntersectionPolygonCount);

        return new GenerateKmlResult
        {
            Kml = BuildKml(nativeResult.Polygons, features),
            IntersectionPolygonCount = nativeResult.IntersectionPolygonCount,
            CoveredCellCount = nativeResult.CoveredCellCount,
            FeatureCount = nativeResult.FeatureCount,
            Bounds = nativeResult.Bounds
        };
    }

    public CoverageDiagnosticResult DiagnoseCoverage(GenerateKmlRequest request, double latitude, double longitude, double radiusMiles, int topPerCategory)
    {
        Validate(request);
        ValidateCoordinate(latitude, longitude);
        if (radiusMiles <= 0d)
        {
            throw new KmlValidationException("RadiusMiles must be greater than zero.");
        }

        if (topPerCategory <= 0)
        {
            throw new KmlValidationException("TopPerCategory must be greater than zero.");
        }

        var features = MaterializeFeatures(request);
        var frame = BuildFrame(features);
        var categories = BuildCategories(features, request, frame);
        var diagnostics = new List<CategoryCoverageDiagnostic>(categories.Count);
        var missing = new List<string>();

        foreach (var category in categories.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
        {
            var nearest = category.Value.Features
                .Select(feature => BuildNearestFeature(feature, frame, latitude, longitude))
                .OrderBy(feature => feature.DistanceMiles)
                .ThenBy(feature => feature.Label, StringComparer.OrdinalIgnoreCase)
                .Take(topPerCategory)
                .ToArray();
            var effectiveRadius = radiusMiles > 0d ? radiusMiles : category.Value.RadiusMiles;
            var hasMatch = nearest.Any(feature => feature.DistanceMiles <= effectiveRadius);
            diagnostics.Add(new CategoryCoverageDiagnostic
            {
                Category = category.Key,
                HasMatchWithinRadius = hasMatch,
                NearestLocations = nearest
            });

            if (!hasMatch)
            {
                missing.Add(category.Key);
            }
        }

        return new CoverageDiagnosticResult
        {
            Latitude = latitude,
            Longitude = longitude,
            RadiusMiles = radiusMiles,
            Categories = diagnostics,
            MissingCategories = missing
        };
    }

    public static double GetDistanceMiles(double lat1, double lon1, double lat2, double lon2)
    {
        var cosLatitude = Math.Cos(lat2 * DegreesToRadians);
        var latMiles = (lat1 - lat2) * 69d;
        var lonMiles = (lon1 - lon2) * 69d * cosLatitude;
        return Math.Sqrt((latMiles * latMiles) + (lonMiles * lonMiles));
    }

    private static IReadOnlyList<GeometryFeatureInput> MaterializeFeatures(GenerateKmlRequest request)
    {
        if (request.Features.Count > 0)
        {
            return request.Features;
        }

        return request.Locations
            .Select(location => new GeometryFeatureInput
            {
                Category = location.Category,
                Label = location.Label,
                GeometryType = "point",
                Points =
                [
                    new CoordinateInput { Latitude = location.Latitude, Longitude = location.Longitude }
                ]
            })
            .ToArray();
    }

    private void Validate(GenerateKmlRequest request)
    {
        if (request is null)
        {
            throw new KmlValidationException("The request body is required.");
        }

        if ((request.Locations is null || request.Locations.Count == 0) &&
            (request.Features is null || request.Features.Count == 0))
        {
            throw new KmlValidationException("At least one location or feature is required.");
        }

        if (request.Step <= 0d || request.RadiusMiles <= 0d)
        {
            throw new KmlValidationException("Step and RadiusMiles must be greater than zero.");
        }

        if (request.PaddingDegrees < 0d)
        {
            throw new KmlValidationException("PaddingDegrees cannot be negative.");
        }

        foreach (var location in request.Locations ?? Array.Empty<LocationInput>())
        {
            if (string.IsNullOrWhiteSpace(location.Category))
            {
                throw new KmlValidationException("Each location must include a category.");
            }

            ValidateCoordinate(location.Latitude, location.Longitude);
        }

        foreach (var feature in request.Features)
        {
            if (feature is null || string.IsNullOrWhiteSpace(feature.Category) || string.IsNullOrWhiteSpace(feature.Label))
            {
                throw new KmlValidationException("Each feature must include a category and label.");
            }
        }
    }

    private static void ValidateCoordinate(double latitude, double longitude)
    {
        if (latitude is < -90d or > 90d)
        {
            throw new KmlValidationException($"Latitude {latitude} is out of range.");
        }

        if (longitude is < -180d or > 180d)
        {
            throw new KmlValidationException($"Longitude {longitude} is out of range.");
        }
    }

    private static ReferenceFrame BuildFrame(IReadOnlyList<GeometryFeatureInput> features)
    {
        var coordinates = EnumerateCoordinates(features).ToArray();
        var referenceLatitude = (coordinates.Min(point => point.Latitude) + coordinates.Max(point => point.Latitude)) / 2d;
        var referenceLongitude = (coordinates.Min(point => point.Longitude) + coordinates.Max(point => point.Longitude)) / 2d;
        return new ReferenceFrame(referenceLatitude, referenceLongitude, FeetPerDegreeLatitude * Math.Cos(referenceLatitude * DegreesToRadians));
    }

    private static IEnumerable<CoordinateInput> EnumerateCoordinates(IReadOnlyList<GeometryFeatureInput> features)
    {
        foreach (var feature in features)
        {
            foreach (var point in feature.Points)
            {
                yield return point;
            }

            foreach (var line in feature.Lines)
            {
                foreach (var point in line.Coordinates)
                {
                    yield return point;
                }
            }

            foreach (var polygon in feature.Polygons)
            {
                foreach (var point in polygon.OuterRing)
                {
                    yield return point;
                }

                foreach (var ring in polygon.InnerRings)
                {
                    foreach (var point in ring)
                    {
                        yield return point;
                    }
                }
            }
        }
    }

    private static BoundingBox BuildBounds(IReadOnlyList<GeometryFeatureInput> features, GenerateKmlRequest request)
    {
        var coordinates = EnumerateCoordinates(features).ToArray();
        var maxRadius = request.CategoryRadiusMiles.Count > 0
            ? Math.Max(request.RadiusMiles, request.CategoryRadiusMiles.Max(entry => entry.Value))
            : request.RadiusMiles;
        var padding = request.PaddingDegrees + (maxRadius / 69d);

        return new BoundingBox
        {
            MinLatitude = coordinates.Min(point => point.Latitude) - padding,
            MaxLatitude = coordinates.Max(point => point.Latitude) + padding,
            MinLongitude = coordinates.Min(point => point.Longitude) - padding,
            MaxLongitude = coordinates.Max(point => point.Longitude) + padding
        };
    }

    private static Dictionary<string, CategoryFeatureSet> BuildCategories(
        IReadOnlyList<GeometryFeatureInput> features,
        GenerateKmlRequest request,
        ReferenceFrame frame)
    {
        return features
            .GroupBy(feature => feature.Category.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var radiusMiles = request.CategoryRadiusMiles.TryGetValue(group.Key, out var configuredRadius)
                        ? configuredRadius
                        : request.RadiusMiles;
                    return new CategoryFeatureSet(group.Key, radiusMiles, group.Select(feature => ProjectFeature(feature, frame, radiusMiles)).ToArray());
                },
                StringComparer.OrdinalIgnoreCase);
    }

    private static ProjectedFeature ProjectFeature(GeometryFeatureInput feature, ReferenceFrame frame, double radiusMiles)
    {
        var coordinates = EnumerateCoordinates([feature]).ToArray();
        var padding = radiusMiles / 69d;
        return new ProjectedFeature(
            feature.Label.Trim(),
            feature.GeometryType.Trim().ToLowerInvariant(),
            feature.Points.Select(point => Project(point, frame)).ToArray(),
            feature.Lines.Select(line => line.Coordinates.Select(point => Project(point, frame)).ToArray()).ToArray(),
            feature.Polygons.Select(polygon => new ProjectedPolygon(
                polygon.OuterRing.Select(point => Project(point, frame)).ToArray(),
                polygon.InnerRings.Select(ring => ring.Select(point => Project(point, frame)).ToArray()).ToArray())).ToArray(),
            new BoundingBox
            {
                MinLatitude = coordinates.Min(point => point.Latitude) - padding,
                MaxLatitude = coordinates.Max(point => point.Latitude) + padding,
                MinLongitude = coordinates.Min(point => point.Longitude) - padding,
                MaxLongitude = coordinates.Max(point => point.Longitude) + padding
            });
    }

    private static ProjectedCoordinate Project(CoordinateInput point, ReferenceFrame frame) =>
        new(point.Latitude, point.Longitude, (point.Longitude - frame.ReferenceLongitude) * frame.FeetPerDegreeLongitude, (point.Latitude - frame.ReferenceLatitude) * FeetPerDegreeLatitude);

    private static HashSet<GridCell> ScanCoveredCells(
        BoundingBox bounds,
        double step,
        IReadOnlyDictionary<string, CategoryFeatureSet> categories,
        ReferenceFrame frame)
    {
        var coveredCells = new ConcurrentBag<GridCell>();
        var totalLatSteps = (int)Math.Round((bounds.MaxLatitude - bounds.MinLatitude) / step);
        var totalLonSteps = (int)Math.Round((bounds.MaxLongitude - bounds.MinLongitude) / step);

        Parallel.For(
            0,
            totalLatSteps,
            () => new List<GridCell>(),
            (latIndex, _, localCells) =>
            {
                var latitude = bounds.MinLatitude + (latIndex * step) + (step / 2d);
                for (var lonIndex = 0; lonIndex < totalLonSteps; lonIndex++)
                {
                    var longitude = bounds.MinLongitude + (lonIndex * step) + (step / 2d);
                    if (IsCoveredByAllCategories(latitude, longitude, categories, frame))
                    {
                        localCells.Add(new GridCell(latIndex, lonIndex));
                    }
                }

                return localCells;
            },
            localCells =>
            {
                foreach (var cell in localCells)
                {
                    coveredCells.Add(cell);
                }
            });

        return coveredCells.ToHashSet();
    }

    private static bool IsCoveredByAllCategories(
        double latitude,
        double longitude,
        IReadOnlyDictionary<string, CategoryFeatureSet> categories,
        ReferenceFrame frame)
    {
        var probe = Project(new CoordinateInput { Latitude = latitude, Longitude = longitude }, frame);
        foreach (var category in categories.Values)
        {
            var radiusFeet = category.RadiusMiles * 5_280d;
            var hasCoverage = false;
            foreach (var feature in category.Features)
            {
                if (!Contains(feature.ExpandedBounds, latitude, longitude))
                {
                    continue;
                }

                if (DistanceToFeatureFeet(feature, probe) <= radiusFeet)
                {
                    hasCoverage = true;
                    break;
                }
            }

            if (!hasCoverage)
            {
                return false;
            }
        }

        return true;
    }

    private static bool Contains(BoundingBox bounds, double latitude, double longitude) =>
        latitude >= bounds.MinLatitude
        && latitude <= bounds.MaxLatitude
        && longitude >= bounds.MinLongitude
        && longitude <= bounds.MaxLongitude;

    private static double DistanceToFeatureFeet(ProjectedFeature feature, ProjectedCoordinate point) =>
        feature.GeometryType switch
        {
            "point" => feature.Points.Min(candidate => DistanceFeet(candidate, point)),
            "linestring" => feature.Lines.Min(line => DistanceToLineFeet(line, point)),
            "polygon" => feature.Polygons.Min(polygon => DistanceToPolygonFeet(polygon, point)),
            _ => double.MaxValue
        };

    private static double DistanceFeet(ProjectedCoordinate left, ProjectedCoordinate right)
    {
        var dx = left.X - right.X;
        var dy = left.Y - right.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static double DistanceToLineFeet(IReadOnlyList<ProjectedCoordinate> line, ProjectedCoordinate point)
    {
        if (line.Count == 0)
        {
            return double.MaxValue;
        }

        if (line.Count == 1)
        {
            return DistanceFeet(line[0], point);
        }

        var minDistance = double.MaxValue;
        for (var index = 1; index < line.Count; index++)
        {
            minDistance = Math.Min(minDistance, DistanceToSegmentFeet(line[index - 1], line[index], point));
        }

        return minDistance;
    }

    private static double DistanceToPolygonFeet(ProjectedPolygon polygon, ProjectedCoordinate point)
    {
        if (IsInsidePolygon(polygon, point))
        {
            return 0d;
        }

        var minDistance = DistanceToRingFeet(polygon.OuterRing, point);
        foreach (var hole in polygon.InnerRings)
        {
            minDistance = Math.Min(minDistance, DistanceToRingFeet(hole, point));
        }

        return minDistance;
    }

    private static bool IsInsidePolygon(ProjectedPolygon polygon, ProjectedCoordinate point)
    {
        if (!IsInsideRing(polygon.OuterRing, point))
        {
            return false;
        }

        foreach (var hole in polygon.InnerRings)
        {
            if (IsInsideRing(hole, point))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsInsideRing(IReadOnlyList<ProjectedCoordinate> ring, ProjectedCoordinate point)
    {
        var inside = false;
        var previous = ring.Count - 1;
        for (var index = 0; index < ring.Count; previous = index++)
        {
            var current = ring[index];
            var last = ring[previous];
            var crosses = ((current.Y > point.Y) != (last.Y > point.Y))
                          && (point.X < ((last.X - current.X) * (point.Y - current.Y) / ((last.Y - current.Y) + double.Epsilon)) + current.X);
            if (crosses)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static double DistanceToRingFeet(IReadOnlyList<ProjectedCoordinate> ring, ProjectedCoordinate point)
    {
        var minDistance = double.MaxValue;
        for (var index = 1; index < ring.Count; index++)
        {
            minDistance = Math.Min(minDistance, DistanceToSegmentFeet(ring[index - 1], ring[index], point));
        }

        if (ring.Count > 2)
        {
            minDistance = Math.Min(minDistance, DistanceToSegmentFeet(ring[^1], ring[0], point));
        }

        return minDistance;
    }

    private static double DistanceToSegmentFeet(ProjectedCoordinate start, ProjectedCoordinate end, ProjectedCoordinate point)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        if (dx == 0d && dy == 0d)
        {
            return DistanceFeet(start, point);
        }

        var projection = ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / ((dx * dx) + (dy * dy));
        projection = Math.Clamp(projection, 0d, 1d);
        var projectedX = start.X + (projection * dx);
        var projectedY = start.Y + (projection * dy);
        var diffX = point.X - projectedX;
        var diffY = point.Y - projectedY;
        return Math.Sqrt((diffX * diffX) + (diffY * diffY));
    }

    private static IReadOnlyList<OutputRectangle> BuildRectangles(HashSet<GridCell> cells)
    {
        if (cells.Count == 0)
        {
            return Array.Empty<OutputRectangle>();
        }

        var runsByRow = cells
            .GroupBy(cell => cell.Row)
            .OrderBy(group => group.Key)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var columns = group.Select(cell => cell.Column).OrderBy(column => column).ToArray();
                    var runs = new List<(int Start, int End)>();
                    var start = columns[0];
                    var end = columns[0];
                    for (var index = 1; index < columns.Length; index++)
                    {
                        if (columns[index] == end + 1)
                        {
                            end = columns[index];
                            continue;
                        }

                        runs.Add((start, end));
                        start = columns[index];
                        end = columns[index];
                    }

                    runs.Add((start, end));
                    return runs;
                });

        var rectangles = new List<OutputRectangle>();
        var active = new Dictionary<(int Start, int End), OutputRectangle>();
        foreach (var row in runsByRow.Keys.OrderBy(row => row))
        {
            var currentRuns = runsByRow[row].ToHashSet();
            var next = new Dictionary<(int Start, int End), OutputRectangle>();
            foreach (var run in currentRuns)
            {
                if (active.TryGetValue(run, out var rectangle) && rectangle.EndRow == row - 1)
                {
                    next[run] = rectangle with { EndRow = row };
                }
                else
                {
                    next[run] = new OutputRectangle(row, row, run.Start, run.End);
                }
            }

            foreach (var entry in active)
            {
                if (!currentRuns.Contains(entry.Key))
                {
                    rectangles.Add(entry.Value);
                }
            }

            active = next;
        }

        rectangles.AddRange(active.Values);
        return rectangles;
    }

    private static CoverageDiagnosticLocation BuildNearestFeature(ProjectedFeature feature, ReferenceFrame frame, double latitude, double longitude)
    {
        var probe = Project(new CoordinateInput { Latitude = latitude, Longitude = longitude }, frame);
        return new CoverageDiagnosticLocation
        {
            Label = feature.Label,
            Latitude = feature.Representative.Latitude,
            Longitude = feature.Representative.Longitude,
            DistanceMiles = DistanceToFeatureFeet(feature, probe) / 5_280d
        };
    }

    private string BuildKml(
        IReadOnlyList<PolygonInput> polygons,
        IReadOnlyList<GeometryFeatureInput> features)
    {
        return NativeGeometryLibrary.BuildKmlDocument(new NativeKmlDocumentPayload
        {
            IntersectionPolygons = polygons,
            SourceFeatures = features
        });
    }

    private sealed record ReferenceFrame(double ReferenceLatitude, double ReferenceLongitude, double FeetPerDegreeLongitude);
    private sealed record ProjectedCoordinate(double Latitude, double Longitude, double X, double Y);
    private sealed record ProjectedPolygon(IReadOnlyList<ProjectedCoordinate> OuterRing, IReadOnlyList<IReadOnlyList<ProjectedCoordinate>> InnerRings);
    private sealed record ProjectedFeature(
        string Label,
        string GeometryType,
        IReadOnlyList<ProjectedCoordinate> Points,
        IReadOnlyList<IReadOnlyList<ProjectedCoordinate>> Lines,
        IReadOnlyList<ProjectedPolygon> Polygons,
        BoundingBox ExpandedBounds)
    {
        public CoordinateInput Representative =>
            Points.Count > 0
                ? new CoordinateInput { Latitude = Points[0].Latitude, Longitude = Points[0].Longitude }
                : Lines.Count > 0 && Lines[0].Count > 0
                    ? new CoordinateInput { Latitude = Lines[0][0].Latitude, Longitude = Lines[0][0].Longitude }
                    : new CoordinateInput { Latitude = Polygons[0].OuterRing[0].Latitude, Longitude = Polygons[0].OuterRing[0].Longitude };
    }
    private sealed record CategoryFeatureSet(string Category, double RadiusMiles, IReadOnlyList<ProjectedFeature> Features);
    private readonly record struct GridCell(int Row, int Column);
    private readonly record struct OutputRectangle(int StartRow, int EndRow, int StartColumn, int EndColumn);
}
