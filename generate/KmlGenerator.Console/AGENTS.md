# KmlGenerator.Console

- Purpose: run KML generation from a local request JSON file.
- Entry point: `Program.cs`
- Resolve the traced `IKmlConsoleApp` boundary from DI; keep runtime logic out of the composition root.
- Input contract: `GenerateKmlRequest`
- Output: `.kml`
- Keep this host file-based and thin; the overlap algorithm belongs in `KmlGenerator.Core`.
- Current main output is a point-based overlap boundary plus optional supporting category points.
