# KmlGenerator.Console

- Purpose: run KML generation from a local request JSON file.
- Entry point: `Program.cs`
- Input contract: `GenerateKmlRequest`
- Output: `.kml`
- Keep this host file-based and thin; the overlap algorithm belongs in `KmlGenerator.Core`.
