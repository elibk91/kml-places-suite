using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using KmlSuite.Shared.DependencyInjection;
using KmlSuite.Shared.Diagnostics;
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
        services.AddLogging(builder =>
        {
            builder.AddSimpleConsole(console =>
            {
                console.TimestampFormat = "HH:mm:ss.fff ";
                console.SingleLine = true;
            });
            builder.SetMinimumLevel(LogLevel.Trace);
        });
        services.AddKmlSuiteTracing();
        services.AddTracedSingleton<IArcGeometryExtractorApp, ArcGeometryExtractorApp>();

        await using var serviceProvider = services.BuildServiceProvider();
        return await serviceProvider.GetRequiredService<IArcGeometryExtractorApp>().RunAsync(args, output, error);
    }
}

public sealed class ArcGeometryExtractorApp : IArcGeometryExtractorApp
{
    private readonly ILogger<ArcGeometryExtractorApp> _logger;

    public ArcGeometryExtractorApp(ILogger<ArcGeometryExtractorApp> logger)
    {
        _logger = logger;
    }

    public async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        using var _ = MethodTrace.Enter(_logger, nameof(ArcGeometryExtractorApp), new Dictionary<string, object?> { ["ArgumentCount"] = args.Length });
        var parsed = ParseArguments(args);
        if (parsed is null)
        {
            await error.WriteLineAsync("Usage: arc-geometry-extractor --input parks.kml --input trails.kmz --output points.jsonl [--park-output parks.jsonl] [--trail-output trails.jsonl] [--feature-output features.jsonl] [--park-outline-kml-output parks.kml]");
            return 1;
        }

