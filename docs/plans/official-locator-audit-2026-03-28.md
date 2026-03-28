# Official Locator Audit

Date: `2026-03-28`

Purpose: one-time audit of official brand-owned locator sources for the currently configured gym and grocery chains. This is not a reusable scraping framework design. It is a grounded map of what the current official sources look like and how a one-time extraction should proceed.

## Use Rules

- Only official brand-owned domains count as source of truth.
- Search engines are allowed only to find the official page.
- Google Maps, Yelp, Apple Maps, directory aggregators, and news articles are not authoritative sources.
- If a locator is too hostile to scrape reliably, manual collection from official pages is acceptable.

## Gym Brands

| Brand | Official source | Rough scrape mode | First-pass notes |
|---|---|---|---|
| Planet Fitness | `https://www.planetfitness.com/?clubid=10353` | `xhr_json_or_search_results` | Official site exposes nearby club cards with address in rendered HTML. Likely backed by XHR. |
| LA Fitness | `https://www.lafitness.com/Pages/findClub.aspx` | `html_results_or_xhr` | Search results page clearly lists clubs and addresses. Very promising for direct scrape or result-page parsing. |
| Esporta Fitness | `https://www.esportafitness.com/Pages/clubhome.aspx?clubid=507` | `lafitness_family` | Official site indicates Esporta clubs are rebranded to LA Fitness. Treat as LA Fitness-family source. |
| Crunch Fitness | `https://www.crunch.com/locations` | `xhr_json_or_browser` | Official locations surface exists but appears app-like. Expect network/XHR inspection. |
| Workout Anytime | `https://www.workoutanytime.com/locations/` | `xhr_json_or_directory` | Official locations page exists. Need to inspect whether state/city/club links are rendered or loaded dynamically. |
| Anytime Fitness | `https://www.anytimefitness.com/locations/us/ga/` | `directory_or_browser` | Official location path structure exists. Need to confirm state index completeness and whether club pages are server-rendered. |
| Snap Fitness | `https://www.snapfitness.com/us/gyms` | `browser_or_xhr` | Official site has gym pages, but the locator looks JS-heavy. Search result surfaced a single-gym page, not a state directory. |
| YMCA | `https://ymcaatlanta.org/locations` | `html_directory` | Best official gym source in the set. Metro Atlanta-specific and strongly server-rendered. |
| Life Time | `https://www.lifetime.life/locations.html` | `html_directory_and_detail_crawl` | Official all-locations page with state lists and detail links. Strong direct scrape candidate. |
| Onelife Fitness | `https://www.onelifefitness.com/gyms/atlanta-perimeter` | `detail_crawl` | Official club detail pages are clear. Need the clubs index or sitemap to enumerate Georgia clubs. |

## Grocery Brands

| Brand | Official source | Rough scrape mode | First-pass notes |
|---|---|---|---|
| Kroger | `https://www.kroger.com/stores/grocery/ga/atlanta` | `html_directory_and_detail_crawl` | Official city/state listing pattern is strong. Good direct scrape candidate. |
| Publix | `https://www.publix.com/locations` | `search_or_directory` | Official locations root exists. Need to inspect whether Georgia results are directory-based or search-backed. |
| Walmart | `https://www.walmart.com/store/finder` | `search_and_detail_crawl` | Official finder plus detail pages. Likely easiest to use store pages after discovery. |
| Walmart Neighborhood Market | `https://www.walmart.com/store/7601-atlanta-ga` | `search_and_detail_crawl` | Same official family as Walmart. Must filter explicitly by page/store type label. |
| Target Grocery | `https://www.target.com/store-locator/store-directory/georgia` | `html_directory` | Official directory is good. Need store-service filtering if only grocery-capable Targets should count. |
| ALDI | `https://stores.aldi.us/ga` | `html_directory_and_detail_crawl` | Excellent state/city/store hierarchy. |
| Trader Joe's | `https://locations.traderjoes.com/ga/` | `html_directory_and_detail_crawl` | Excellent state/city/store hierarchy. |
| Lidl | `https://www.lidl.com/stores` | `browser_or_xhr` | Official locator exists, but first pass did not surface a crawlable Georgia directory. Likely XHR-backed. |
| Whole Foods Market | `https://www.wholefoodsmarket.com/stores` | `directory_or_search` | Official store root exists with detail pages. Need to inspect the listing mechanism. |
| Sprouts Farmers Market | `https://www.sprouts.com/stores/ga/` | `html_directory_and_detail_crawl` | Excellent state directory with clean detail pages. |
| Costco | `https://www.costco.com/warehouse-locations` | `detail_crawl_after_discovery` | Official detail pages are rich. Need a reliable discovery path for all Georgia warehouses. |
| Sam's Club | `https://www.samsclub.com/club-finder` | `search_and_detail_crawl` | Official club pages are rich once discovered. Need club-finder result discovery. |
| The Fresh Market | `https://stores.thefreshmarket.com/ga` | `html_directory_and_detail_crawl` | Good state/city/store hierarchy. |

## Immediate Extraction Order

Start with the sources that are both official and easy to verify:

1. `YMCA`
2. `ALDI`
3. `Trader Joe's`
4. `Sprouts Farmers Market`
5. `Kroger`
6. `Target Grocery`
7. `The Fresh Market`
8. `Life Time`
9. `Onelife Fitness`

Then handle the medium/harder sources:

1. `Publix`
2. `Walmart`
3. `Walmart Neighborhood Market`
4. `Costco`
5. `Sam's Club`
6. `Planet Fitness`
7. `LA Fitness`
8. `Crunch Fitness`
9. `Workout Anytime`
10. `Anytime Fitness`
11. `Snap Fitness`
12. `Lidl`

## Validation Plan

### Record-level validation

- Every output record must include:
  - brand
  - category
  - full address
  - official source URL
- No record may come from a non-official domain.
- No record may be included if the page is clearly not a store/club location page.

### Brand-level validation

- Each brand extraction should emit:
  - count of official pages visited
  - count of locations emitted
  - list of official source URLs used
- Large drops relative to expectation should be reviewed manually.

### Anchor checks

Use known metro-Atlanta anchors to spot-check completeness:

- `LA Fitness | 3535 Peachtree Rd NE Ste 300`
- one Atlanta `Trader Joe's`
- one Buckhead `Publix`
- one Atlanta `ALDI`
- one Atlanta-area `YMCA` branch

### Manual review acceptance

Before replacing active master lists:

- review additions that were not in the Google-built files
- review removals that were in the Google-built files
- confirm obvious false positives are gone
- confirm the anchor locations exist in the official-source output

## Notes

- This audit is intentionally concrete and one-time.
- If an official site exposes coordinates, keep them.
- If it does not, discovery still counts as successful; coordinates can be solved later from official addresses.
