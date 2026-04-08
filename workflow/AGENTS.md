# workflow

- Purpose: orchestration entrypoints, helpers, diagnostics, and generated workflow outputs.
- `run/run-category-workflow.ps1`: main category assembly flow that reuses existing master lists, regenerates ARC artifacts from source inputs, and generates the whole-area overlap KML for one city at a time.
- Category configs may include an `independentDiagnostics` array. When present, `run/run-category-workflow.ps1` runs each configured coordinate diagnostic as the final workflow step and writes text artifacts under the run folder.
- `diagnostics/extract-park-outline.ps1`: runs `ingest/authority/ArcGeometryExtractor.Console` to produce the same park-outline KML shape data as the real ARC workflow, then filters that extractor output down to one named park for inspection.
- `diagnostics/diagnose-coordinate-coverage.ps1`: independent diagnostic that reports nearest points per category for a coordinate and tells you which categories are missing inside the target radius.
- For city-output disputes, `diagnostics/diagnose-coordinate-coverage.ps1` is the first independent verification step after locating the current request artifact. Do not rely only on code reading or generic unit tests when the complaint is about a real generated run.
- `helpers/Common.ps1` owns shared tracing/report helpers. Active scripts are responsible for initializing and finalizing trace artifacts under `workflow/out/runs/<workflow>/<RunId>/trace/`.
- Active scripts should build `KmlSuite.slnx` once and then run projects with `--no-build`.
- Active scripts should reuse existing master-list artifacts; rebuilding Google-backed master lists is archive-only behavior.
- Do not include anything under `archive/workflow-scripts/` in active runtime proof, trace summaries, or current workflow guidance.
- Use `data/inputs/<city>/` for durable script-consumed artifacts and `workflow/out/` for regenerated workflow outputs.
- Active run outputs should live under `workflow/out/runs/`.
- Group active run outputs by city and then config name.
- `diagnostics/diagnose-coordinate-coverage.ps1` is read-only and should not create run-output folders.
- Do not use `run/run-category-workflow.ps1` for gym/grocery-only changes if the ARC source files and ARC extraction logic have not changed.
