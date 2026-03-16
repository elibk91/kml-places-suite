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
                  "query": "Piedmont Park",
                  "category": "park",
                  "expansion": {
                    "enabled": true,
                    "templates": [ "entrance" ]
                  }
                }
              ]
            }
            """);

        var handler = new StubHttpMessageHandler(request =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();

            if (body.Contains("Piedmont Park entrance", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "places": [
                            {
                              "id": "park-entrance-1",
                              "displayName": { "text": "Piedmont Park" },
                              "formattedAddress": "1320 Monroe Dr NE, Atlanta, GA",
                              "location": { "latitude": 40.11, "longitude": -73.91 },
                              "types": [ "park" ]
                            }
                          ]
                        }
                        """,
                        Encoding.UTF8,
                        "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "places": [
                        {
                          "id": "park-1",
                          "displayName": { "text": "Piedmont Park" },
                          "formattedAddress": "1071 Piedmont Ave NE, Atlanta, GA",
                          "location": { "latitude": 40.1, "longitude": -73.9 },
                          "types": [ "park" ]
                        }
                      ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        });

        var exitCode = await PlacesGathererRunner.RunAsync(
            ["--config", configPath, "--output", outputPath],
            TextWriter.Null,
            TextWriter.Null,
            new HttpClient(handler));

        Assert.Equal(0, exitCode);
        var lines = await File.ReadAllLinesAsync(outputPath);
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"sourceQueryType\":\"base\"", lines[0]);
        Assert.Contains("\"sourceQueryType\":\"expanded\"", lines[1]);
        Assert.Contains("\"name\":\"Piedmont Park | Piedmont Ave NE\"", lines[0]);
        Assert.Contains("\"name\":\"Piedmont Park | Monroe Dr NE\"", lines[1]);

        Environment.SetEnvironmentVariable(variableName, null);
    }
}
