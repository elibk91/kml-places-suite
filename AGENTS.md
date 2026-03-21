# KML Places Suite

## Start Here
- `KmlGenerator.Api/Program.cs` boots the HTTP host and maps the KML endpoints.
- `KmlGenerator.Console/Program.cs` is the file-based CLI entrypoint for KML generation.
- `ArcGeometryExtractor.Console/Program.cs` extracts authoritative local ARC geometry into normalized point JSONL.
- `PlacesGatherer.Console/Program.cs` is the console-only Google Places data gatherer.
- `LocationAssembler.Console/Program.cs` converts gathered `jsonl` records into `GenerateKmlRequest` JSON.
- `KmlTiler.Console/Program.cs` walks a fixed lat/lon grid and generates per-tile KML outputs.
- `MasterListBuilder.Console/Program.cs` builds category-specific master lists from small-box and direct search groups.
- `ResearchPointResolver.Console/Program.cs` resolves manually researched address/cross-street targets back into normalized points.
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
- `ResearchPointResolver.Console/AGENTS.md`: legacy/supporting manual research guidance.

## Project Layout
- `ArcGeometryExtractor.Console`: local CLI that reads authoritative KML/KMZ files and emits normalized park/trail/MARTA JSONL records.
- `KmlGenerator.Core`: shared models, validation, algorithm, and KML serialization.
- `KmlGenerator.Api`: stateless API surface for returning KML as JSON or a downloadable file.
- `KmlGenerator.Console`: local CLI that reads a JSON request and saves `.kml` output.
- `PlacesGatherer.Console`: local CLI that queries Google Places and writes normalized `jsonl`.
- `LocationAssembler.Console`: local CLI that dedupes exact duplicate points and emits final KML request JSON.
- `KmlTiler.Console`: local CLI that filters a request into fixed-degree tiles and writes non-empty tile KMLs.
- `MasterListBuilder.Console`: local CLI that creates master JSONL outputs for gyms, groceries, parks/trails, and direct categories such as MARTA.
- `ResearchPointResolver.Console`: local CLI that takes researched address targets and turns them into normalized JSONL point records.
- `KmlGenerator.Tests`: unit and integration tests for the KML stack.
- `PlacesGatherer.Console.Tests`: unit tests, mocked integration tests, and gated live Google API tests.

## Conventions
- Keep business logic in `KmlGenerator.Core`; the hosts should stay thin.
- Keep Google-specific code inside `PlacesGatherer.Console`.
- Keep authoritative local KML/KMZ ingestion inside `ArcGeometryExtractor.Console`.
- Keep the assembler point-only; it should not invent coordinates or implement geometry heuristics.
- Keep the tiler degree-grid based; it should reuse the existing KML engine rather than implementing new overlap logic.
- Use Google Places for gyms and groceries. Use authoritative ARC geometry for parks, trails, and MARTA when available.
- Keep legacy Google/manual park-trail workflows available for QA and fallback, but not as the primary source of record.
- Default MARTA source is the ARC rail-stations KMZ, not Google-resolved station search.
- Current authoritative overlap output uses point-based boundary dots, not stitched polygons.
- Read secrets through the secret-provider plumbing, not directly in domain code.
- Use comments as signposts around major flows and non-obvious math, not on trivial assignments.

## Output Layout
- Use `out/authority/` for authoritative current workflow artifacts.
- Use `out/legacy/` for earlier Google/manual workflow artifacts and superseded outputs kept for comparison.
- Preferred authority subfolders:
  - `out/authority/master-lists/`
  - `out/authority/arc/`
  - `out/authority/requests/`
  - `out/authority/kml/`
- Preferred legacy subfolders:
  - `out/legacy/requests/`
  - `out/legacy/kml/`
  - `out/legacy/tiles/`
  - `out/legacy/research/`

## Repo Layout
- Keep code projects at repo root as sibling directories.
- Keep runner scripts under `scripts/`.
- Keep checked-in workflow/config JSON under `config/`.
- Split config by source of truth:
  - `config/authority/` for current authoritative inputs
  - `config/legacy/` for older or fallback inputs
- Keep planning and architecture docs under `docs/`.

## Secrets
- Local development reads `GoogleMaps__ApiKey` from the environment.
- Secret-provider selection is configuration-driven so Google Secret Manager can be added later.
- Never commit API keys or developer-local config with secrets.

## Testing
- Standard test runs should stay deterministic and not require live Google access.
- Live Places integration tests are opt-in and require `RUN_LIVE_GOOGLE_TESTS=true` plus `GoogleMaps__ApiKey`.
- `scripts/run-workflow.ps1` is the top-level local runner; it builds each project it invokes before running gatherer, assembler, optional single KML generation, and optional tiled KML generation.
- `scripts/build-master-lists.ps1` builds `MasterListBuilder.Console` before running category-specific master list generation.
- `scripts/resolve-research-points.ps1` builds `ResearchPointResolver.Console` before resolving researched park/trail address targets.
- `scripts/run-category-workflow.ps1` builds each invoked project and can assemble category data from Google-built master lists plus authoritative local ARC outputs.
