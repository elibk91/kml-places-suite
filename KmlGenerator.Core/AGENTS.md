# KmlGenerator.Core

- Purpose: shared KML models, validation, overlap logic, and serialization.
- Key implementation area: `Services/`
- Put reusable algorithmic logic here, not in console or API hosts.
- Any KML behavior change should be implemented and tested here first.
- The overlap engine is grid-scan based with spatial binning and parallel row scanning for large datasets.
- Current authoritative KML output writes overlap boundary points, not stitched boundary polygons.
- Keep support-point selection constrained to the small top set per category per detected shape.
