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
                new LocationInput { Latitude = 40.0d, Longitude = -73.0d, Category = "coffee" },
                new LocationInput { Latitude = 40.0d, Longitude = -73.0d, Category = "grocery" }
            ]
        };

        var result = _service.Generate(request);

        Assert.Contains("<Placemark>", result.Kml);
        Assert.True(result.BoundaryPointCount > 0);
        Assert.True(result.ValidPointCount >= result.BoundaryPointCount);
    }
}
