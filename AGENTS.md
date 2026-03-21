# KML Places Suite

## Start Here
- `KmlGenerator.Api/Program.cs` boots the HTTP host and maps the KML endpoints.
- `KmlGenerator.Console/Program.cs` is the file-based CLI entrypoint for KML generation.
- `PlacesGatherer.Console/Program.cs` is the console-only Google Places data gatherer.
- `LocationAssembler.Console/Program.cs` converts gathered `jsonl` records into `GenerateKmlRequest` JSON.
- `KmlTiler.Console/Program.cs` walks a fixed lat/lon grid and generates per-tile KML outputs.
- `MasterListBuilder.Console/Program.cs` builds category-specific master lists from small-box and direct search groups.
- `ResearchPointResolver.Console/Program.cs` resolves manually researched address/cross-street targets back into normalized points.
- `KmlGenerator.Core/Services/KmlGenerationService.cs` contains the shared KML generation pipeline.

## Project Layout
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
- Keep the assembler point-only; it should not invent coordinates or implement geometry heuristics.
- Keep the tiler degree-grid based; it should reuse the existing KML engine rather than implementing new overlap logic.
- Use Google Places for first-pass discovery and chain gathering. Use human research only for park/trail refinement after the master list is built.
- Read secrets through the secret-provider plumbing, not directly in domain code.
- Use comments as signposts around major flows and non-obvious math, not on trivial assignments.

## Secrets
- Local development reads `GoogleMaps__ApiKey` from the environment.
- Secret-provider selection is configuration-driven so Google Secret Manager can be added later.
- Never commit API keys or developer-local config with secrets.

## Testing
- Standard test runs should stay deterministic and not require live Google access.
- Live Places integration tests are opt-in and require `RUN_LIVE_GOOGLE_TESTS=true` plus `GoogleMaps__ApiKey`.
- `run-workflow.ps1` is the top-level local runner; it always builds before running gatherer, assembler, optional single KML generation, and optional tiled KML generation.
- `build-master-lists.ps1` builds category-specific master lists.
- `resolve-research-points.ps1` resolves researched park/trail address targets into normalized points.
