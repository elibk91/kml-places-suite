using System.Net;
using System.Text;

namespace KmlGenerator.Tests;

public sealed class ResearchPointResolverRunnerTests
{
    [Fact]
    public async Task RunAsync_ResolvesResearchTargetsIntoJsonl()
    {
        const string variableName = "RESEARCH_POINT_RESOLVER_TEST_KEY";
        Environment.SetEnvironmentVariable(variableName, "test-key");

        var tempDirectory = Directory.CreateTempSubdirectory();
        var configPath = Path.Combine(tempDirectory.FullName, "research-targets.json");
        var outputPath = Path.Combine(tempDirectory.FullName, "resolved.jsonl");

        await File.WriteAllTextAsync(
            configPath,
            """
            {
              "bounds": {
                "north": 33.80,
                "south": 33.70,
                "east": -84.30,
                "west": -84.40
              },
              "secrets": {
                "provider": "Local",
                "environmentVariableName": "RESEARCH_POINT_RESOLVER_TEST_KEY"
              },
              "targets": [
                {
                  "label": "Piedmont Park Southwest Entrance",
                  "category": "park",
                  "query": "10th St NE and Charles Allen Dr NE, Atlanta, GA"
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
                          "id": "park-1",
                          "displayName": { "text": "Piedmont Park, Charles Allen Drive Entrance" },
                          "formattedAddress": "929 Charles Allen Dr, Atlanta, GA 30306, USA",
                          "location": { "latitude": 33.7818847, "longitude": -84.3727706 },
                          "types": [ "park" ]
                        }
                      ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            });

        var exitCode = await ResearchPointResolverRunner.RunAsync(
            ["--config", configPath, "--output", outputPath],
            TextWriter.Null,
            TextWriter.Null,
            new HttpClient(handler));

        Assert.Equal(0, exitCode);
        var lines = await File.ReadAllLinesAsync(outputPath);
        Assert.Single(lines);
        Assert.Contains("\"name\":\"Piedmont Park Southwest Entrance\"", lines[0]);

        Environment.SetEnvironmentVariable(variableName, null);
    }
}
