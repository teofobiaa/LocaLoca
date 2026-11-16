# PostgreSQL runtime permission-drift audit

## Purpose

`database/audits/check_runtime_permission_drift.sql` is a read-only audit for the
production PostgreSQL database. Run it as a database administrator or another
role that can inspect PostgreSQL catalogs and ACLs.

A clean run returns zero rows. Each result row is a finding with a severity,
stable finding code, affected object, expected state, observed state and a
recommended investigation/remediation. The query does not execute GRANT,
REVOKE, ALTER ROLE, DROP or other repair statements.

## Reviewed runtime-role baseline

The audit checks the `localoca_app` role against the least-privilege setup in:

- `database/security/05_create_app_role.sql`
- `database/security/06_update_runtime_permissions.sql`

It checks:

- Role existence and login/security attributes: SUPERUSER, CREATEDB,
  CREATEROLE, INHERIT, REPLICATION, BYPASSRLS, connection limit, validity and
  role-level GUC overrides.
- Direct and reverse role memberships. NOINHERIT does not make an unexpected
  membership harmless because a member may still be able to SET ROLE.
- Direct grants to any other login/group role on databases, schemas, tables,
  columns, sequences, functions and large objects. The owner, the reviewed
  runtime role and explicitly permitted PUBLIC defaults are treated separately.
- Database CONNECT grants, unexpected database privileges and grant options.
- Schema USAGE/CREATE grants, unexpected schema access and PUBLIC schema grants.
- Exact table grants for catalog, account, visits, photos, wishlist, map,
  sessions, rate-limit and avatar tables.
- Column grants. The only expected column-level grants are UPDATE on
  `users.display_name`, `users.residence_country` and `users.residence_city`.
- Exact USAGE grants on the identity sequences required by application inserts.
- Grant options, PUBLIC table/column/sequence access and default privileges that
  would silently grant access to future objects.
- Unexpected function EXECUTE grants, PUBLIC function access and
  SECURITY DEFINER functions.
- Row-level security enablement and policies, which change the API/database
  authorization model and therefore require explicit review.
- Ownership of databases, schemas, relations, functions, types, sequences,
  default ACLs and large objects.
- Large-object privileges. LocaLoca stores image payloads in `bytea` columns
  and should not need PostgreSQL large objects.

The allowlist is intentionally strict. When a reviewed schema or runtime-role
change is introduced, update the audit baseline and the tests in the same
change. Do not weaken the checks merely to make an unexpected grant disappear.

## Running against production

Open the production database's SQL editor using an administrative connection,
paste the contents of `database/audits/check_runtime_permission_drift.sql`,
and execute it.

Review every returned row. Treat CRITICAL findings as a release/security
incident until explained. HIGH findings require explicit review before a
permission or schema change is accepted. Do not automatically apply the
recommendation text: some findings may be legitimate changes that need a
reviewed baseline update rather than a blind rollback.

The audit is scoped to PostgreSQL role and object permissions in the connected
database. It does not inspect credential values, network rules, provider-level
IAM, application secrets, database server configuration outside the role
catalog, or permissions in other databases.

## Automated regression coverage

`Tests/LocaLoca.Database.Tests/PostgreSqlPermissionDriftTests.cs` runs the same
audit against a dedicated disposable PostgreSQL 16 Testcontainers instance.
The fixture creates the current schema and applies the two reviewed runtime
permission scripts before auditing.

The test suite verifies:

- A clean baseline produces zero findings.
- Each supported drift category is detected independently.
- Multiple independent changes are returned in one audit run.
- Re-running the audit is stable and read-only; it reports drift but does not
  repair or conceal it.
- Every synthetic mutation is executed inside a transaction and rolled back,
  so the test cannot leave the test database in a modified state.

The normal database CI job runs this test class with the rest of
`LocaLoca.Database.Tests`. No separate third-party bot or production write
credential is required.
