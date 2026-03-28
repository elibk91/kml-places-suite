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
    public void Generate_ReturnsBoundaryKml_ForKnownInput()
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

        Assert.Contains("Overlap Points", result.Kml);
        Assert.Contains("Category Points", result.Kml);
        Assert.Contains("overlap-point", result.Kml);
        Assert.Contains("category-coffee", result.Kml);
        Assert.Contains("category-grocery", result.Kml);
        Assert.Contains("Main Coffee (", result.Kml);
        Assert.Contains("Main Grocery (", result.Kml);
        Assert.DoesNotContain("Far Coffee", result.Kml, StringComparison.Ordinal);
        Assert.DoesNotContain("-74", result.Kml, StringComparison.Ordinal);
        Assert.True(result.BoundaryPointCount > 0);
        Assert.True(result.ValidPointCount >= result.BoundaryPointCount);
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
                new LocationInput { Latitude = 33.7506d, Longitude = -84.3893d, Category = "marta", Label = "Marta" }
            ]
        };

        var result = _service.Generate(request);

        Assert.True(result.ValidPointCount > 0);
        Assert.True(result.BoundaryPointCount > 0);
        Assert.Contains("Overlap Points", result.Kml);
    }

    [Fact]
    public void Generate_LimitsDisplayedSupportPointsPerCategoryPerShape()
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
                new LocationInput { Latitude = 33.7500d, Longitude = -84.3900d, Category = "marta", Label = "MARTA 1" }
            ]
        };

        var result = _service.Generate(request);
        var gymLabelCount = CountOccurrences(result.Kml, "<name>Gym ");

        Assert.True(gymLabelCount <= 3);
        Assert.DoesNotContain("Gym 4", result.Kml, StringComparison.Ordinal);
    }

    [Fact]
    public void LatestWorkflowOutput_AllSampledOutputPointsSatisfyConfiguredCategoryRadii()
    {
        var repoRoot = FindRepoRoot();
        var latestRunDirectory = Directory
            .GetDirectories(Path.Combine(repoRoot, "scripts", "out", "runs"), "category-workflow-*")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Last();
        var latestOutlinePath = Path.Combine(latestRunDirectory, "atlanta-category-outline.arc.kml");
        var requestPath = Path.Combine(latestRunDirectory, "atlanta-category-request.arc.json");
        if (!File.Exists(requestPath))
        {
            requestPath = Path.Combine(repoRoot, "scripts", "out", "diagnostics", "atlanta-category-request.current.json");
        }

        Assert.True(File.Exists(latestOutlinePath), $"Latest outline KML not found at '{latestOutlinePath}'.");
        Assert.True(File.Exists(requestPath), $"Validation request JSON not found at '{requestPath}'.");

        Console.WriteLine($"Validating latest outline: {latestOutlinePath}");
        Console.WriteLine($"Using request JSON: {requestPath}");

        var request = JsonSerializer.Deserialize<GenerateKmlRequest>(
            File.ReadAllText(requestPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(request);

        var overlapPoints = ParseEmittedPoints(File.ReadAllText(latestOutlinePath), "#overlap-point")
            .OrderByDescending(point => point.Latitude)
            .ThenBy(point => point.Longitude)
            .ToArray();
        var sampledPointCount = 0;
        var categories = request!.Locations
            .Select(location => location.Category)
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
                if (HasCategoryMatch(request, category, point.Latitude, point.Longitude))
                {
                    continue;
                }

                var nearestMatch = FindNearestCategoryMatch(request, category, point.Latitude, point.Longitude);
                var radiusMiles = request.CategoryRadiusMiles.TryGetValue(category, out var configuredRadiusMiles)
                    ? configuredRadiusMiles
                    : request.RadiusMiles;

                failures.Add(
                    $"Point ({point.Latitude}, {point.Longitude}) failed '{category}' coverage. "
                    + $"nearest='{nearestMatch.Label}' distance={nearestMatch.DistanceMiles:F3} radius={radiusMiles:F3}");
            }
        }

        Assert.True(sampledPointCount > 0);
        Console.WriteLine($"Validated sampled output points: {sampledPointCount}");
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

    private static IReadOnlyList<(double Latitude, double Longitude)> ParseEmittedPoints(string kml, string styleUrl)
    {
        var document = XDocument.Parse(kml);
        return document
            .Descendants()
            .Where(element => element.Name.LocalName.Equals("Placemark", StringComparison.Ordinal))
            .Where(element => element
                .Descendants()
                .Any(descendant =>
                    descendant.Name.LocalName.Equals("styleUrl", StringComparison.Ordinal)
                    && string.Equals(descendant.Value.Trim(), styleUrl, StringComparison.Ordinal)))
            .Select(element => element
                .Descendants()
                .First(descendant => descendant.Name.LocalName.Equals("coordinates", StringComparison.Ordinal)))
            .Select(coordinatesElement =>
            {
                var token = coordinatesElement.Value.Trim();
                var parts = token.Split(',');
                return (Latitude: double.Parse(parts[1]), Longitude: double.Parse(parts[0]));
            })
            .ToArray();
    }

    private static string FindRepoRoot()
    {
        var directory = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(directory))
        {
            if (File.Exists(Path.Combine(directory, "AGENTS.md"))
                && Directory.Exists(Path.Combine(directory, "scripts"))
                && Directory.Exists(Path.Combine(directory, "KmlGenerator.Tests")))
            {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new DirectoryNotFoundException("Could not locate the repository root from the test output directory.");
    }

    private static bool HasCategoryMatch(GenerateKmlRequest request, string category, double latitude, double longitude)
    {
        var radiusMiles = request.CategoryRadiusMiles.TryGetValue(category, out var configuredRadiusMiles)
            ? configuredRadiusMiles
            : request.RadiusMiles;

        return request.Locations
            .Where(location => location.Category.Equals(category, StringComparison.OrdinalIgnoreCase))
            .Any(location => KmlGenerationService.GetDistanceMiles(latitude, longitude, location.Latitude, location.Longitude) <= radiusMiles);
    }

    private static (string Label, double DistanceMiles) FindNearestCategoryMatch(GenerateKmlRequest request, string category, double latitude, double longitude) =>
        request.Locations
            .Where(location => location.Category.Equals(category, StringComparison.OrdinalIgnoreCase))
            .Select(location => (
                Label: location.Label,
                DistanceMiles: KmlGenerationService.GetDistanceMiles(latitude, longitude, location.Latitude, location.Longitude)))
            .OrderBy(result => result.DistanceMiles)
            .First();
}
