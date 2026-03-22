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
                }
              ]
            }
            """);

        var handler = new StubHttpMessageHandler(_ => JsonResponse(
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
            """));

        var exitCode = await MasterListBuilderProgram.RunAsync(
            ["--config", configPath, "--output-dir", outputDirectory],
            TextWriter.Null,
            TextWriter.Null,
            new HttpClient(handler));

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(Path.Combine(outputDirectory, "gyms-master.jsonl")));
        Assert.True(File.Exists(Path.Combine(outputDirectory, "master-lists-summary.json")));

        var gymLines = await File.ReadAllLinesAsync(Path.Combine(outputDirectory, "gyms-master.jsonl"));
        Assert.Single(gymLines);

        Environment.SetEnvironmentVariable(variableName, null);
    }

    [Fact]
    public async Task RunAsync_RejectsUnsupportedGroupName()
    {
        const string variableName = "MASTER_LIST_BUILDER_UNSUPPORTED_GROUP_TEST_KEY";
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
                  "name": "marta",
                  "mode": "tiled",
                  "searches": [
                    { "query": "MARTA Midtown Station", "category": "marta" }
                  ]
                }
              ]
            }
            """);

        var exitCode = await MasterListBuilderProgram.RunAsync(
            ["--config", configPath, "--output-dir", outputDirectory],
            TextWriter.Null,
            TextWriter.Null,
            new HttpClient(new StubHttpMessageHandler(_ => JsonResponse("""{ "places": [] }"""))));

        Assert.Equal(1, exitCode);

        Environment.SetEnvironmentVariable(variableName, null);
    }

    [Fact]
    public async Task RunAsync_FiltersGymResultsToMatchingChainName()
    {
        const string variableName = "MASTER_LIST_BUILDER_GYM_CHAIN_TEST_KEY";
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
                  "name": "gyms",
                  "mode": "tiled",
                  "searches": [
                    { "query": "LA Fitness", "category": "gym" }
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
                  "displayName": { "text": "BACH Fitness" },
                  "formattedAddress": "1745 Peachtree St NE, Atlanta, GA 30309, USA",
                  "location": { "latitude": 33.7980, "longitude": -84.3920 },
                  "types": [ "gym" ]
                },
                {
                  "id": "right-1",
                  "displayName": { "text": "LA Fitness" },
                  "formattedAddress": "3535 Peachtree Rd NE Ste 300, Atlanta, GA 30326, USA",
                  "location": { "latitude": 33.8510, "longitude": -84.3620 },
                  "types": [ "gym" ]
                }
              ]
            }
            """));

        var exitCode = await MasterListBuilderProgram.RunAsync(
            ["--config", configPath, "--output-dir", outputDirectory],
            TextWriter.Null,
            TextWriter.Null,
            new HttpClient(handler));

        Assert.Equal(0, exitCode);
        var lines = await File.ReadAllLinesAsync(Path.Combine(outputDirectory, "gyms-master.jsonl"));
        Assert.Single(lines);
        Assert.Contains("LA Fitness", lines[0]);
        Assert.DoesNotContain("BACH Fitness", lines[0], StringComparison.Ordinal);

        Environment.SetEnvironmentVariable(variableName, null);
    }

    [Fact]
    public async Task RunAsync_FiltersGroceryResultsToMatchingChainName()
    {
        const string variableName = "MASTER_LIST_BUILDER_GROCERY_CHAIN_TEST_KEY";
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
                  "name": "groceries",
                  "mode": "tiled",
                  "searches": [
                    { "query": "Publix", "category": "grocery" }
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
                  "displayName": { "text": "Buckhead Butcher Shop" },
                  "formattedAddress": "3193 Roswell Rd NE, Atlanta, GA 30305, USA",
                  "location": { "latitude": 33.8410, "longitude": -84.3790 },
                  "types": [ "grocery_store" ]
                },
                {
                  "id": "right-1",
                  "displayName": { "text": "Publix Super Market at Buckhead Landing" },
                  "formattedAddress": "3330 Piedmont Rd NE, Atlanta, GA 30305, USA",
                  "location": { "latitude": 33.8460, "longitude": -84.3700 },
                  "types": [ "grocery_store", "supermarket" ]
                }
              ]
            }
            """));

        var exitCode = await MasterListBuilderProgram.RunAsync(
            ["--config", configPath, "--output-dir", outputDirectory],
            TextWriter.Null,
            TextWriter.Null,
            new HttpClient(handler));

        Assert.Equal(0, exitCode);
        var lines = await File.ReadAllLinesAsync(Path.Combine(outputDirectory, "groceries-master.jsonl"));
        Assert.Single(lines);
        Assert.Contains("Publix", lines[0]);
        Assert.DoesNotContain("Buckhead Butcher Shop", lines[0], StringComparison.Ordinal);

        Environment.SetEnvironmentVariable(variableName, null);
    }

    [Fact]
    public async Task RunAsync_RejectsUnsupportedMode()
    {
        const string variableName = "MASTER_LIST_BUILDER_UNSUPPORTED_MODE_TEST_KEY";
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
                  "name": "gyms",
                  "mode": "direct",
                  "searches": [
                    { "query": "Planet Fitness", "category": "gym" }
                  ]
                }
              ]
            }
            """);

        var exitCode = await MasterListBuilderProgram.RunAsync(
            ["--config", configPath, "--output-dir", outputDirectory],
            TextWriter.Null,
            TextWriter.Null,
            new HttpClient(new StubHttpMessageHandler(_ => JsonResponse("""{ "places": [] }"""))));

        Assert.Equal(1, exitCode);

        Environment.SetEnvironmentVariable(variableName, null);
    }

    private static HttpResponseMessage JsonResponse(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
}
