# SQL job profile and catalog selectors

## Intent

Make SQL job setup easier by replacing the free-text profile ID with a profile selector, then offering that profile's configured catalogs in a catalog selector. Job revisions continue to store the same `profileId` and `catalog` values.

## Approaches

- **Dependent selectors (chosen):** select an active profile by name and server, then select one entry from its `allowedCatalogs`. This uses the existing profile-list API and preserves current job/API formats.
- **Combined profile/catalog selector:** show each profile/catalog pair as one option. This duplicates profile names and makes catalog choices harder to scan.
- **Live SQL discovery:** connect to the selected server and enumerate databases. This adds database permissions and connection failure states, and could reveal catalogs beyond the profile's explicit allowlist.

## Behavior

- Load profiles from the existing connection-profile endpoint for SQL job configuration. Show the profile name and server; store its ID in `configuration.profileId`.
- After a profile is selected, offer only its `allowedCatalogs` values in the catalog selector and store the chosen catalog in `configuration.catalog`.
- Changing profiles clears the prior catalog so a catalog cannot accidentally carry over to a profile that does not allow it.
- Keep existing job revision and template validation contracts. Do not add database objects or a new backend endpoint.
- Keep non-SQL template authoring independent of profile-list availability. Provide loading, empty, and request-error states for SQL profile choices, and translate new labels in English and pt-BR.
- For editing an existing job whose profile is retired or whose saved catalog is no longer allowlisted, preserve its saved values and show that the selection is unavailable until the user chooses an active profile/catalog. Do not silently rewrite saved job configuration.

## Out of scope

No live database enumeration, profile creation/editing inside the job wizard, credential display, or change to persisted configuration shape.

## Verification

Cover profile loading and labels, ID persistence, allowed-catalog options, catalog reset on profile change, non-SQL form independence, and preservation of unavailable values when editing. Run relevant UI tests and frontend build.
