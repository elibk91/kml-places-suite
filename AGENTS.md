# KML Places Suite

## Start Here
- `generate/KmlGenerator.Api/Program.cs` boots the HTTP host and maps the KML endpoints.
- `generate/KmlGenerator.Console/Program.cs` is the file-based CLI entrypoint for final KML generation.
- `ingest/authority/ArcGeometryExtractor.Console/Program.cs` ingests authoritative ARC KML/KMZ and emits normalized geometry artifacts.
- `ingest/assemble/LocationAssembler.Console/Program.cs` assembles point and geometry inputs into `GenerateKmlRequest` JSON.
- `generate/KmlTiler.Console/Program.cs` tiles generated request/output data into per-tile KML outputs.
- `platform/core/KmlGenerator.Core/Services/KmlGenerationService.cs` contains the shared generation pipeline and native geometry interop.

## Action-Based Layout
- `ingest/`: bring external and authoritative sources into normalized internal form.
- `generate/`: runnable hosts that generate, tile, or serve output.
- `platform/`: shared managed runtime, analyzers, and native geometry code.
- `workflow/`: orchestration scripts, helpers, diagnostics, and generated workflow outputs.
- `data/`: checked-in durable inputs and checked-in config.
- `tests/`: deterministic test projects.
- `archive/`: retired scripts, one-off investigations, and historical data not on the active path.

## Project AGENTS
- `AGENTS.md` in repo root: cross-repo workflow, source-of-truth, and layout guidance.
- `ingest/authority/ArcGeometryExtractor.Console/AGENTS.md`: authoritative local geometry extraction rules.
- `generate/KmlGenerator.Api/AGENTS.md`: API host guidance.
- `generate/KmlGenerator.Console/AGENTS.md`: local KML CLI guidance.
- `platform/core/KmlGenerator.Core/AGENTS.md`: shared algorithm/model guidance.
- `tests/KmlGenerator.Tests/AGENTS.md`: KML stack test guidance.
- `generate/KmlTiler.Console/AGENTS.md`: tiled KML guidance.
- `ingest/assemble/LocationAssembler.Console/AGENTS.md`: request assembly guidance.
- `ingest/places/MasterListBuilder.Console/AGENTS.md`: Google master-list collection guidance.
- `ingest/places/PlacesGatherer.Console/AGENTS.md`: Google Places host guidance.
- `tests/PlacesGatherer.Console.Tests/AGENTS.md`: Places test guidance.

## Conventions
- Keep business logic in `platform/core/KmlGenerator.Core`; the hosts stay thin.
- Active runtime logic must sit behind interfaces. Composition roots should resolve app/service interfaces rather than concrete runtime collaborators.
- Active composition roots use `AddKmlSuiteTracing()` plus `AddTracedSingleton(...)` for proxied runtime boundaries. Do not proxy concrete classes directly.
- Keep Google-specific collection code inside `ingest/places/PlacesGatherer.Console`.
- Keep authoritative local KML/KMZ ingestion inside `ingest/authority/ArcGeometryExtractor.Console`.
- Use existing master-list artifacts for gyms and groceries in the active workflow. Use authoritative ARC geometry for parks, trails, and MARTA when available.
- Default MARTA source is the ARC rail-stations KMZ, not Google-resolved station search.
- Active overlap generation is geometry-native: it buffers real source geometry and writes KML polygons, not point-based boundary dots.
- Do not rerun ARC extraction just because gym/grocery filtering or chain lists changed.
- Read secrets through the secret-provider plumbing, not directly in domain code.
- Runtime DI should prefer interface injection over concrete runtime service injection. The analyzer enforces that rule and also bans the null-forgiving operator in repo code.
- Use comments as signposts around major flows and non-obvious math, not on trivial assignments.

## Data And Output Layout
- Keep reusable master-list inputs under `data/inputs/master-lists/`.
- Keep durable raw ARC source files under `data/inputs/arc-sources/`.
- Keep checked-in workflow/config JSON under `data/config/authority/`.
- Keep active workflow outputs under `workflow/out/runs/`.
- Keep workflow diagnostics under `workflow/out/diagnostics/`.
- Name active run folders with filename-safe ISO timestamps and group them by config name.
- Keep retired or one-off workflow assets under `archive/`; they are not part of the active proof surface.

## Testing
- Standard test runs should stay deterministic and not require live Google access.
- Live Places integration tests are opt-in and require `RUN_LIVE_GOOGLE_TESTS=true` plus `GoogleMaps__ApiKey`.
- Use solution builds as the supported compile path.
- `workflow/run/run-category-workflow.ps1` is the active end-to-end workflow entrypoint.
- `workflow/diagnostics/extract-park-outline.ps1` is diagnostic-only, but it must still invoke `ingest/authority/ArcGeometryExtractor.Console` so the extracted park shape comes from the same parsing and filtering code path as the active workflow.
- Do not default to the full category workflow for gym/grocery-only refreshes; prefer a partial refresh that reuses existing ARC outputs.
