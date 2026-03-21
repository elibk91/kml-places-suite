# LocationAssembler.Console

- Purpose: merge normalized point JSONL inputs into `GenerateKmlRequest`.
- Entry point: `Program.cs`
- Input model: `PlacesGatherer.Console.Models.NormalizedPlaceRecord`
- Keep this project point-only. It should dedupe exact point/category duplicates but should not invent geometry or coordinates.
- Preserve source place names into `LocationInput.Label`; downstream KML labeling depends on this.
