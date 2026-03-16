using System.Net.Http.Json;
using System.Text.Json.Serialization;
using PlacesGatherer.Console.Models;

namespace PlacesGatherer.Console.Services;

/// <summary>
/// Google integration signpost: all request construction and payload mapping lives here so the CLI stays simple.
/// </summary>
public sealed class GooglePlacesClient
{
    public const string FieldMask =
        "places.id,places.displayName,places.formattedAddress,places.location,places.types";

    private readonly HttpClient _httpClient;

    public GooglePlacesClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<IReadOnlyList<NormalizedPlaceRecord>> SearchAsync(
        PlacesSearchDefinition search,
        RectangleBounds bounds,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://places.googleapis.com/v1/places:searchText");
        request.Headers.Add("X-Goog-Api-Key", apiKey);
        request.Headers.Add("X-Goog-FieldMask", FieldMask);
        request.Content = JsonContent.Create(new SearchTextRequest
        {
            TextQuery = search.Query,
            LocationRestriction = new LocationRestriction
            {
                Rectangle = new Rectangle
                {
                    Low = new LatLng
                    {
                        Latitude = bounds.South,
                        Longitude = bounds.West
                    },
                    High = new LatLng
                    {
                        Latitude = bounds.North,
                        Longitude = bounds.East
                    }
                }
            }
        });

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<SearchTextResponse>(cancellationToken: cancellationToken);
        return payload?.Places?
            .Where(place => place.Location is not null)
            .Select(place => new NormalizedPlaceRecord
            {
                Query = search.Query,
                Category = search.Category,
                PlaceId = place.Id ?? string.Empty,
                Name = place.DisplayName?.Text ?? string.Empty,
                FormattedAddress = place.FormattedAddress,
                Latitude = place.Location!.Latitude,
                Longitude = place.Location.Longitude,
                Types = place.Types ?? Array.Empty<string>(),
                SourceQueryType = search.SourceQueryType
            })
            .ToArray() ?? Array.Empty<NormalizedPlaceRecord>();
    }

    public static void ValidateConfig(PlacesGathererConfig config)
    {
        if (config.Searches is null || config.Searches.Count == 0)
        {
            throw new InvalidOperationException("At least one search entry is required.");
        }

        if (config.Bounds.South > config.Bounds.North)
        {
            throw new InvalidOperationException("Bounds south cannot be greater than north.");
        }

        if (config.Bounds.West > config.Bounds.East)
        {
            throw new InvalidOperationException("Bounds west cannot be greater than east.");
        }

        foreach (var search in config.Searches)
        {
            if (string.IsNullOrWhiteSpace(search.Query))
            {
                throw new InvalidOperationException("Each search entry must include a query.");
            }

            if (string.IsNullOrWhiteSpace(search.Category))
            {
                throw new InvalidOperationException("Each search entry must include a category.");
            }
        }
    }

    private sealed class SearchTextRequest
    {
        [JsonPropertyName("textQuery")]
        public string TextQuery { get; init; } = string.Empty;

        [JsonPropertyName("locationRestriction")]
        public LocationRestriction LocationRestriction { get; init; } = new();
    }

    private sealed class LocationRestriction
    {
        [JsonPropertyName("rectangle")]
        public Rectangle Rectangle { get; init; } = new();
    }

    private sealed class Rectangle
    {
        [JsonPropertyName("low")]
        public LatLng Low { get; init; } = new();

        [JsonPropertyName("high")]
        public LatLng High { get; init; } = new();
    }

    private sealed class LatLng
    {
        [JsonPropertyName("latitude")]
        public double Latitude { get; init; }

        [JsonPropertyName("longitude")]
        public double Longitude { get; init; }
    }

    private sealed class SearchTextResponse
    {
        [JsonPropertyName("places")]
        public SearchTextPlace[]? Places { get; init; }
    }

    private sealed class SearchTextPlace
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("displayName")]
        public DisplayName? DisplayName { get; init; }

        [JsonPropertyName("formattedAddress")]
        public string? FormattedAddress { get; init; }

        [JsonPropertyName("location")]
        public LatLng? Location { get; init; }

        [JsonPropertyName("types")]
        public string[]? Types { get; init; }
    }

    private sealed class DisplayName
    {
        [JsonPropertyName("text")]
        public string? Text { get; init; }
    }
}
