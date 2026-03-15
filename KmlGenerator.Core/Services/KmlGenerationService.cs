using System.Globalization;
using System.Text;
using KmlGenerator.Core.Exceptions;
using KmlGenerator.Core.Models;

namespace KmlGenerator.Core.Services;

/// <summary>
/// Shared orchestration service for the KML pipeline.
/// Hosts should call this service rather than duplicating any scan logic.
/// </summary>
public sealed class KmlGenerationService : IKmlGenerationService
{
    private const double DegreesToRadians = Math.PI / 180d;

    public GenerateKmlResult Generate(GenerateKmlRequest request)
    {
        Validate(request);

        var bounds = BuildBounds(request);
        var locationsByCategory = request.Locations
            .GroupBy(location => location.Category.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        var validPoints = ScanValidPoints(bounds, request.Step, request.RadiusMiles, locationsByCategory);
        var boundaryPoints = ExtractBoundaryPoints(validPoints);
        var kml = BuildKml(bounds, request.Step, boundaryPoints);

        return new GenerateKmlResult
        {
            Kml = kml,
            BoundaryPointCount = boundaryPoints.Count,
            ValidPointCount = validPoints.Count,
            Bounds = bounds
        };
    }

    public static double GetDistanceMiles(double lat1, double lon1, double lat2, double lon2)
    {
        // This mirrors the original Python script's approximation so the port stays behaviorally consistent.
        var cosLatitude = Math.Cos(lat2 * DegreesToRadians);
        var latMiles = (lat1 - lat2) * 69d;
        var lonMiles = (lon1 - lon2) * 69d * cosLatitude;
        return Math.Sqrt((latMiles * latMiles) + (lonMiles * lonMiles));
    }

    private static void Validate(GenerateKmlRequest request)
    {
        if (request is null)
        {
            throw new KmlValidationException("The request body is required.");
        }

        if (request.Locations is null || request.Locations.Count == 0)
        {
            throw new KmlValidationException("At least one location is required.");
        }

        if (request.Step <= 0d)
        {
            throw new KmlValidationException("Step must be greater than zero.");
        }

        if (request.RadiusMiles <= 0d)
        {
            throw new KmlValidationException("RadiusMiles must be greater than zero.");
        }

        if (request.PaddingDegrees < 0d)
        {
            throw new KmlValidationException("PaddingDegrees cannot be negative.");
        }

        foreach (var location in request.Locations)
        {
            if (location is null)
            {
                throw new KmlValidationException("Locations cannot contain null items.");
            }

            if (string.IsNullOrWhiteSpace(location.Category))
            {
                throw new KmlValidationException("Each location must include a category.");
            }

            if (location.Latitude is < -90d or > 90d)
            {
                throw new KmlValidationException($"Latitude {location.Latitude} is out of range.");
            }

            if (location.Longitude is < -180d or > 180d)
            {
                throw new KmlValidationException($"Longitude {location.Longitude} is out of range.");
            }
        }
    }

    private static BoundingBox BuildBounds(GenerateKmlRequest request)
    {
        var minLat = request.Locations.Min(location => location.Latitude) - request.PaddingDegrees;
        var maxLat = request.Locations.Max(location => location.Latitude) + request.PaddingDegrees;
        var minLon = request.Locations.Min(location => location.Longitude) - request.PaddingDegrees;
        var maxLon = request.Locations.Max(location => location.Longitude) + request.PaddingDegrees;

        return new BoundingBox
        {
            MinLatitude = minLat,
            MaxLatitude = maxLat,
            MinLongitude = minLon,
            MaxLongitude = maxLon
        };
    }

    private static HashSet<GridPoint> ScanValidPoints(
        BoundingBox bounds,
        double step,
        double radiusMiles,
        IReadOnlyDictionary<string, LocationInput[]> locationsByCategory)
    {
        var validPoints = new HashSet<GridPoint>();
        var totalLatSteps = (int)Math.Round((bounds.MaxLatitude - bounds.MinLatitude) / step) + 1;
        var totalLonSteps = (int)Math.Round((bounds.MaxLongitude - bounds.MinLongitude) / step) + 1;

        // The scan walks a dense grid because the algorithm is looking for the region where every category overlaps.
        for (var latIndex = 0; latIndex < totalLatSteps; latIndex++)
        {
            var latitude = bounds.MinLatitude + (latIndex * step);

            for (var lonIndex = 0; lonIndex < totalLonSteps; lonIndex++)
            {
                var longitude = bounds.MinLongitude + (lonIndex * step);
                var matchesAllCategories = true;

                foreach (var locations in locationsByCategory.Values)
                {
                    if (!locations.Any(location =>
                            GetDistanceMiles(latitude, longitude, location.Latitude, location.Longitude) <= radiusMiles))
                    {
                        matchesAllCategories = false;
                        break;
                    }
                }

                if (matchesAllCategories)
                {
                    validPoints.Add(new GridPoint(latIndex, lonIndex));
                }
            }
        }

        return validPoints;
    }

    private static List<GridPoint> ExtractBoundaryPoints(HashSet<GridPoint> validPoints)
    {
        var boundaryPoints = new List<GridPoint>();

        // A point is part of the outline when any direct neighbor falls outside the valid region.
        foreach (var point in validPoints)
        {
            if (!validPoints.Contains(new GridPoint(point.LatitudeIndex + 1, point.LongitudeIndex)) ||
                !validPoints.Contains(new GridPoint(point.LatitudeIndex - 1, point.LongitudeIndex)) ||
                !validPoints.Contains(new GridPoint(point.LatitudeIndex, point.LongitudeIndex + 1)) ||
                !validPoints.Contains(new GridPoint(point.LatitudeIndex, point.LongitudeIndex - 1)))
            {
                boundaryPoints.Add(point);
            }
        }

        boundaryPoints.Sort(static (left, right) =>
        {
            var latComparison = left.LatitudeIndex.CompareTo(right.LatitudeIndex);
            return latComparison != 0 ? latComparison : left.LongitudeIndex.CompareTo(right.LongitudeIndex);
        });

        return boundaryPoints;
    }

    private static string BuildKml(BoundingBox bounds, double step, IReadOnlyCollection<GridPoint> boundaryPoints)
    {
        var builder = new StringBuilder();
        builder.AppendLine("""<?xml version="1.0" encoding="UTF-8"?>""");
        builder.AppendLine("""<kml xmlns="http://www.opengis.net/kml/2.2">""");
        builder.AppendLine("<Document>");
        builder.AppendLine("""  <Style id="dot"><IconStyle><scale>0.4</scale><Icon><href>http://maps.google.com/mapfiles/kml/shapes/placemark_circle.png</href></Icon><color>ff0000ff</color></IconStyle></Style>""");

        foreach (var point in boundaryPoints)
        {
            var longitude = bounds.MinLongitude + (point.LongitudeIndex * step);
            var latitude = bounds.MinLatitude + (point.LatitudeIndex * step);

            builder.Append("    <Placemark><styleUrl>#dot</styleUrl><Point><coordinates>");
            builder.Append(longitude.ToString("G17", CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(latitude.ToString("G17", CultureInfo.InvariantCulture));
            builder.AppendLine(",0</coordinates></Point></Placemark>");
        }

        builder.AppendLine("</Document>");
        builder.AppendLine("</kml>");
        return builder.ToString();
    }

    private readonly record struct GridPoint(int LatitudeIndex, int LongitudeIndex);
}
