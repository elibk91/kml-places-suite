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
        services.AddTracedSingleton<IArcSourceReader, ArcGeometryExtractorApp.ArcSourceReader>();
        services.AddTracedSingleton<IArcFeatureExtractor, ArcGeometryExtractorApp.ArcFeatureExtractor>();
        services.AddTracedSingleton<IArcEntityCollapser, ArcGeometryExtractorApp.ArcEntityCollapser>();
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
    private readonly IArcEntityCollapser _entityCollapser;
    private readonly IArcOutputWriter _outputWriter;

    public ArcGeometryExtractorApp(
        ILogger<ArcGeometryExtractorApp> logger,
        IArcSourceReader sourceReader,
        IArcFeatureExtractor featureExtractor,
        IArcEntityCollapser entityCollapser,
        IArcOutputWriter outputWriter)
    {
        _logger = logger;
        _sourceReader = sourceReader;
        _featureExtractor = featureExtractor;
        _entityCollapser = entityCollapser;
        _outputWriter = outputWriter;
    }

    public async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        var parsed = ParseArguments(args);
        if (parsed is null)
        {
            await error.WriteLineAsync("Usage: arc-geometry-extractor --input parks.kml --input trails.kmz --output points.jsonl [--park-output parks.jsonl] [--trail-output trails.jsonl] [--feature-output features.jsonl] [--park-outline-kml-output parks.kml]");
            return 1;
        }

        try
        {
            var sourceDocuments = await _sourceReader.ReadAsync(parsed.InputPaths);
            var pointSpacingFeet = parsed.PointSpacingMiles * 5_280d;
            var extracted = _featureExtractor.Extract(
                sourceDocuments,
                Math.Max(parsed.MaximumCollapseGapMiles * 5_280d, pointSpacingFeet),
                pointSpacingFeet,
                parsed.MinimumParkSquareFeet,
                parsed.MinimumTrailMiles,
                parsed.MinimumCombinedParkTrailMiles);
            var collapsed = _entityCollapser.Collapse(
                extracted.CollapsibleEntities,
                parsed.EnableEntityCollapse,
                parsed.MaximumCollapseGapMiles,
                parsed.CollapseEligibleCategories,
                parsed.MinimumParkSquareFeet,
                parsed.MinimumTrailMiles,
                parsed.MinimumCombinedParkTrailMiles);

            var allPoints = extracted.DirectPointRecords
                .Concat(collapsed.PointRecords)
                .ToList();
            var parkPoints = allPoints
                .Where(record => record.Category.Equals("park", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var trailPoints = allPoints
                .Where(record => record.Category.Equals("trail", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var allFeatures = extracted.Features
                .Concat(collapsed.FeatureRecords)
                .ToList();

            await _outputWriter.WriteAsync(parsed, allPoints, parkPoints, trailPoints, allFeatures, extracted.ParkPolygons, extracted.TrailLines);

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

    private static CollapsibleEntity BuildCollapsibleEntity(
        string rawEntityId,
        string sourceFileName,
        string featureName,
        string category,
        string geometryType,
        IReadOnlyDictionary<string, string> metadata,
        int placemarkIndex,
        IReadOnlyList<NormalizedPlaceRecord> outputRecords,
        IReadOnlyList<GeoPoint> collapsePoints,
        double collapseGridCellSizeFeet,
        bool tryGetParkSquareFeet,
        double parkSquareFeet,
        bool tryGetTrailFeet,
        double trailFeet,
        double linearFeet)
    {
        var searchNames = BuildSearchNames(featureName, metadata);
        var projectedCollapsePoints = ProjectPoints(collapsePoints);
        var collapseBounds = ComputeBounds(projectedCollapsePoints);
        var coveredCells = EnumerateCoveredCells(collapseBounds, collapseGridCellSizeFeet);
        return new CollapsibleEntity(
            rawEntityId,
            sourceFileName,
            featureName,
            category,
            geometryType,
            metadata,
            placemarkIndex,
            outputRecords,
            collapsePoints,
            projectedCollapsePoints,
            collapseBounds,
            coveredCells,
            searchNames,
            tryGetParkSquareFeet ? parkSquareFeet : null,
            category.Equals("park", StringComparison.OrdinalIgnoreCase) && !tryGetParkSquareFeet,
            tryGetTrailFeet ? trailFeet : null,
            category.Equals("trail", StringComparison.OrdinalIgnoreCase) && !tryGetTrailFeet,
            linearFeet);
    }

    private static ArcCollapsedEntityResult CollapseEntities(
        IReadOnlyList<CollapsibleEntity> entities,
        bool enableEntityCollapse,
        double maximumCollapseGapMiles,
        IReadOnlySet<string> collapseEligibleCategories,
        double minimumParkSquareFeet,
        double minimumTrailMiles,
        double minimumCombinedParkTrailMiles)
    {
        if (entities.Count == 0)
        {
            return new ArcCollapsedEntityResult(Array.Empty<NormalizedPlaceRecord>(), Array.Empty<ArcFeatureRecord>());
        }

        var unionFind = new UnionFind(entities.Count);
        var maximumCollapseGapFeet = maximumCollapseGapMiles * 5_280d;

        if (enableEntityCollapse && maximumCollapseGapFeet > 0d)
        {
            var grid = BuildSpatialIndex(entities);
            var candidateCache = BuildCandidateCache(entities, grid);
            var pairConnectivityCache = new Dictionary<long, bool>();

            for (var leftIndex = 0; leftIndex < entities.Count; leftIndex++)
            {
                var left = entities[leftIndex];
                if (!collapseEligibleCategories.Contains(left.Category))
                {
                    continue;
                }

                foreach (var rightIndex in candidateCache[leftIndex])
                {
                    if (rightIndex <= leftIndex)
                    {
                        continue;
                    }

                    var right = entities[rightIndex];
                    if (!collapseEligibleCategories.Contains(right.Category))
                    {
                        continue;
                    }

                    if (unionFind.Find(leftIndex) == unionFind.Find(rightIndex))
                    {
                        continue;
                    }

                    var pairKey = BuildPairKey(leftIndex, rightIndex);
                    if (!pairConnectivityCache.TryGetValue(pairKey, out var isConnected))
                    {
                        isConnected =
                            ApproximateBoundingBoxGapFeet(left.CollapseBounds, right.CollapseBounds) <= maximumCollapseGapFeet
                            && ComputeMinimumGapFeet(left.CollapseProjectedPoints, right.CollapseProjectedPoints, maximumCollapseGapFeet) <= maximumCollapseGapFeet;
                        pairConnectivityCache[pairKey] = isConnected;
                    }

                    if (isConnected)
                    {
                        unionFind.Union(leftIndex, rightIndex);
                    }
                }
            }
        }

        var collapsedRecords = new List<NormalizedPlaceRecord>();
        var collapsedFeatures = new List<ArcFeatureRecord>();
        foreach (var component in entities
                     .Select((entity, index) => new { Entity = entity, Root = unionFind.Find(index) })
                     .GroupBy(item => item.Root, item => item.Entity))
        {
            var memberEntities = component.ToArray();
            if (!ShouldKeepCollapsedComponent(memberEntities, minimumCombinedParkTrailMiles))
            {
                continue;
            }

            var collapsedCategory = DetermineCollapsedCategory(memberEntities);
            var memberNames = memberEntities
                .SelectMany(entity => entity.SearchNames)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var collapsedLabel = string.Join(" + ", memberEntities
                .Select(entity => entity.FeatureName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
            var collapsedDisplayLabel = BuildCollapsedDisplayLabel(memberEntities);
            var collapsedEntityId = BuildCollapsedEntityId(memberEntities);
            var collapsedTypes = memberEntities
                .SelectMany(entity => entity.OutputRecords)
                .SelectMany(record => record.Types)
                .Append("collapsed-entity")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var outputPoints = DeduplicatePoints(memberEntities
                .SelectMany(entity => entity.OutputRecords)
                .Select(record => new GeoPoint(record.Latitude, record.Longitude))
                .ToArray());

            collapsedFeatures.Add(new ArcFeatureRecord
            {
                SourceFile = "collapsed-component",
                Name = collapsedDisplayLabel,
                Category = collapsedCategory,
                GeometryType = "collapsed-component",
                PointCount = outputPoints.Count,
                Metadata = new Dictionary<string, string>
                {
                    ["CollapsedLabel"] = collapsedLabel,
                    ["MemberCount"] = memberEntities.Length.ToString(CultureInfo.InvariantCulture)
                },
                CollapsedEntityId = collapsedEntityId,
                SearchNames = memberNames,
                MemberNames = memberEntities
                    .Select(entity => entity.FeatureName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            });

            for (var pointIndex = 0; pointIndex < outputPoints.Count; pointIndex++)
            {
                var point = outputPoints[pointIndex];
                collapsedRecords.Add(new NormalizedPlaceRecord
                {
                    Query = collapsedDisplayLabel,
                    Category = collapsedCategory,
                    PlaceId = BuildCollapsedPlaceId(collapsedEntityId, pointIndex),
                    Name = collapsedDisplayLabel,
                    FormattedAddress = BuildCollapsedAddressHint(memberEntities),
                    Latitude = point.Latitude,
                    Longitude = point.Longitude,
                    Types = collapsedTypes,
                    SourceQueryType = "arc",
                    SearchNames = [collapsedDisplayLabel],
                    CollapsedEntityId = collapsedEntityId
                });
            }
        }

        return new ArcCollapsedEntityResult(collapsedRecords, collapsedFeatures);
    }

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

    private static string DetermineCollapsedCategory(IReadOnlyList<CollapsibleEntity> entities) =>
        entities.Any(entity => entity.Category.Equals("park", StringComparison.OrdinalIgnoreCase))
            ? "park"
            : "trail";

    private static string BuildCollapsedDisplayLabel(IReadOnlyList<CollapsibleEntity> entities)
    {
        var names = entities
            .Select(entity => entity.FeatureName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (names.Length == 0)
        {
            return "Collapsed entity";
        }

        if (names.Length == 1)
        {
            return names[0];
        }

        return $"{names[0]} + {names.Length - 1} more";
    }

    private static bool ShouldKeepCollapsedComponent(
        IReadOnlyList<CollapsibleEntity> entities,
        double minimumCombinedParkTrailMiles)
    {
        var minimumCombinedFeet = minimumCombinedParkTrailMiles * 5_280d;
        var combinedLinearFeet = entities.Sum(entity => entity.LinearFeet);
        return combinedLinearFeet >= minimumCombinedFeet;
    }

    private static string BuildCollapsedEntityId(IReadOnlyList<CollapsibleEntity> entities)
    {
        var identity = string.Join("|", entities
            .Select(entity => entity.RawEntityId)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return $"collapsed-{Convert.ToHexString(bytes[..12])}";
    }

    private static string BuildCollapsedPlaceId(string collapsedEntityId, int pointIndex)
    {
        var raw = $"{collapsedEntityId}|{pointIndex}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes[..12]);
    }

    private static string BuildCollapsedAddressHint(IReadOnlyList<CollapsibleEntity> entities) =>
        string.Join(
            " | ",
            entities.Select(entity => entity.SourceFile)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase));

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

    private static bool ShouldKeepParkFeature(IReadOnlyDictionary<string, string> metadata, double minimumParkSquareFeet)
    {
        if (TryGetSquareFeet(metadata, out var squareFeet))
        {
            return squareFeet >= minimumParkSquareFeet;
        }

        return true;
    }

    private static bool ShouldKeepTrailFeature(IReadOnlyDictionary<string, string> metadata, IReadOnlyList<GeoPoint> linePoints, double minimumTrailMiles)
    {
        var minimumTrailFeet = minimumTrailMiles * 5_280d;
        if (TryGetLengthMiles(metadata, out var miles))
        {
            return miles >= minimumTrailMiles;
        }

        if (TryGetShapeLengthFeet(metadata, out var feet))
        {
            return feet >= minimumTrailFeet;
        }

        return ComputePolylineLengthFeet(linePoints) >= minimumTrailFeet;
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

    private static double? GetParkSquareFeet(IReadOnlyDictionary<string, string> metadata) =>
        TryGetSquareFeet(metadata, out var squareFeet) ? squareFeet : null;

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

    private static double ComputePolygonPerimeterFeet(IReadOnlyList<IReadOnlyList<GeoPoint>> rings)
    {
        var totalFeet = 0d;
        foreach (var ring in rings)
        {
            totalFeet += ComputePolylineLengthFeet(ring);
        }

        return totalFeet;
    }

    private static double ResolveTrailLengthFeet(IReadOnlyDictionary<string, string> metadata, IReadOnlyList<GeoPoint> linePoints)
    {
        if (TryGetLengthMiles(metadata, out var miles))
        {
            return miles * 5_280d;
        }

        if (TryGetShapeLengthFeet(metadata, out var feet))
        {
            return feet;
        }

        return ComputePolylineLengthFeet(linePoints);
    }

    private static ProjectedBounds ComputeBounds(IReadOnlyList<ProjectedPoint> points)
    {
        if (points.Count == 0)
        {
            return new ProjectedBounds(0d, 0d, 0d, 0d);
        }

        var minX = points[0].X;
        var maxX = points[0].X;
        var minY = points[0].Y;
        var maxY = points[0].Y;

        for (var index = 1; index < points.Count; index++)
        {
            var point = points[index];
            minX = Math.Min(minX, point.X);
            maxX = Math.Max(maxX, point.X);
            minY = Math.Min(minY, point.Y);
            maxY = Math.Max(maxY, point.Y);
        }

        return new ProjectedBounds(minX, maxX, minY, maxY);
    }

    private static double ApproximateBoundingBoxGapFeet(ProjectedBounds left, ProjectedBounds right)
    {
        var xGapFeet = Math.Max(0d, Math.Max(left.MinX - right.MaxX, right.MinX - left.MaxX));
        var yGapFeet = Math.Max(0d, Math.Max(left.MinY - right.MaxY, right.MinY - left.MaxY));
        return Math.Sqrt((xGapFeet * xGapFeet) + (yGapFeet * yGapFeet));
    }

    private static double ComputeMinimumGapFeet(IReadOnlyList<ProjectedPoint> leftPoints, IReadOnlyList<ProjectedPoint> rightPoints, double earlyExitThresholdFeet)
    {
        if (leftPoints.Count == 0 || rightPoints.Count == 0)
        {
            return double.MaxValue;
        }

        var minimumGapFeet = double.MaxValue;
        foreach (var leftPoint in leftPoints)
        {
            foreach (var rightPoint in rightPoints)
            {
                var distanceFeet = ComputeProjectedDistanceFeet(leftPoint, rightPoint);
                if (distanceFeet < minimumGapFeet)
                {
                    minimumGapFeet = distanceFeet;
                    if (minimumGapFeet <= earlyExitThresholdFeet)
                    {
                        return minimumGapFeet;
                    }
                }
            }
        }

        return minimumGapFeet;
    }

    private static Dictionary<(int X, int Y), List<int>> BuildSpatialIndex(IReadOnlyList<CollapsibleEntity> entities)
    {
        var grid = new Dictionary<(int X, int Y), List<int>>();
        for (var index = 0; index < entities.Count; index++)
        {
            foreach (var cell in entities[index].CoveredCells)
            {
                if (!grid.TryGetValue(cell, out var members))
                {
                    members = [];
                    grid[cell] = members;
                }

                members.Add(index);
            }
        }

        return grid;
    }

    private static IReadOnlyList<int>[] BuildCandidateCache(
        IReadOnlyList<CollapsibleEntity> entities,
        IReadOnlyDictionary<(int X, int Y), List<int>> grid)
    {
        var candidateCache = new IReadOnlyList<int>[entities.Count];
        for (var index = 0; index < entities.Count; index++)
        {
            var seen = new HashSet<int>();
            var candidates = new List<int>();
            foreach (var cell in entities[index].CoveredCells)
            {
                if (!grid.TryGetValue(cell, out var members))
                {
                    continue;
                }

                foreach (var member in members)
                {
                    if (seen.Add(member))
                    {
                        candidates.Add(member);
                    }
                }
            }

            candidates.Sort();
            candidateCache[index] = candidates;
        }

        return candidateCache;
    }

    private static IReadOnlyList<(int X, int Y)> EnumerateCoveredCells(ProjectedBounds bounds, double cellSizeFeet)
    {
        var minCell = ToGridCell(bounds.MinX, bounds.MinY, cellSizeFeet);
        var maxCell = ToGridCell(bounds.MaxX, bounds.MaxY, cellSizeFeet);
        var cells = new List<(int X, int Y)>((maxCell.X - minCell.X + 3) * (maxCell.Y - minCell.Y + 3));

        for (var x = minCell.X - 1; x <= maxCell.X + 1; x++)
        {
            for (var y = minCell.Y - 1; y <= maxCell.Y + 1; y++)
            {
                cells.Add((x, y));
            }
        }

        return cells;
    }

    private static (int X, int Y) ToGridCell(double xFeet, double yFeet, double cellSizeFeet) =>
        ((int)Math.Floor(xFeet / cellSizeFeet), (int)Math.Floor(yFeet / cellSizeFeet));

    private static long BuildPairKey(int leftIndex, int rightIndex) =>
        ((long)leftIndex << 32) | (uint)rightIndex;

    private static IReadOnlyList<ProjectedPoint> ProjectPoints(IReadOnlyList<GeoPoint> points)
    {
        if (points.Count == 0)
        {
            return Array.Empty<ProjectedPoint>();
        }

        var projected = new ProjectedPoint[points.Count];
        for (var index = 0; index < points.Count; index++)
        {
            projected[index] = ProjectPoint(points[index]);
        }

        return projected;
    }

    private static ProjectedPoint ProjectPoint(GeoPoint point)
    {
        var yFeet = point.Latitude * FeetPerDegreeLatitude;
        var xFeet = point.Longitude * FeetPerDegreeLongitudeAtReferenceLatitude;
        return new ProjectedPoint(xFeet, yFeet);
    }

    private static double ComputeProjectedDistanceFeet(ProjectedPoint start, ProjectedPoint end)
    {
        var deltaX = end.X - start.X;
        var deltaY = end.Y - start.Y;
        return Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
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
        string? originalGeometryKmlOutputPath = null;
        var minimumParkSquareFeet = DefaultMinimumParkSquareFeet;
        var minimumTrailMiles = DefaultMinimumTrailMiles;
        var minimumCombinedParkTrailMiles = DefaultMinimumCombinedParkTrailMiles;
        var pointSpacingMiles = DefaultPointSpacingMiles;
        var enableEntityCollapse = false;
        var maximumCollapseGapMiles = DefaultMaximumCollapseGapMiles;
        var collapseEligibleCategories = new HashSet<string>(DefaultCollapseEligibleCategories, StringComparer.OrdinalIgnoreCase);

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
                case "--enable-entity-collapse":
                    enableEntityCollapse = true;
                    break;
                case "--maximum-collapse-gap-miles" when index + 1 < args.Count:
                    maximumCollapseGapMiles = double.Parse(args[++index], CultureInfo.InvariantCulture);
                    break;
                case "--collapse-category" when index + 1 < args.Count:
                    collapseEligibleCategories.Add(args[++index]);
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
                originalGeometryKmlOutputPath,
                minimumParkSquareFeet,
                minimumTrailMiles,
                minimumCombinedParkTrailMiles,
                pointSpacingMiles,
                enableEntityCollapse,
                maximumCollapseGapMiles,
                collapseEligibleCategories);
    }

    private static readonly Regex MetadataRowPattern = new(
        @"<tr>\s*<td>(?<key>.*?)</td>\s*<td>(?<value>.*?)</td>\s*</tr>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private const double SquareFeetPerAcre = 43_560d;
    private const double DefaultPointSpacingMiles = 0.5d;
    private const double DefaultMaximumCollapseGapMiles = 0.3d;
    private const double CollapseReferenceLatitudeDegrees = 33.75d;
    private const double FeetPerDegreeLatitude = 364_000d;
    private static readonly double FeetPerDegreeLongitudeAtReferenceLatitude =
        FeetPerDegreeLatitude * Math.Cos(DegreesToRadians(CollapseReferenceLatitudeDegrees));
    private static readonly string[] DefaultCollapseEligibleCategories = ["park", "trail"];

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

    internal sealed record ExtractorArguments(
        IReadOnlyList<string> InputPaths,
        string OutputPath,
        string? ParkOutputPath,
        string? TrailOutputPath,
        string? FeatureOutputPath,
        string? OriginalGeometryKmlOutputPath,
        double MinimumParkSquareFeet,
        double MinimumTrailMiles,
        double MinimumCombinedParkTrailMiles,
        double PointSpacingMiles,
        bool EnableEntityCollapse,
        double MaximumCollapseGapMiles,
        IReadOnlySet<string> CollapseEligibleCategories);

    internal sealed record GeoPoint(double Latitude, double Longitude);
    internal sealed record ProjectedPoint(double X, double Y);
    internal sealed record ProjectedBounds(double MinX, double MaxX, double MinY, double MaxY);
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
    internal sealed record CollapsibleEntity(
        string RawEntityId,
        string SourceFile,
        string FeatureName,
        string Category,
        string GeometryType,
        IReadOnlyDictionary<string, string> Metadata,
        int PlacemarkIndex,
        IReadOnlyList<NormalizedPlaceRecord> OutputRecords,
        IReadOnlyList<GeoPoint> CollapsePoints,
        IReadOnlyList<ProjectedPoint> CollapseProjectedPoints,
        ProjectedBounds CollapseBounds,
        IReadOnlyList<(int X, int Y)> CoveredCells,
        IReadOnlyList<string> SearchNames,
        double? ParkSquareFeet,
        bool HasUnknownParkSize,
        double? TrailFeet,
        bool HasUnknownTrailLength,
        double LinearFeet);

    private sealed class UnionFind
    {
        private readonly int[] _parent;
        private readonly int[] _rank;

        public UnionFind(int size)
        {
            _parent = Enumerable.Range(0, size).ToArray();
            _rank = new int[size];
        }

        public int Find(int index)
        {
            if (_parent[index] != index)
            {
                _parent[index] = Find(_parent[index]);
            }

            return _parent[index];
        }

        public void Union(int left, int right)
        {
            var leftRoot = Find(left);
            var rightRoot = Find(right);
            if (leftRoot == rightRoot)
            {
                return;
            }

            if (_rank[leftRoot] < _rank[rightRoot])
            {
                _parent[leftRoot] = rightRoot;
                return;
            }

            if (_rank[leftRoot] > _rank[rightRoot])
            {
                _parent[rightRoot] = leftRoot;
                return;
            }

            _parent[rightRoot] = leftRoot;
            _rank[leftRoot]++;
        }
    }

    internal sealed record class ArcFeatureRecord
    {
        public string SourceFile { get; init; } = string.Empty;

        public string Name { get; init; } = string.Empty;

        public string Category { get; init; } = string.Empty;

        public string GeometryType { get; init; } = string.Empty;

        public int PointCount { get; init; }

        public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();

        public string? CollapsedEntityId { get; init; }

        public IReadOnlyList<string> SearchNames { get; init; } = Array.Empty<string>();

        public IReadOnlyList<string> MemberNames { get; init; } = Array.Empty<string>();
    }

    internal sealed class ArcSourceReader : IArcSourceReader
    {
        public async Task<IReadOnlyList<ArcSourceDocument>> ReadAsync(IReadOnlyList<string> inputPaths)
        {
            var processedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sourceDocuments = new List<ArcSourceDocument>(inputPaths.Count);

            foreach (var inputPath in inputPaths)
            {
                var fileIdentity = ComputeFileIdentity(inputPath);
                if (!processedFiles.Add(fileIdentity))
                {
                    continue;
                }

                var sourceFileName = Path.GetFileName(inputPath);
                var document = XDocument.Parse(await ReadSanitizedKmlAsync(inputPath), LoadOptions.None);
                var placemarks = document
                    .Descendants()
                    .Where(element => element.Name.LocalName.Equals("Placemark", StringComparison.Ordinal))
                    .ToArray();
                sourceDocuments.Add(new ArcSourceDocument(sourceFileName, placemarks));
            }

            return sourceDocuments;
        }
    }

    internal sealed class ArcFeatureExtractor : IArcFeatureExtractor
    {
        public ArcExtractionStageResult Extract(
            IReadOnlyList<ArcSourceDocument> sourceDocuments,
            double collapseGridCellSizeFeet,
            double pointSpacingFeet,
            double minimumParkSquareFeet,
            double minimumTrailMiles,
            double minimumCombinedParkTrailMiles)
        {
            var directPointRecords = new List<NormalizedPlaceRecord>();
            var collapsibleEntities = new List<CollapsibleEntity>();
            var features = new List<ArcFeatureRecord>();
            var parkPolygons = new List<ParkPolygonRecord>();
            var trailLines = new List<TrailLineRecord>();
            var rawEntityIndex = 0;

            foreach (var sourceDocument in sourceDocuments)
            {
                for (var placemarkIndex = 0; placemarkIndex < sourceDocument.Placemarks.Count; placemarkIndex++)
                {
                    var placemark = sourceDocument.Placemarks[placemarkIndex];
                    var metadata = ReadMetadata(placemark);
                    var featureName = ResolveFeatureName(ReadTrimmedChildValue(placemark, "name"), metadata, sourceDocument.SourceFileName);

                    var lineStrings = placemark.Descendants().Where(element => element.Name.LocalName.Equals("LineString", StringComparison.Ordinal)).ToArray();
                    var polygons = placemark.Descendants().Where(element => element.Name.LocalName.Equals("Polygon", StringComparison.Ordinal)).ToArray();
                    var points = placemark.Descendants().Where(element => element.Name.LocalName.Equals("Point", StringComparison.Ordinal)).ToArray();

                    if (polygons.Length > 0)
                    {
                        var polygonPoints = polygons.Select(ParsePolygonRings).Where(rings => rings.Count > 0).ToArray();
                        if (polygonPoints.Length > 0)
                        {
                            if (!ShouldKeepParkFeature(metadata, minimumParkSquareFeet))
                            {
                                continue;
                            }

                            parkPolygons.Add(new ParkPolygonRecord(sourceDocument.SourceFileName, featureName, metadata, polygonPoints));
                            var outlinePoints = polygonPoints.SelectMany(rings => DensifyPolygonBoundaryPoints(rings, pointSpacingFeet)).ToArray();
                            if (outlinePoints.Length > 0)
                            {
                                features.Add(BuildFeatureRecord(sourceDocument.SourceFileName, featureName, "park", "polygon", metadata, polygonPoints.Sum(rings => rings.Sum(ring => ring.Count))));
                                var records = BuildPointRecords(outlinePoints, sourceDocument.SourceFileName, featureName, "park", "polygon-densified-edge", metadata, placemarkIndex);
                                collapsibleEntities.Add(BuildCollapsibleEntity(
                                    $"arc-entity-{rawEntityIndex++}",
                                    sourceDocument.SourceFileName,
                                    featureName,
                                    "park",
                                    "polygon-densified-edge",
                                    metadata,
                                    placemarkIndex,
                                    records,
                                    outlinePoints,
                                    collapseGridCellSizeFeet,
                                    tryGetParkSquareFeet: GetParkSquareFeet(metadata) is double parkSquareFeet,
                                    parkSquareFeet: GetParkSquareFeet(metadata) ?? 0d,
                                    tryGetTrailFeet: false,
                                    trailFeet: 0d,
                                    linearFeet: polygonPoints.Sum(ComputePolygonPerimeterFeet)));
                            }
                        }
                    }

                    if (points.Length > 0)
                    {
                        var pointCategory = DeterminePointCategory(sourceDocument.SourceFileName, featureName, metadata);
                        if (pointCategory is not null)
                        {
                            var pointValues = points.SelectMany(ParsePointValues).ToArray();
                            if (pointValues.Length > 0)
                            {
                                if (pointCategory.Equals("park", StringComparison.OrdinalIgnoreCase)
                                    && !ShouldKeepParkFeature(metadata, minimumParkSquareFeet))
                                {
                                    continue;
                                }

                                features.Add(BuildFeatureRecord(sourceDocument.SourceFileName, featureName, pointCategory, "point", metadata, pointValues.Length));
                                var records = BuildPointRecords(pointValues, sourceDocument.SourceFileName, featureName, pointCategory, "point", metadata, placemarkIndex);
                                if (pointCategory.Equals("park", StringComparison.OrdinalIgnoreCase) || pointCategory.Equals("trail", StringComparison.OrdinalIgnoreCase))
                                {
                                    collapsibleEntities.Add(BuildCollapsibleEntity(
                                        $"arc-entity-{rawEntityIndex++}",
                                        sourceDocument.SourceFileName,
                                        featureName,
                                        pointCategory,
                                        "point",
                                        metadata,
                                        placemarkIndex,
                                        records,
                                        pointValues,
                                        collapseGridCellSizeFeet,
                                        tryGetParkSquareFeet: pointCategory.Equals("park", StringComparison.OrdinalIgnoreCase) && GetParkSquareFeet(metadata) is double pointParkSquareFeet,
                                        parkSquareFeet: GetParkSquareFeet(metadata) ?? 0d,
                                        tryGetTrailFeet: false,
                                        trailFeet: 0d,
                                        linearFeet: 0d));
                                }
                                else
                                {
                                    directPointRecords.AddRange(records);
                                }
                            }
                        }
                    }

                    if (lineStrings.Length == 0)
                    {
                        continue;
                    }

                    var category = DetermineLineCategory(sourceDocument.SourceFileName, featureName, metadata);
                    if (category is null)
                    {
                        continue;
                    }

                    var linePoints = lineStrings.SelectMany(ParseLineStringPoints).ToArray();
                    if (linePoints.Length == 0)
                    {
                        continue;
                    }

                    if (category.Equals("park", StringComparison.OrdinalIgnoreCase)
                        && !ShouldKeepParkFeature(metadata, minimumParkSquareFeet))
                    {
                        continue;
                    }

                    if (category.Equals("trail", StringComparison.OrdinalIgnoreCase)
                        && !ShouldKeepTrailFeature(metadata, linePoints, minimumTrailMiles))
                    {
                        continue;
                    }

                    features.Add(BuildFeatureRecord(sourceDocument.SourceFileName, featureName, category, "line", metadata, linePoints.Length));
                    trailLines.Add(new TrailLineRecord(sourceDocument.SourceFileName, featureName, metadata, linePoints));
                    var lineRecords = BuildPointRecords(linePoints, sourceDocument.SourceFileName, featureName, category, "line", metadata, placemarkIndex);
                    collapsibleEntities.Add(BuildCollapsibleEntity(
                        $"arc-entity-{rawEntityIndex++}",
                        sourceDocument.SourceFileName,
                        featureName,
                        category,
                        "line",
                        metadata,
                        placemarkIndex,
                        lineRecords,
                        DensifyPolylinePoints(linePoints, pointSpacingFeet),
                        collapseGridCellSizeFeet,
                        tryGetParkSquareFeet: category.Equals("park", StringComparison.OrdinalIgnoreCase) && GetParkSquareFeet(metadata) is double lineParkSquareFeet,
                        parkSquareFeet: GetParkSquareFeet(metadata) ?? 0d,
                        tryGetTrailFeet: category.Equals("trail", StringComparison.OrdinalIgnoreCase),
                        trailFeet: category.Equals("trail", StringComparison.OrdinalIgnoreCase) ? ResolveTrailLengthFeet(metadata, linePoints) : 0d,
                        linearFeet: ResolveTrailLengthFeet(metadata, linePoints)));
                }
            }

            return new ArcExtractionStageResult(directPointRecords, collapsibleEntities, features, parkPolygons, trailLines);
        }
    }

    internal sealed class ArcEntityCollapser : IArcEntityCollapser
    {
        public ArcCollapsedEntityResult Collapse(
            IReadOnlyList<CollapsibleEntity> entities,
            bool enableEntityCollapse,
            double maximumCollapseGapMiles,
            IReadOnlySet<string> collapseEligibleCategories,
            double minimumParkSquareFeet,
            double minimumTrailMiles,
            double minimumCombinedParkTrailMiles) =>
            CollapseEntities(
                entities,
                enableEntityCollapse,
                maximumCollapseGapMiles,
                collapseEligibleCategories,
                minimumParkSquareFeet,
                minimumTrailMiles,
                minimumCombinedParkTrailMiles);
    }

    internal sealed class ArcOutputWriter : IArcOutputWriter
    {
        public async Task WriteAsync(
            ExtractorArguments arguments,
            IReadOnlyList<NormalizedPlaceRecord> allPoints,
            IReadOnlyList<NormalizedPlaceRecord> parkPoints,
            IReadOnlyList<NormalizedPlaceRecord> trailPoints,
            IReadOnlyList<ArcFeatureRecord> features,
            IReadOnlyList<ParkPolygonRecord> parkPolygons,
            IReadOnlyList<TrailLineRecord> trailLines)
        {
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

            if (!string.IsNullOrWhiteSpace(arguments.OriginalGeometryKmlOutputPath))
            {
                await WriteOriginalGeometryKmlAsync(arguments.OriginalGeometryKmlOutputPath, parkPolygons, trailLines);
            }
        }
    }
}
