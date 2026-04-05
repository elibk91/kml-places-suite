# KmlTiler.Console

- Purpose: run fixed-degree tiled KML generation over an assembled request.
- Entry point: `Program.cs`
- Resolve the traced `IKmlTilerApp` boundary from DI; keep the host limited to argument parsing and wiring.
- Keep tiling simple and degree-grid based.
- Reuse `KmlGenerator.Core` for overlap generation instead of implementing separate geometry logic here.
- Tiled outputs should mirror the same boundary representation used by the shared core.
