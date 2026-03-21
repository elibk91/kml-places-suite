# Complete Atlanta Point Dataset and Boundary Workflow

## Summary
This plan is for me, the AI agent, to execute end-to-end. It is not limited to coding infrastructure or running the already-automated pieces. I am responsible for:

- running and validating the automated discovery passes
- correcting gaps between intended workflow and current implementation
- doing the second-pass independent research for parks and trails myself
- producing researched address/cross-street target files
- resolving those researched targets into coordinates
- assembling the full final point dataset
- running the existing boundary/KML logic on the completed dataset
- inspecting results and iterating until the dataset and outputs are usable

This plan is only complete when:
- the full combined point dataset exists
- the boundary-generation workflow has been run against it
- the outputs have been reviewed
- and the resulting data/output is judged usable or any remaining failure is explicitly demonstrated with evidence from the final runs

## Current State
The repo already contains these implemented tools:
- `PlacesGatherer.Console`: Google Places point collection
- `LocationAssembler.Console`: merges normalized point files into `GenerateKmlRequest`
- `KmlGenerator.Console` and `KmlGenerator.Api`: run the boundary/KML logic
- `KmlTiler.Console`: splits a request into fixed lat/lon tiles and runs the existing KML engine per tile
- `MasterListBuilder.Console`: builds category-specific master lists
- `ResearchPointResolver.Console`: resolves researched address/cross-street targets into normalized points
- `run-workflow.ps1`, `build-master-lists.ps1`, `resolve-research-points.ps1`

Important correction:
- gyms/groceries are structurally set up for proper tiled master-list building
- MARTA is partially set up as direct search
- parks/trails are not yet aligned with the intended broad generic small-box discovery pass
- the earlier park/trail work relied too much on seeded named searches and expansion
- the research content itself has not yet been completed

Known live artifacts already present:
- `out/marietta-stonecrest-places.jsonl`
- `out/marietta-stonecrest-request.json`
- `out/marietta-stonecrest-tiles/tiles-summary.json`
- related metro-wide outputs

These are useful as diagnostics and a provisional starting point, but they are not the final accepted dataset.

## Geography and Bounding Region
Primary study area for the main runs:
- upper-left anchor: `Marietta Square`
- lower-right anchor: `Stonecrest Mall`

Current working bounds already used in repo config:
- `north = 33.952876`
- `south = 33.698669`
- `west = -84.54903`
- `east = -84.095141`

These bounds define:
- the broad discovery area
- the gym/grocery tiled collection area
- the broad park/trail discovery area
- the tiler bounds for boundary runs unless later refined

## Category Strategy
The workflow is intentionally different by category.

### Gyms
Goal:
- fully programmatic master list

Method:
- use the full chain list
- run small fixed-degree tiled discovery across the whole Marietta Square -> Stonecrest Mall box
- dedupe and normalize results
- produce a clean `gyms-master.jsonl`

Required chain list:
- Planet Fitness
- LA Fitness
- Esporta Fitness
- Crunch Fitness
- Workout Anytime
- Anytime Fitness
- Snap Fitness
- YMCA

Expectation:
- no manual research phase should be needed for gyms except sanity-checking obvious false positives if necessary

### Groceries
Goal:
- fully programmatic master list

Method:
- use the full chain list
- run small fixed-degree tiled discovery across the whole Marietta Square -> Stonecrest Mall box
- dedupe and normalize results
- produce a clean `groceries-master.jsonl`

Required chain list:
- Kroger
- Publix
- Walmart
- Walmart Neighborhood Market
- Target
- ALDI
- Trader Joe's
- Lidl
- Whole Foods Market
- Sprouts Farmers Market

Important note:
- Target may need search phrasing tuned if generic `Target` returns too much non-grocery noise
- if needed, use `Target Grocery` or similar search variants, but coverage must still reflect the original intent to include Target

### MARTA
Goal:
- stable direct point list, not broad discovery

Method:
- query/search each approved station directly, or use a once-curated stable list if the direct results are noisy
- produce a clean `marta-master.jsonl` or equivalent stable point file

Approved rail station set should be the full set previously approved, not just a Midtown subset.

At minimum include:
- Airport
- Arts Center
- Ashby
- Avondale
- Bankhead
- Brookhaven / Oglethorpe
- Buckhead
- Chamblee
- Civic Center
- College Park
- Decatur
- Doraville
- Dunwoody
- East Lake
- East Point
- Edgewood / Candler Park
- Five Points
- Garnett
- Georgia State
- Hamilton E. Holmes
- Indian Creek
- Inman Park / Reynoldstown
- Kensington
- King Memorial
- Lakewood / Ft. McPherson
- Lenox
- Lindbergh Center
- Medical Center
- Midtown
- North Avenue
- North Springs
- Oakland City
- Peachtree Center
- Sandy Springs
- SEC District
- Vine City
- West End
- West Lake

Expectation:
- MARTA should not need the same research workflow as parks/trails

