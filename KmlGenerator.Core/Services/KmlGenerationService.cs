using System.Globalization;
using System.Security;
using System.Text;
using System.Collections.Concurrent;
using KmlGenerator.Core.Exceptions;
using KmlGenerator.Core.Models;

namespace KmlGenerator.Core.Services;

/// <summary>
/// Shared orchestration service for the KML pipeline.
/// Hosts should call this service rather than duplicating any scan logic.
/// </summary>
public sealed class KmlGenerationService : IKmlGenerationService
{
    private const double DegreesToRadians = Math.PI / 180d;

    public GenerateKmlResult Generate(GenerateKmlRequest request)
    {
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

    private static void Validate(GenerateKmlRequest request)
    {
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

    private static BoundingBox BuildBounds(GenerateKmlRequest request)
    {
        var minLat = request.Locations.Min(location => location.Latitude) - request.PaddingDegrees;
        var maxLat = request.Locations.Max(location => location.Latitude) + request.PaddingDegrees;
        var minLon = request.Locations.Min(location => location.Longitude) - request.PaddingDegrees;
        var maxLon = request.Locations.Max(location => location.Longitude) + request.PaddingDegrees;

        return new BoundingBox
        {
            MinLatitude = minLat,
            MaxLatitude = maxLat,
            MinLongitude = minLon,
            MaxLongitude = maxLon
        };
    }

    private static HashSet<GridPoint> ScanValidPoints(
        BoundingBox bounds,
        double step,
        double radiusMiles,
        IReadOnlyList<CategoryIndex> categoryIndexes)
    {
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

        return validPoints.ToHashSet();
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

    private static IReadOnlyList<ShapeRegion> BuildShapeRegions(
        BoundingBox bounds,
        double step,
        double radiusMiles,
        HashSet<GridPoint> validPoints,
        IReadOnlyList<CategoryIndex> categoryIndexes)
    {
        var components = ExtractConnectedComponents(validPoints);
        var shapeRegions = new List<ShapeRegion>(components.Count);

        foreach (var component in components)
        {
            var componentSet = component.ToHashSet();
            var boundaryPoints = ExtractBoundaryPoints(componentSet);
            var topMatches = CollectTopRelevantLocations(bounds, step, radiusMiles, boundaryPoints, categoryIndexes);

            shapeRegions.Add(new ShapeRegion(boundaryPoints, topMatches));
        }

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

    private static IReadOnlyList<CategoryIndex> BuildCategoryIndexes(
        IReadOnlyDictionary<string, LocationInput[]> locationsByCategory,
        double step,
        double radiusMiles)
    {
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

        return indexes;
    }

    private static bool HasCategoryMatch(CategoryIndex categoryIndex, double latitude, double longitude, double radiusMiles)
    {
        return FindCategoryMatches(categoryIndex, latitude, longitude, radiusMiles).Any();
    }

    private static int GetBinIndex(double value, double cellSizeDegrees) =>
        (int)Math.Floor(value / cellSizeDegrees);

    private static IReadOnlyDictionary<string, IReadOnlyList<ScoredLocation>> CollectTopRelevantLocations(
        BoundingBox bounds,
        double step,
        double radiusMiles,
        IReadOnlyCollection<GridPoint> boundaryPoints,
        IReadOnlyList<CategoryIndex> categoryIndexes)
    {
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

        return relevantByCategory.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlyList<ScoredLocation>)entry.Value
                .OrderByDescending(static pair => pair.Value)
                .ThenBy(static pair => pair.Key.Latitude)
                .ThenBy(static pair => pair.Key.Longitude)
                .Take(3)
                .Select(static pair => new ScoredLocation(pair.Key, pair.Value))
                .ToArray(),
            StringComparer.OrdinalIgnoreCase);
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

    private static string BuildKml(IReadOnlyList<ShapeRegion> shapeRegions, BoundingBox bounds, double step)
    {
        var builder = new StringBuilder();
        builder.AppendLine("""<?xml version="1.0" encoding="UTF-8"?>""");
        builder.AppendLine("""<kml xmlns="http://www.opengis.net/kml/2.2">""");
        builder.AppendLine("<Document>");
        builder.AppendLine("""  <Style id="boundary-point"><IconStyle><scale>0.45</scale><Icon><href>http://maps.google.com/mapfiles/kml/shapes/placemark_circle.png</href></Icon><color>ff0000ff</color></IconStyle></Style>""");

        var categories = shapeRegions
            .SelectMany(static region => region.TopMatchesByCategory.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static category => category, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var category in categories)
        {
            var styleId = BuildCategoryStyleId(category);
            var color = GetCategoryColor(category);
            builder.Append("  <Style id=\"");
            builder.Append(styleId);
            builder.Append("\"><IconStyle><scale>0.7</scale><Icon><href>http://maps.google.com/mapfiles/kml/shapes/placemark_circle.png</href></Icon><color>");
            builder.Append(color);
            builder.AppendLine("</color></IconStyle></Style>");
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
        return builder.ToString();
    }

    private static string BuildCategoryStyleId(string category) =>
        $"category-{new string(category.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant()}";

    private static string GetCategoryColor(string category) =>
        category.Trim().ToLowerInvariant() switch
        {
            "gym" => "ff00a5ff",
            "grocery" => "ff3cb44b",
            "marta" => "ff4b4bff",
            "park" => "ff2e8b57",
            "trail" => "ff00d7ff",
            _ => "ffffffff"
        };

    private static string BuildLocationLabel(string category, ScoredLocation scoredLocation)
    {
        var label = string.IsNullOrWhiteSpace(scoredLocation.Location.Label)
            ? category
            : scoredLocation.Location.Label!;

        return $"{label} ({scoredLocation.Count})";
    }

    private readonly record struct GridPoint(int LatitudeIndex, int LongitudeIndex);

    private readonly record struct BinKey(int LatitudeBin, int LongitudeBin);

    private sealed record CategoryIndex(
        string Category,
        IReadOnlyDictionary<BinKey, LocationInput[]> Bins,
        double CellSizeDegrees,
        double LatitudeRadiusDegrees);

    private sealed record ShapeRegion(
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
