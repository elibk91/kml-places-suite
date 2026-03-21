# KmlGenerator.Api

- Purpose: expose the KML generator over HTTP.
- Entry point: `Program.cs`
- Controllers live under `Controllers/`.
- Keep this project thin. Request validation and KML logic belong in `KmlGenerator.Core`.
- Avoid adding workflow-specific orchestration here.