### Parks and Trails
Goal:
- two-stage process:
  1. broad generic small-box discovery pass
  2. second-pass researched point derivation

Critical correction:
- do not limit this category to named seeded searches like only Piedmont/BeltLine/Freedom
- do not assume the first-pass examples are the final park/trail queue
- do not artificially trim the list to only a few top places

Stage 1:
- broad generic discovery by small box across the full Marietta Square -> Stonecrest Mall box

Stage 2:
- research one named place at a time to derive better address/cross-street targets

## Park/Trail Broad Discovery Pass
This must be a true generic tiled discovery pass, not just a seeded named-search pass.

### Query style
Use generic park/trail search terms in every small box.

Broad park discovery terms:
- `park`
- `city park`
- `nature preserve`
- `greenway`

Broad trail discovery terms:
- `trail`
- `walking trail`
- `multi-use trail`
- `trailhead`
- `greenway trail`

If needed after inspection, add carefully chosen broad terms such as:
- `path`
- `bike trail`
- `beltline`

but only if they improve real discovery instead of adding noise

### Tile strategy
Use fixed-degree tiles for broad discovery.

Starting tile size for master-list builder:
- `tileLatitudeStep = 0.05`
- `tileLongitudeStep = 0.05`

If discovery is too sparse or too noisy, this can be tuned, but the first proper pass should use a consistent whole-box tiled run.

### Output
Produce:
- `parks-trails-master.jsonl`

This file is the broad candidate inventory only. It is not the final park/trail point set.

### Filtering rules
Because generic park/trail discovery is noisy, the broad pass needs conservative filtering.

Reject obvious false positives where the result is clearly:
- apartment complex
- condo building
- office
- restaurant
- bar
- retail store
- unrelated service business
- generic parking-only result unless it is clearly a real park/trail access point
- unrelated places that only contain the park/trail name in branding

Retain likely true candidates such as:
- park
- city park
- preserve
- botanical garden
- trail
- trailhead
- greenway
- named access point
- named intersection with trail
- named park entrance
- real trail segment
- real public parking/access location clearly tied to a park/trail when useful

The first-pass goal is:
- broad and useful candidate coverage

not
- perfect final geometry

## Park/Trail Research Pass
This is still performed by me, the AI agent, not by the user, unless the user later chooses to contribute their own targets.

### Research input
Start from:
- `parks-trails-master.jsonl`
- plus any additional real Atlanta parks/trails mentioned by the user or identified as obvious omissions

Examples that should be expected to enter the queue if relevant:
- Piedmont Park
- Grant Park
- Westside Park / Westside Reservoir Park
- Atlanta BeltLine Eastside Trail
- Atlanta BeltLine Westside Trail
- Freedom Park / Freedom Park Trail
- PATH400
- Historic Fourth Ward Park
- Central Park
- DeKalb greenway-style trails
- Silver Comet Trail
- other legitimate Atlanta-area parks/trails found during discovery

### Research unit
Research one named place at a time.

A "place" can be:
- one park
- one named trail
- one named trail segment if the trail is too large to sensibly handle as a single unit

### Research output format
Each researched place must produce address-style targets, not prose notes and not raw geometry.

Store them in:
- a dedicated research config file consumed by `ResearchPointResolver.Console`

Each target should include:
- `label`
- `category`
- `query`

Where:
- `label` is concise and descriptive
- `category` is `park` or `trail`
- `query` is a resolvable address or cross-street search string

### Park research rules
For a large park, the repeatable research steps are:

1. Confirm the place is a real Atlanta-area park worth keeping.
2. Identify the rough perimeter using maps and official/public references.
3. Capture bounding-edge streets that represent the park's outer shape.
4. Capture major entrances, gates, parking/access locations, or named edge points where those are better than raw perimeter streets.
5. Stop when the park has enough points to represent shape and access.
6. Do not try to exhaustively map every internal feature.

Examples of acceptable park target labels:
- `Piedmont Park Southwest Entrance`
- `Piedmont Park Monroe Drive Edge`
- `Grant Park Boulevard Gate`
- `Historic Fourth Ward Park Lake Edge`

### Trail research rules
For a trail, the repeatable research steps are:

1. Confirm the trail/segment is real and relevant.
2. Trace it on maps and identify real access points, cross streets, trailheads, bridge crossings, and named intersections.
3. Space targets roughly every `0.5` miles where practical.
4. Include endpoints and major transitions even if spacing is uneven.
5. Prefer real named cross streets/access points over vague landmarks.
6. Stop when the target list adequately represents the trail's access and shape through the study area.

Examples of acceptable trail target labels:
- `BeltLine Eastside Trail at Monroe Drive`
- `BeltLine Eastside Trail at Ponce de Leon`
- `Freedom Park Trail at Moreland Avenue`
- `PATH400 at Lenox Road`
- `Silver Comet Trail at Floyd Road`

