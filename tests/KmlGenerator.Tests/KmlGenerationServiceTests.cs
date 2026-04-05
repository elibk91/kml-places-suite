using KmlGenerator.Core.Exceptions;
using KmlGenerator.Core.Models;
using KmlGenerator.Core.Services;
using System.Text.Json;
using System.Xml.Linq;

namespace KmlGenerator.Tests;

public sealed class KmlGenerationServiceTests
{
    private readonly KmlGenerationService _service = new();

    [Fact]
    public void Generate_Throws_WhenRequestIsInvalid()
    {
        var request = new GenerateKmlRequest
        {
            Locations = Array.Empty<LocationInput>()
        };

        Assert.Throws<KmlValidationException>(() => _service.Generate(request));
    }

    [Fact]
    public void GetDistanceMiles_ReturnsZero_ForSamePoint()
    {
        var distance = KmlGenerationService.GetDistanceMiles(40.0d, -73.0d, 40.0d, -73.0d);
        Assert.Equal(0d, distance, 8);
    }

    [Fact]
    public void Generate_ReturnsIntersectionKml_ForKnownInput()
    {
        var request = new GenerateKmlRequest
        {
            Step = 0.01d,
            PaddingDegrees = 0.01d,
            RadiusMiles = 1d,
            Locations =
            [
                new LocationInput { Latitude = 40.0d, Longitude = -73.0d, Category = "coffee", Label = "Main Coffee" },
                new LocationInput { Latitude = 40.0d, Longitude = -73.0d, Category = "grocery", Label = "Main Grocery" },
                new LocationInput { Latitude = 41.0d, Longitude = -74.0d, Category = "coffee", Label = "Far Coffee" }
            ]
        };

        var result = _service.Generate(request);

        Assert.Contains("Intersection", result.Kml);
        Assert.Contains("Source Features", result.Kml);
        Assert.Contains("Main Coffee", result.Kml);
        Assert.Contains("Main Grocery", result.Kml);
        Assert.True(result.IntersectionPolygonCount > 0);
        Assert.True(result.CoveredCellCount > 0);
    }

    [Fact]
    public void Generate_FindsOverlapAcrossSpatialBinBoundaries()
    {
        var request = new GenerateKmlRequest
        {
            Step = 0.001d,
            PaddingDegrees = 0.002d,
            RadiusMiles = 0.2d,
            Locations =
            [
                new LocationInput { Latitude = 33.7500d, Longitude = -84.3900d, Category = "gym", Label = "Gym" },
                new LocationInput { Latitude = 33.7512d, Longitude = -84.3888d, Category = "grocery", Label = "Grocery" },
                new LocationInput { Latitude = 33.7506d, Longitude = -84.3893d, Category = "transit", Label = "Transit" }
            ]
        };

        var result = _service.Generate(request);

        Assert.True(result.CoveredCellCount > 0);
        Assert.True(result.IntersectionPolygonCount > 0);
        Assert.Contains("Intersection", result.Kml);
    }

    [Fact]
    public void Generate_WritesSourceFeatures_ForAllInputGeometries()
    {
        var request = new GenerateKmlRequest
        {
            Step = 0.001d,
            PaddingDegrees = 0.002d,
            RadiusMiles = 0.5d,
            Locations =
            [
                new LocationInput { Latitude = 33.7500d, Longitude = -84.3900d, Category = "gym", Label = "Gym 1" },
                new LocationInput { Latitude = 33.7501d, Longitude = -84.3901d, Category = "gym", Label = "Gym 2" },
                new LocationInput { Latitude = 33.7502d, Longitude = -84.3902d, Category = "gym", Label = "Gym 3" },
                new LocationInput { Latitude = 33.7503d, Longitude = -84.3903d, Category = "gym", Label = "Gym 4" },
                new LocationInput { Latitude = 33.7500d, Longitude = -84.3900d, Category = "grocery", Label = "Grocery 1" },
                new LocationInput { Latitude = 33.7500d, Longitude = -84.3900d, Category = "transit", Label = "Transit 1" }
            ]
        };

        var result = _service.Generate(request);
        Assert.Contains("Gym 1", result.Kml);
        Assert.Contains("Gym 4", result.Kml);
        Assert.Contains("Source Features", result.Kml);
    }

