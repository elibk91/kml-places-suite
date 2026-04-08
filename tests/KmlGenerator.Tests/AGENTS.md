# KmlGenerator.Tests

- Purpose: deterministic unit and integration coverage for the KML workflow hosts.
- Prefer runner-style tests for console projects and focused service tests for core logic.
- Keep tests local and deterministic; avoid live network dependencies.
- Cover authoritative ARC extraction, assembler label preservation, generator diagnostics, and current geometry-native overlap rendering here.
- Do not treat `KmlGenerator.Console` coverage diagnostics alone as proof that extraction and assembly are correct. Add extractor or assembler coverage when a source feature can disappear before the final request is built.
