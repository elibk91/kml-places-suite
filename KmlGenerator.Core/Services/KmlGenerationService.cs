using System.Globalization;
using System.Security;
using System.Text;
using System.Collections.Concurrent;
using KmlGenerator.Core.Exceptions;
using KmlGenerator.Core.Models;
using KmlSuite.Shared.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KmlGenerator.Core.Services;

/// <summary>
/// Shared orchestration service for the KML pipeline.
/// Hosts should call this service rather than duplicating any scan logic.
/// </summary>
public sealed class KmlGenerationService : IKmlGenerationService
{
    private const double DegreesToRadians = Math.PI / 180d;
    private readonly ILogger<KmlGenerationService> _logger;

    public KmlGenerationService(ILogger<KmlGenerationService>? logger = null)
    {
        _logger = logger ?? NullLogger<KmlGenerationService>.Instance;
    }

    public GenerateKmlResult Generate(GenerateKmlRequest request)
    {
        using var _ = MethodTrace.Enter(_logger, nameof(KmlGenerationService));
        Validate(request);

        var bounds = BuildBounds(request);
        var locationsByCategory = request.Locations
            .GroupBy(location => location.Category.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var categoryIndexes = BuildCategoryIndexes(locationsByCategory, request.Step, request.RadiusMiles);

        var validPoints = ScanValidPoints(bounds, request.Step, request.RadiusMiles, categoryIndexes);
        var shapeRegions = BuildShapeRegions(bounds, request.Step, request.RadiusMiles, validPoints, categoryIndexes);
        var boundaryPointCount = shapeRegions.Sum(region => region.BoundaryPoints.Count);
        var kml = BuildKml(shapeRegions, bounds, request.Step);
        _logger.LogInformation(
            "Generated KML with {CategoryCount} categories, {ValidPointCount} valid points, {ShapeCount} shapes, and {BoundaryPointCount} boundary points",
            categoryIndexes.Count,
            validPoints.Count,
            shapeRegions.Count,
            boundaryPointCount);

        return new GenerateKmlResult
        {
            Kml = kml,
            BoundaryPointCount = boundaryPointCount,
            ValidPointCount = validPoints.Count,
            Bounds = bounds
        };
    }

    public static double GetDistanceMiles(double lat1, double lon1, double lat2, double lon2)
    {
        // This mirrors the original Python script's approximation so the port stays behaviorally consistent.
        var cosLatitude = Math.Cos(lat2 * DegreesToRadians);
        var latMiles = (lat1 - lat2) * 69d;
        var lonMiles = (lon1 - lon2) * 69d * cosLatitude;
        return Math.Sqrt((latMiles * latMiles) + (lonMiles * lonMiles));
    }

    private void Validate(GenerateKmlRequest request)
    {
        using var _ = MethodTrace.Enter(_logger, nameof(KmlGenerationService));
        if (request is null)
        {
            throw new KmlValidationException("The request body is required.");
        }

        if (request.Locations is null || request.Locations.Count == 0)
        {
            throw new KmlValidationException("At least one location is required.");
        }

        if (request.Step <= 0d)
        {
            throw new KmlValidationException("Step must be greater than zero.");
        }

        if (request.RadiusMiles <= 0d)
        {
            throw new KmlValidationException("RadiusMiles must be greater than zero.");
        }

        if (request.PaddingDegrees < 0d)
        {
            throw new KmlValidationException("PaddingDegrees cannot be negative.");
        }

        foreach (var location in request.Locations)
        {
            if (location is null)
            {
                throw new KmlValidationException("Locations cannot contain null items.");
            }

            if (string.IsNullOrWhiteSpace(location.Category))
            {
                throw new KmlValidationException("Each location must include a category.");
            }

            if (location.Latitude is < -90d or > 90d)
            {
                throw new KmlValidationException($"Latitude {location.Latitude} is out of range.");
            }

            if (location.Longitude is < -180d or > 180d)
            {
                throw new KmlValidationException($"Longitude {location.Longitude} is out of range.");
            }
        }
    }

    private BoundingBox BuildBounds(GenerateKmlRequest request)
    {
        using var _ = MethodTrace.Enter(_logger, nameof(KmlGenerationService));
        var minLat = request.Locations.Min(location => location.Latitude) - request.PaddingDegrees;
        var maxLat = request.Locations.Max(location => location.Latitude) + request.PaddingDegrees;
        var minLon = request.Locations.Min(location => location.Longitude) - request.PaddingDegrees;
        var maxLon = request.Locations.Max(location => location.Longitude) + request.PaddingDegrees;

        var bounds = new BoundingBox
        {
            MinLatitude = minLat,
            MaxLatitude = maxLat,
            MinLongitude = minLon,
            MaxLongitude = maxLon
        };

        _logger.LogDebug("Computed bounds {@Bounds}", bounds);
        return bounds;
    }

    private HashSet<GridPoint> ScanValidPoints(
        BoundingBox bounds,
        double step,
        double radiusMiles,
        IReadOnlyList<CategoryIndex> categoryIndexes)
    {
        using var _ = MethodTrace.Enter(_logger, nameof(KmlGenerationService));
        var validPoints = new ConcurrentBag<GridPoint>();
        var totalLatSteps = (int)Math.Round((bounds.MaxLatitude - bounds.MinLatitude) / step) + 1;
        var totalLonSteps = (int)Math.Round((bounds.MaxLongitude - bounds.MinLongitude) / step) + 1;

        // The scan walks a dense grid because the algorithm is looking for the region where every category overlaps.
        Parallel.For(
            0,
            totalLatSteps,
            () => new List<GridPoint>(),
            (latIndex, _, localMatches) =>
            {
                var latitude = bounds.MinLatitude + (latIndex * step);

                for (var lonIndex = 0; lonIndex < totalLonSteps; lonIndex++)
                {
                    var longitude = bounds.MinLongitude + (lonIndex * step);
                    var matchesAllCategories = true;

                    foreach (var categoryIndex in categoryIndexes)
                    {
                        if (!HasCategoryMatch(categoryIndex, latitude, longitude, radiusMiles))
                        {
                            matchesAllCategories = false;
                            break;
                        }
                    }

                    if (matchesAllCategories)
                    {
                        localMatches.Add(new GridPoint(latIndex, lonIndex));
                    }
                }

                return localMatches;
            },
            localMatches =>
            {
                foreach (var match in localMatches)
                {
                    validPoints.Add(match);
                }
            });

        var result = validPoints.ToHashSet();
        _logger.LogDebug("Scanned grid produced {ValidPointCount} valid points", result.Count);
        return result;
    }

    private static List<GridPoint> ExtractBoundaryPoints(HashSet<GridPoint> validPoints)
    {
        var boundaryPoints = new List<GridPoint>();

        // A point is part of the outline when any direct neighbor falls outside the valid region.
        foreach (var point in validPoints)
        {
            if (!validPoints.Contains(new GridPoint(point.LatitudeIndex + 1, point.LongitudeIndex)) ||
                !validPoints.Contains(new GridPoint(point.LatitudeIndex - 1, point.LongitudeIndex)) ||
                !validPoints.Contains(new GridPoint(point.LatitudeIndex, point.LongitudeIndex + 1)) ||
                !validPoints.Contains(new GridPoint(point.LatitudeIndex, point.LongitudeIndex - 1)))
            {
                boundaryPoints.Add(point);
            }
        }

        boundaryPoints.Sort(static (left, right) =>
        {
            var latComparison = left.LatitudeIndex.CompareTo(right.LatitudeIndex);
            return latComparison != 0 ? latComparison : left.LongitudeIndex.CompareTo(right.LongitudeIndex);
        });

        return boundaryPoints;
    }

    private IReadOnlyList<ShapeRegion> BuildShapeRegions(
        BoundingBox bounds,
        double step,
        double radiusMiles,
        HashSet<GridPoint> validPoints,
        IReadOnlyList<CategoryIndex> categoryIndexes)
    {
        using var _ = MethodTrace.Enter(_logger, nameof(KmlGenerationService));
        var components = ExtractConnectedComponents(validPoints);
        var shapeRegions = new List<ShapeRegion>(components.Count);

        foreach (var component in components)
        {
            var componentSet = component.ToHashSet();
            var boundaryPoints = ExtractBoundaryPoints(componentSet);
            var loops = BuildBoundaryLoops(bounds, step, componentSet);
            var topMatches = CollectTopRelevantLocations(bounds, step, radiusMiles, boundaryPoints, categoryIndexes);

            shapeRegions.Add(new ShapeRegion(loops, boundaryPoints, topMatches));
        }

        _logger.LogDebug("Built {ShapeRegionCount} shape regions from {ComponentCount} connected components", shapeRegions.Count, components.Count);
        return shapeRegions;
    }

    private static IReadOnlyList<List<GridPoint>> ExtractConnectedComponents(HashSet<GridPoint> validPoints)
    {
        var remaining = new HashSet<GridPoint>(validPoints);
        var components = new List<List<GridPoint>>();

        while (remaining.Count > 0)
        {
            var start = remaining.First();
            var queue = new Queue<GridPoint>();
            var component = new List<GridPoint>();
            queue.Enqueue(start);
            remaining.Remove(start);

            while (queue.Count > 0)
            {
                var point = queue.Dequeue();
                component.Add(point);

                foreach (var neighbor in EnumerateNeighbors(point))
                {
                    if (remaining.Remove(neighbor))
                    {
                        queue.Enqueue(neighbor);
                    }
                }
            }

            components.Add(component);
        }

        return components;
    }

    private static IEnumerable<GridPoint> EnumerateNeighbors(GridPoint point)
    {
        yield return new GridPoint(point.LatitudeIndex + 1, point.LongitudeIndex);
        yield return new GridPoint(point.LatitudeIndex - 1, point.LongitudeIndex);
        yield return new GridPoint(point.LatitudeIndex, point.LongitudeIndex + 1);
        yield return new GridPoint(point.LatitudeIndex, point.LongitudeIndex - 1);
    }

    private IReadOnlyList<CategoryIndex> BuildCategoryIndexes(
        IReadOnlyDictionary<string, LocationInput[]> locationsByCategory,
        double step,
        double radiusMiles)
    {
        using var _ = MethodTrace.Enter(_logger, nameof(KmlGenerationService));
        var latitudeRadiusDegrees = radiusMiles / 69d;
        var cellSizeDegrees = Math.Max(step * 4d, latitudeRadiusDegrees);
        var indexes = new List<CategoryIndex>(locationsByCategory.Count);

        foreach (var entry in locationsByCategory)
        {
            var bins = entry.Value
                .GroupBy(location => new BinKey(GetBinIndex(location.Latitude, cellSizeDegrees), GetBinIndex(location.Longitude, cellSizeDegrees)))
                .ToDictionary(group => group.Key, group => group.ToArray());

            indexes.Add(new CategoryIndex(entry.Key, bins, cellSizeDegrees, latitudeRadiusDegrees));
        }

        _logger.LogDebug("Built {IndexCount} category indexes", indexes.Count);
        return indexes;
    }

    private static bool HasCategoryMatch(CategoryIndex categoryIndex, double latitude, double longitude, double radiusMiles)
    {
        return FindCategoryMatches(categoryIndex, latitude, longitude, radiusMiles).Any();
    }

    private static int GetBinIndex(double value, double cellSizeDegrees) =>
        (int)Math.Floor(value / cellSizeDegrees);

    private IReadOnlyDictionary<string, IReadOnlyList<ScoredLocation>> CollectTopRelevantLocations(
        BoundingBox bounds,
        double step,
        double radiusMiles,
        IReadOnlyCollection<GridPoint> boundaryPoints,
        IReadOnlyList<CategoryIndex> categoryIndexes)
    {
        using var _ = MethodTrace.Enter(_logger, nameof(KmlGenerationService));
        var relevantByCategory = categoryIndexes.ToDictionary(
            categoryIndex => categoryIndex.Category,
            _ => new Dictionary<LocationInput, int>(LocationInputComparer.Instance),
            StringComparer.OrdinalIgnoreCase);

        foreach (var boundaryPoint in boundaryPoints)
        {
            var longitude = bounds.MinLongitude + (boundaryPoint.LongitudeIndex * step);
            var latitude = bounds.MinLatitude + (boundaryPoint.LatitudeIndex * step);

            foreach (var categoryIndex in categoryIndexes)
            {
                foreach (var location in FindCategoryMatches(categoryIndex, latitude, longitude, radiusMiles))
                {
                    var counts = relevantByCategory[categoryIndex.Category];
                    counts[location] = counts.TryGetValue(location, out var count) ? count + 1 : 1;
                }
            }
        }

        var result = relevantByCategory.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlyList<ScoredLocation>)entry.Value
                .OrderByDescending(static pair => pair.Value)
                .ThenBy(static pair => pair.Key.Latitude)
                .ThenBy(static pair => pair.Key.Longitude)
                .Take(3)
                .Select(static pair => new ScoredLocation(pair.Key, pair.Value))
                .ToArray(),
            StringComparer.OrdinalIgnoreCase);
        _logger.LogDebug("Collected top relevant locations for {CategoryCount} categories", result.Count);
        return result;
    }

    private static IEnumerable<LocationInput> FindCategoryMatches(CategoryIndex categoryIndex, double latitude, double longitude, double radiusMiles)
    {
        var latBin = GetBinIndex(latitude, categoryIndex.CellSizeDegrees);
        var lonBin = GetBinIndex(longitude, categoryIndex.CellSizeDegrees);
        var latitudeNeighbors = Math.Max(1, (int)Math.Ceiling(categoryIndex.LatitudeRadiusDegrees / categoryIndex.CellSizeDegrees));

        var cosLatitude = Math.Max(0.01d, Math.Cos(latitude * DegreesToRadians));
        var longitudeRadiusDegrees = radiusMiles / (69d * cosLatitude);
        var longitudeNeighbors = Math.Max(1, (int)Math.Ceiling(longitudeRadiusDegrees / categoryIndex.CellSizeDegrees));

        for (var latOffset = -latitudeNeighbors; latOffset <= latitudeNeighbors; latOffset++)
        {
            for (var lonOffset = -longitudeNeighbors; lonOffset <= longitudeNeighbors; lonOffset++)
            {
                if (!categoryIndex.Bins.TryGetValue(new BinKey(latBin + latOffset, lonBin + lonOffset), out var locations))
                {
                    continue;
                }

                foreach (var location in locations)
                {
                    if (GetDistanceMiles(latitude, longitude, location.Latitude, location.Longitude) <= radiusMiles)
                    {
                        yield return location;
                    }
                }
            }
        }
    }

    private List<List<GeoCoordinate>> BuildBoundaryLoops(BoundingBox bounds, double step, HashSet<GridPoint> validPoints)
    {
        using var _ = MethodTrace.Enter(_logger, nameof(KmlGenerationService));
        var adjacency = new Dictionary<VertexKey, List<int>>();
        var edges = new List<BoundaryEdge>();

        foreach (var point in validPoints)
        {
            var left = (point.LongitudeIndex * 2) - 1;
            var right = (point.LongitudeIndex * 2) + 1;
            var bottom = (point.LatitudeIndex * 2) - 1;
            var top = (point.LatitudeIndex * 2) + 1;

            if (!validPoints.Contains(new GridPoint(point.LatitudeIndex + 1, point.LongitudeIndex)))
            {
                AddBoundaryEdge(adjacency, edges, new VertexKey(top, left), new VertexKey(top, right));
            }

            if (!validPoints.Contains(new GridPoint(point.LatitudeIndex - 1, point.LongitudeIndex)))
            {
                AddBoundaryEdge(adjacency, edges, new VertexKey(bottom, right), new VertexKey(bottom, left));
            }

            if (!validPoints.Contains(new GridPoint(point.LatitudeIndex, point.LongitudeIndex + 1)))
            {
                AddBoundaryEdge(adjacency, edges, new VertexKey(top, right), new VertexKey(bottom, right));
            }

            if (!validPoints.Contains(new GridPoint(point.LatitudeIndex, point.LongitudeIndex - 1)))
            {
                AddBoundaryEdge(adjacency, edges, new VertexKey(bottom, left), new VertexKey(top, left));
            }
        }

        var usedEdges = new bool[edges.Count];
        var loops = new List<List<GeoCoordinate>>();

        for (var edgeIndex = 0; edgeIndex < edges.Count; edgeIndex++)
        {
            if (usedEdges[edgeIndex])
            {
                continue;
            }

            var firstEdge = edges[edgeIndex];
            var loop = new List<VertexKey> { firstEdge.Start };
            usedEdges[edgeIndex] = true;
            var previous = firstEdge.Start;
            var current = firstEdge.End;
            loop.Add(current);

            while (!current.Equals(firstEdge.Start))
            {
                if (!adjacency.TryGetValue(current, out var candidates))
                {
                    break;
                }

                var nextEdgeIndex = SelectNextEdge(previous, current, candidates, edges, usedEdges);
                if (nextEdgeIndex < 0)
                {
                    break;
                }

                usedEdges[nextEdgeIndex] = true;
                previous = current;
                current = edges[nextEdgeIndex].End;
                loop.Add(current);
            }

            if (loop.Count >= 4 && loop[^1].Equals(loop[0]))
            {
                loops.Add(SimplifyLoop(
                    loop
                        .Select(vertex => ToGeoCoordinate(vertex, bounds, step))
                        .ToArray()));
            }
        }

        loops = loops
            .Where(static loop => loop.Count >= 4)
            .OrderByDescending(ComputePolygonArea)
            .ToList();

        _logger.LogDebug("Built {LoopCount} boundary loops", loops.Count);
        return loops;
    }

    private static List<GeoCoordinate> SimplifyLoop(IReadOnlyList<GeoCoordinate> loop)
    {
        if (loop.Count < 4)
        {
            return loop.ToList();
        }

        var simplified = new List<GeoCoordinate> { loop[0] };

        for (var index = 1; index < loop.Count - 1; index++)
        {
            var previous = simplified[^1];
            var current = loop[index];
            var next = loop[index + 1];

            if (IsCollinear(previous, current, next))
            {
                continue;
            }

            simplified.Add(current);
        }

        simplified.Add(simplified[0]);
        return simplified;
    }

    private static bool IsCollinear(GeoCoordinate previous, GeoCoordinate current, GeoCoordinate next)
    {
        const double epsilon = 1e-12;
        var crossProduct =
            ((current.Longitude - previous.Longitude) * (next.Latitude - current.Latitude)) -
            ((current.Latitude - previous.Latitude) * (next.Longitude - current.Longitude));

        return Math.Abs(crossProduct) <= epsilon;
    }

    private static int SelectNextEdge(
        VertexKey previous,
        VertexKey current,
        IReadOnlyList<int> candidateIndexes,
        IReadOnlyList<BoundaryEdge> edges,
        IReadOnlyList<bool> usedEdges)
    {
        var incomingDirection = GetDirection(previous, current);
        var bestEdgeIndex = -1;
        var bestTurn = int.MaxValue;

        foreach (var candidateIndex in candidateIndexes)
        {
            if (usedEdges[candidateIndex])
            {
                continue;
            }

            var candidateDirection = GetDirection(current, edges[candidateIndex].End);
            var turn = ((candidateDirection - incomingDirection) + 4) % 4;
            if (turn == 2)
            {
                continue;
            }

            if (turn < bestTurn)
            {
                bestTurn = turn;
                bestEdgeIndex = candidateIndex;
            }
        }

        return bestEdgeIndex;
    }

    private static int GetDirection(VertexKey start, VertexKey end)
    {
        if (end.LatitudeUnit == start.LatitudeUnit)
        {
            return end.LongitudeUnit > start.LongitudeUnit ? 0 : 2;
        }

        return end.LatitudeUnit < start.LatitudeUnit ? 1 : 3;
    }

    private static GeoCoordinate ToGeoCoordinate(VertexKey vertex, BoundingBox bounds, double step) =>
        new(
            bounds.MinLatitude + ((vertex.LatitudeUnit * step) / 2d),
            bounds.MinLongitude + ((vertex.LongitudeUnit * step) / 2d));

    private static double ComputePolygonArea(IReadOnlyList<GeoCoordinate> loop)
    {
        double area = 0d;

        for (var index = 0; index < loop.Count - 1; index++)
        {
            var current = loop[index];
            var next = loop[index + 1];
            area += (current.Longitude * next.Latitude) - (next.Longitude * current.Latitude);
        }

        return Math.Abs(area) / 2d;
    }

    private static void AddBoundaryEdge(
        IDictionary<VertexKey, List<int>> adjacency,
        IList<BoundaryEdge> edges,
        VertexKey start,
        VertexKey end)
    {
        var edgeIndex = edges.Count;
        edges.Add(new BoundaryEdge(start, end));

        if (!adjacency.TryGetValue(start, out var outgoing))
        {
            outgoing = new List<int>();
            adjacency[start] = outgoing;
        }

        outgoing.Add(edgeIndex);
    }

    private string BuildKml(IReadOnlyList<ShapeRegion> shapeRegions, BoundingBox bounds, double step)
    {
        using var _ = MethodTrace.Enter(_logger, nameof(KmlGenerationService));
        var builder = new StringBuilder();
        builder.AppendLine("""<?xml version="1.0" encoding="UTF-8"?>""");
        builder.AppendLine("""<kml xmlns="http://www.opengis.net/kml/2.2">""");
        builder.AppendLine("<Document>");
        builder.AppendLine("""  <Style id="boundary-point"><IconStyle><scale>0.45</scale><Icon><href>http://maps.google.com/mapfiles/kml/shapes/placemark_circle.png</href></Icon><color>ff0000ff</color></IconStyle></Style>""");
        builder.AppendLine("""  <Style id="boundary-polygon"><LineStyle><color>ff0000ff</color><width>2</width></LineStyle><PolyStyle><color>330000ff</color></PolyStyle></Style>""");

        var categories = shapeRegions
            .SelectMany(static region => region.TopMatchesByCategory.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static category => category, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var category in categories)
        {
            var styleId = BuildCategoryStyleId(category);
            var iconHref = GetCategoryIconHref(category);
            builder.Append("  <Style id=\"");
            builder.Append(styleId);
            builder.Append("\"><IconStyle><scale>0.9</scale><Icon><href>");
            builder.Append(iconHref);
            builder.AppendLine("</href></Icon></IconStyle></Style>");
        }

        for (var shapeIndex = 0; shapeIndex < shapeRegions.Count; shapeIndex++)
        {
            var shapeRegion = shapeRegions[shapeIndex];
            builder.AppendLine("  <Folder>");
            builder.Append("    <name>Shape ");
            builder.Append(shapeIndex + 1);
            builder.AppendLine("</name>");

            builder.AppendLine("    <Folder>");
            builder.AppendLine("      <name>Overlap Boundary</name>");

            if (shapeRegion.BoundaryLoops.Count > 0)
            {
                builder.AppendLine("      <Placemark>");
                builder.AppendLine("        <styleUrl>#boundary-polygon</styleUrl>");
                builder.AppendLine("        <Polygon>");
                builder.AppendLine("          <outerBoundaryIs>");
                builder.AppendLine("            <LinearRing>");
                builder.AppendLine("              <coordinates>");

                foreach (var coordinate in shapeRegion.BoundaryLoops[0])
                {
                    builder.Append("                ");
                    builder.Append(coordinate.Longitude.ToString("G17", CultureInfo.InvariantCulture));
                    builder.Append(',');
                    builder.Append(coordinate.Latitude.ToString("G17", CultureInfo.InvariantCulture));
                    builder.AppendLine(",0");
                }

                builder.AppendLine("              </coordinates>");
                builder.AppendLine("            </LinearRing>");
                builder.AppendLine("          </outerBoundaryIs>");

                for (var holeIndex = 1; holeIndex < shapeRegion.BoundaryLoops.Count; holeIndex++)
                {
                    builder.AppendLine("          <innerBoundaryIs>");
                    builder.AppendLine("            <LinearRing>");
                    builder.AppendLine("              <coordinates>");

                    foreach (var coordinate in shapeRegion.BoundaryLoops[holeIndex])
                    {
                        builder.Append("                ");
                        builder.Append(coordinate.Longitude.ToString("G17", CultureInfo.InvariantCulture));
                        builder.Append(',');
                        builder.Append(coordinate.Latitude.ToString("G17", CultureInfo.InvariantCulture));
                        builder.AppendLine(",0");
                    }

                    builder.AppendLine("              </coordinates>");
                    builder.AppendLine("            </LinearRing>");
                    builder.AppendLine("          </innerBoundaryIs>");
                }

                builder.AppendLine("        </Polygon>");
                builder.AppendLine("      </Placemark>");
            }
            else
            {
                foreach (var boundaryPoint in shapeRegion.BoundaryPoints)
                {
                    builder.AppendLine("      <Placemark>");
                    builder.AppendLine("        <styleUrl>#boundary-point</styleUrl>");
                    builder.AppendLine("        <Point>");
                    builder.Append("          <coordinates>");
                    builder.Append((bounds.MinLongitude + (boundaryPoint.LongitudeIndex * step)).ToString("G17", CultureInfo.InvariantCulture));
                    builder.Append(',');
                    builder.Append((bounds.MinLatitude + (boundaryPoint.LatitudeIndex * step)).ToString("G17", CultureInfo.InvariantCulture));
                    builder.AppendLine(",0</coordinates>");
                    builder.AppendLine("        </Point>");
                    builder.AppendLine("      </Placemark>");
                }
            }

            builder.AppendLine("    </Folder>");

            builder.AppendLine("    <Folder>");
            builder.AppendLine("      <name>Category Points</name>");

            foreach (var entry in shapeRegion.TopMatchesByCategory.OrderBy(static entry => entry.Key, StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine("      <Folder>");
                builder.Append("        <name>");
                builder.Append(SecurityElement.Escape(entry.Key));
                builder.AppendLine("</name>");

                var styleId = BuildCategoryStyleId(entry.Key);
                foreach (var scoredLocation in entry.Value)
                {
                    builder.Append("        <Placemark><name>");
                    builder.Append(SecurityElement.Escape(BuildLocationLabel(entry.Key, scoredLocation)));
                    builder.AppendLine("</name>");
                    builder.Append("          <styleUrl>#");
                    builder.Append(styleId);
                    builder.AppendLine("</styleUrl>");
                    builder.Append("          <Point><coordinates>");
                    builder.Append(scoredLocation.Location.Longitude.ToString("G17", CultureInfo.InvariantCulture));
                    builder.Append(',');
                    builder.Append(scoredLocation.Location.Latitude.ToString("G17", CultureInfo.InvariantCulture));
                    builder.AppendLine(",0</coordinates></Point>");
                    builder.AppendLine("        </Placemark>");
                }

                builder.AppendLine("      </Folder>");
            }

            builder.AppendLine("    </Folder>");
            builder.AppendLine("  </Folder>");
        }
        builder.AppendLine("</Document>");
        builder.AppendLine("</kml>");
        var kml = builder.ToString();
        _logger.LogDebug("Built KML document with length {KmlLength}", kml.Length);
        return kml;
    }

    private static string BuildCategoryStyleId(string category) =>
        $"category-{new string(category.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant()}";

    private static string GetCategoryIconHref(string category) =>
        category.Trim().ToLowerInvariant() switch
        {
            "gym" => "http://maps.google.com/mapfiles/kml/paddle/red-circle.png",
            "grocery" => "http://maps.google.com/mapfiles/kml/paddle/grn-circle.png",
            "marta" => "http://maps.google.com/mapfiles/kml/paddle/blu-circle.png",
            "park" => "http://maps.google.com/mapfiles/kml/paddle/ltblu-circle.png",
            "trail" => "http://maps.google.com/mapfiles/kml/paddle/ylw-circle.png",
            _ => "http://maps.google.com/mapfiles/kml/shapes/placemark_circle.png"
        };

    private static string BuildLocationLabel(string category, ScoredLocation scoredLocation)
    {
        var label = string.IsNullOrWhiteSpace(scoredLocation.Location.Label)
            ? category
            : scoredLocation.Location.Label;

        return $"{label} ({scoredLocation.Count})";
    }

    private readonly record struct GridPoint(int LatitudeIndex, int LongitudeIndex);

    private readonly record struct BinKey(int LatitudeBin, int LongitudeBin);

    private readonly record struct GeoCoordinate(double Latitude, double Longitude);

    private readonly record struct VertexKey(int LatitudeUnit, int LongitudeUnit);

    private readonly record struct BoundaryEdge(VertexKey Start, VertexKey End);

    private sealed record CategoryIndex(
        string Category,
        IReadOnlyDictionary<BinKey, LocationInput[]> Bins,
        double CellSizeDegrees,
        double LatitudeRadiusDegrees);

    private sealed record ShapeRegion(
        IReadOnlyList<List<GeoCoordinate>> BoundaryLoops,
        IReadOnlyList<GridPoint> BoundaryPoints,
        IReadOnlyDictionary<string, IReadOnlyList<ScoredLocation>> TopMatchesByCategory);

    private sealed record ScoredLocation(LocationInput Location, int Count);

    private sealed class LocationInputComparer : IEqualityComparer<LocationInput>
    {
        public static LocationInputComparer Instance { get; } = new();

        public bool Equals(LocationInput? x, LocationInput? y)
        {
            if (ReferenceEquals(x, y))
            {
                return true;
            }

            if (x is null || y is null)
            {
                return false;
            }

            return x.Category.Equals(y.Category, StringComparison.OrdinalIgnoreCase)
                   && x.Latitude.Equals(y.Latitude)
                   && x.Longitude.Equals(y.Longitude)
                   && string.Equals(x.Label, y.Label, StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode(LocationInput obj) =>
            HashCode.Combine(obj.Category.ToUpperInvariant(), obj.Latitude, obj.Longitude, obj.Label?.ToUpperInvariant());
    }
}