### Definition of "researched enough"
A park/trail is complete for this pass when it has:
- enough vetted targets to represent shape/access well
- not necessarily every possible entrance or crossing

This should remain practical, not perfectionist.

## Resolving Researched Targets
Use:
- `ResearchPointResolver.Console`

Input:
- researched address/cross-street config file

Output:
- normalized JSONL point file of researched park/trail targets

This step converts researched targets into actual point records that can join the rest of the workflow.

## Master List and Final Dataset Assembly
After collecting all category-specific outputs, merge them into a full final point dataset.

Expected inputs:
- `gyms-master.jsonl`
- `groceries-master.jsonl`
- `marta-master.jsonl`
- `parks-trails-master.jsonl` as broad candidate inventory where useful
- researched resolved park/trail points JSONL

Important rule:
- the broad `parks-trails-master.jsonl` is the candidate inventory and may contain noise
- the researched/resolved park-trail points are the refined park/trail contribution that should drive the final park/trail point quality
- if needed, only a vetted subset of the broad baseline should flow into the final assembled dataset

Use:
- `LocationAssembler.Console`

The assembler must merge multiple `--input` files into a final:
- `GenerateKmlRequest` JSON

This assembled request is the final full point dataset artifact for the KML logic.

## Boundary Generation and Completion Criteria
Once the full dataset exists, run:
- `KmlGenerator.Console` for whole-request generation
- `KmlTiler.Console` if the whole-area run is empty or too broad

### Whole-area run
Run the standard KML boundary generation against the completed full point dataset.

If it produces a usable result:
- inspect and preserve that output

If it produces no useful result:
- move to tiled analysis

### Tiled run
Use the existing fixed-degree tiler across the study box.

Purpose:
- identify which tiles truly contain overlap
- determine whether the problem is:
  - missing categories
  - insufficient radius
  - weak park/trail points
  - geographic spread that is too large

### Iteration loop
If outputs are empty or weak:
- inspect tile summaries
- inspect category coverage
- refine park/trail research where needed
- optionally refine noisy discovery filters
- rerun assembly and boundary generation

I must continue iterating until one of these is true:
- there is a usable final boundary output
- or the current category definitions, radius, and coverage make overlap impossible in the chosen area

## Definition of Done
This work is not done when:
- code compiles
- the automation runs
- master lists exist
- research targets exist
- the final assembled request exists

This work is done only when:
- all category baselines are built correctly
- the park/trail research pass has been performed by me
- researched targets have been resolved
- the final combined point dataset has been assembled
- the boundary/KML logic has been run against that final dataset
- outputs have been inspected
- and the result is either usable or explicitly demonstrated to fail for documented, data-backed reasons

## Specific Execution Order
Follow this order exactly:

1. Verify repo state and existing tools.
2. Correct the `MasterListBuilder.Console` config for a proper broad park/trail tiled discovery pass.
3. Build the authoritative gym master list using the full chain list.
4. Build the authoritative grocery master list using the full chain list.
5. Build/validate the full MARTA master list using direct station lookup.
6. Build the broad `parks-trails-master.jsonl` across the full Marietta Square -> Stonecrest Mall box using generic park/trail search terms.
7. Review the broad park/trail candidate inventory and identify the real research queue.
8. Perform second-pass research myself, one place at a time, for parks and trails.
9. Create/update the researched target config file with vetted labels and resolvable queries.
10. Resolve researched targets into normalized point records.
11. Assemble all required category point files into the final `GenerateKmlRequest`.
12. Run the whole-area boundary/KML generation.
13. If needed, run tiled boundary generation and inspect summaries.
14. Refine and rerun as needed until the outputs are acceptable or the failure mode is clearly proven.
15. Commit and push the resulting changes and research artifacts.

## Files and Artifacts Expected by the End
By the time this is complete, the repo should contain, at minimum:

Code/tools:
- `MasterListBuilder.Console`
- `ResearchPointResolver.Console`
- `LocationAssembler.Console`
- `KmlGenerator.Console`
- `KmlTiler.Console`

Configs and scripts:
- master-list config tuned for the full intended workflow
- researched park/trail target config file with real researched targets
- runner scripts as needed

Output/data artifacts:
- `gyms-master.jsonl`
- `groceries-master.jsonl`
- `marta-master.jsonl`
- `parks-trails-master.jsonl`
- researched resolved points JSONL
- final assembled request JSON
- final KML output and/or tiled KML outputs
- tile summary if tiled analysis is needed

## Assumptions
- I am responsible for both automation and research-heavy enrichment.
- The broad generic park/trail discovery pass must still be implemented properly; the earlier seeded-only approach was insufficient.
- Every gym and grocery chain originally named must be included in the proper tiled master-list pass.
- MARTA should be handled directly and completely.
- Parks and trails require a broad baseline plus a researched refinement pass.
- The final acceptance artifact is the full dataset plus the actual boundary-generation result, not just code or intermediate files.
