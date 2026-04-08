using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using KmlGenerator.Core.Models;
using KmlGenerator.Core.Services;
using KmlSuite.Shared.DependencyInjection;
using KmlSuite.Shared.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PlacesGatherer.Console.Models;

return await ArcGeometryExtractorProgram.RunAsync(args, Console.Out, Console.Error);

/// <summary>
/// Console signpost: this host extracts authoritative ARC park/trail geometry into normalized point records.
/// </summary>
public static class ArcGeometryExtractorProgram
{
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        var services = new ServiceCollection();
        services.AddKmlSuiteHostDiagnostics("arc-geometry-extractor");
        services.AddKmlSuiteTracing();
        services.AddTracedSingleton<IArcGeometryExtractorApp, ArcGeometryExtractorApp>();
        services.AddTracedSingleton<IArcSourceReader, ArcGeometryExtractorApp.ArcSourceReader>();
        services.AddTracedSingleton<IArcFeatureExtractor, ArcGeometryExtractorApp.ArcFeatureExtractor>();
        services.AddTracedSingleton<IArcOutputWriter, ArcGeometryExtractorApp.ArcOutputWriter>();

        await using var serviceProvider = services.BuildServiceProvider();
        return await serviceProvider.GetRequiredService<IArcGeometryExtractorApp>().RunAsync(args, output, error);
    }
}

internal sealed class ArcGeometryExtractorApp : IArcGeometryExtractorApp
{
    private const double DefaultMinimumParkSquareFeet = 800d;
    private const double DefaultMinimumTrailMiles = 1.65d;
    private const double DefaultMinimumCombinedParkTrailMiles = 1.65d;
    private readonly ILogger<ArcGeometryExtractorApp> _logger;
    private readonly IArcSourceReader _sourceReader;
    private readonly IArcFeatureExtractor _featureExtractor;
    private readonly IArcOutputWriter _outputWriter;

    public ArcGeometryExtractorApp(
        ILogger<ArcGeometryExtractorApp> logger,
        IArcSourceReader sourceReader,
        IArcFeatureExtractor featureExtractor,
        IArcOutputWriter outputWriter)
    {
        _logger = logger;
        _sourceReader = sourceReader;
        _featureExtractor = featureExtractor;
        _outputWriter = outputWriter;
    }

    public async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        var parsed = ParseArguments(args);
        if (parsed is null)
        {
            await error.WriteLineAsync("Usage: arc-geometry-extractor --input parks.kml --input trails.kmz --output points.jsonl [--park-output parks.jsonl] [--trail-output trails.jsonl] [--feature-output features.jsonl] [--geometry-output geometry.json] [--park-outline-kml-output parks.kml]");
            return 1;
        }

