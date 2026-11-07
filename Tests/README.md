# Loca Loca tests

This directory is reserved for automated tests.

Planned projects:

- `LocaLoca.Tests/` — unit tests for shared and client-side .NET logic where it can be tested independently of WinUI.
- `LocaLoca.Api.Tests/` — API tests, including endpoint and application integration tests.
- `LocaLoca.Database.Tests/` — PostgreSQL schema, migration, and database integration tests.

Test projects and dependencies will be added in a separate implementation step. Database integration tests must use an isolated test database and must never target a user's local application database.
