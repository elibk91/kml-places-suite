# scripts

- Purpose: orchestration entrypoints for local workflow runs.
- `legacy/build-master-lists.ps1`: legacy Google master-list generation only. It is not part of the active trace-proof surface.
- `run-category-workflow.ps1`: main category assembly flow that reuses existing master lists, regenerates ARC artifacts from source inputs, and generates the whole-area overlap KML.
- `extract-park-outline.ps1`: runs `ArcGeometryExtractor.Console` to produce the same park-outline KML shape data as the real ARC workflow, then filters that extractor output down to one named park for inspection.
- `diagnose-coordinate-coverage.ps1`: reports nearest points per category for a coordinate and tells you which categories are missing inside the target radius.
- `Common.ps1` owns shared tracing/report helpers. Active scripts are responsible for initializing and finalizing trace artifacts under `workflow/out/runs/<workflow>/<RunId>/trace/`.
- Active scripts should build `KmlSuite.slnx` once and then run projects with `--no-build`.
- Active scripts should reuse existing master-list artifacts; rebuilding Google-backed master lists is legacy-only behavior.
- Do not include anything under `archive/workflow-scripts/` in active runtime proof, trace summaries, or current workflow guidance.
- Use `scripts/in/` for durable script-consumed artifacts and `workflow/out/` for regenerated workflow outputs.
- Active run outputs should be flattened directly under `workflow/out/runs/`.
- Name active artifacts as `<run-type>-<iso-timestamp>-<artifact-name>` so alphabetical sort groups by run type and then by time.
- Use filename-safe ISO timestamps such as `2026-03-22T12-10-03`.
- `run-category-workflow.ps1` should default to names like:
  - `category-workflow-<iso-timestamp>-atlanta-category-outline.arc.kml`
  - `category-workflow-<iso-timestamp>-atlanta-category-request.arc.json`
  - `category-workflow-<iso-timestamp>-tiles/`
  - `category-workflow-<iso-timestamp>-trace/`
- `extract-park-outline.ps1` should default to names like:
  - `extract-park-outline-<iso-timestamp>-Piedmont-Park.kml`
  - `extract-park-outline-<iso-timestamp>-arc-park-points.jsonl`
- `legacy/build-master-lists.ps1` should default to one timestamped legacy run root under `workflow/out/legacy/build-master-lists/<RunId>/` and keep its master-list outputs and trace artifacts together there unless a caller explicitly overrides them.
- `diagnose-coordinate-coverage.ps1` is read-only and should not create run-output folders.
- Keep reusable master-list inputs under:
  - `data/inputs/master-lists/`
- Keep durable raw ARC source files under:
  - `data/inputs/arc-sources/`
- Do not use `run-category-workflow.ps1` for gym/grocery-only changes if the ARC source files and ARC extraction logic have not changed.
- For gym/grocery-only refreshes:
  1. rebuild `gyms-master.jsonl` and `groceries-master.jsonl`
  2. reuse the last known-good ARC outputs from a prior run if ARC source files and ARC logic are unchanged
  4. rerun assembler and KML generation only into `out/runs/`
- Full ARC extraction should be rerun only when the authoritative ARC source files or ARC extraction logic changed.


