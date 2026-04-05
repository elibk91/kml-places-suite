using System.Text.Json;
using KmlGenerator.Core.Models;

namespace KmlGenerator.Tests;

public sealed class KmlConsoleRunnerTests
{
    [Fact]
    public async Task RunAsync_WritesKmlFile()
    {
        var tempDirectory = Directory.CreateTempSubdirectory();
        var inputPath = Path.Combine(tempDirectory.FullName, "request.json");
        var outputPath = Path.Combine(tempDirectory.FullName, "outline.kml");

        var request = new GenerateKmlRequest
        {
            Step = 0.01d,
            PaddingDegrees = 0.01d,
            RadiusMiles = 1d,
            Locations =
            [
                new LocationInput { Latitude = 40.0d, Longitude = -73.0d, Category = "coffee", Label = "Coffee" },
                new LocationInput { Latitude = 40.0d, Longitude = -73.0d, Category = "grocery", Label = "Grocery" }
            ]
        };

        await File.WriteAllTextAsync(inputPath, JsonSerializer.Serialize(request));

        var exitCode = await KmlConsoleProgram.RunAsync(
            ["--input", inputPath, "--output", outputPath],
            TextWriter.Null,
            TextWriter.Null);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(outputPath));
        Assert.Contains("<kml", await File.ReadAllTextAsync(outputPath));
    }

    [Fact]
    public async Task RunAsync_DiagnoseCoverage_PrintsMissingCategories()
    {
        var tempDirectory = Directory.CreateTempSubdirectory();
        var inputPath = Path.Combine(tempDirectory.FullName, "request.json");
        var output = new StringWriter();

        var request = new GenerateKmlRequest
        {
            Step = 0.01d,
            PaddingDegrees = 0.01d,
            RadiusMiles = 1d,
            Locations =
            [
                new LocationInput { Latitude = 40.0d, Longitude = -73.0d, Category = "coffee", Label = "Near Coffee" },
                new LocationInput { Latitude = 41.0d, Longitude = -74.0d, Category = "grocery", Label = "Far Grocery" }
            ]
        };

        await File.WriteAllTextAsync(inputPath, JsonSerializer.Serialize(request));

        var exitCode = await KmlConsoleProgram.RunAsync(
            [
                "--input", inputPath,
                "--diagnose-latitude", "40.0",
                "--diagnose-longitude", "-73.0",
                "--diagnose-radius-miles", "0.5",
                "--diagnose-top-per-category", "2"
            ],
            output,
            TextWriter.Null);

        var text = output.ToString();
        Assert.Equal(0, exitCode);
        Assert.Contains("[coffee]", text);
        Assert.Contains("[grocery]", text);
        Assert.Contains("Missing within radius: grocery", text);
    }
}
