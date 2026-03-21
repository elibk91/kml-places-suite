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
        Assert.Equal("base", record.SourceQueryType);
    }

    [Fact]
    public async Task SearchAsync_RetriesTransientServiceUnavailableResponses()
    {
        var attempts = 0;
        var handler = new StubHttpMessageHandler(_ =>
        {
            attempts++;
            if (attempts < 3)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "places": [
                        {
                          "id": "park-1",
                          "displayName": { "text": "Piedmont Park" },
                          "formattedAddress": "1320 Monroe Dr NE, Atlanta, GA 30306, USA",
                          "location": { "latitude": 33.7851, "longitude": -84.3738 },
                          "types": [ "park" ]
                        }
                      ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        });

        var client = new GooglePlacesClient(new HttpClient(handler));

        var results = await client.SearchAsync(
            new PlacesSearchDefinition { Query = "park", Category = "park" },
            new RectangleBounds { North = 33.80d, South = 33.70d, East = -84.30d, West = -84.40d },
            "key");

        Assert.Equal(3, attempts);
        Assert.Single(results);
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

    [Fact]
    public void Expand_BuildsBaseAndExpandedQueries()
    {
        var expanded = PlacesSearchExpander.Expand(new PlacesSearchDefinition
        {
            Query = "Piedmont Park",
            Category = "park",
            Expansion = new PlacesSearchExpansion
            {
                Enabled = true,
                Templates = ["entrance", "{query} north entrance"]
            }
        });

        Assert.Equal(3, expanded.Count);
        Assert.Equal("base", expanded[0].SourceQueryType);
        Assert.Equal("Piedmont Park entrance", expanded[1].Query);
        Assert.Equal("expanded", expanded[1].SourceQueryType);
        Assert.Equal("Piedmont Park north entrance", expanded[2].Query);
    }

    [Fact]
    public void Normalize_AddsHints_WhenNamesConflict()
    {
        var normalized = PlaceNameNormalizer.Normalize(
        [
            new NormalizedPlaceRecord
            {
                Query = "Planet Fitness",
                Category = "gym",
                PlaceId = "pf-001",
                Name = "Planet Fitness",
                FormattedAddress = "123 Peachtree St NE, Atlanta, GA",
                Latitude = 1,
                Longitude = 1
            },
            new NormalizedPlaceRecord
            {
                Query = "Planet Fitness",
                Category = "gym",
                PlaceId = "pf-002",
                Name = "Planet Fitness",
                FormattedAddress = "456 Piedmont Ave NE, Atlanta, GA",
                Latitude = 2,
                Longitude = 2
            }
        ]);

        Assert.Contains(normalized, record => record.Name == "Planet Fitness | Peachtree St NE");
        Assert.Contains(normalized, record => record.Name == "Planet Fitness | Piedmont Ave NE");
    }
}
