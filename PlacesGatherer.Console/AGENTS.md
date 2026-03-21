# PlacesGatherer.Console

- Purpose: Google Places search host and normalized record producer.
- Entry point: `Program.cs`
- Keep all Google API-specific logic, retries, and response normalization here.
- Shared normalized record shape lives here and is consumed by other local tools.