    [Fact]
    public void LatestWorkflowOutput_AllSampledOutputPointsSatisfyConfiguredCategoryRadii()
    {
        var repoRoot = FindRepoRoot();
        var latestOutlinePath = Directory
            .GetFiles(Path.Combine(repoRoot, "workflow", "out", "runs"), "*-category-outline.arc.kml", SearchOption.AllDirectories)
            .OrderBy(path => File.GetLastWriteTimeUtc(path))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Last();
        var latestRunDirectory = Path.GetDirectoryName(latestOutlinePath)!;
        var cityPrefix = Path.GetFileName(latestOutlinePath).Replace("-category-outline.arc.kml", string.Empty, StringComparison.OrdinalIgnoreCase);
        var requestPath = Path.Combine(latestRunDirectory, $"{cityPrefix}-category-request.arc.json");

        Assert.True(File.Exists(latestOutlinePath), $"Latest outline KML not found at '{latestOutlinePath}'.");
        Assert.True(File.Exists(requestPath), $"Validation request JSON not found at '{requestPath}'.");

        Console.WriteLine($"Validating latest outline: {latestOutlinePath}");
        Console.WriteLine($"Using request JSON: {requestPath}");

        var request = JsonSerializer.Deserialize<GenerateKmlRequest>(
            File.ReadAllText(requestPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(request);

        var overlapPoints = ParseIntersectionPolygonCenters(File.ReadAllText(latestOutlinePath))
            .OrderByDescending(point => point.Latitude)
            .ThenBy(point => point.Longitude)
            .ToArray();
        var sampledPointCount = 0;
        var features = MaterializeFeatures(request!);
        var categories = features
            .Select(feature => feature.Category)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var failures = new List<string>();

        Console.WriteLine($"Emitted points={overlapPoints.Length}, categories={categories.Length}");

        for (var pointIndex = 0; pointIndex < overlapPoints.Length; pointIndex++)
        {
            var point = overlapPoints[pointIndex];
            sampledPointCount++;
            if (sampledPointCount == 1 || ((sampledPointCount % 100) == 0) || pointIndex == overlapPoints.Length - 1)
            {
                Console.WriteLine($"Point {pointIndex + 1}/{overlapPoints.Length}: latitude={point.Latitude:F6}, longitude={point.Longitude:F6}, checked={sampledPointCount}");
            }

            foreach (var category in categories)
            {
                if (HasCategoryMatch(features, request!, category, point.Latitude, point.Longitude))
                {
                    continue;
                }

                var nearestMatch = FindNearestCategoryMatch(features, category, point.Latitude, point.Longitude);
                var radiusMiles = request!.CategoryRadiusMiles.TryGetValue(category, out var configuredRadiusMiles)
                    ? configuredRadiusMiles
                    : request!.RadiusMiles;

                failures.Add(
                    $"Point ({point.Latitude}, {point.Longitude}) failed '{category}' coverage. "
                    + $"nearest='{nearestMatch.Label}' distance={nearestMatch.DistanceMiles:F3} radius={radiusMiles:F3}");
            }
        }

        Assert.True(sampledPointCount > 0);
        Console.WriteLine($"Validated sampled polygon centers: {sampledPointCount}");
        if (failures.Count > 0)
        {
            Console.WriteLine($"Validation failures: {failures.Count}");
            foreach (var failure in failures)
            {
                Console.WriteLine(failure);
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static IReadOnlyList<(double Latitude, double Longitude)> ParseIntersectionPolygonCenters(string kml)
    {
        var document = XDocument.Parse(kml);
        return document
            .Descendants()
            .Where(element => element.Name.LocalName.Equals("Folder", StringComparison.Ordinal))
            .Where(element => element.Elements().Any(descendant =>
                descendant.Name.LocalName.Equals("name", StringComparison.Ordinal)
                && string.Equals(descendant.Value.Trim(), "Intersection", StringComparison.Ordinal)))
            .Elements()
            .Where(element => element.Name.LocalName.Equals("Placemark", StringComparison.Ordinal))
            .Select(element => element.Descendants().First(descendant => descendant.Name.LocalName.Equals("coordinates", StringComparison.Ordinal)).Value)
            .Select(coordinatesText =>
            {
                var coordinates = coordinatesText
                    .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                    .Select(token =>
                    {
                        var parts = token.Split(',');
                        return (Latitude: double.Parse(parts[1]), Longitude: double.Parse(parts[0]));
                    })
                    .ToArray();
                return (
                    Latitude: coordinates.Average(point => point.Latitude),
                    Longitude: coordinates.Average(point => point.Longitude));
            })
            .ToArray();
    }

    private static string FindRepoRoot()
    {
        var directory = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(directory))
        {
            if (File.Exists(Path.Combine(directory, "AGENTS.md"))
                && Directory.Exists(Path.Combine(directory, "workflow"))
                && Directory.Exists(Path.Combine(directory, "tests")))
            {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new DirectoryNotFoundException("Could not locate the repository root from the test output directory.");
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

    private static bool HasCategoryMatch(IReadOnlyList<GeometryFeatureInput> features, GenerateKmlRequest request, string category, double latitude, double longitude)
    {
        var radiusMiles = request.CategoryRadiusMiles.TryGetValue(category, out var configuredRadiusMiles)
            ? configuredRadiusMiles
            : request.RadiusMiles;

        return features
            .Where(feature => feature.Category.Equals(category, StringComparison.OrdinalIgnoreCase))
            .Any(feature => DistanceMilesToFeature(feature, latitude, longitude) <= radiusMiles);
    }

    private static (string Label, double DistanceMiles) FindNearestCategoryMatch(IReadOnlyList<GeometryFeatureInput> features, string category, double latitude, double longitude) =>
        features
            .Where(feature => feature.Category.Equals(category, StringComparison.OrdinalIgnoreCase))
            .Select(feature => (
                Label: feature.Label,
                DistanceMiles: DistanceMilesToFeature(feature, latitude, longitude)))
            .OrderBy(result => result.DistanceMiles)
            .First();

    private static double DistanceMilesToFeature(GeometryFeatureInput feature, double latitude, double longitude)
    {
        var allCoordinates = EnumerateCoordinates([feature]).ToArray();
        var referenceLatitude = allCoordinates.Average(point => point.Latitude);
        var feetPerDegreeLongitude = 364_000d * Math.Cos(referenceLatitude * Math.PI / 180d);
        var probe = Project(latitude, longitude, referenceLatitude, allCoordinates.Average(point => point.Longitude), feetPerDegreeLongitude);

        return feature.GeometryType.Trim().ToLowerInvariant() switch
        {
            "point" => feature.Points.Min(point => DistanceFeet(Project(point.Latitude, point.Longitude, referenceLatitude, allCoordinates.Average(p => p.Longitude), feetPerDegreeLongitude), probe)) / 5_280d,
            "linestring" => feature.Lines.Min(line => DistanceToLineFeet(line, probe, referenceLatitude, allCoordinates.Average(point => point.Longitude), feetPerDegreeLongitude)) / 5_280d,
            "polygon" => feature.Polygons.Min(polygon => DistanceToPolygonFeet(polygon, probe, referenceLatitude, allCoordinates.Average(point => point.Longitude), feetPerDegreeLongitude)) / 5_280d,
            _ => double.MaxValue
        };
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

    private static (double X, double Y) Project(double latitude, double longitude, double referenceLatitude, double referenceLongitude, double feetPerDegreeLongitude) =>
        ((longitude - referenceLongitude) * feetPerDegreeLongitude, (latitude - referenceLatitude) * 364_000d);

    private static double DistanceFeet((double X, double Y) left, (double X, double Y) right)
    {
        var dx = left.X - right.X;
        var dy = left.Y - right.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static double DistanceToLineFeet(LineStringInput line, (double X, double Y) probe, double referenceLatitude, double referenceLongitude, double feetPerDegreeLongitude)
    {
        var points = line.Coordinates.Select(point => Project(point.Latitude, point.Longitude, referenceLatitude, referenceLongitude, feetPerDegreeLongitude)).ToArray();
        var minDistance = double.MaxValue;
        for (var index = 1; index < points.Length; index++)
        {
            minDistance = Math.Min(minDistance, DistanceToSegmentFeet(points[index - 1], points[index], probe));
        }

        return minDistance;
    }

    private static double DistanceToPolygonFeet(PolygonInput polygon, (double X, double Y) probe, double referenceLatitude, double referenceLongitude, double feetPerDegreeLongitude)
    {
        var outer = polygon.OuterRing.Select(point => Project(point.Latitude, point.Longitude, referenceLatitude, referenceLongitude, feetPerDegreeLongitude)).ToArray();
        if (IsInsideRing(outer, probe))
        {
            return 0d;
        }

        var minDistance = DistanceToRingFeet(outer, probe);
        foreach (var ring in polygon.InnerRings)
        {
            minDistance = Math.Min(minDistance, DistanceToRingFeet(ring.Select(point => Project(point.Latitude, point.Longitude, referenceLatitude, referenceLongitude, feetPerDegreeLongitude)).ToArray(), probe));
        }

        return minDistance;
    }

    private static double DistanceToRingFeet((double X, double Y)[] ring, (double X, double Y) probe)
    {
        var minDistance = double.MaxValue;
        for (var index = 1; index < ring.Length; index++)
        {
            minDistance = Math.Min(minDistance, DistanceToSegmentFeet(ring[index - 1], ring[index], probe));
        }

        if (ring.Length > 2)
        {
            minDistance = Math.Min(minDistance, DistanceToSegmentFeet(ring[^1], ring[0], probe));
        }

        return minDistance;
    }

    private static bool IsInsideRing((double X, double Y)[] ring, (double X, double Y) probe)
    {
        var inside = false;
        var previous = ring.Length - 1;
        for (var index = 0; index < ring.Length; previous = index++)
        {
            var current = ring[index];
            var last = ring[previous];
            var crosses = ((current.Y > probe.Y) != (last.Y > probe.Y))
                          && (probe.X < ((last.X - current.X) * (probe.Y - current.Y) / ((last.Y - current.Y) + double.Epsilon)) + current.X);
            if (crosses)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static double DistanceToSegmentFeet((double X, double Y) start, (double X, double Y) end, (double X, double Y) probe)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        if (dx == 0d && dy == 0d)
        {
            return DistanceFeet(start, probe);
        }

        var projection = ((probe.X - start.X) * dx + (probe.Y - start.Y) * dy) / ((dx * dx) + (dy * dy));
        projection = Math.Clamp(projection, 0d, 1d);
        var projected = (start.X + (projection * dx), start.Y + (projection * dy));
        return DistanceFeet(projected, probe);
    }
}
