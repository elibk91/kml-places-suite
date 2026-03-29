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
                new LocationInput { Latitude = 33.95d, Longitude = -84.54d, Category = "gym", Label = "gym-a" },
                new LocationInput { Latitude = 33.949d, Longitude = -84.539d, Category = "grocery", Label = "grocery-a" },
                new LocationInput { Latitude = 33.80d, Longitude = -84.20d, Category = "gym", Label = "gym-b" }
            ]
        };

        await File.WriteAllTextAsync(inputPath, JsonSerializer.Serialize(request));

        var exitCode = await KmlTilerProgram.RunAsync(
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

    [Fact]
    public async Task RunAsync_AssignsGeometryFeatures_ToTilesUsingBufferedBounds()
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
                new LocationInput { Latitude = 34.0d, Longitude = -84.6d, Category = "gym", Label = "north-west" },
                new LocationInput { Latitude = 33.85d, Longitude = -84.35d, Category = "gym", Label = "interior-boundary" },
                new LocationInput { Latitude = 33.7d, Longitude = -84.1d, Category = "gym", Label = "south-east" }
            ]
        };

        await File.WriteAllTextAsync(inputPath, JsonSerializer.Serialize(request));

        var exitCode = await KmlTilerProgram.RunAsync(
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
        var summary = JsonSerializer.Deserialize<List<TileSummaryContract>>(
            await File.ReadAllTextAsync(summaryPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(summary);
        Assert.Contains(summary, tile => tile.Row == 0 && tile.Column == 0 && tile.PointCount == 2);
        Assert.Contains(summary, tile => tile.Row == 1 && tile.Column == 1 && tile.PointCount == 2);
        Assert.Contains(summary, tile => tile.Row == 1 && tile.Column == 1 && tile.Status == "written");
        Assert.Contains(summary, tile => tile.Row == 0 && tile.Column == 1 && tile.PointCount == 1);
    }

    private sealed class TileSummaryContract
    {
        public int Row { get; init; }

        public int Column { get; init; }

        public int PointCount { get; init; }

        public string Status { get; init; } = string.Empty;
    }
}
