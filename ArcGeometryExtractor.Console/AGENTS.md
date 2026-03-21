# ArcGeometryExtractor.Console

- Purpose: extract authoritative local KML/KMZ geometry into normalized JSONL point records.
- Entry point: `Program.cs`
- Inputs: ARC `kml/kmz` files, including KMZ `doc.kml` payloads.
- Outputs: normalized `jsonl` records compatible with `LocationAssembler.Console`, plus optional park/trail/feature split artifacts.
- Keep this host local-only and geometry-driven. It should not call external APIs.
- Category assignment should come from source geometry and metadata, not Google-style inference.
