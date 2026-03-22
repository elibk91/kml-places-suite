# MasterListBuilder.Console

- Purpose: build category-specific master lists from bounded Google Places searches.
- Entry point: `Program.cs`
- Resolve the traced `IMasterListBuilderApp` boundary from DI; keep the host thin and leave runtime behavior behind interfaces.
- Current intended authoritative Google-built categories: gyms and groceries.
- Keep Google-specific collection and category filtering here; do not mix in ARC geometry ingestion logic.
- Gym and grocery collection must reject Google "related" results that do not actually match the searched chain name.
- Changes to gym/grocery filtering should trigger only gym/grocery master-list rebuilds, not ARC re-extraction.
