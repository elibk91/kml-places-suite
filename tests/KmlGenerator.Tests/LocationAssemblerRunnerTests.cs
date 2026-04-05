using System.Text.Json;
using KmlGenerator.Core.Models;

namespace KmlGenerator.Tests;

public sealed class LocationAssemblerRunnerTests
{
    [Fact]
    public async Task RunAsync_ConvertsJsonlIntoGenerateKmlRequest()
    {
        var tempDirectory = Directory.CreateTempSubdirectory();
        var inputPath = Path.Combine(tempDirectory.FullName, "places-a.jsonl");
        var secondInputPath = Path.Combine(tempDirectory.FullName, "places-b.jsonl");
        var outputPath = Path.Combine(tempDirectory.FullName, "request.json");

        await File.WriteAllLinesAsync(
            inputPath,
            [
                """{"query":"Piedmont Park","category":"park","placeId":"park-1","name":"Piedmont Park","formattedAddress":"1320 Monroe Dr NE, Atlanta, GA","latitude":33.7851,"longitude":-84.3738,"types":["park"],"sourceQueryType":"base"}""",
                """{"query":"Piedmont Park entrance","category":"park","placeId":"park-entrance-1","name":"Piedmont Park | Monroe Dr","formattedAddress":"1320 Monroe Dr NE, Atlanta, GA","latitude":33.7851,"longitude":-84.3738,"types":["park"],"sourceQueryType":"expanded"}"""
            ]);

        await File.WriteAllLinesAsync(
            secondInputPath,
            [
                """{"query":"Atlanta BeltLine Eastside Trail access","category":"trail","placeId":"trail-1","name":"Atlanta BeltLine Eastside Trail","formattedAddress":"Atlanta, GA","latitude":33.7648,"longitude":-84.3680,"types":["park"],"sourceQueryType":"expanded"}"""
            ]);

        var exitCode = await LocationAssemblerProgram.RunAsync(
            ["--input", inputPath, "--input", secondInputPath, "--output", outputPath],
            TextWriter.Null,
            TextWriter.Null);

        Assert.Equal(0, exitCode);

        var request = JsonSerializer.Deserialize<GenerateKmlRequest>(await File.ReadAllTextAsync(outputPath), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(request);
        Assert.Equal(3, request.Locations.Count);
        Assert.Contains(request.Locations, location => location.Label == "Piedmont Park");
        Assert.Contains(request.Locations, location => location.Label == "Piedmont Park | Monroe Dr");
        Assert.Contains(request.Locations, location => location.Label == "Atlanta BeltLine Eastside Trail");
    }

    [Fact]
    public async Task RunAsync_AppliesConfiguredCategoryGrouping()
    {
        var tempDirectory = Directory.CreateTempSubdirectory();
        var inputPath = Path.Combine(tempDirectory.FullName, "places-a.jsonl");
        var secondInputPath = Path.Combine(tempDirectory.FullName, "places-b.jsonl");
        var outputPath = Path.Combine(tempDirectory.FullName, "request.json");
        var categoryConfigPath = Path.Combine(tempDirectory.FullName, "category-config.json");

        await File.WriteAllLinesAsync(
            inputPath,
            [
                """{"query":"Piedmont Park","category":"park","placeId":"park-1","name":"Piedmont Park","formattedAddress":"1320 Monroe Dr NE, Atlanta, GA","latitude":33.7851,"longitude":-84.3738,"types":["park"],"sourceQueryType":"base"}"""
            ]);

        await File.WriteAllLinesAsync(
            secondInputPath,
            [
                """{"query":"Atlanta BeltLine Eastside Trail access","category":"trail","placeId":"trail-1","name":"Atlanta BeltLine Eastside Trail","formattedAddress":"Atlanta, GA","latitude":33.7648,"longitude":-84.3680,"types":["park"],"sourceQueryType":"expanded"}"""
            ]);

        await File.WriteAllTextAsync(
            categoryConfigPath,
            """
            {
              "includedCategories": ["park"],
              "categoryGroups": [
                {
                  "targetCategory": "park",
                  "sourceCategories": ["park", "trail"]
                }
              ]
            }
            """);

        var exitCode = await LocationAssemblerProgram.RunAsync(
            ["--input", inputPath, "--input", secondInputPath, "--output", outputPath, "--category-config", categoryConfigPath],
            TextWriter.Null,
            TextWriter.Null);

        Assert.Equal(0, exitCode);

        var request = JsonSerializer.Deserialize<GenerateKmlRequest>(await File.ReadAllTextAsync(outputPath), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(request);
        Assert.Equal(2, request.Locations.Count);
        Assert.All(request.Locations, location => Assert.Equal("park", location.Category));
    }
}
