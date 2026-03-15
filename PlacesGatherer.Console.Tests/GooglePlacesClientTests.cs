using System.Net;
using System.Text;
using PlacesGatherer.Console.Models;
using PlacesGatherer.Console.Services;

namespace PlacesGatherer.Console.Tests;

public sealed class GooglePlacesClientTests
{
    [Fact]
    public async Task SearchAsync_MapsNormalizedRecords()
    {
        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "places": [
                        {
                          "id": "abc123",
                          "displayName": { "text": "Starbucks" },
                          "formattedAddress": "123 Main St",
                          "location": { "latitude": 40.1, "longitude": -73.9 },
                          "types": [ "cafe", "food" ]
                        }
                      ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            });

        var client = new GooglePlacesClient(new HttpClient(handler));

        var results = await client.SearchAsync(
            new PlacesSearchDefinition { Query = "Starbucks", Category = "coffee" },
            new RectangleBounds { North = 40.2d, South = 40.0d, East = -73.8d, West = -74.0d },
            "key");

        var record = Assert.Single(results);
        Assert.Equal("Starbucks", record.Name);
        Assert.Equal("coffee", record.Category);
        Assert.Equal(40.1d, record.Latitude);
    }

    [Fact]
    public void ValidateConfig_Throws_WhenCategoryIsMissing()
    {
        var config = new PlacesGathererConfig
        {
            Bounds = new RectangleBounds { North = 1, South = 0, East = 1, West = 0 },
            Searches =
            [
                new PlacesSearchDefinition { Query = "Starbucks", Category = "" }
            ]
        };

        Assert.Throws<InvalidOperationException>(() => GooglePlacesClient.ValidateConfig(config));
    }
}
