# PlacesGatherer.Console.Tests

- Purpose: deterministic tests for Google Places client and gatherer behavior.
- Mock HTTP by default.
- Live Google tests must remain opt-in and gated by environment variables.
- Keep retry-behavior coverage here; do not turn standard test runs into live API checks.
