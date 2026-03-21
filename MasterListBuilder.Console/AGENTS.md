# MasterListBuilder.Console

- Purpose: build category-specific master lists from bounded Google Places searches.
- Entry point: `Program.cs`
- Current intended authoritative Google-built categories: gyms and groceries.
- Legacy/supporting categories can include broad park/trail discovery and older direct MARTA search flows.
- Keep Google-specific collection and category filtering here; do not mix in ARC geometry ingestion logic.
