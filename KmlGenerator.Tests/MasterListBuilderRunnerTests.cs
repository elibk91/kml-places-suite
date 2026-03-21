using System.Net;
using System.Text;
using System.Text.Json;

namespace KmlGenerator.Tests;

public sealed class MasterListBuilderRunnerTests
{
    [Fact]
    public async Task RunAsync_WritesCategorySpecificMasterLists()
    {
        const string variableName = "MASTER_LIST_BUILDER_TEST_KEY";
        Environment.SetEnvironmentVariable(variableName, "test-key");

        var tempDirectory = Directory.CreateTempSubdirectory();
        var configPath = Path.Combine(tempDirectory.FullName, "master-list-config.json");
        var outputDirectory = Path.Combine(tempDirectory.FullName, "master-lists");

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
                "environmentVariableName": "MASTER_LIST_BUILDER_TEST_KEY"
              },
              "tileLatitudeStep": 0.05,
              "tileLongitudeStep": 0.05,
              "groups": [
                {
                  "name": "gyms",
                  "mode": "tiled",
                  "searches": [
                    { "query": "Planet Fitness", "category": "gym" }
                  ]
                },
                {
                  "name": "marta",
                  "mode": "direct",
                  "searches": [
                    { "query": "MARTA Midtown Station", "category": "marta" }
                  ]
                }
              ]
            }
            """);

        var handler = new StubHttpMessageHandler(request =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();

            if (body.Contains("MARTA Midtown Station", StringComparison.Ordinal))
            {
                return JsonResponse(
                    """
                    {
                      "places": [
                        {
                          "id": "marta-1",
                          "displayName": { "text": "Midtown Station" },
                          "formattedAddress": "Atlanta, GA 30309, USA",
                          "location": { "latitude": 33.781355, "longitude": -84.386353 },
                          "types": [ "transit_station" ]
                        }
                      ]
                    }
                    """);
            }

            return JsonResponse(
                """
                {
                  "places": [
                    {
                      "id": "gym-1",
                      "displayName": { "text": "Planet Fitness" },
                      "formattedAddress": "675 W Peachtree St NW, Atlanta, GA 30308, USA",
                      "location": { "latitude": 33.773463, "longitude": -84.3864977 },
                      "types": [ "gym" ]
                    }
                  ]
                }
                """);
        });

        var exitCode = await MasterListBuilderRunner.RunAsync(
            ["--config", configPath, "--output-dir", outputDirectory],
            TextWriter.Null,
            TextWriter.Null,
            new HttpClient(handler));

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(Path.Combine(outputDirectory, "gyms-master.jsonl")));
        Assert.True(File.Exists(Path.Combine(outputDirectory, "marta-master.jsonl")));
        Assert.True(File.Exists(Path.Combine(outputDirectory, "master-lists-summary.json")));

        var gymLines = await File.ReadAllLinesAsync(Path.Combine(outputDirectory, "gyms-master.jsonl"));
        Assert.Single(gymLines);

        Environment.SetEnvironmentVariable(variableName, null);
    }

    private static HttpResponseMessage JsonResponse(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
}