        try
        {
            var processedHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var allPoints = new List<NormalizedPlaceRecord>();
            var parkPoints = new List<NormalizedPlaceRecord>();
            var trailPoints = new List<NormalizedPlaceRecord>();
            var features = new List<ArcFeatureRecord>();
            var parkPolygons = new List<ParkPolygonRecord>();

            foreach (var inputPath in parsed.Value.InputPaths)
            {
                var hash = ComputeFileHash(inputPath);
                if (!processedHashes.Add(hash))
                {
                    continue;
                }

                var sourceFileName = Path.GetFileName(inputPath);
                var document = XDocument.Parse(await ReadSanitizedKmlAsync(inputPath), LoadOptions.None);
                var placemarks = document
                    .Descendants()
                    .Where(element => element.Name.LocalName.Equals("Placemark", StringComparison.Ordinal))
                    .ToArray();

                for (var placemarkIndex = 0; placemarkIndex < placemarks.Length; placemarkIndex++)
                {
                    var placemark = placemarks[placemarkIndex];
                    var metadata = ReadMetadata(placemark);
                    var featureName = ResolveFeatureName(ReadTrimmedChildValue(placemark, "name"), metadata, sourceFileName);

                    var lineStrings = placemark
                        .Descendants()
                        .Where(element => element.Name.LocalName.Equals("LineString", StringComparison.Ordinal))
                        .ToArray();

                    var polygons = placemark
                        .Descendants()
                        .Where(element => element.Name.LocalName.Equals("Polygon", StringComparison.Ordinal))
                        .ToArray();

                    var points = placemark
                        .Descendants()
                        .Where(element => element.Name.LocalName.Equals("Point", StringComparison.Ordinal))
                        .ToArray();

                    if (polygons.Length > 0)
                    {
                        if (!ShouldKeepParkFeature(metadata))
                        {
                            continue;
                        }

                        var polygonPoints = polygons
                            .Select(ParsePolygonRings)
                            .Where(rings => rings.Count > 0)
                            .ToArray();

                        if (polygonPoints.Length > 0)
                        {
                            parkPolygons.Add(new ParkPolygonRecord(sourceFileName, featureName, metadata, polygonPoints));

                            var outlinePoints = polygonPoints
                                .SelectMany(DensifyPolygonBoundaryPoints)
                                .ToArray();

                            if (outlinePoints.Length == 0)
                            {
                                continue;
                            }

                            var sourceVertexCount = polygonPoints.Sum(rings => rings.Sum(ring => ring.Count));
                            features.Add(BuildFeatureRecord(sourceFileName, featureName, "park", "polygon", metadata, sourceVertexCount));

                            var records = BuildPointRecords(
                                outlinePoints,
                                sourceFileName,
                                featureName,
                                "park",
                                "polygon-densified-edge",
                                metadata,
                                placemarkIndex);

                            parkPoints.AddRange(records);
                            allPoints.AddRange(records);
                        }
                    }

                    if (points.Length > 0)
                    {
                        var pointCategory = DeterminePointCategory(sourceFileName, featureName, metadata);
                        if (pointCategory is not null)
                        {
                            var pointValues = points
                                .SelectMany(ParsePointValues)
                                .ToArray();

                            if (pointValues.Length > 0)
                            {
                                features.Add(BuildFeatureRecord(sourceFileName, featureName, pointCategory, "point", metadata, pointValues.Length));

                                var records = BuildPointRecords(
                                    pointValues,
                                    sourceFileName,
                                    featureName,
                                    pointCategory,
                                    "point",
                                    metadata,
                                    placemarkIndex);

                                if (pointCategory.Equals("park", StringComparison.OrdinalIgnoreCase))
                                {
                                    parkPoints.AddRange(records);
                                }
                                else if (pointCategory.Equals("trail", StringComparison.OrdinalIgnoreCase))
                                {
                                    trailPoints.AddRange(records);
                                }

                                allPoints.AddRange(records);
                            }
                        }
                    }

                    if (lineStrings.Length == 0)
                    {
                        continue;
                    }

                    var category = DetermineLineCategory(sourceFileName, featureName, metadata);
                    if (category is null)
                    {
                        continue;
                    }

                    var linePoints = lineStrings
                        .SelectMany(ParseLineStringPoints)
                        .ToArray();

                    if (linePoints.Length == 0)
                    {
                        continue;
                    }

                    if (category.Equals("trail", StringComparison.OrdinalIgnoreCase)
                        && !ShouldKeepTrailFeature(metadata, linePoints))
                    {
                        continue;
                    }

                    features.Add(BuildFeatureRecord(sourceFileName, featureName, category, "line", metadata, linePoints.Length));

                    var lineRecords = BuildPointRecords(
                        linePoints,
                        sourceFileName,
                        featureName,
                        category,
                        "line",
                        metadata,
                        placemarkIndex);

                    if (category.Equals("park", StringComparison.OrdinalIgnoreCase))
                    {
                        parkPoints.AddRange(lineRecords);
                    }
                    else
                    {
                        trailPoints.AddRange(lineRecords);
                    }

                    allPoints.AddRange(lineRecords);
                }
            }

            await WriteJsonLinesAsync(parsed.Value.OutputPath, allPoints);

            if (!string.IsNullOrWhiteSpace(parsed.Value.ParkOutputPath))
            {
                await WriteJsonLinesAsync(parsed.Value.ParkOutputPath, parkPoints);
            }

            if (!string.IsNullOrWhiteSpace(parsed.Value.TrailOutputPath))
            {
                await WriteJsonLinesAsync(parsed.Value.TrailOutputPath, trailPoints);
            }

            if (!string.IsNullOrWhiteSpace(parsed.Value.FeatureOutputPath))
            {
                await WriteJsonLinesAsync(parsed.Value.FeatureOutputPath, features);
            }

            if (!string.IsNullOrWhiteSpace(parsed.Value.ParkOutlineKmlOutputPath))
            {
                await WriteParkOutlineKmlAsync(parsed.Value.ParkOutlineKmlOutputPath, parkPolygons);
            }

            await output.WriteLineAsync($"Saved {allPoints.Count} ARC-derived points to {parsed.Value.OutputPath}");
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
                SourceQueryType = "arc"
            });
        }

        return records;
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

    private static string ComputeFileHash(string inputPath)
    {
        using var stream = File.OpenRead(inputPath);
        var bytes = SHA256.HashData(stream);
        return Convert.ToHexString(bytes);
    }

    private static IReadOnlyDictionary<string, string> ReadMetadata(XElement placemark)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var description = ReadTrimmedChildValue(placemark, "description");
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

        foreach (var dataElement in placemark.Descendants().Where(element => element.Name.LocalName.Equals("Data", StringComparison.Ordinal)))
        {
            var key = dataElement.Attribute("name")?.Value?.Trim();
            var value = dataElement.Descendants().FirstOrDefault(child => child.Name.LocalName.Equals("value", StringComparison.Ordinal))?.Value?.Trim();
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
            {
                metadata[key] = value;
            }
        }

        foreach (var simpleDataElement in placemark.Descendants().Where(element => element.Name.LocalName.Equals("SimpleData", StringComparison.Ordinal)))
        {
            var key = simpleDataElement.Attribute("name")?.Value?.Trim();
            var value = simpleDataElement.Value.Trim();
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
            {
                metadata[key] = value;
            }
        }

        return metadata;
    }

    private static string ReadTrimmedChildValue(XElement parent, string localName) =>
        parent.Elements().FirstOrDefault(element => element.Name.LocalName.Equals(localName, StringComparison.Ordinal))?.Value.Trim() ?? string.Empty;

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

    private static IReadOnlyList<IReadOnlyList<GeoPoint>> ParsePolygonRings(XElement polygon)
    {
        var outerRing = polygon
            .Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName.Equals("outerBoundaryIs", StringComparison.Ordinal));

        var rings = new List<IReadOnlyList<GeoPoint>>();
        if (outerRing is not null)
        {
            var ringPoints = outerRing
                .Descendants()
                .Where(element => element.Name.LocalName.Equals("coordinates", StringComparison.Ordinal))
                .SelectMany(ParseCoordinates)
                .ToArray();

            if (ringPoints.Length > 0)
            {
                rings.Add(ringPoints);
            }
        }

        return rings;
    }

    private static IEnumerable<GeoPoint> ParsePointValues(XElement point) =>
        point
            .Descendants()
            .Where(element => element.Name.LocalName.Equals("coordinates", StringComparison.Ordinal))
            .SelectMany(ParseCoordinates);

    private static IEnumerable<GeoPoint> ParseLineStringPoints(XElement lineString) =>
        lineString
            .Descendants()
            .Where(element => element.Name.LocalName.Equals("coordinates", StringComparison.Ordinal))
            .SelectMany(ParseCoordinates);

    private static IEnumerable<GeoPoint> ParseCoordinates(XElement coordinatesElement)
    {
        var text = coordinatesElement.Value;
        var segments = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        foreach (var segment in segments)
        {
            var parts = segment.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude)
                || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude))
            {
                continue;
            }

            yield return new GeoPoint(latitude, longitude);
        }
    }

    private static string? DetermineLineCategory(string sourceFileName, string featureName, IReadOnlyDictionary<string, string> metadata)
    {
        var projectType = GetMetadataValue(metadata, "Project_Type");
        var normalizedSource = Normalize(sourceFileName);
        var normalizedFeatureName = Normalize($"{featureName} {GetMetadataValue(metadata, "Name")}");

        if (normalizedSource.Contains("trail plan inventory", StringComparison.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(projectType))
            {
                var normalizedProjectType = Normalize(projectType);
                if (BlockedTrailTypes.Contains(normalizedProjectType))
                {
                    return null;
                }

                if (AllowedTrailTypes.Contains(normalizedProjectType)
                    || normalizedProjectType.Contains("trail", StringComparison.Ordinal)
                    || normalizedProjectType.Contains("path", StringComparison.Ordinal)
                    || normalizedProjectType.Contains("greenway", StringComparison.Ordinal))
                {
                    return "trail";
                }
            }

            return HasTrailSignals(normalizedFeatureName) ? "trail" : null;
        }

        if (!string.IsNullOrWhiteSpace(projectType))
        {
            var normalizedProjectType = Normalize(projectType);
            if (BlockedTrailTypes.Contains(normalizedProjectType))
            {
                return null;
            }

            if (AllowedTrailTypes.Contains(normalizedProjectType) || normalizedProjectType.Contains("trail", StringComparison.Ordinal))
            {
                return "trail";
            }
        }

        var composite = Normalize($"{sourceFileName} {featureName} {GetMetadataValue(metadata, "Name")}");

        if (HasTrailSignals(composite))
        {
            return "trail";
        }

        return sourceFileName.Contains("trail", StringComparison.OrdinalIgnoreCase)
               || sourceFileName.Contains("path", StringComparison.OrdinalIgnoreCase)
               || sourceFileName.Contains("beltline", StringComparison.OrdinalIgnoreCase)
            ? "trail"
            : null;
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
            return "marta";
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

    private static IReadOnlyList<GeoPoint> DensifyPolygonBoundaryPoints(IReadOnlyList<IReadOnlyList<GeoPoint>> polygonRings)
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
                var extraPointCount = Math.Max(0, (int)Math.Floor(segmentFeet / ParkOutlineSpacingFeet) - 1);
                for (var pointIndex = 1; pointIndex <= extraPointCount; pointIndex++)
                {
                    var fraction = (pointIndex * ParkOutlineSpacingFeet) / segmentFeet;
                    points.Add(Interpolate(start, end, fraction));
                }
            }

            points.Add(ring[^1]);
        }

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

    private static bool ShouldKeepParkFeature(IReadOnlyDictionary<string, string> metadata)
    {
        if (TryGetSquareFeet(metadata, out var squareFeet))
        {
            return squareFeet >= MinimumParkSquareFeet;
        }

        return true;
    }

    private static bool ShouldKeepTrailFeature(IReadOnlyDictionary<string, string> metadata, IReadOnlyList<GeoPoint> linePoints)
    {
        if (TryGetLengthMiles(metadata, out var miles))
        {
            return miles >= MinimumTrailMiles;
        }

        if (TryGetShapeLengthFeet(metadata, out var feet))
        {
            return feet >= MinimumTrailFeet;
        }

        return ComputePolylineLengthFeet(linePoints) >= MinimumTrailFeet;
    }

    private static bool TryGetSquareFeet(IReadOnlyDictionary<string, string> metadata, out double squareFeet)
    {
        if (TryParseDouble(GetMetadataValue(metadata, "AllParks_Fields_Park_Size_SQFT"), out squareFeet))
        {
            return true;
        }

        if (TryParseDouble(GetMetadataValue(metadata, "Park_Size_SQFT"), out squareFeet))
        {
            return true;
        }

        if (TryGetAcres(metadata, out var acres))
        {
            squareFeet = acres * SquareFeetPerAcre;
            return true;
        }

        squareFeet = 0;
        return false;
    }

    private static bool TryGetAcres(IReadOnlyDictionary<string, string> metadata, out double acres)
    {
        if (TryParseDouble(GetMetadataValue(metadata, "AllParks_Fields_Park_Size_Acres"), out acres))
        {
            return true;
        }

        return TryParseDouble(GetMetadataValue(metadata, "Park_Size_Acres"), out acres);
    }

    private static bool TryGetLengthMiles(IReadOnlyDictionary<string, string> metadata, out double miles)
    {
        if (TryParseDouble(GetMetadataValue(metadata, "Length"), out miles))
        {
            return true;
        }

        miles = 0;
        return false;
    }

    private static bool TryGetShapeLengthFeet(IReadOnlyDictionary<string, string> metadata, out double feet)
    {
        if (TryParseDouble(GetMetadataValue(metadata, "Shape__Length"), out feet))
        {
            return true;
        }

        feet = 0;
        return false;
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

    private static async Task WriteParkOutlineKmlAsync(string outputPath, IReadOnlyList<ParkPolygonRecord> parkPolygons)
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
        builder.AppendLine("    <name>ARC True Park Outlines</name>");
        builder.AppendLine("""    <Style id="park-outline"><LineStyle><color>ffddaa00</color><width>2</width></LineStyle><PolyStyle><color>33ddaa00</color></PolyStyle></Style>""");

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

        builder.AppendLine("  </Document>");
        builder.AppendLine("</kml>");

        await File.WriteAllTextAsync(outputPath, builder.ToString());
    }

    private (IReadOnlyList<string> InputPaths, string OutputPath, string? ParkOutputPath, string? TrailOutputPath, string? FeatureOutputPath, string? ParkOutlineKmlOutputPath)? ParseArguments(IReadOnlyList<string> args)
    {
        using var _ = MethodTrace.Enter(_logger, nameof(ArcGeometryExtractorApp));
        var inputPaths = new List<string>();
        string? outputPath = null;
        string? parkOutputPath = null;
        string? trailOutputPath = null;
        string? featureOutputPath = null;
        string? parkOutlineKmlOutputPath = null;

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
                case "--park-outline-kml-output" when index + 1 < args.Count:
                    parkOutlineKmlOutputPath = args[++index];
                    break;
            }
        }

        return inputPaths.Count == 0 || string.IsNullOrWhiteSpace(outputPath)
            ? null
            : (inputPaths, outputPath, parkOutputPath, trailOutputPath, featureOutputPath, parkOutlineKmlOutputPath);
    }

    private static readonly Regex MetadataRowPattern = new(
        @"<tr>\s*<td>(?<key>.*?)</td>\s*<td>(?<value>.*?)</td>\s*</tr>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private const double SquareFeetPerAcre = 43_560d;
    private const double MinimumParkSquareFeet = 800d;
    private const double MinimumTrailMiles = 1.65d;
    private const double MinimumTrailFeet = MinimumTrailMiles * 5_280d;
    private const double ParkOutlineSpacingFeet = 50d;

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

    private sealed record GeoPoint(double Latitude, double Longitude);
    private sealed record ParkPolygonRecord(
        string SourceFile,
        string Name,
        IReadOnlyDictionary<string, string> Metadata,
        IReadOnlyList<IReadOnlyList<IReadOnlyList<GeoPoint>>> Rings);

    public sealed record class ArcFeatureRecord
    {
        public string SourceFile { get; init; } = string.Empty;

        public string Name { get; init; } = string.Empty;

        public string Category { get; init; } = string.Empty;

        public string GeometryType { get; init; } = string.Empty;

        public int PointCount { get; init; }

        public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
    }
}
