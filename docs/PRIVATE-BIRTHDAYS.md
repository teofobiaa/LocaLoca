# Private birthdays and age-based recommendations

## Privacy boundary

A user's full date of birth is stored in `public.users.date_of_birth` and is available only through the authenticated owner endpoints:

- `GET /api/me/profile/birthday`
- `PUT /api/me/profile/birthday`

The full date is not included in authentication responses, the standard `UserDto`, public profile DTOs, or public search results. The endpoints send `Cache-Control: private, no-store`.

Birthday contacts are a private, per-account list. Every list, create, update, delete, and calendar-export query is scoped by the authenticated `user_id`. The API does not accept a user ID from the caller to select another account's contacts.

The database migration applies ownership constraints and access controls for `birthday_contacts`. Runtime permissions are checked by `/ready`; an API instance reports not-ready when the birthday schema or required grants are missing.

## Age-based recommendations

Attractions may define `min_recommended_age` and/or `max_recommended_age`. The recommendations query calculates the user's age from the private date of birth and applies the configured bounds.

When an attraction has an age restriction and the user has not provided a date of birth, that attraction is excluded. Missing personal data must not bypass a configured age limit. Attractions without age restrictions remain eligible.

## Calendar reminders

A birthday contact can be exported as an iCalendar (`.ics`) event. The event has yearly recurrence and a display reminder one day before the birthday. Its UID is derived from the persistent contact ID so repeated exports identify the same logical event.

## Aggregate age report

`database/analytics/02_audience_age.sql` is an optional, read-only administrator query. It returns age bands only: no exact birth dates, account IDs, or individual profile records.

The query applies a minimum group size of five. If any populated band contains fewer than five accounts, the query suppresses the entire result rather than publishing small groups. The report is not an application API and must not be treated as an individual-level data source.

## Database rollout

The repository's current schema baseline is version **13** and the manifest is at schema version **20**. The birthday feature uses the canonical migration sequence:

1. Apply baseline `database/schema/01_schema.sql` (version 13) for a fresh database.
2. Apply migrations in manifest order. Birthday columns and contacts are introduced by `database/migrations/14_user_birthdays_and_age_filtering.sql`, before profile-privacy migration 015.
3. For an existing `localoca_app` role, run the repeatable `database/security/06_update_runtime_permissions.sql` after applying the required migrations.
4. For a new runtime role, run `database/security/05_create_app_role.sql`, then run `06_update_runtime_permissions.sql` so the role receives the later birthday and privacy grants.
5. Deploy the API only after the schema and runtime grants are in place. The `/ready` endpoint validates the birthday schema and permissions.

Do not renumber migration 014, duplicate it in another branch, or change the baseline to make a feature branch fit. The migration manifest and drift tests are the source of truth for schema ordering.
