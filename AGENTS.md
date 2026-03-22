# KML Places Suite

## Start Here
- `KmlGenerator.Api/Program.cs` boots the HTTP host and maps the KML endpoints.
- `KmlGenerator.Console/Program.cs` is the file-based CLI entrypoint for KML generation.
- `ArcGeometryExtractor.Console/Program.cs` extracts authoritative local ARC geometry into normalized point JSONL.
- `PlacesGatherer.Console/Program.cs` is the console-only Google Places data gatherer.
- `LocationAssembler.Console/Program.cs` converts gathered `jsonl` records into `GenerateKmlRequest` JSON.
- `KmlTiler.Console/Program.cs` walks a fixed lat/lon grid and generates per-tile KML outputs.
- `MasterListBuilder.Console/Program.cs` builds category-specific master lists for the legacy Google-backed gym and grocery workflow.
- `KmlGenerator.Core/Services/KmlGenerationService.cs` contains the shared KML generation pipeline.

## Project AGENTS
- `AGENTS.md` in repo root: cross-repo workflow, source-of-truth, and output layout guidance.
- `ArcGeometryExtractor.Console/AGENTS.md`: authoritative local geometry extraction rules.
- `KmlGenerator.Api/AGENTS.md`: API host guidance.
- `KmlGenerator.Console/AGENTS.md`: local KML CLI guidance.
- `KmlGenerator.Core/AGENTS.md`: shared algorithm/model guidance.
- `KmlGenerator.Tests/AGENTS.md`: KML stack test guidance.
- `KmlTiler.Console/AGENTS.md`: tiled KML guidance.
- `LocationAssembler.Console/AGENTS.md`: request assembly guidance.
- `MasterListBuilder.Console/AGENTS.md`: Google master-list collection guidance.
- `PlacesGatherer.Console/AGENTS.md`: Google Places host guidance.
- `PlacesGatherer.Console.Tests/AGENTS.md`: Places test guidance.

## Project Layout
- `ArcGeometryExtractor.Console`: local CLI that reads authoritative KML/KMZ files and emits normalized park/trail/MARTA JSONL records.
- `KmlGenerator.Core`: shared models, validation, algorithm, and KML serialization.
- `KmlGenerator.Api`: stateless API surface for returning KML as JSON or a downloadable file.
- `KmlGenerator.Console`: local CLI that reads a JSON request and saves `.kml` output.
- `PlacesGatherer.Console`: local CLI that queries Google Places and writes normalized `jsonl`.
- `LocationAssembler.Console`: local CLI that dedupes exact duplicate points and emits final KML request JSON.
- `KmlTiler.Console`: local CLI that filters a request into fixed-degree tiles and writes non-empty tile KMLs.
- `MasterListBuilder.Console`: local CLI that creates master JSONL outputs for the legacy Google-backed gyms and groceries workflow.
- `KmlGenerator.Tests`: unit and integration tests for the KML stack.
- `PlacesGatherer.Console.Tests`: unit tests, mocked integration tests, and gated live Google API tests.

## Conventions
- Keep business logic in `KmlGenerator.Core`; the hosts should stay thin.
- Active runtime logic must sit behind interfaces. Composition roots should resolve app/service interfaces rather than concrete runtime collaborators.
- Active composition roots use `AddKmlSuiteTracing()` plus `AddTracedSingleton(...)` for proxied runtime boundaries. Do not proxy concrete classes directly.
- Keep Google-specific code inside `PlacesGatherer.Console`.
- Keep authoritative local KML/KMZ ingestion inside `ArcGeometryExtractor.Console`.
- Keep the assembler point-only; it should not invent coordinates or implement geometry heuristics.
- Keep the tiler degree-grid based; it should reuse the existing KML engine rather than implementing new overlap logic.
- Use existing master-list artifacts for gyms and groceries in the active workflow. Use authoritative ARC geometry for parks, trails, and MARTA when available.
- Default MARTA source is the ARC rail-stations KMZ, not Google-resolved station search.
- Current authoritative overlap output uses point-based boundary dots, not stitched polygons.
- When only gyms/groceries change, rerun only those Google-built master lists, then reuse the existing ARC MARTA and ARC park/trail artifacts during assembly.
- Do not rerun ARC extraction just because gym/grocery filtering or chain lists changed.
- Read secrets through the secret-provider plumbing, not directly in domain code.
- Runtime DI should prefer interface injection over concrete runtime service injection. The analyzer enforces that rule and also bans the null-forgiving operator in repo code.
- Use comments as signposts around major flows and non-obvious math, not on trivial assignments.

## Output Layout
- Keep script-owned inputs and outputs under `scripts/` so the workflow artifacts live with the orchestration entrypoints that use them.
- Keep reusable master-list inputs under:
  - `scripts/in/master-lists/`
- Keep durable raw source files for local geometry extraction under:
  - `scripts/in/arc-sources/`
- Keep active workflow run products under:
  - `scripts/out/runs/<workflow>/<RunId>/arc/`
  - `scripts/out/runs/<workflow>/<RunId>/requests/`
  - `scripts/out/runs/<workflow>/<RunId>/kml/`
  - `scripts/out/runs/<workflow>/<RunId>/tiles/`
  - `scripts/out/runs/<workflow>/<RunId>/trace/`
- Keep retired workflow outputs under:
  - `scripts/out/legacy/`
- Scripts that emit files should default to timestamped run folders so one execution does not overwrite another.

## Repo Layout
- Keep code projects at repo root as sibling directories.
- Keep runner scripts under `scripts/`.
- Keep retired workflow entrypoints under `scripts/legacy/`; legacy scripts are not part of the active trace-proof surface.
- Keep checked-in workflow/config JSON under `config/`.
- Split config by source of truth:
  - `config/authority/` for current authoritative inputs
- Keep planning and architecture docs under `docs/`.

## Secrets
- Local development reads `GoogleMaps__ApiKey` from the environment.
- Secret-provider selection is configuration-driven so Google Secret Manager can be added later.
- Never commit API keys or developer-local config with secrets.

## Testing
- Standard test runs should stay deterministic and not require live Google access.
- Live Places integration tests are opt-in and require `RUN_LIVE_GOOGLE_TESTS=true` plus `GoogleMaps__ApiKey`.
- Use solution builds as the supported compile path. Direct project builds are blocked so analyzer bootstrap and repo-wide rules stay consistent.
- `scripts/legacy/build-master-lists.ps1` is a legacy entrypoint and not part of the active proof surface.
- `scripts/run-category-workflow.ps1` builds `KmlSuite.slnx` once, regenerates ARC artifacts from source inputs, and writes active run outputs under a timestamped `scripts/out/runs/category-workflow/<RunId>/` folder.
- `scripts/extract-park-outline.ps1` is diagnostic-only, but it must still invoke `ArcGeometryExtractor.Console` so the extracted park shape comes from the same ARC parsing and filtering code path as the active workflow.
- Do not default to `scripts/run-category-workflow.ps1` for gym/grocery-only refreshes; prefer a partial refresh that reuses existing ARC outputs.
- Proof of active runtime usage comes from proxy event logs, runtime hit summaries, and C# file classification reports under `out/runs/<workflow>/trace/`.

