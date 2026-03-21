using System.Text.Json;
using KmlGenerator.Core.Models;

namespace KmlGenerator.Tests;

public sealed class KmlTilerRunnerTests
{
    [Fact]
    public async Task RunAsync_WritesOnlyTilesContainingAllCategories()
    {
        var tempDirectory = Directory.CreateTempSubdirectory();
        var inputPath = Path.Combine(tempDirectory.FullName, "request.json");
        var outputDirectory = Path.Combine(tempDirectory.FullName, "tiles");

        var request = new GenerateKmlRequest
        {
            Step = 0.01d,
            PaddingDegrees = 0.005d,
            RadiusMiles = 1d,
            Locations =
            [
                new LocationInput { Latitude = 33.95d, Longitude = -84.54d, Category = "gym" },
                new LocationInput { Latitude = 33.949d, Longitude = -84.539d, Category = "grocery" },
                new LocationInput { Latitude = 33.80d, Longitude = -84.20d, Category = "gym" }
            ]
        };

        await File.WriteAllTextAsync(inputPath, JsonSerializer.Serialize(request));

        var exitCode = await KmlTilerRunner.RunAsync(
            [
                "--input", inputPath,
                "--output-dir", outputDirectory,
                "--north", "34.0",
                "--south", "33.7",
                "--west", "-84.6",
                "--east", "-84.1",
                "--lat-step", "0.15",
                "--lon-step", "0.25"
            ],
            TextWriter.Null,
            TextWriter.Null);

        Assert.Equal(0, exitCode);

        var summaryPath = Path.Combine(outputDirectory, "tiles-summary.json");
        Assert.True(File.Exists(summaryPath));

        var summary = await File.ReadAllTextAsync(summaryPath);
        Assert.Contains("\"status\": \"written\"", summary);
        Assert.Contains("\"status\": \"missing_categories\"", summary);
    }
}