        try
        {
            var sourceDocuments = await _sourceReader.ReadAsync(parsed.InputPaths);
            var pointSpacingFeet = parsed.PointSpacingMiles * 5_280d;
            var extracted = _featureExtractor.Extract(
                sourceDocuments,
                pointSpacingFeet,
                parsed.MinimumParkSquareFeet,
                parsed.MinimumTrailMiles,
                parsed.MinimumCombinedParkTrailMiles);

            var allPoints = extracted.PointRecords.ToList();
            var parkPoints = allPoints
                .Where(record => record.Category.Equals("park", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var trailPoints = allPoints
                .Where(record => record.Category.Equals("trail", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var allFeatures = extracted.Features.ToList();

            await _outputWriter.WriteAsync(parsed, allPoints, parkPoints, trailPoints, allFeatures, extracted.GeometryFeatures, extracted.ParkPolygons, extracted.TrailLines);

            await output.WriteLineAsync($"Saved {allPoints.Count} ARC-derived points to {parsed.OutputPath}");
            return 0;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "ARC geometry extractor failed");
            await error.WriteLineAsync(exception.Message);
            return 1;
        }
    }

    private static ArcFeatureRecord BuildFeatureRecord(
        string sourceFileName,
        string featureName,
        string category,
        string geometryType,
        IReadOnlyDictionary<string, string> metadata,
        int pointCount) =>
        new()
        {
            SourceFile = sourceFileName,
            Name = featureName,
            Category = category,
            GeometryType = geometryType,
            PointCount = pointCount,
            Metadata = metadata
        };

    private static IReadOnlyList<NormalizedPlaceRecord> BuildPointRecords(
        IReadOnlyList<GeoPoint> points,
        string sourceFileName,
        string featureName,
        string category,
        string geometryType,
        IReadOnlyDictionary<string, string> metadata,
        int placemarkIndex)
    {
        var records = new List<NormalizedPlaceRecord>(points.Count);
        var query = string.IsNullOrWhiteSpace(featureName) ? sourceFileName : featureName;
        var projectType = GetMetadataValue(metadata, "Project_Type");
        var types = BuildTypes(category, geometryType, projectType);

        for (var pointIndex = 0; pointIndex < points.Count; pointIndex++)
        {
            var point = points[pointIndex];
            records.Add(new NormalizedPlaceRecord
            {
                Query = query,
                Category = category,
                PlaceId = BuildPlaceId(sourceFileName, placemarkIndex, geometryType, pointIndex),
                Name = query,
                FormattedAddress = BuildAddressHint(sourceFileName, metadata),
                Latitude = point.Latitude,
                Longitude = point.Longitude,
                Types = types,
                SourceQueryType = "arc",
                SearchNames = BuildSearchNames(featureName, metadata)
            });
        }

        return records;
    }

    private static GeometryFeatureInput BuildPointGeometryFeature(
        string featureName,
        string category,
        IReadOnlyList<GeoPoint> points) =>
        new()
        {
            Category = category,
            Label = featureName,
            GeometryType = "point",
            Points = points.Select(point => new CoordinateInput
            {
                Latitude = point.Latitude,
                Longitude = point.Longitude
            }).ToArray()
        };

    private static GeometryFeatureInput BuildLineGeometryFeature(
        string featureName,
        string category,
        IReadOnlyList<IReadOnlyList<GeoPoint>> lines) =>
        new()
        {
            Category = category,
            Label = featureName,
            GeometryType = "linestring",
            Lines = lines.Select(line => new LineStringInput
            {
                Coordinates = line.Select(point => new CoordinateInput
                {
                    Latitude = point.Latitude,
                    Longitude = point.Longitude
                }).ToArray()
            }).ToArray()
        };

    private static GeometryFeatureInput BuildPolygonGeometryFeature(
        string featureName,
        string category,
        IReadOnlyList<IReadOnlyList<IReadOnlyList<GeoPoint>>> polygons) =>
        new()
        {
            Category = category,
            Label = featureName,
            GeometryType = "polygon",
            Polygons = polygons.Select(polygon => new PolygonInput
            {
                OuterRing = polygon[0].Select(point => new CoordinateInput
                {
                    Latitude = point.Latitude,
                    Longitude = point.Longitude
                }).ToArray(),
                InnerRings = polygon.Skip(1)
                    .Select(ring => (IReadOnlyList<CoordinateInput>)ring.Select(point => new CoordinateInput
                    {
                        Latitude = point.Latitude,
                        Longitude = point.Longitude
                    }).ToArray())
                    .ToArray()
            }).ToArray()
        };

    private static IReadOnlyList<string> BuildSearchNames(string featureName, IReadOnlyDictionary<string, string> metadata)
    {
        var searchNames = new List<string>();
        if (!string.IsNullOrWhiteSpace(featureName))
        {
            searchNames.Add(featureName.Trim());
        }

        foreach (var key in PreferredNameKeys)
        {
            if (metadata.TryGetValue(key, out var value)
                && !string.IsNullOrWhiteSpace(value)
                && !value.Equals("Null", StringComparison.OrdinalIgnoreCase))
            {
                searchNames.Add(value.Trim());
            }
        }

        return searchNames
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string BuildAddressHint(string sourceFileName, IReadOnlyDictionary<string, string> metadata)
    {
        var parts = new List<string> { sourceFileName };
        var planName = GetMetadataValue(metadata, "Plan_");
        if (!string.IsNullOrWhiteSpace(planName))
        {
            parts.Add(planName);
        }

        return string.Join(" | ", parts);
    }

    private static IReadOnlyList<string> BuildTypes(string category, string geometryType, string? projectType)
    {
        var values = new List<string> { category, geometryType };
        if (!string.IsNullOrWhiteSpace(projectType))
        {
            values.AddRange(
                projectType
                    .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Select(value => value.Replace(' ', '_').ToLowerInvariant()));
        }

        return values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string BuildPlaceId(string sourceFileName, int placemarkIndex, string geometryType, int pointIndex)
    {
        var raw = $"{sourceFileName}|{placemarkIndex}|{geometryType}|{pointIndex}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes[..12]);
    }

    private static async Task<string> ReadSanitizedKmlAsync(string inputPath)
    {
        var extension = Path.GetExtension(inputPath);
        string rawText;

        if (extension.Equals(".kmz", StringComparison.OrdinalIgnoreCase))
        {
            using var stream = File.OpenRead(inputPath);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            var entry = archive.GetEntry("doc.kml") ?? throw new InvalidOperationException($"The KMZ '{inputPath}' does not contain doc.kml.");
            await using var entryStream = entry.Open();
            using var reader = new StreamReader(entryStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            rawText = await reader.ReadToEndAsync();
        }
        else
        {
            rawText = await File.ReadAllTextAsync(inputPath);
        }

        return SanitizeXml(rawText);
    }

    private static string SanitizeXml(string rawText)
    {
        var builder = new StringBuilder(rawText.Length);
        foreach (var character in rawText)
        {
            if (XmlConvertIsLegal(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static bool XmlConvertIsLegal(char character) =>
        character == 0x9
        || character == 0xA
        || character == 0xD
        || (character >= 0x20 && character <= 0xD7FF)
        || (character >= 0xE000 && character <= 0xFFFD);

    private static string ComputeFileIdentity(string inputPath)
    {
        var fileInfo = new FileInfo(inputPath);
        using var stream = File.OpenRead(inputPath);
        var bytes = SHA256.HashData(stream);
        return $"{fileInfo.Length}:{Convert.ToHexString(bytes)}";
    }

    private static IReadOnlyDictionary<string, string> ReadMetadata(ArcSourcePlacemark placemark)
    {
        var metadata = new Dictionary<string, string>(placemark.Metadata, StringComparer.OrdinalIgnoreCase);
        var description = placemark.Description.Trim();
        if (!string.IsNullOrWhiteSpace(description))
        {
            foreach (Match match in MetadataRowPattern.Matches(description))
            {
                var key = WebUtility.HtmlDecode(match.Groups["key"].Value).Trim();
                var value = WebUtility.HtmlDecode(match.Groups["value"].Value).Trim();
                if (!string.IsNullOrWhiteSpace(key))
                {
                    metadata[key] = value;
                }
            }
        }

        return metadata;
    }

    private static string ResolveFeatureName(string directName, IReadOnlyDictionary<string, string> metadata, string sourceFileName)
    {
        if (!string.IsNullOrWhiteSpace(directName))
        {
            return directName;
        }

        foreach (var key in PreferredNameKeys)
        {
            if (metadata.TryGetValue(key, out var value)
                && !string.IsNullOrWhiteSpace(value)
                && !value.Equals("Null", StringComparison.OrdinalIgnoreCase))
            {
                return value.Trim();
            }
        }

        return sourceFileName;
    }


    private static LineCategoryDecision DetermineLineCategory(string sourceFileName, string featureName, IReadOnlyDictionary<string, string> metadata)
    {
        var projectType = GetMetadataValue(metadata, "Project_Type");
        var normalizedSource = Normalize(sourceFileName);
        var normalizedFeatureName = Normalize($"{featureName} {GetMetadataValue(metadata, "Name")}");
        var hcClass = NormalizeMetadataValue(GetMetadataValue(metadata, "HC_Class"));
        var hcStatus = NormalizeMetadataValue(GetMetadataValue(metadata, "HC_Status"));
        var lcClass = NormalizeMetadataValue(GetMetadataValue(metadata, "LC_Class"));
        var lcStatus = NormalizeMetadataValue(GetMetadataValue(metadata, "LC_Status"));

        if (normalizedSource.Contains("trail plan inventory", StringComparison.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(projectType))
            {
                var normalizedProjectType = Normalize(projectType);
                if (BlockedTrailTypes.Contains(normalizedProjectType))
                {
                    return new LineCategoryDecision(null, $"blocked-project-type:{normalizedProjectType}");
                }

                if (AllowedTrailTypes.Contains(normalizedProjectType)
                    || normalizedProjectType.Contains("trail", StringComparison.Ordinal)
                    || normalizedProjectType.Contains("path", StringComparison.Ordinal)
                    || normalizedProjectType.Contains("greenway", StringComparison.Ordinal))
                {
                    return new LineCategoryDecision("trail", $"project-type:{normalizedProjectType}");
                }
            }

            return HasTrailSignals(normalizedFeatureName)
                ? new LineCategoryDecision("trail", "trail-plan-inventory-name-signal")
                : new LineCategoryDecision(null, "trail-plan-inventory-no-signal");
        }

        if (!string.IsNullOrWhiteSpace(projectType))
        {
            var normalizedProjectType = Normalize(projectType);
            if (BlockedTrailTypes.Contains(normalizedProjectType))
            {
                return new LineCategoryDecision(null, $"blocked-project-type:{normalizedProjectType}");
            }

            if (AllowedTrailTypes.Contains(normalizedProjectType)
                || normalizedProjectType.Contains("trail", StringComparison.Ordinal)
                || normalizedProjectType.Contains("path", StringComparison.Ordinal)
                || normalizedProjectType.Contains("greenway", StringComparison.Ordinal))
            {
                return new LineCategoryDecision("trail", $"project-type:{normalizedProjectType}");
            }
        }

        if (IsBlockedFacilityClass(hcClass) || IsBlockedFacilityClass(lcClass))
        {
            var blockedClass = !string.IsNullOrWhiteSpace(hcClass) && IsBlockedFacilityClass(hcClass)
                ? hcClass
                : lcClass;
            return new LineCategoryDecision(null, $"blocked-facility-class:{blockedClass}");
        }

        if (IsAuthoritativeOffStreetTrailFacility(hcClass, hcStatus, lcClass, lcStatus))
        {
            return new LineCategoryDecision("trail", "authoritative-off-street-metadata");
        }

        var composite = Normalize($"{sourceFileName} {featureName} {GetMetadataValue(metadata, "Name")}");

        if (HasTrailSignals(composite))
        {
            return new LineCategoryDecision("trail", "name-or-source-signal");
        }

        return sourceFileName.Contains("trail", StringComparison.OrdinalIgnoreCase)
               || sourceFileName.Contains("path", StringComparison.OrdinalIgnoreCase)
               || sourceFileName.Contains("beltline", StringComparison.OrdinalIgnoreCase)
            ? new LineCategoryDecision("trail", "source-file-signal")
            : new LineCategoryDecision(null, "no-supported-category-signal");
    }

    private static string? DeterminePointCategory(string sourceFileName, string featureName, IReadOnlyDictionary<string, string> metadata)
    {
        var normalizedSource = Normalize(sourceFileName);
        var normalizedFeatureName = Normalize(featureName);

        if (normalizedSource.Contains("marta", StringComparison.Ordinal)
            || normalizedFeatureName.Contains("station", StringComparison.Ordinal)
            || metadata.ContainsKey("STATION")
            || metadata.ContainsKey("Station_Code"))
        {
            return "transit";
        }

        if (normalizedSource.Contains("park", StringComparison.Ordinal))
        {
            return "park";
        }

        if (HasTrailSignals(normalizedSource) || HasTrailSignals(normalizedFeatureName))
        {
            return "trail";
        }

        return null;
    }

    private static bool HasTrailSignals(string value) =>
        value.Contains("trail", StringComparison.Ordinal)
        || value.Contains("greenway", StringComparison.Ordinal)
        || value.Contains("beltline", StringComparison.Ordinal)
        || value.Contains("path", StringComparison.Ordinal);

    private static bool IsAuthoritativeOffStreetTrailFacility(string? hcClass, string? hcStatus, string? lcClass, string? lcStatus)
    {
        var classValue = FirstNonEmpty(hcClass, lcClass);
        if (string.IsNullOrWhiteSpace(classValue))
        {
            return false;
        }

        if (!OffStreetTrailClasses.Contains(classValue))
        {
            return false;
        }

        var statusValue = FirstNonEmpty(hcStatus, lcStatus);
        return string.IsNullOrWhiteSpace(statusValue) || AllowedFacilityStatuses.Contains(statusValue);
    }

    private static bool IsBlockedFacilityClass(string? value) =>
        !string.IsNullOrWhiteSpace(value) && BlockedFacilityClasses.Contains(value);

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string? NormalizeMetadataValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("Null", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Normalize(value);
    }

    private readonly record struct LineCategoryDecision(string? Category, string Reason);

    private static IReadOnlyList<GeoPoint> DensifyPolygonBoundaryPoints(
        IReadOnlyList<IReadOnlyList<GeoPoint>> polygonRings,
        double pointSpacingFeet)
    {
        var points = new List<GeoPoint>();

        foreach (var ring in polygonRings)
        {
            if (ring.Count == 0)
            {
                continue;
            }

            for (var index = 0; index < ring.Count - 1; index++)
            {
                var start = ring[index];
                var end = ring[index + 1];
                points.Add(start);

                var segmentFeet = ComputeDistanceFeet(start, end);
                var extraPointCount = Math.Max(0, (int)Math.Floor(segmentFeet / pointSpacingFeet) - 1);
                for (var pointIndex = 1; pointIndex <= extraPointCount; pointIndex++)
                {
                    var fraction = (pointIndex * pointSpacingFeet) / segmentFeet;
                    points.Add(Interpolate(start, end, fraction));
                }
            }

            points.Add(ring[^1]);
        }

        return DeduplicatePoints(points);
    }

    private static IReadOnlyList<GeoPoint> DensifyPolylinePoints(IReadOnlyList<GeoPoint> linePoints, double pointSpacingFeet)
    {
        if (linePoints.Count == 0)
        {
            return Array.Empty<GeoPoint>();
        }

        var points = new List<GeoPoint>(linePoints.Count);
        for (var index = 0; index < linePoints.Count - 1; index++)
        {
            var start = linePoints[index];
            var end = linePoints[index + 1];
            points.Add(start);

            var segmentFeet = ComputeDistanceFeet(start, end);
            var extraPointCount = Math.Max(0, (int)Math.Floor(segmentFeet / pointSpacingFeet) - 1);
            for (var pointIndex = 1; pointIndex <= extraPointCount; pointIndex++)
            {
                var fraction = (pointIndex * pointSpacingFeet) / segmentFeet;
                points.Add(Interpolate(start, end, fraction));
            }
        }

        points.Add(linePoints[^1]);
        return DeduplicatePoints(points);
    }

    private static GeoPoint Interpolate(GeoPoint start, GeoPoint end, double fraction) =>
        new(
            start.Latitude + ((end.Latitude - start.Latitude) * fraction),
            start.Longitude + ((end.Longitude - start.Longitude) * fraction));

    private static IReadOnlyList<GeoPoint> DeduplicatePoints(IReadOnlyList<GeoPoint> points)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var deduped = new List<GeoPoint>(points.Count);
        foreach (var point in points)
        {
            var key = $"{Math.Round(point.Latitude, 7):F7}|{Math.Round(point.Longitude, 7):F7}";
            if (seen.Add(key))
            {
                deduped.Add(point);
            }
        }

        return deduped;
    }

    private static string? GetMetadataValue(IReadOnlyDictionary<string, string> metadata, string key) =>
        metadata.TryGetValue(key, out var value) ? value : null;

    private const double MaximumCorridorPolygonWidthFeet = 250d;
    private const double MinimumCorridorPolygonAspectRatio = 8d;

    private static bool ShouldKeepParkFeature(
        double parkSquareFeet,
        double polygonPerimeterFeet,
        double minimumParkSquareFeet,
        double minimumTrailMiles)
    {
        if (parkSquareFeet >= minimumParkSquareFeet)
        {
            return true;
        }

        return IsLinearCorridorPolygon(parkSquareFeet, polygonPerimeterFeet, minimumTrailMiles);
    }

    private static bool ShouldKeepTrailFeature(IReadOnlyList<GeoPoint> linePoints, double minimumTrailMiles)
    {
        var minimumTrailFeet = minimumTrailMiles * 5_280d;
        return ComputePolylineLengthFeet(linePoints) >= minimumTrailFeet;
    }

    private static bool TryParseDouble(string? value, out double parsed)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("Null", StringComparison.OrdinalIgnoreCase))
        {
            parsed = 0;
            return false;
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed);
    }

    private static double ComputePolylineLengthFeet(IReadOnlyList<GeoPoint> points)
    {
        var totalFeet = 0d;
        for (var index = 1; index < points.Count; index++)
        {
            totalFeet += ComputeDistanceFeet(points[index - 1], points[index]);
        }

        return totalFeet;
    }

    private static double ComputePolygonPerimeterFeet(IReadOnlyList<IReadOnlyList<GeoPoint>> rings)
    {
        var totalFeet = 0d;
        foreach (var ring in rings)
        {
            totalFeet += ComputePolylineLengthFeet(ring);
        }

        return totalFeet;
    }

    private static double ComputePolygonAreaSquareFeet(IReadOnlyList<IReadOnlyList<GeoPoint>> rings)
    {
        var totalArea = 0d;
        foreach (var ring in rings)
        {
            totalArea += ComputeRingAreaSquareFeet(ring);
        }

        return totalArea;
    }

    private static double ComputeRingAreaSquareFeet(IReadOnlyList<GeoPoint> ring)
    {
        if (ring.Count < 3)
        {
            return 0d;
        }

        var referenceLatitudeRadians = DegreesToRadians(ring.Average(point => point.Latitude));
        var projected = ring
            .Select(point => ProjectToLocalFeet(point, referenceLatitudeRadians))
            .ToArray();

        var doubledArea = 0d;
        for (var index = 0; index < projected.Length; index++)
        {
            var current = projected[index];
            var next = projected[(index + 1) % projected.Length];
            doubledArea += (current.X * next.Y) - (next.X * current.Y);
        }

        return Math.Abs(doubledArea) / 2d;
    }

    private static bool IsLinearCorridorPolygon(double polygonAreaSquareFeet, double polygonPerimeterFeet, double minimumTrailMiles)
    {
        if (polygonAreaSquareFeet <= 0d || polygonPerimeterFeet <= 0d)
        {
            return false;
        }

        var (estimatedLengthFeet, estimatedWidthFeet) = EstimateRectangleDimensions(polygonAreaSquareFeet, polygonPerimeterFeet);
        if (estimatedLengthFeet <= 0d || estimatedWidthFeet <= 0d)
        {
            return false;
        }

        var minimumTrailFeet = minimumTrailMiles * 5_280d;
        var aspectRatio = estimatedLengthFeet / estimatedWidthFeet;

        return estimatedLengthFeet >= minimumTrailFeet
            && estimatedWidthFeet <= MaximumCorridorPolygonWidthFeet
            && aspectRatio >= MinimumCorridorPolygonAspectRatio;
    }

    private static (double LengthFeet, double WidthFeet) EstimateRectangleDimensions(double areaSquareFeet, double perimeterFeet)
    {
        var discriminant = Math.Max(0d, (perimeterFeet * perimeterFeet) - (16d * areaSquareFeet));
        var root = Math.Sqrt(discriminant);
        var first = (perimeterFeet + root) / 4d;
        var second = (perimeterFeet - root) / 4d;

        return first >= second ? (first, second) : (second, first);
    }

    private static ProjectedPoint ProjectToLocalFeet(GeoPoint point, double referenceLatitudeRadians)
    {
        const double earthRadiusFeet = 20_925_524.9d;
        var latitudeRadians = DegreesToRadians(point.Latitude);
        var longitudeRadians = DegreesToRadians(point.Longitude);

        return new ProjectedPoint(
            earthRadiusFeet * longitudeRadians * Math.Cos(referenceLatitudeRadians),
            earthRadiusFeet * latitudeRadians);
    }

    private static double ComputeDistanceFeet(GeoPoint start, GeoPoint end)
    {
        const double earthRadiusFeet = 20_925_524.9d;
        var latitudeDelta = DegreesToRadians(end.Latitude - start.Latitude);
        var longitudeDelta = DegreesToRadians(end.Longitude - start.Longitude);
        var startLatitude = DegreesToRadians(start.Latitude);
        var endLatitude = DegreesToRadians(end.Latitude);

        var sinLatitude = Math.Sin(latitudeDelta / 2d);
        var sinLongitude = Math.Sin(longitudeDelta / 2d);
        var a = (sinLatitude * sinLatitude)
                + (Math.Cos(startLatitude) * Math.Cos(endLatitude) * sinLongitude * sinLongitude);
        var c = 2d * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1d - a));
        return earthRadiusFeet * c;
    }

    private static double DegreesToRadians(double degrees) =>
        degrees * (Math.PI / 180d);

    private static string Normalize(string value) =>
        value
            .Replace('-', ' ')
            .Replace('_', ' ')
            .Trim()
            .ToLowerInvariant();

    private static async Task WriteJsonLinesAsync<TRecord>(string outputPath, IReadOnlyList<TRecord> records)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var lines = records.Select(record => JsonSerializer.Serialize(record, JsonOptions));
        await File.WriteAllLinesAsync(outputPath, lines);
    }

    private static async Task WriteOriginalGeometryKmlAsync(
        string outputPath,
        IReadOnlyList<ParkPolygonRecord> parkPolygons,
        IReadOnlyList<TrailLineRecord> trailLines)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var builder = new StringBuilder();
        builder.AppendLine("""<?xml version="1.0" encoding="UTF-8"?>""");
        builder.AppendLine("""<kml xmlns="http://www.opengis.net/kml/2.2">""");
        builder.AppendLine("  <Document>");
        builder.AppendLine("    <name>ARC Original Geometry</name>");
        builder.AppendLine("""    <Style id="park-outline"><LineStyle><color>ffddaa00</color><width>2</width></LineStyle><PolyStyle><color>33ddaa00</color></PolyStyle></Style>""");
        builder.AppendLine("""    <Style id="trail-line"><LineStyle><color>ff00aaee</color><width>3</width></LineStyle></Style>""");

        foreach (var park in parkPolygons.OrderBy(record => record.Name, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"    <Folder><name>{SecurityElement.Escape(park.Name)}</name>");
            foreach (var ringSet in park.Rings)
            {
                builder.AppendLine($"      <Placemark><name>{SecurityElement.Escape(park.Name)}</name><styleUrl>#park-outline</styleUrl><Polygon><outerBoundaryIs><LinearRing><coordinates>");
                foreach (var ring in ringSet)
                {
                    foreach (var point in ring)
                    {
                        builder.AppendLine($"{point.Longitude.ToString(CultureInfo.InvariantCulture)},{point.Latitude.ToString(CultureInfo.InvariantCulture)},0");
                    }
                }

                builder.AppendLine("      </coordinates></LinearRing></outerBoundaryIs></Polygon></Placemark>");
            }

            builder.AppendLine("    </Folder>");
        }

        foreach (var trail in trailLines.OrderBy(record => record.Name, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"    <Placemark><name>{SecurityElement.Escape(trail.Name)}</name><styleUrl>#trail-line</styleUrl><LineString><coordinates>");
            foreach (var point in trail.Points)
            {
                builder.AppendLine($"{point.Longitude.ToString(CultureInfo.InvariantCulture)},{point.Latitude.ToString(CultureInfo.InvariantCulture)},0");
            }

            builder.AppendLine("    </coordinates></LineString></Placemark>");
        }

        builder.AppendLine("  </Document>");
        builder.AppendLine("</kml>");

        await File.WriteAllTextAsync(outputPath, builder.ToString());
    }

    private ExtractorArguments? ParseArguments(IReadOnlyList<string> args)
    {
        var inputPaths = new List<string>();
        string? outputPath = null;
        string? parkOutputPath = null;
        string? trailOutputPath = null;
        string? featureOutputPath = null;
        string? geometryOutputPath = null;
        string? originalGeometryKmlOutputPath = null;
        var minimumParkSquareFeet = DefaultMinimumParkSquareFeet;
        var minimumTrailMiles = DefaultMinimumTrailMiles;
        var minimumCombinedParkTrailMiles = DefaultMinimumCombinedParkTrailMiles;
        var pointSpacingMiles = DefaultPointSpacingMiles;

        for (var index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--input" when index + 1 < args.Count:
                    inputPaths.Add(args[++index]);
                    break;
                case "--output" when index + 1 < args.Count:
                    outputPath = args[++index];
                    break;
                case "--park-output" when index + 1 < args.Count:
                    parkOutputPath = args[++index];
                    break;
                case "--trail-output" when index + 1 < args.Count:
                    trailOutputPath = args[++index];
                    break;
                case "--feature-output" when index + 1 < args.Count:
                    featureOutputPath = args[++index];
                    break;
                case "--geometry-output" when index + 1 < args.Count:
                    geometryOutputPath = args[++index];
                    break;
                case "--original-geometry-kml-output" when index + 1 < args.Count:
                    originalGeometryKmlOutputPath = args[++index];
                    break;
                case "--minimum-park-square-feet" when index + 1 < args.Count:
                    minimumParkSquareFeet = double.Parse(args[++index], CultureInfo.InvariantCulture);
                    break;
                case "--minimum-trail-miles" when index + 1 < args.Count:
                    minimumTrailMiles = double.Parse(args[++index], CultureInfo.InvariantCulture);
                    break;
                case "--minimum-combined-park-trail-miles" when index + 1 < args.Count:
                    minimumCombinedParkTrailMiles = double.Parse(args[++index], CultureInfo.InvariantCulture);
                    break;
                case "--point-spacing-miles" when index + 1 < args.Count:
                    pointSpacingMiles = double.Parse(args[++index], CultureInfo.InvariantCulture);
                    break;
            }
        }

        return inputPaths.Count == 0 || string.IsNullOrWhiteSpace(outputPath)
            ? null
            : new ExtractorArguments(
                inputPaths,
                outputPath,
                parkOutputPath,
                trailOutputPath,
                featureOutputPath,
                geometryOutputPath,
                originalGeometryKmlOutputPath,
                minimumParkSquareFeet,
                minimumTrailMiles,
                minimumCombinedParkTrailMiles,
                pointSpacingMiles);
    }

    private static readonly Regex MetadataRowPattern = new(
        @"<tr>\s*<td>(?<key>.*?)</td>\s*<td>(?<value>.*?)</td>\s*</tr>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private const double SquareFeetPerAcre = 43_560d;
    private const double DefaultPointSpacingMiles = 0.5d;
    private const double FeetPerDegreeLatitude = 364_000d;

    private static readonly HashSet<string> AllowedTrailTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "multi use trail",
        "multi use path",
        "mixed use path",
        "trail",
        "greenway",
        "off road pedestrian trail",
        "bike, sw, trail",
        "bike, diet, trail",
        "sw, trail"
    };

    private static readonly HashSet<string> BlockedTrailTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "on street bicycle facility",
        "on street bicycle network",
        "on street facility",
        "sidewalk",
        "sidewalk segment"
    };

