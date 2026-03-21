using KmlGenerator.Core.Exceptions;
using KmlGenerator.Core.Models;
using KmlGenerator.Core.Services;

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

        Assert.Contains("Overlap Boundary", result.Kml);
        Assert.Contains("Category Points", result.Kml);
        Assert.Contains("boundary-point", result.Kml);
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
                new LocationInput { Latitude = 33.7500d, Longitude = -84.3900d, Category = "gym" },
                new LocationInput { Latitude = 33.7512d, Longitude = -84.3888d, Category = "grocery" },
                new LocationInput { Latitude = 33.7506d, Longitude = -84.3893d, Category = "marta" }
            ]
        };

        var result = _service.Generate(request);

        Assert.True(result.ValidPointCount > 0);
        Assert.True(result.BoundaryPointCount > 0);
        Assert.Contains("boundary-point", result.Kml);
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
}
