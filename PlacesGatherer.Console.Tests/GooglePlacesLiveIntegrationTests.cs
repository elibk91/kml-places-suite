using PlacesGatherer.Console.Models;
using PlacesGatherer.Console.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace PlacesGatherer.Console.Tests;

public sealed class GooglePlacesLiveIntegrationTests
{
    [Fact]
    public async Task SearchAsync_WorksAgainstLiveGoogleApi_WhenEnabled()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("RUN_LIVE_GOOGLE_TESTS"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var apiKey = Environment.GetEnvironmentVariable("GoogleMaps__ApiKey");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("GoogleMaps__ApiKey must be set when RUN_LIVE_GOOGLE_TESTS=true.");
        }

        using var client = new HttpClient();
        var placesClient = new GooglePlacesClient(client, new NullLogger<GooglePlacesClient>());

        var results = await placesClient.SearchAsync(
            new PlacesSearchDefinition { Query = "Starbucks", Category = "coffee" },
            new RectangleBounds
            {
                North = 40.759d,
                South = 40.757d,
                East = -73.983d,
                West = -73.986d
            },
            apiKey);

        Assert.NotEmpty(results);
        Assert.All(results, result =>
        {
            Assert.False(string.IsNullOrWhiteSpace(result.PlaceId));
            Assert.False(string.IsNullOrWhiteSpace(result.Name));
        });
    }

    [Fact]
    public async Task SearchAsync_WorksWithExpandedParkQueries_WhenEnabled()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("RUN_LIVE_GOOGLE_TESTS"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var apiKey = Environment.GetEnvironmentVariable("GoogleMaps__ApiKey");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("GoogleMaps__ApiKey must be set when RUN_LIVE_GOOGLE_TESTS=true.");
        }

        using var client = new HttpClient();
        var placesClient = new GooglePlacesClient(client, new NullLogger<GooglePlacesClient>());

        var searches = new PlacesSearchExpander(new NullLogger<PlacesSearchExpander>()).Expand(new PlacesSearchDefinition
        {
            Query = "Piedmont Park",
            Category = "park",
            Expansion = new PlacesSearchExpansion
            {
                Enabled = true,
                Templates = ["entrance"]
            }
        });

        var totalCount = 0;
        foreach (var search in searches)
        {
            var results = await placesClient.SearchAsync(
                search,
                new RectangleBounds
                {
                    North = 33.80d,
                    South = 33.77d,
                    East = -84.36d,
                    West = -84.39d
                },
                apiKey);

            totalCount += results.Count;
        }

        Assert.True(totalCount > 0);
    }
}