    private static readonly HashSet<string> OffStreetTrailClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "off street",
        "off-street",
        "multi use trail",
        "multi use path",
        "mixed use path",
        "greenway"
    };

    private static readonly HashSet<string> BlockedFacilityClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "on street",
        "on-street",
        "on street bicycle facility",
        "on street bicycle network",
        "on street facility",
        "sidewalk",
        "sidewalk segment"
    };

    private static readonly HashSet<string> AllowedFacilityStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "existing",
        "programmed",
        "proposed"
    };

    private static readonly string[] PreferredNameKeys =
    [
        "AllParks_Fields_Park_Name",
        "Atlanta__GA_ServiceAreas_Park_N",
        "Park_Name",
        "PARK_NAME",
        "Name",
        "LABEL",
        "STATION",
        "Station",
        "Trail_Name"
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    internal sealed record ExtractorArguments(
        IReadOnlyList<string> InputPaths,
        string OutputPath,
        string? ParkOutputPath,
        string? TrailOutputPath,
        string? FeatureOutputPath,
        string? GeometryOutputPath,
        string? OriginalGeometryKmlOutputPath,
        double MinimumParkSquareFeet,
        double MinimumTrailMiles,
        double MinimumCombinedParkTrailMiles,
        double PointSpacingMiles);

    internal sealed record GeoPoint(double Latitude, double Longitude);
    internal sealed record ProjectedPoint(double X, double Y);
    internal sealed record ParkPolygonRecord(
        string SourceFile,
        string Name,
        IReadOnlyDictionary<string, string> Metadata,
        IReadOnlyList<IReadOnlyList<IReadOnlyList<GeoPoint>>> Rings);
    internal sealed record TrailLineRecord(
        string SourceFile,
        string Name,
        IReadOnlyDictionary<string, string> Metadata,
        IReadOnlyList<GeoPoint> Points);

    internal sealed record class ArcFeatureRecord
    {
        public string SourceFile { get; init; } = string.Empty;

        public string Name { get; init; } = string.Empty;

        public string Category { get; init; } = string.Empty;

        public string GeometryType { get; init; } = string.Empty;

        public int PointCount { get; init; }

        public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();

        public IReadOnlyList<string> SearchNames { get; init; } = Array.Empty<string>();
    }

    internal sealed class ArcSourceReader : IArcSourceReader
    {
        private readonly ILogger<ArcSourceReader> _logger;

        public ArcSourceReader(ILogger<ArcSourceReader> logger)
        {
            _logger = logger;
        }

        public async Task<IReadOnlyList<ArcSourceDocument>> ReadAsync(IReadOnlyList<string> inputPaths)
        {
            var processedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sourceDocuments = new List<ArcSourceDocument>(inputPaths.Count);

            foreach (var inputPath in inputPaths)
            {
                var fileIdentity = ComputeFileIdentity(inputPath);
                if (!processedFiles.Add(fileIdentity))
                {
                    _logger.LogDebug("Skipping duplicate ARC input {InputPath}", inputPath);
                    continue;
                }

                var sourceFileName = Path.GetFileName(inputPath);
                var sanitizedKml = await ReadSanitizedKmlAsync(inputPath);
                var nativeResult = NativeGeometryLibrary.ReadKmlText(sanitizedKml);
                var placemarks = nativeResult.Placemarks
                    .Select(placemark => new ArcSourcePlacemark(
                        placemark.Name,
                        placemark.Description,
                        placemark.Metadata,
                        placemark.Points,
                        placemark.Lines.Select(line => (IReadOnlyList<CoordinateInput>)line.Coordinates.ToArray()).ToArray(),
                        placemark.Polygons.Select(polygon =>
                        {
                            var rings = new List<IReadOnlyList<CoordinateInput>>();
                            rings.Add(polygon.OuterRing.ToArray());
                            foreach (var innerRing in polygon.InnerRings)
                            {
                                rings.Add(innerRing.ToArray());
                            }

                            return (IReadOnlyList<IReadOnlyList<CoordinateInput>>)rings;
                        }).ToArray()))
                    .ToArray();
                sourceDocuments.Add(new ArcSourceDocument(sourceFileName, placemarks));
                _logger.LogInformation("Loaded {PlacemarkCount} ARC placemarks from {InputPath}", placemarks.Length, inputPath);
            }

            _logger.LogInformation("Prepared {SourceCount} distinct ARC source documents", sourceDocuments.Count);
            return sourceDocuments;
        }
    }

    internal sealed class ArcFeatureExtractor : IArcFeatureExtractor
    {
        private readonly ILogger<ArcFeatureExtractor> _logger;

        public ArcFeatureExtractor(ILogger<ArcFeatureExtractor> logger)
        {
            _logger = logger;
        }

        public ArcExtractionStageResult Extract(
            IReadOnlyList<ArcSourceDocument> sourceDocuments,
            double pointSpacingFeet,
            double minimumParkSquareFeet,
            double minimumTrailMiles,
            double minimumCombinedParkTrailMiles)
        {
            var directPointRecords = new List<NormalizedPlaceRecord>();
            var features = new List<ArcFeatureRecord>();
            var geometryFeatures = new List<GeometryFeatureInput>();
            var parkPolygons = new List<ParkPolygonRecord>();
            var trailLines = new List<TrailLineRecord>();

            _logger.LogInformation(
                "Starting ARC extraction across {SourceCount} source documents. pointSpacingFeet={PointSpacingFeet} minimumParkSquareFeet={MinimumParkSquareFeet} minimumTrailMiles={MinimumTrailMiles} minimumCombinedParkTrailMiles={MinimumCombinedParkTrailMiles}",
                sourceDocuments.Count,
                pointSpacingFeet,
                minimumParkSquareFeet,
                minimumTrailMiles,
                minimumCombinedParkTrailMiles);

            foreach (var sourceDocument in sourceDocuments)
            {
                var sourceStartFeatureCount = features.Count;
                var sourceStartGeometryCount = geometryFeatures.Count;
                var sourceStartPointCount = directPointRecords.Count;

                for (var placemarkIndex = 0; placemarkIndex < sourceDocument.Placemarks.Count; placemarkIndex++)
                {
                    var placemark = sourceDocument.Placemarks[placemarkIndex];
                    var metadata = ReadMetadata(placemark);
                    var featureName = ResolveFeatureName(placemark.Name, metadata, sourceDocument.SourceFileName);

                    if (placemark.Polygons.Count > 0)
                    {
                        var polygonPoints = placemark.Polygons
                            .Where(rings => rings.Count > 0)
                            .Select(rings => (IReadOnlyList<IReadOnlyList<GeoPoint>>)rings
                                .Select(ring => (IReadOnlyList<GeoPoint>)ring.Select(point => new GeoPoint(point.Latitude, point.Longitude)).ToArray())
                                .ToArray())
                            .ToArray();
                        if (polygonPoints.Length > 0)
                        {
                            var polygonAreaSquareFeet = polygonPoints.Sum(ComputePolygonAreaSquareFeet);
                            var polygonPerimeterFeet = polygonPoints.Sum(ComputePolygonPerimeterFeet);
                            if (!ShouldKeepParkFeature(
                                polygonAreaSquareFeet,
                                polygonPerimeterFeet,
                                minimumParkSquareFeet,
                                minimumTrailMiles))
                            {
                                _logger.LogDebug(
                                    "Dropping park polygon {FeatureName} from {SourceFile} because it did not meet size thresholds. areaSquareFeet={AreaSquareFeet} perimeterFeet={PerimeterFeet}",
                                    featureName,
                                    sourceDocument.SourceFileName,
                                    polygonAreaSquareFeet,
                                    polygonPerimeterFeet);
                                continue;
                            }

                            _logger.LogDebug(
                                "Keeping park polygon {FeatureName} from {SourceFile}. areaSquareFeet={AreaSquareFeet} perimeterFeet={PerimeterFeet}",
                                featureName,
                                sourceDocument.SourceFileName,
                                polygonAreaSquareFeet,
                                polygonPerimeterFeet);
                            parkPolygons.Add(new ParkPolygonRecord(sourceDocument.SourceFileName, featureName, metadata, polygonPoints));
                            geometryFeatures.Add(BuildPolygonGeometryFeature(featureName, "park", polygonPoints));
                            var outlinePoints = polygonPoints.SelectMany(rings => DensifyPolygonBoundaryPoints(rings, pointSpacingFeet)).ToArray();
                            if (outlinePoints.Length > 0)
                            {
                                features.Add(BuildFeatureRecord(sourceDocument.SourceFileName, featureName, "park", "polygon", metadata, polygonPoints.Sum(rings => rings.Sum(ring => ring.Count))));
                                directPointRecords.AddRange(BuildPointRecords(outlinePoints, sourceDocument.SourceFileName, featureName, "park", "polygon-densified-edge", metadata, placemarkIndex));
                            }
                        }
                    }

                    if (placemark.Points.Count > 0)
                    {
                        var pointCategory = DeterminePointCategory(sourceDocument.SourceFileName, featureName, metadata);
                        if (pointCategory is not null)
                        {
                            var pointValues = placemark.Points
                                .Select(point => new GeoPoint(point.Latitude, point.Longitude))
                                .ToArray();
                            if (pointValues.Length > 0)
                            {
                                if (pointCategory.Equals("park", StringComparison.OrdinalIgnoreCase)
                                    || pointCategory.Equals("trail", StringComparison.OrdinalIgnoreCase))
                                {
                                    _logger.LogDebug(
                                        "Skipping point placemark {FeatureName} from {SourceFile} because category {Category} is geometry-only",
                                        featureName,
                                        sourceDocument.SourceFileName,
                                        pointCategory);
                                    continue;
                                }

                                features.Add(BuildFeatureRecord(sourceDocument.SourceFileName, featureName, pointCategory, "point", metadata, pointValues.Length));
                                if (!pointCategory.Equals("park", StringComparison.OrdinalIgnoreCase)
                                    && !pointCategory.Equals("trail", StringComparison.OrdinalIgnoreCase))
                                {
                                    geometryFeatures.Add(BuildPointGeometryFeature(featureName, pointCategory, pointValues));
                                }
                                var records = BuildPointRecords(pointValues, sourceDocument.SourceFileName, featureName, pointCategory, "point", metadata, placemarkIndex);
                                directPointRecords.AddRange(records);
                            }
                        }
                    }

                    if (placemark.Lines.Count == 0)
                    {
                        continue;
                    }

                    var lineCategoryDecision = DetermineLineCategory(sourceDocument.SourceFileName, featureName, metadata);
                    if (lineCategoryDecision.Category is null)
                    {
                        _logger.LogDebug(
                            "Skipping line placemark {FeatureName} from {SourceFile}. reason={Reason} projectType={ProjectType} hcClass={HcClass} hcStatus={HcStatus} lcClass={LcClass} lcStatus={LcStatus}",
                            featureName,
                            sourceDocument.SourceFileName,
                            lineCategoryDecision.Reason,
                            GetMetadataValue(metadata, "Project_Type") ?? "<null>",
                            GetMetadataValue(metadata, "HC_Class") ?? "<null>",
                            GetMetadataValue(metadata, "HC_Status") ?? "<null>",
                            GetMetadataValue(metadata, "LC_Class") ?? "<null>",
                            GetMetadataValue(metadata, "LC_Status") ?? "<null>");
                        continue;
                    }

                    var category = lineCategoryDecision.Category;

                    var linePoints = placemark.Lines
                        .SelectMany(line => line)
                        .Select(point => new GeoPoint(point.Latitude, point.Longitude))
                        .ToArray();
                    if (linePoints.Length == 0)
                    {
                        _logger.LogDebug("Skipping empty line placemark {FeatureName} from {SourceFile}", featureName, sourceDocument.SourceFileName);
                        continue;
                    }

                    if (category.Equals("park", StringComparison.OrdinalIgnoreCase)
                        || (category.Equals("trail", StringComparison.OrdinalIgnoreCase)
                            && !ShouldKeepTrailFeature(linePoints, minimumTrailMiles)))
                    {
                        var trailLengthFeet = ComputePolylineLengthFeet(linePoints);
                        _logger.LogDebug(
                            "Dropping line placemark {FeatureName} from {SourceFile}. category={Category} lengthFeet={LengthFeet} minimumTrailFeet={MinimumTrailFeet}",
                            featureName,
                            sourceDocument.SourceFileName,
                            category,
                            trailLengthFeet,
                            minimumTrailMiles * 5_280d);
                        continue;
                    }

                    _logger.LogDebug(
                        "Keeping line placemark {FeatureName} from {SourceFile}. category={Category} reason={Reason} pointCount={PointCount} projectType={ProjectType} hcClass={HcClass} hcStatus={HcStatus} lcClass={LcClass} lcStatus={LcStatus}",
                        featureName,
                        sourceDocument.SourceFileName,
                        category,
                        lineCategoryDecision.Reason,
                        linePoints.Length,
                        GetMetadataValue(metadata, "Project_Type") ?? "<null>",
                        GetMetadataValue(metadata, "HC_Class") ?? "<null>",
                        GetMetadataValue(metadata, "HC_Status") ?? "<null>",
                        GetMetadataValue(metadata, "LC_Class") ?? "<null>",
                        GetMetadataValue(metadata, "LC_Status") ?? "<null>");
                    features.Add(BuildFeatureRecord(sourceDocument.SourceFileName, featureName, category, "line", metadata, linePoints.Length));
                    trailLines.Add(new TrailLineRecord(sourceDocument.SourceFileName, featureName, metadata, linePoints));
                    var geometryLines = placemark.Lines
                        .Where(points => points.Count > 1)
                        .Select(points => points.Select(point => new GeoPoint(point.Latitude, point.Longitude)).ToArray())
                        .ToArray();
                    geometryFeatures.Add(BuildLineGeometryFeature(featureName, category, geometryLines));
                    directPointRecords.AddRange(BuildPointRecords(
                        DensifyPolylinePoints(linePoints, pointSpacingFeet),
                        sourceDocument.SourceFileName,
                        featureName,
                        category,
                        "line",
                        metadata,
                        placemarkIndex));
                }

                _logger.LogInformation(
                    "Extracted ARC features from {SourceFile}: pointRecords={PointRecordCount} features={FeatureCount} geometryFeatures={GeometryFeatureCount}",
                    sourceDocument.SourceFileName,
                    directPointRecords.Count - sourceStartPointCount,
                    features.Count - sourceStartFeatureCount,
                    geometryFeatures.Count - sourceStartGeometryCount);
            }

            _logger.LogInformation(
                "Completed ARC extraction. pointRecords={PointRecordCount} features={FeatureCount} geometryFeatures={GeometryFeatureCount} parkPolygons={ParkPolygonCount} trailLines={TrailLineCount}",
                directPointRecords.Count,
                features.Count,
                geometryFeatures.Count,
                parkPolygons.Count,
                trailLines.Count);
            return new ArcExtractionStageResult(directPointRecords, features, geometryFeatures, parkPolygons, trailLines);
        }
    }

    internal sealed class ArcOutputWriter : IArcOutputWriter
    {
        private readonly ILogger<ArcOutputWriter> _logger;

        public ArcOutputWriter(ILogger<ArcOutputWriter> logger)
        {
            _logger = logger;
        }

        public async Task WriteAsync(
            ExtractorArguments arguments,
            IReadOnlyList<NormalizedPlaceRecord> allPoints,
            IReadOnlyList<NormalizedPlaceRecord> parkPoints,
            IReadOnlyList<NormalizedPlaceRecord> trailPoints,
            IReadOnlyList<ArcFeatureRecord> features,
            IReadOnlyList<GeometryFeatureInput> geometryFeatures,
            IReadOnlyList<ParkPolygonRecord> parkPolygons,
            IReadOnlyList<TrailLineRecord> trailLines)
        {
            _logger.LogInformation(
                "Writing ARC outputs. allPoints={AllPointCount} parkPoints={ParkPointCount} trailPoints={TrailPointCount} features={FeatureCount} geometryFeatures={GeometryFeatureCount}",
                allPoints.Count,
                parkPoints.Count,
                trailPoints.Count,
                features.Count,
                geometryFeatures.Count);
            await WriteJsonLinesAsync(arguments.OutputPath, allPoints);

            if (!string.IsNullOrWhiteSpace(arguments.ParkOutputPath))
            {
                await WriteJsonLinesAsync(arguments.ParkOutputPath, parkPoints);
            }

            if (!string.IsNullOrWhiteSpace(arguments.TrailOutputPath))
            {
                await WriteJsonLinesAsync(arguments.TrailOutputPath, trailPoints);
            }

            if (!string.IsNullOrWhiteSpace(arguments.FeatureOutputPath))
            {
                await WriteJsonLinesAsync(arguments.FeatureOutputPath, features);
            }

            if (!string.IsNullOrWhiteSpace(arguments.GeometryOutputPath))
            {
                await File.WriteAllTextAsync(arguments.GeometryOutputPath, JsonSerializer.Serialize(new GenerateKmlRequest
                {
                    Features = geometryFeatures
                }, JsonOptions));
            }

            if (!string.IsNullOrWhiteSpace(arguments.OriginalGeometryKmlOutputPath))
            {
                await WriteOriginalGeometryKmlAsync(arguments.OriginalGeometryKmlOutputPath, parkPolygons, trailLines);
            }

            _logger.LogInformation("Finished writing ARC outputs to primary path {OutputPath}", arguments.OutputPath);
        }
    }
}
