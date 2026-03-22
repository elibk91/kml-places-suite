using System.Net;
using System.Net.Http.Json;
using KmlGenerator.Core.Models;
using Microsoft.AspNetCore.Mvc.Testing;

namespace KmlGenerator.Tests;

public sealed class KmlApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public KmlApiTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task PostKml_ReturnsJsonPayload()
    {
        var response = await _client.PostAsJsonAsync("/kml", BuildRequest());
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<GenerateKmlResult>();
        Assert.NotNull(payload);
        Assert.Contains("<kml", payload.Kml);
    }

    [Fact]
    public async Task PostKmlFile_ReturnsKmlFile()
    {
        var response = await _client.PostAsJsonAsync("/kml/file", BuildRequest());
        response.EnsureSuccessStatusCode();

        Assert.Equal("application/vnd.google-earth.kml+xml", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("outline.kml", response.Content.Headers.ContentDisposition?.FileNameStar);
    }

    [Fact]
    public async Task PostKml_ReturnsBadRequest_WhenPayloadIsInvalid()
    {
        var response = await _client.PostAsJsonAsync("/kml", new GenerateKmlRequest());
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static GenerateKmlRequest BuildRequest() =>
        new()
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
}
