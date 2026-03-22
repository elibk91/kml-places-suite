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
                new LocationInput { Latitude = 40.0d, Longitude = -73.0d, Category = "coffee" },
                new LocationInput { Latitude = 40.0d, Longitude = -73.0d, Category = "grocery" }
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
}
