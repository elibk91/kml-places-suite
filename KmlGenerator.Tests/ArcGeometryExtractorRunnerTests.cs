using System.IO.Compression;
using System.Text.Json;
using PlacesGatherer.Console.Models;

namespace KmlGenerator.Tests;

public sealed class ArcGeometryExtractorRunnerTests
{
    [Fact]
    public async Task RunAsync_ExtractsParkAndTrailPointsFromArcGeometry()
    {
        var tempDirectory = Directory.CreateTempSubdirectory();
        var inputPath = Path.Combine(tempDirectory.FullName, "arc.kml");
        var outputPath = Path.Combine(tempDirectory.FullName, "points.jsonl");
        var featureOutputPath = Path.Combine(tempDirectory.FullName, "features.jsonl");
        var parkOutputPath = Path.Combine(tempDirectory.FullName, "parks.jsonl");
        var trailOutputPath = Path.Combine(tempDirectory.FullName, "trails.jsonl");
        var parkOutlineKmlPath = Path.Combine(tempDirectory.FullName, "park-outlines.kml");

        await File.WriteAllTextAsync(
            inputPath,
            """
            <kml xmlns="http://www.opengis.net/kml/2.2">
              <Document>
                <Placemark>
                  <name>Piedmont Park</name>
                  <description><![CDATA[<html><body><table><tr><td>AllParks_Fields_Park_Size_SQFT</td><td>200000</td></tr></table></body></html>]]></description>
                  <Polygon>
                    <outerBoundaryIs>
                      <LinearRing>
                        <coordinates>-84.3745,33.7845 -84.3725,33.7845 -84.3725,33.7865 -84.3745,33.7865 -84.3745,33.7845</coordinates>
                      </LinearRing>
                    </outerBoundaryIs>
                  </Polygon>
                </Placemark>
                <Placemark>
                  <name>Atlanta Beltline</name>
                  <description><![CDATA[<html><body><table><tr><td>Project_Type</td><td>Multi-Use Trail</td></tr><tr><td>Plan_</td><td>Atlanta Transportation Plan</td></tr><tr><td>Length</td><td>2.0</td></tr></table></body></html>]]></description>
                  <LineString>
                    <coordinates>-84.3636,33.7528 -84.3637,33.7529</coordinates>
                  </LineString>
                </Placemark>
                <Placemark>
                  <name>Ponce de Leon Ave</name>
                  <description><![CDATA[<html><body><table><tr><td>Project_Type</td><td>On-Street Bicycle Facility</td></tr></table></body></html>]]></description>
                  <LineString>
                    <coordinates>-84.3610,33.7720 -84.3609,33.7721</coordinates>
                  </LineString>
                </Placemark>
              </Document>
            </kml>
            """);

        var exitCode = await ArcGeometryExtractorProgram.RunAsync(
            [
                "--input", inputPath,
                "--output", outputPath,
                "--park-output", parkOutputPath,
                "--trail-output", trailOutputPath,
                "--feature-output", featureOutputPath,
                "--original-geometry-kml-output", parkOutlineKmlPath,
                "--minimum-park-square-feet", "1000",
                "--minimum-combined-park-trail-miles", "0.01"
            ],
            TextWriter.Null,
            TextWriter.Null);

        Assert.Equal(0, exitCode);

        var points = await ReadRecordsAsync(outputPath);
        Assert.True(points.Count(record => record.Category == "park") > 3);
        Assert.Equal(2, points.Count(record => record.Category == "trail"));
        Assert.DoesNotContain(points, record => record.Name.Contains("Ponce", StringComparison.Ordinal));

        var parks = await ReadRecordsAsync(parkOutputPath);
        var trails = await ReadRecordsAsync(trailOutputPath);
        Assert.True(parks.Count > 3);
        Assert.Equal(2, trails.Count);
        Assert.Contains(parks, point => point.Types.Contains("polygon-densified-edge"));

        var features = await ReadFeatureRecordsAsync(featureOutputPath);
        Assert.Equal(4, features.Count);
        Assert.Contains(features, feature => feature.GeometryType == "collapsed-component" && feature.Category == "park");
        Assert.Contains(features, feature => feature.GeometryType == "collapsed-component" && feature.Category == "trail");
        var parkOutlineKml = await File.ReadAllTextAsync(parkOutlineKmlPath);
        Assert.Contains("Piedmont Park", parkOutlineKml, StringComparison.Ordinal);
        Assert.Contains("<Polygon>", parkOutlineKml, StringComparison.Ordinal);
        Assert.Contains("<LineString>", parkOutlineKml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_SanitizesInvalidXmlAndSkipsExactDuplicateInputs()
    {
        var tempDirectory = Directory.CreateTempSubdirectory();
        var kmzPath = Path.Combine(tempDirectory.FullName, "trail-a.kmz");
        var duplicateKmzPath = Path.Combine(tempDirectory.FullName, "trail-a-copy.kmz");
        var outputPath = Path.Combine(tempDirectory.FullName, "points.jsonl");

        var kml = """
                  <kml xmlns="http://www.opengis.net/kml/2.2">
                    <Document>
                      <Placemark>
                        <name>Atlanta Beltline</name>
                        <description><![CDATA[<html><body><table><tr><td>Project_Type</td><td>Multi-Use Trail</td></tr><tr><td>Length</td><td>2.0</td></tr></table></body></html>]]></description>
                        <LineString>
                          <coordinates>-84.3636,33.7528 -84.3637,33.7529</coordinates>
                        </LineString>
                      </Placemark>
                    </Document>
                  </kml>
                  """;

        await CreateKmzAsync(kmzPath, kml);
        File.Copy(kmzPath, duplicateKmzPath);

        var exitCode = await ArcGeometryExtractorProgram.RunAsync(
            ["--input", kmzPath, "--input", duplicateKmzPath, "--output", outputPath],
            TextWriter.Null,
            TextWriter.Null);

        Assert.Equal(0, exitCode);

        var points = await ReadRecordsAsync(outputPath);
        Assert.Equal(2, points.Count);
    }

    [Fact]
    public async Task RunAsync_FiltersTrailPlanInventoryToRealTrailSignals()
    {
        var tempDirectory = Directory.CreateTempSubdirectory();
        var inputPath = Path.Combine(tempDirectory.FullName, "Trail_Plan_Inventory.kml");
        var outputPath = Path.Combine(tempDirectory.FullName, "points.jsonl");

        await File.WriteAllTextAsync(
            inputPath,
            """
            <kml xmlns="http://www.opengis.net/kml/2.2">
              <Document>
                <Placemark>
                  <name>Atlanta Beltline</name>
                  <description><![CDATA[<html><body><table><tr><td>Project_Type</td><td>Multi-Use Trail</td></tr><tr><td>Length</td><td>2.0</td></tr></table></body></html>]]></description>
                  <LineString>
                    <coordinates>-84.3636,33.7528 -84.3637,33.7529</coordinates>
                  </LineString>
                </Placemark>
                <Placemark>
                  <name>CTP-84</name>
                  <description><![CDATA[<html><body><table><tr><td>Project_Type</td><td>Active Transportation</td></tr></table></body></html>]]></description>
                  <LineString>
                    <coordinates>-84.4600,33.7000 -84.4601,33.7001</coordinates>
                  </LineString>
                </Placemark>
                <Placemark>
                  <name>NULL</name>
                  <description><![CDATA[<html><body><table><tr><td>Project_Type</td><td>Null</td></tr><tr><td>Plan_</td><td>Chattahoochee RiverLands</td></tr><tr><td>Length</td><td>2.2</td></tr></table></body></html>]]></description>
                  <LineString>
                    <coordinates>-84.4700,33.7100 -84.4701,33.7101</coordinates>
                  </LineString>
                </Placemark>
              </Document>
            </kml>
            """);

        var exitCode = await ArcGeometryExtractorProgram.RunAsync(
            [
                "--input", inputPath,
                "--output", outputPath,
                "--minimum-park-square-feet", "1000",
                "--minimum-trail-miles", "1.65",
                "--minimum-combined-park-trail-miles", "0.01"
            ],
            TextWriter.Null,
            TextWriter.Null);

        Assert.Equal(0, exitCode);

        var points = await ReadRecordsAsync(outputPath);
        Assert.Equal(2, points.Count);
        Assert.All(points, point => Assert.Contains("Atlanta Beltline", point.Name, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_ExtractsMartaPointFeatures()
    {
        var tempDirectory = Directory.CreateTempSubdirectory();
        var inputPath = Path.Combine(tempDirectory.FullName, "MARTA_Rail_Stations.kml");
        var outputPath = Path.Combine(tempDirectory.FullName, "points.jsonl");

        await File.WriteAllTextAsync(
            inputPath,
            """
            <kml xmlns="http://www.opengis.net/kml/2.2">
              <Document>
                <Placemark>
                  <name>Decatur</name>
                  <description><![CDATA[<html><body><table><tr><td>STATION</td><td>Decatur</td></tr><tr><td>Station_Code</td><td>E6</td></tr><tr><td>LABEL</td><td>MARTA Decatur Station</td></tr></table></body></html>]]></description>
                  <Point>
                    <coordinates>-84.2953720094924,33.77451705802737,0</coordinates>
                  </Point>
                </Placemark>
              </Document>
            </kml>
            """);

        var exitCode = await ArcGeometryExtractorProgram.RunAsync(
            [
                "--input", inputPath,
                "--output", outputPath,
                "--minimum-park-square-feet", "1000",
                "--minimum-combined-park-trail-miles", "0.01"
            ],
            TextWriter.Null,
            TextWriter.Null);

        Assert.Equal(0, exitCode);

        var points = await ReadRecordsAsync(outputPath);
        var point = Assert.Single(points);
        Assert.Equal("marta", point.Category);
        Assert.Equal("Decatur", point.Name);
    }

    [Fact]
    public async Task RunAsync_ExtractsRealisticArcMartaFolderShape()
    {
        var tempDirectory = Directory.CreateTempSubdirectory();
        var kmzPath = Path.Combine(tempDirectory.FullName, "MARTA_Rail_Stations.kmz");
        var outputPath = Path.Combine(tempDirectory.FullName, "points.jsonl");

        var kml = """
                  <?xml version="1.0" encoding="UTF-8"?>
                  <kml xmlns="http://www.opengis.net/kml/2.2">
                    <Document id="MARTA_Rail_Stations">
                      <name>MARTA Rail Stations</name>
                      <Folder id="FeatureLayer0">
                        <name>MARTA Rail Stations</name>
                        <Placemark id="ID_00000">
                          <name>Decatur</name>
                          <snippet></snippet>
                          <description><![CDATA[<html><body><table border="1"><tr><th>Field Name</th><th>Field Value</th></tr><tr><td>STATION</td><td>Decatur</td></tr><tr><td>Station_Code</td><td>E6</td></tr><tr><td>Street</td><td>400 Church St</td></tr><tr><td>City</td><td>Decatur</td></tr><tr><td>Zip</td><td>30030</td></tr><tr><td>LABEL</td><td>MARTA Decatur Station</td></tr></table></body></html>]]></description>
                          <styleUrl>#IconStyle00</styleUrl>
                          <Point>
                            <extrude>0</extrude><altitudeMode>clampToGround</altitudeMode>
                            <coordinates> -84.2953720094924,33.77451705802737,0</coordinates>
                          </Point>
                        </Placemark>
                        <Placemark id="ID_00001">
                          <name>Avondale</name>
                          <description><![CDATA[<html><body><table border="1"><tr><th>Field Name</th><th>Field Value</th></tr><tr><td>STATION</td><td>Avondale</td></tr><tr><td>Station_Code</td><td>E7</td></tr><tr><td>LABEL</td><td>MARTA Avondale Station</td></tr></table></body></html>]]></description>
                          <Point>
                            <coordinates> -84.28233337395447,33.77502441308098,0</coordinates>
                          </Point>
                        </Placemark>
                      </Folder>
                    </Document>
                  </kml>
                  """;

        await CreateKmzAsync(kmzPath, kml);

        var exitCode = await ArcGeometryExtractorProgram.RunAsync(
            ["--input", kmzPath, "--output", outputPath],
            TextWriter.Null,
            TextWriter.Null);

        Assert.Equal(0, exitCode);

        var points = await ReadRecordsAsync(outputPath);
        Assert.Equal(2, points.Count);
        Assert.All(points, point => Assert.Equal("marta", point.Category));
        Assert.Contains(points, point => point.Name == "Decatur");
        Assert.Contains(points, point => point.Name == "Avondale");
    }

    [Fact]
    public async Task RunAsync_FiltersOutTinyParksAndShortTrails()
    {
        var tempDirectory = Directory.CreateTempSubdirectory();
        var inputPath = Path.Combine(tempDirectory.FullName, "arc.kml");
        var outputPath = Path.Combine(tempDirectory.FullName, "points.jsonl");
        var featureOutputPath = Path.Combine(tempDirectory.FullName, "features.jsonl");

        await File.WriteAllTextAsync(
            inputPath,
            """
            <kml xmlns="http://www.opengis.net/kml/2.2">
              <Document>
                <Placemark>
                  <name>Tiny Park</name>
                  <description><![CDATA[<html><body><table><tr><td>AllParks_Fields_Park_Size_SQFT</td><td>700</td></tr></table></body></html>]]></description>
                  <Polygon>
                    <outerBoundaryIs>
                      <LinearRing>
                        <coordinates>-84.3738,33.7851 -84.3737,33.7852 -84.3738,33.7851</coordinates>
                      </LinearRing>
                    </outerBoundaryIs>
                  </Polygon>
                </Placemark>
                <Placemark>
                  <name>Good Park</name>
                  <description><![CDATA[<html><body><table><tr><td>AllParks_Fields_Park_Size_SQFT</td><td>1000</td></tr></table></body></html>]]></description>
                  <Polygon>
                    <outerBoundaryIs>
                      <LinearRing>
                        <coordinates>-84.3845,33.7845 -84.3825,33.7845 -84.3825,33.7865 -84.3845,33.7865 -84.3845,33.7845</coordinates>
                      </LinearRing>
                    </outerBoundaryIs>
                  </Polygon>
                </Placemark>
                <Placemark>
                  <name>Short Trail</name>
                  <description><![CDATA[<html><body><table><tr><td>Project_Type</td><td>Multi-Use Trail</td></tr><tr><td>Length</td><td>1.2</td></tr></table></body></html>]]></description>
                  <LineString>
                    <coordinates>-84.3636,33.7528 -84.3637,33.7529</coordinates>
                  </LineString>
                </Placemark>
                <Placemark>
                  <name>Long Trail</name>
                  <description><![CDATA[<html><body><table><tr><td>Project_Type</td><td>Multi-Use Trail</td></tr><tr><td>Length</td><td>1.8</td></tr></table></body></html>]]></description>
                  <LineString>
                    <coordinates>-84.3536,33.7528 -84.3537,33.7529</coordinates>
                  </LineString>
                </Placemark>
              </Document>
            </kml>
            """);

        var exitCode = await ArcGeometryExtractorProgram.RunAsync(
            ["--input", inputPath, "--output", outputPath],
            TextWriter.Null,
            TextWriter.Null);

        Assert.Equal(0, exitCode);

        var points = await ReadRecordsAsync(outputPath);
        Assert.DoesNotContain(points, point => point.Name == "Tiny Park");
        Assert.DoesNotContain(points, point => point.Name == "Short Trail");
        Assert.Contains(points, point => point.Name == "Good Park");
        Assert.Contains(points, point => point.Name == "Long Trail");
    }

    [Fact]
    public async Task RunAsync_GeneratesDenseParkOutlineNearExpectedSouthwestCorner()
    {
        var tempDirectory = Directory.CreateTempSubdirectory();
        var inputPath = Path.Combine(tempDirectory.FullName, "parks.kml");
        var outputPath = Path.Combine(tempDirectory.FullName, "parks.jsonl");

        await File.WriteAllTextAsync(
            inputPath,
            """
            <kml xmlns="http://www.opengis.net/kml/2.2">
              <Document>
                <Placemark>
                  <name>Piedmont Park</name>
                  <description><![CDATA[<html><body><table><tr><td>AllParks_Fields_Park_Size_SQFT</td><td>8430161</td></tr></table></body></html>]]></description>
                  <Polygon>
                    <outerBoundaryIs>
                      <LinearRing>
                        <coordinates>-84.3781119781858,33.78195649884475 -84.3765,33.78195649884475 -84.3765,33.7835 -84.3781119781858,33.7835 -84.3781119781858,33.78195649884475</coordinates>
                      </LinearRing>
                    </outerBoundaryIs>
                  </Polygon>
                </Placemark>
              </Document>
            </kml>
            """);

        var exitCode = await ArcGeometryExtractorProgram.RunAsync(
            ["--input", inputPath, "--output", outputPath],
            TextWriter.Null,
            TextWriter.Null);

        Assert.Equal(0, exitCode);

        var points = await ReadRecordsAsync(outputPath);
        Assert.NotEmpty(points);
        Assert.Contains(
            points,
            point => GetDistanceFeet(point.Latitude, point.Longitude, 33.78195649884475, -84.3781119781858) <= 60d);
    }

    [Fact]
    public async Task RunAsync_CollapsesNearbyParkAndTrailAfterThresholding()
    {
        var tempDirectory = Directory.CreateTempSubdirectory();
        var inputPath = Path.Combine(tempDirectory.FullName, "arc.kml");
        var outputPath = Path.Combine(tempDirectory.FullName, "points.jsonl");
        var featureOutputPath = Path.Combine(tempDirectory.FullName, "features.jsonl");

        await File.WriteAllTextAsync(
            inputPath,
            """
            <kml xmlns="http://www.opengis.net/kml/2.2">
              <Document>
                <Placemark>
                  <name>Tiny Park</name>
                  <description><![CDATA[<html><body><table><tr><td>AllParks_Fields_Park_Size_SQFT</td><td>700</td></tr></table></body></html>]]></description>
                  <Polygon>
                    <outerBoundaryIs>
                      <LinearRing>
                        <coordinates>-84.3738,33.7851 -84.3732,33.7851 -84.3732,33.7857 -84.3738,33.7857 -84.3738,33.7851</coordinates>
                      </LinearRing>
                    </outerBoundaryIs>
                  </Polygon>
                </Placemark>
                <Placemark>
                  <name>Connected Trail</name>
                  <description><![CDATA[<html><body><table><tr><td>Project_Type</td><td>Multi-Use Trail</td></tr><tr><td>Length</td><td>1.8</td></tr></table></body></html>]]></description>
                  <LineString>
                    <coordinates>-84.3730,33.7854 -84.3725,33.7854</coordinates>
                  </LineString>
                </Placemark>
              </Document>
            </kml>
            """);

        var exitCode = await ArcGeometryExtractorProgram.RunAsync(
            ["--input", inputPath, "--output", outputPath, "--feature-output", featureOutputPath, "--enable-entity-collapse", "--maximum-collapse-gap-miles", "0.3"],
            TextWriter.Null,
            TextWriter.Null);

        Assert.Equal(0, exitCode);

        var points = await ReadRecordsAsync(outputPath);
        Assert.NotEmpty(points);
        Assert.All(points, point => Assert.Equal("trail", point.Category));
        Assert.All(points, point =>
        {
            Assert.NotNull(point.CollapsedEntityId);
            Assert.Contains("collapsed", point.CollapsedEntityId, StringComparison.OrdinalIgnoreCase);
        });
        Assert.All(points, point => Assert.Equal("Connected Trail", point.Name));

        var features = await ReadFeatureRecordsAsync(featureOutputPath);
        var collapsedFeature = Assert.Single(features.Where(feature => feature.GeometryType == "collapsed-component"));
        Assert.Contains("Connected Trail", collapsedFeature.SearchNames, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Tiny Park", collapsedFeature.SearchNames, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_CollapsesTransitivelyAcrossMultipleGreenEntities()
    {
        var tempDirectory = Directory.CreateTempSubdirectory();
        var inputPath = Path.Combine(tempDirectory.FullName, "arc.kml");
        var outputPath = Path.Combine(tempDirectory.FullName, "points.jsonl");
        var featureOutputPath = Path.Combine(tempDirectory.FullName, "features.jsonl");

        await File.WriteAllTextAsync(
            inputPath,
            """
            <kml xmlns="http://www.opengis.net/kml/2.2">
              <Document>
                <Placemark>
                  <name>Park A</name>
                  <description><![CDATA[<html><body><table><tr><td>AllParks_Fields_Park_Size_SQFT</td><td>600000</td></tr></table></body></html>]]></description>
                  <Polygon>
                    <outerBoundaryIs>
                      <LinearRing>
                        <coordinates>-84.3800,33.7800 -84.3794,33.7800 -84.3794,33.7806 -84.3800,33.7806 -84.3800,33.7800</coordinates>
                      </LinearRing>
                    </outerBoundaryIs>
                  </Polygon>
                </Placemark>
                <Placemark>
                  <name>Connector Trail</name>
                  <description><![CDATA[<html><body><table><tr><td>Project_Type</td><td>Multi-Use Trail</td></tr><tr><td>Length</td><td>2.0</td></tr></table></body></html>]]></description>
                  <LineString>
                    <coordinates>-84.3792,33.7803 -84.3770,33.7803</coordinates>
                  </LineString>
                </Placemark>
                <Placemark>
                  <name>Park B</name>
                  <description><![CDATA[<html><body><table><tr><td>AllParks_Fields_Park_Size_SQFT</td><td>650000</td></tr></table></body></html>]]></description>
                  <Polygon>
                    <outerBoundaryIs>
                      <LinearRing>
                        <coordinates>-84.3768,33.7800 -84.3762,33.7800 -84.3762,33.7806 -84.3768,33.7806 -84.3768,33.7800</coordinates>
                      </LinearRing>
                    </outerBoundaryIs>
                  </Polygon>
                </Placemark>
              </Document>
            </kml>
            """);

        var exitCode = await ArcGeometryExtractorProgram.RunAsync(
            ["--input", inputPath, "--output", outputPath, "--feature-output", featureOutputPath, "--enable-entity-collapse", "--maximum-collapse-gap-miles", "0.3"],
            TextWriter.Null,
            TextWriter.Null);

        Assert.Equal(0, exitCode);

        var points = await ReadRecordsAsync(outputPath);
        Assert.NotEmpty(points);
        var collapsedIds = points.Select(point => point.CollapsedEntityId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.Single(collapsedIds);

        var features = await ReadFeatureRecordsAsync(featureOutputPath);
        var collapsedFeature = Assert.Single(features.Where(feature => feature.GeometryType == "collapsed-component"));
        Assert.Contains("Park A", collapsedFeature.SearchNames, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Connector Trail", collapsedFeature.SearchNames, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Park B", collapsedFeature.SearchNames, StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<List<NormalizedPlaceRecord>> ReadRecordsAsync(string path) =>
        (await File.ReadAllLinesAsync(path))
        .Where(line => !string.IsNullOrWhiteSpace(line))
        .Select(line => JsonSerializer.Deserialize<NormalizedPlaceRecord>(line, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }))
        .OfType<NormalizedPlaceRecord>()
        .ToList();

    private static async Task<List<FeatureRecordView>> ReadFeatureRecordsAsync(string path) =>
        (await File.ReadAllLinesAsync(path))
        .Where(line => !string.IsNullOrWhiteSpace(line))
        .Select(line => JsonSerializer.Deserialize<FeatureRecordView>(line, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }))
        .OfType<FeatureRecordView>()
        .ToList();

    private static async Task CreateKmzAsync(string path, string kml)
    {
        await using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false);
        var entry = archive.CreateEntry("doc.kml");
        await using var entryStream = entry.Open();
        await using var writer = new StreamWriter(entryStream);
        await writer.WriteAsync(kml);
    }

    private static double GetDistanceFeet(double lat1, double lon1, double lat2, double lon2)
    {
        const double earthRadiusFeet = 20_925_524.9d;
        var latitudeDelta = DegreesToRadians(lat2 - lat1);
        var longitudeDelta = DegreesToRadians(lon2 - lon1);
        var startLatitude = DegreesToRadians(lat1);
        var endLatitude = DegreesToRadians(lat2);
        var sinLatitude = Math.Sin(latitudeDelta / 2d);
        var sinLongitude = Math.Sin(longitudeDelta / 2d);
        var a = (sinLatitude * sinLatitude)
                + (Math.Cos(startLatitude) * Math.Cos(endLatitude) * sinLongitude * sinLongitude);
        var c = 2d * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1d - a));
        return earthRadiusFeet * c;
    }

    private static double DegreesToRadians(double degrees) =>
        degrees * (Math.PI / 180d);

    private sealed record FeatureRecordView(
        string Name,
        string Category,
        string GeometryType,
        string? CollapsedEntityId,
        IReadOnlyList<string> SearchNames,
        IReadOnlyList<string> MemberNames);
}
