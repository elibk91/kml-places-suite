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

    [Fact]
    public async Task RunAsync_FiltersBroadParkTrailNoise()
    {
        const string variableName = "MASTER_LIST_BUILDER_FILTER_TEST_KEY";
        Environment.SetEnvironmentVariable(variableName, "test-key");

        var tempDirectory = Directory.CreateTempSubdirectory();
        var configPath = Path.Combine(tempDirectory.FullName, "master-list-config.json");
        var outputDirectory = Path.Combine(tempDirectory.FullName, "master-lists");

        await File.WriteAllTextAsync(
            configPath,
            $$"""
            {
              "bounds": {
                "north": 33.80,
                "south": 33.70,
                "east": -84.30,
                "west": -84.40
              },
              "secrets": {
                "provider": "Local",
                "environmentVariableName": "{{variableName}}"
              },
              "tileLatitudeStep": 0.05,
              "tileLongitudeStep": 0.05,
              "groups": [
                {
                  "name": "parks-trails",
                  "mode": "tiled",
                  "searches": [
                    { "query": "park", "category": "park" }
                  ]
                }
              ]
            }
            """);

        var handler = new StubHttpMessageHandler(_ => JsonResponse(
            """
            {
              "places": [
                {
                  "id": "park-1",
                  "displayName": { "text": "Historic Fourth Ward Park" },
                  "formattedAddress": "680 Dallas St NE, Atlanta, GA 30308, USA",
                  "location": { "latitude": 33.7677, "longitude": -84.3718 },
                  "types": [ "park", "tourist_attraction" ]
                },
                {
                  "id": "noise-1",
                  "displayName": { "text": "Parkside Apartments" },
                  "formattedAddress": "101 Example St NE, Atlanta, GA 30308, USA",
                  "location": { "latitude": 33.7670, "longitude": -84.3720 },
                  "types": [ "apartment_building" ]
                },
                {
                  "id": "noise-2",
                  "displayName": { "text": "Glover Park Brewery" },
                  "formattedAddress": "65 Atlanta St SE, Marietta, GA 30060, USA",
                  "location": { "latitude": 33.9501961, "longitude": -84.5486682 },
                  "types": [ "brewery", "food" ]
                }
              ]
            }
            """));

        var exitCode = await MasterListBuilderRunner.RunAsync(
            ["--config", configPath, "--output-dir", outputDirectory],
            TextWriter.Null,
            TextWriter.Null,
            new HttpClient(handler));

        Assert.Equal(0, exitCode);
        var lines = await File.ReadAllLinesAsync(Path.Combine(outputDirectory, "parks-trails-master.jsonl"));
        Assert.Single(lines);
        Assert.Contains("Historic Fourth Ward Park", lines[0]);

        Environment.SetEnvironmentVariable(variableName, null);
    }

    [Fact]
    public async Task RunAsync_FiltersMartaResultsToMatchingStationName()
    {
        const string variableName = "MASTER_LIST_BUILDER_MARTA_FILTER_TEST_KEY";
        Environment.SetEnvironmentVariable(variableName, "test-key");

        var tempDirectory = Directory.CreateTempSubdirectory();
        var configPath = Path.Combine(tempDirectory.FullName, "master-list-config.json");
        var outputDirectory = Path.Combine(tempDirectory.FullName, "master-lists");

        await File.WriteAllTextAsync(
            configPath,
            $$"""
            {
              "bounds": {
                "north": 33.95,
                "south": 33.69,
                "east": -84.09,
                "west": -84.55
              },
              "secrets": {
                "provider": "Local",
                "environmentVariableName": "{{variableName}}"
              },
              "tileLatitudeStep": 0.05,
              "tileLongitudeStep": 0.05,
              "groups": [
                {
                  "name": "marta",
                  "mode": "direct",
                  "searches": [
                    { "query": "MARTA Airport Station", "category": "marta" }
                  ]
                }
              ]
            }
            """);

        var handler = new StubHttpMessageHandler(_ => JsonResponse(
            """
            {
              "places": [
                {
                  "id": "wrong-1",
                  "displayName": { "text": "Sandy Springs Station" },
                  "formattedAddress": "1101 Mount Vernon Hwy, Atlanta, GA 30338, USA",
                  "location": { "latitude": 33.9331546, "longitude": -84.3525154 },
                  "types": [ "transit_station" ]
                },
                {
                  "id": "right-1",
                  "displayName": { "text": "Airport Station" },
                  "formattedAddress": "6000 N Terminal Pkwy Suite 4000, Atlanta, GA 30320, USA",
                  "location": { "latitude": 33.6405539, "longitude": -84.4461985 },
                  "types": [ "subway_station", "transit_station" ]
                }
              ]
            }
            """));

        var exitCode = await MasterListBuilderRunner.RunAsync(
            ["--config", configPath, "--output-dir", outputDirectory],
            TextWriter.Null,
            TextWriter.Null,
            new HttpClient(handler));

        Assert.Equal(0, exitCode);
        var lines = await File.ReadAllLinesAsync(Path.Combine(outputDirectory, "marta-master.jsonl"));
        Assert.Single(lines);
        Assert.Contains("Airport Station", lines[0]);

        Environment.SetEnvironmentVariable(variableName, null);
    }

    [Fact]
    public async Task RunAsync_ChoosesBestMartaStationVariantPerQuery()
    {
        const string variableName = "MASTER_LIST_BUILDER_MARTA_VARIANT_TEST_KEY";
        Environment.SetEnvironmentVariable(variableName, "test-key");

        var tempDirectory = Directory.CreateTempSubdirectory();
        var configPath = Path.Combine(tempDirectory.FullName, "master-list-config.json");
        var outputDirectory = Path.Combine(tempDirectory.FullName, "master-lists");

        await File.WriteAllTextAsync(
            configPath,
            $$"""
            {
              "bounds": {
                "north": 33.95,
                "south": 33.69,
                "east": -84.09,
                "west": -84.55
              },
              "secrets": {
                "provider": "Local",
                "environmentVariableName": "{{variableName}}"
              },
              "tileLatitudeStep": 0.05,
              "tileLongitudeStep": 0.05,
              "groups": [
                {
                  "name": "marta",
                  "mode": "direct",
                  "searches": [
                    { "query": "MARTA Arts Center Station", "category": "marta" }
                  ]
                }
              ]
            }
            """);

        var handler = new StubHttpMessageHandler(_ => JsonResponse(
            """
            {
              "places": [
                {
                  "id": "bus-1",
                  "displayName": { "text": "Arts Center Marta Station" },
                  "formattedAddress": "Atlanta, GA 30309, USA",
                  "location": { "latitude": 33.7894354, "longitude": -84.3877153 },
                  "types": [ "bus_stop", "transit_stop", "transit_station" ]
                },
                {
                  "id": "rail-1",
                  "displayName": { "text": "MARTA Arts Center Station" },
                  "formattedAddress": "Atlanta, GA 30309, USA",
                  "location": { "latitude": 33.789304, "longitude": -84.3870078 },
                  "types": [ "transit_station" ]
                }
              ]
            }
            """));

        var exitCode = await MasterListBuilderRunner.RunAsync(
            ["--config", configPath, "--output-dir", outputDirectory],
            TextWriter.Null,
            TextWriter.Null,
            new HttpClient(handler));

        Assert.Equal(0, exitCode);
        var lines = await File.ReadAllLinesAsync(Path.Combine(outputDirectory, "marta-master.jsonl"));
        Assert.Single(lines);
        Assert.Contains("MARTA Arts Center Station", lines[0]);

        Environment.SetEnvironmentVariable(variableName, null);
    }

    private static HttpResponseMessage JsonResponse(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
}
