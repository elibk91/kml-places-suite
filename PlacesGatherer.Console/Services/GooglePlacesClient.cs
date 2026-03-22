using System.Net.Http.Json;
using System.Text.Json.Serialization;
using KmlSuite.Shared.Diagnostics;
using Microsoft.Extensions.Logging;
using PlacesGatherer.Console.Models;

namespace PlacesGatherer.Console.Services;

/// <summary>
/// Google integration signpost: all request construction and payload mapping lives here so the CLI stays simple.
/// </summary>
public sealed class GooglePlacesClient : IGooglePlacesClient
{
    public const string FieldMask =
        "places.id,places.displayName,places.formattedAddress,places.location,places.types";

    private readonly HttpClient _httpClient;
    private readonly ILogger<GooglePlacesClient> _logger;

    public GooglePlacesClient(HttpClient httpClient, ILogger<GooglePlacesClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<IReadOnlyList<NormalizedPlaceRecord>> SearchAsync(
        PlacesSearchDefinition search,
        RectangleBounds bounds,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        using var _ = MethodTrace.Enter(
            _logger,
            nameof(GooglePlacesClient),
            new Dictionary<string, object?>
            {
                ["Query"] = search.Query,
                ["Category"] = search.Category,
                ["South"] = bounds.South,
                ["North"] = bounds.North,
                ["West"] = bounds.West,
                ["East"] = bounds.East
            });

        SearchTextResponse? payload = null;

        for (var attempt = 0; attempt < RetryDelays.Length; attempt++)
        {
            using var request = CreateRequest(search, bounds, apiKey);
            _logger.LogDebug("Sending Google Places request for {Query} on attempt {Attempt}", search.Query, attempt + 1);
            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                payload = await response.Content.ReadFromJsonAsync<SearchTextResponse>(cancellationToken: cancellationToken);
                _logger.LogInformation(
                    "Google Places request for {Query} succeeded with {PlaceCount} places on attempt {Attempt}",
                    search.Query,
                    payload?.Places?.Length ?? 0,
                    attempt + 1);
                break;
            }

            if (!IsRetriableStatusCode((int)response.StatusCode) || attempt == RetryDelays.Length - 1)
            {
                _logger.LogWarning(
                    "Google Places request for {Query} failed with status {StatusCode} and will not retry",
                    search.Query,
                    (int)response.StatusCode);
                response.EnsureSuccessStatusCode();
            }

            _logger.LogWarning(
                "Google Places request for {Query} failed with retriable status {StatusCode}; delaying for {DelaySeconds} seconds",
                search.Query,
                (int)response.StatusCode,
                RetryDelays[attempt].TotalSeconds);
            await Task.Delay(RetryDelays[attempt], cancellationToken);
        }

        var mapped = payload?.Places?
            .Where(place => place.Location is not null)
            .Select(place =>
            {
                var location = place.Location ?? throw new InvalidOperationException("Google Places returned a place without coordinates.");
                return new NormalizedPlaceRecord
                {
                    Query = search.Query,
                    Category = search.Category,
                    PlaceId = place.Id ?? string.Empty,
                    Name = place.DisplayName?.Text ?? string.Empty,
                    FormattedAddress = place.FormattedAddress,
                    Latitude = location.Latitude,
                    Longitude = location.Longitude,
                    Types = place.Types ?? Array.Empty<string>(),
                    SourceQueryType = search.SourceQueryType
                };
            })
            .ToArray() ?? Array.Empty<NormalizedPlaceRecord>();

        _logger.LogDebug("Mapped {MappedCount} normalized place records for {Query}", mapped.Length, search.Query);
        return mapped;
    }

    private HttpRequestMessage CreateRequest(PlacesSearchDefinition search, RectangleBounds bounds, string apiKey)
    {
        using var _ = MethodTrace.Enter(
            _logger,
            nameof(GooglePlacesClient),
            new Dictionary<string, object?> { ["Query"] = search.Query });

        var request = new HttpRequestMessage(HttpMethod.Post, "https://places.googleapis.com/v1/places:searchText");
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

        return request;
    }

    private bool IsRetriableStatusCode(int statusCode)
    {
        using var _ = MethodTrace.Enter(
            _logger,
            nameof(GooglePlacesClient),
            new Dictionary<string, object?> { ["StatusCode"] = statusCode });
        return statusCode == 429 || statusCode == 500 || statusCode == 502 || statusCode == 503 || statusCode == 504;
    }

    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10)
    ];

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

