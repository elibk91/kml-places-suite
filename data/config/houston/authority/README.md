Place Houston city configs here.

Expected active files:
- `category-config.with-gyms.json`
- `category-config.no-gyms.json`

The active workflow reads:
- `data/config/<city>/authority/...`
- `data/inputs/<city>/master-lists/...`
- `data/inputs/<city>/arc-sources/parks-trails/...`
- `data/inputs/<city>/arc-sources/transit/...`

Optional config section:
- `independentDiagnostics`: array of final-step coordinate checks run by `workflow/run/run-category-workflow.ps1`
- Each entry can include:
  - `label`
  - `latitude`
  - `longitude`
  - `radiusMiles`
  - `topPerCategory`
