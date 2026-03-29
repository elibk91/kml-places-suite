# Official One-Time Master List Workspace

This directory is the working area for the one-time official-source replacement of the Google-built master lists.

## Files

- `official-brand-records.json`
  - manually assembled or scraped official brand rows
  - one record per real store/club
  - must use only official brand-owned source URLs
- `official-brand-records.review.json`
  - generated review artifact
  - shows how each official row got coordinates:
    - `official`
    - `legacy-address-match`
    - `unresolved`

## Process

1. Add official rows to `official-brand-records.json`.
2. Run:
   - `scripts/legacy/build-official-master-lists-2026-03-28.ps1`
3. Review `official-brand-records.review.json`.
4. If any rows are `unresolved`, collect coordinates later or add them explicitly.

This is intentionally one-time and concrete, not a reusable scraping framework.
