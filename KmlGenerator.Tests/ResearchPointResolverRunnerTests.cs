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

    [Fact]
    public async Task RunAsync_PrefersParkTrailMatchesOverNearbyNoise()
    {
        const string variableName = "RESEARCH_POINT_RESOLVER_SCORING_TEST_KEY";
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
                "environmentVariableName": "RESEARCH_POINT_RESOLVER_SCORING_TEST_KEY"
              },
              "targets": [
                {
                  "label": "Historic Fourth Ward Park North Entrance",
                  "category": "park",
                  "query": "665 North Ave NE, Atlanta, GA 30308"
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
                          "id": "noise-1",
                          "displayName": { "text": "North Avenue Medical Clinic" },
                          "formattedAddress": "665 North Avenue NE, Atlanta, GA 30308, USA",
                          "location": { "latitude": 33.7692591, "longitude": -84.3648811 },
                          "types": [ "medical_clinic", "health" ]
                        },
                        {
                          "id": "park-1",
                          "displayName": { "text": "Historic Fourth Ward Park" },
                          "formattedAddress": "680 Dallas St NE, Atlanta, GA 30308, USA",
                          "location": { "latitude": 33.7694043, "longitude": -84.3647968 },
                          "types": [ "city_park", "park", "tourist_attraction" ]
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
        Assert.Contains("\"name\":\"Historic Fourth Ward Park North Entrance\"", lines[0]);
        Assert.Contains("\"placeId\":\"park-1\"", lines[0]);

        Environment.SetEnvironmentVariable(variableName, null);
    }
}
