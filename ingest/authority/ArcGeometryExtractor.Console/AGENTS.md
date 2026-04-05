# ArcGeometryExtractor.Console

- Purpose: extract authoritative local KML/KMZ geometry into normalized JSONL point records.
- Entry point: `Program.cs`
- Runtime entrypoint stays thin and resolves the traced `IArcGeometryExtractorApp` boundary through DI.
- Inputs: ARC `kml/kmz` files, including KMZ `doc.kml` payloads.
- Outputs: normalized `jsonl` records compatible with `LocationAssembler.Console`, plus optional park/trail/feature split artifacts.
- Keep this host local-only and geometry-driven. It should not call external APIs.
- Category assignment should come from source geometry and metadata, not Google-style inference.
- Preferred authoritative uses:
  - parks/trails from ARC KML/KMZ layers
  - MARTA stations from the ARC rail-stations KMZ
- Preserve source place names from metadata fields when placemark `<name>` is missing.
