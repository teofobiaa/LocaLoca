# Profile birthdays and age-based recommendations

## Privacy boundary

A user's full date of birth is stored in `public.users.date_of_birth` and can be read or changed only through the authenticated owner endpoints:

- `GET /api/me/profile/birthday`
- `PUT /api/me/profile/birthday`

The full date is excluded from authentication responses, the standard `UserDto`, search results and other users' public profile DTOs. The owner-only endpoints send `Cache-Control: private, no-store`.

The social profile has an independent `date_of_birth_visibility` setting with the same values as other profile fields:

- `private` — only the account owner can see the birthday;
- `friends` — only confirmed mutual friends can see it;
- `registered` — any authenticated LocaLoca user can see it.

When visible, another user's profile receives only the month and day. The birth year and full date are never returned by the public profile endpoint. Pending friend requests do not count as friendship. A block in either direction hides the entire profile regardless of the selected visibility.

The default for existing and new accounts is `private`. The setting is managed together with the other fields through `GET/PUT /api/me/profile/privacy`.

The former owner-maintained birthday contacts list and its calendar export have been retired. Migration 022 removes `public.birthday_contacts`; the associated API routes, client methods, profile editor and dashboard reminders are no longer part of the application. This list was unrelated to actual LocaLoca accounts and is not a substitute for friend profiles.

## Age-based recommendations

Attractions may define `min_recommended_age` and/or `max_recommended_age`. The recommendations query calculates the user's age from the private full date of birth and applies the configured bounds.

When an attraction has an age restriction and the user has not provided a date of birth, that attraction is excluded. Missing personal data must not bypass a configured age limit. Attractions without age restrictions remain eligible.

## Aggregate age report

`database/analytics/02_audience_age.sql` is an optional, read-only administrator query. It returns age bands only: no exact birth dates, account IDs, or individual profile records.

The query applies a minimum group size of five. If any populated band contains fewer than five accounts, the query suppresses the entire result rather than publishing small groups. The report is not an application API and must not be treated as an individual-level data source.

## Database rollout

The repository's current schema version is **22**. The fresh-install baseline remains the immutable version **20** snapshot; migrations 21 and 22 are applied afterwards.

1. Apply baseline `database/schema/02_schema_v20.sql` (or the documented schema baseline) for a fresh database.
2. Apply migrations in manifest order. Migration 022 adds `date_of_birth_visibility` with a `private` default and drops the retired contacts table.
3. Run the repeatable `database/security/06_update_runtime_permissions.sql` after the migrations so runtime grants and RLS policies match the current schema.
4. Deploy the API only after the schema and runtime grants are in place. The `/ready` endpoint checks the current profile-privacy schema and the age-recommendation columns.

Migration 014 and the version 20 baseline are historical inputs and must not be edited to simulate the current schema. The migration manifest and drift tests are the source of truth for schema ordering.
