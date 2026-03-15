using System.Net;
using System.Text;

namespace PlacesGatherer.Console.Tests;

public sealed class PlacesGathererRunnerTests
{
    [Fact]
    public async Task RunAsync_WritesJsonlFile_WithStubbedApi()
    {
        const string variableName = "PLACES_GATHERER_RUNNER_TEST_KEY";
        Environment.SetEnvironmentVariable(variableName, "test-key");

        var tempDirectory = Directory.CreateTempSubdirectory();
        var configPath = Path.Combine(tempDirectory.FullName, "search-config.json");
        var outputPath = Path.Combine(tempDirectory.FullName, "places.jsonl");

        await File.WriteAllTextAsync(
            configPath,
            """
            {
              "bounds": {
                "north": 40.2,
                "south": 40.0,
                "east": -73.8,
                "west": -74.0
              },
              "secrets": {
                "provider": "Local",
                "environmentVariableName": "PLACES_GATHERER_RUNNER_TEST_KEY"
              },
              "searches": [
                {
                  "query": "Starbucks",
                  "category": "coffee"
                }
              ]
            }
            """);

        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "places": [
                        {
                          "id": "abc123",
                          "displayName": { "text": "Starbucks" },
                          "formattedAddress": "123 Main St",
                          "location": { "latitude": 40.1, "longitude": -73.9 },
                          "types": [ "cafe" ]
                        }
                      ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            });

        var exitCode = await PlacesGathererRunner.RunAsync(
            ["--config", configPath, "--output", outputPath],
            TextWriter.Null,
            TextWriter.Null,
            new HttpClient(handler));

        Assert.Equal(0, exitCode);
        var lines = await File.ReadAllLinesAsync(outputPath);
        Assert.Single(lines);
        Assert.Contains("\"query\":\"Starbucks\"", lines[0]);

        Environment.SetEnvironmentVariable(variableName, null);
    }
}
