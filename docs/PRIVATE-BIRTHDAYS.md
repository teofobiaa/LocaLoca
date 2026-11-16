# Private birthdays and age-based recommendations

Loca Loca stores a user's own date of birth separately from the public profile contract. The full date is available only through the authenticated `/api/me/profile/birthday` owner route. It is not included in registration/login responses, the standard profile DTO, public profile/search responses, or audience analytics.

Birthday contacts are a private, per-account list. Every contact query, update, delete, and calendar export is scoped by the authenticated user ID. The database revokes direct access to `birthday_contacts` from `PUBLIC`, `anon`, and `authenticated`; the API runtime role receives only the grants required to manage the owning account's records.

Calendar export produces an iCalendar (`.ics`) event with a yearly recurrence and a reminder one day before the birthday. The event UID is derived from the persistent contact ID, so exporting the same contact again does not create a new logical event when an existing calendar client updates/imports it.

Age suitability is optional metadata on attractions. When a minimum or maximum age is configured, a user with no date of birth is excluded from that age-restricted recommendation; absence of a date must not bypass the restriction. The date of birth is not used for public display.

The aggregate age report publishes only age bands. It returns no age-band counts when any populated band has fewer than five accounts, reducing the chance of inferring a single person's age group.

## Database rollout

1. Apply `database/migrations/00_migration_history.sql` if the migration ledger has not been initialized.
2. Apply `database/migrations/14_user_birthdays_and_age_filtering.sql` after migration 013.
3. Run `database/security/06_update_runtime_permissions.sql` for an existing `localoca_app` role, or use `05_create_app_role.sql` for a new runtime role. Both scripts are pinned in `database/migrations/manifest.json`.
4. Deploy the API build containing migration-014 readiness checks only after the database schema and runtime grants are in place.

Fresh installations use the version-14 baseline at `database/schema/01_schema.sql`; migration 014 is idempotent and remains in the manifest for existing installations.
