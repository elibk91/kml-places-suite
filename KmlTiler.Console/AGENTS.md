# KmlTiler.Console

- Purpose: run fixed-degree tiled KML generation over an assembled request.
- Entry point: `Program.cs`
- Keep tiling simple and degree-grid based.
- Reuse `KmlGenerator.Core` for overlap generation instead of implementing separate geometry logic here.
- Tiled outputs should mirror the same boundary representation used by the shared core.
