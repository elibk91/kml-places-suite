# PlacesGatherer.Console

- Purpose: Google Places search host and normalized record producer.
- Entry point: `Program.cs`
- Resolve the traced `IPlacesGathererApp` boundary from DI; keep active runtime collaborators behind interfaces.
- Keep all Google API-specific logic, retries, and response normalization here.
- Shared normalized record shape lives here and is consumed by other local tools.
- This remains authoritative for gym and grocery gathering, but not for ARC-sourced parks/trails/MARTA.
