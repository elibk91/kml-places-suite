# KML Places Suite

## Start Here
- `KmlGenerator.Api/Program.cs` boots the HTTP host and maps the KML endpoints.
- `KmlGenerator.Console/Program.cs` is the file-based CLI entrypoint for KML generation.
- `PlacesGatherer.Console/Program.cs` is the console-only Google Places data gatherer.
- `KmlGenerator.Core/Services/KmlGenerationService.cs` contains the shared KML generation pipeline.

## Project Layout
- `KmlGenerator.Core`: shared models, validation, algorithm, and KML serialization.
- `KmlGenerator.Api`: stateless API surface for returning KML as JSON or a downloadable file.
- `KmlGenerator.Console`: local CLI that reads a JSON request and saves `.kml` output.
- `PlacesGatherer.Console`: local CLI that queries Google Places and writes normalized `jsonl`.
- `KmlGenerator.Tests`: unit and integration tests for the KML stack.
- `PlacesGatherer.Console.Tests`: unit tests, mocked integration tests, and gated live Google API tests.

## Conventions
- Keep business logic in `KmlGenerator.Core`; the hosts should stay thin.
- Keep Google-specific code inside `PlacesGatherer.Console`.
- Read secrets through the secret-provider plumbing, not directly in domain code.
- Use comments as signposts around major flows and non-obvious math, not on trivial assignments.

## Secrets
- Local development reads `GoogleMaps__ApiKey` from the environment.
- Secret-provider selection is configuration-driven so Google Secret Manager can be added later.
- Never commit API keys or developer-local config with secrets.

## Testing
- Standard test runs should stay deterministic and not require live Google access.
- Live Places integration tests are opt-in and require `RUN_LIVE_GOOGLE_TESTS=true` plus `GoogleMaps__ApiKey`.
