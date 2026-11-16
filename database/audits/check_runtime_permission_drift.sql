-- PostgreSQL runtime permission-drift audit for LocaLoca.
--
-- Run this query as a database administrator against the production database.
-- An empty result means the localoca_app role matches the reviewed least-privilege
-- baseline encoded below. Every returned row is a finding; do not suppress it
-- without reviewing the underlying grant, ownership change, or role mutation.
--
-- The query is read-only. It does not repair permissions or modify database state.

WITH
app_role AS (
    SELECT *
    FROM pg_roles
    WHERE rolname = 'localoca_app'
),
expected_table_privileges(schema_name, table_name, privilege_type) AS (
    VALUES
        ('public', 'categories', 'SELECT'),
        ('public', 'countries', 'SELECT'),
        ('public', 'cities', 'SELECT'),
        ('public', 'attractions', 'SELECT'),

        ('public', 'users', 'SELECT'),
        ('public', 'users', 'INSERT'),
        ('public', 'visits', 'SELECT'),
        ('public', 'visits', 'INSERT'),
        ('public', 'visits', 'UPDATE'),
        ('public', 'visits', 'DELETE'),
        ('public', 'photos', 'SELECT'),
        ('public', 'photos', 'INSERT'),
        ('public', 'photos', 'DELETE'),
        ('public', 'wishlist', 'SELECT'),
        ('public', 'wishlist', 'INSERT'),
        ('public', 'wishlist', 'DELETE'),
        ('public', 'user_map_places', 'SELECT'),
        ('public', 'user_map_places', 'INSERT'),
        ('public', 'user_map_places', 'UPDATE'),
        ('public', 'user_map_places', 'DELETE'),
        ('public', 'user_map_place_photos', 'SELECT'),
        ('public', 'user_map_place_photos', 'INSERT'),
        ('public', 'user_map_place_photos', 'DELETE'),
        ('public', 'user_sessions', 'SELECT'),
        ('public', 'user_sessions', 'INSERT'),
        ('public', 'user_sessions', 'UPDATE'),
        ('public', 'user_sessions', 'DELETE'),
        ('public', 'auth_rate_limit_counters', 'SELECT'),
        ('public', 'auth_rate_limit_counters', 'INSERT'),
        ('public', 'auth_rate_limit_counters', 'UPDATE'),
        ('public', 'auth_rate_limit_counters', 'DELETE'),
        ('public', 'user_avatars', 'SELECT'),
        ('public', 'user_avatars', 'INSERT'),
        ('public', 'user_avatars', 'UPDATE'),
        ('public', 'user_avatars', 'DELETE')
),
expected_tables AS (
    SELECT DISTINCT schema_name, table_name
    FROM expected_table_privileges
),
expected_sequence_privileges(schema_name, sequence_name, privilege_type) AS (
    VALUES
        ('public', 'users_user_id_seq', 'USAGE'),
        ('public', 'visits_visit_id_seq', 'USAGE'),
        ('public', 'photos_photo_id_seq', 'USAGE'),
        ('public', 'user_map_places_user_map_place_id_seq', 'USAGE'),
        ('public', 'user_map_place_photos_photo_id_seq', 'USAGE')
),
expected_sequences AS (
    SELECT DISTINCT schema_name, sequence_name
    FROM expected_sequence_privileges
),
expected_column_privileges(schema_name, table_name, column_name, privilege_type) AS (
    VALUES
        ('public', 'users', 'display_name', 'UPDATE'),
        ('public', 'users', 'residence_country', 'UPDATE'),
        ('public', 'users', 'residence_city', 'UPDATE')
),
actual_table_acl AS (
    SELECT
        n.nspname AS schema_name,
        c.relname AS table_name,
        acl.privilege_type,
        acl.is_grantable
    FROM pg_class c
    JOIN pg_namespace n ON n.oid = c.relnamespace
    CROSS JOIN app_role r
    CROSS JOIN LATERAL aclexplode(
        COALESCE(c.relacl, acldefault('r', c.relowner))
    ) acl
    WHERE n.nspname NOT IN ('pg_catalog', 'information_schema')
      AND n.nspname NOT LIKE 'pg_toast%'
      AND c.relkind IN ('r', 'p', 'v', 'm', 'f')
      AND acl.grantee = r.oid
),
actual_sequence_acl AS (
    SELECT
        n.nspname AS schema_name,
        c.relname AS sequence_name,
        acl.privilege_type,
        acl.is_grantable
    FROM pg_class c
    JOIN pg_namespace n ON n.oid = c.relnamespace
    CROSS JOIN app_role r
    CROSS JOIN LATERAL aclexplode(
        COALESCE(c.relacl, acldefault('s', c.relowner))
    ) acl
    WHERE n.nspname NOT IN ('pg_catalog', 'information_schema')
      AND n.nspname NOT LIKE 'pg_toast%'
      AND c.relkind = 'S'
      AND acl.grantee = r.oid
),
actual_column_acl AS (
    SELECT
        n.nspname AS schema_name,
        c.relname AS table_name,
        a.attname AS column_name,
        acl.privilege_type,
        acl.is_grantable
    FROM pg_class c
    JOIN pg_namespace n ON n.oid = c.relnamespace
    JOIN pg_attribute a ON a.attrelid = c.oid
    CROSS JOIN app_role r
    CROSS JOIN LATERAL aclexplode(a.attacl) acl
    WHERE n.nspname NOT IN ('pg_catalog', 'information_schema')
      AND n.nspname NOT LIKE 'pg_toast%'
      AND c.relkind IN ('r', 'p', 'v', 'm', 'f')
      AND a.attnum > 0
      AND NOT a.attisdropped
      AND acl.grantee = r.oid
),
findings AS (
    -- Role existence and immutable security posture.
    SELECT
        'CRITICAL'::text AS severity,
        'ROLE_MISSING'::text AS finding_code,
        'role'::text AS object_type,
        'localoca_app'::text AS object_name,
        'A dedicated runtime login role must exist'::text AS expected,
        'Role not found'::text AS actual,
        'Recreate the runtime role from database/security/05_create_app_role.sql, then reapply database/security/06_update_runtime_permissions.sql.'::text AS recommendation
    WHERE NOT EXISTS (SELECT 1 FROM app_role)

    UNION ALL
    SELECT 'CRITICAL', 'ROLE_ATTRIBUTE_DRIFT', 'role', 'localoca_app.rolsuper',
           'false', r.rolsuper::text, 'Restore NOSUPERUSER.'
    FROM app_role r WHERE r.rolsuper

    UNION ALL
    SELECT 'CRITICAL', 'ROLE_ATTRIBUTE_DRIFT', 'role', 'localoca_app.rolcreatedb',
           'false', r.rolcreatedb::text, 'Restore NOCREATEDB.'
    FROM app_role r WHERE r.rolcreatedb

    UNION ALL
    SELECT 'CRITICAL', 'ROLE_ATTRIBUTE_DRIFT', 'role', 'localoca_app.rolcreaterole',
           'false', r.rolcreaterole::text, 'Restore NOCREATEROLE.'
    FROM app_role r WHERE r.rolcreaterole

    UNION ALL
    SELECT 'HIGH', 'ROLE_ATTRIBUTE_DRIFT', 'role', 'localoca_app.rolcanlogin',
           'true', r.rolcanlogin::text, 'Restore LOGIN; verify that the role remains a dedicated runtime identity.'
    FROM app_role r WHERE NOT r.rolcanlogin

    UNION ALL
    SELECT 'HIGH', 'ROLE_ATTRIBUTE_DRIFT', 'role', 'localoca_app.rolinherit',
           'false', r.rolinherit::text, 'Restore NOINHERIT and inspect all role memberships.'
    FROM app_role r WHERE r.rolinherit

    UNION ALL
    SELECT 'CRITICAL', 'ROLE_ATTRIBUTE_DRIFT', 'role', 'localoca_app.rolreplication',
           'false', r.rolreplication::text, 'Restore NOREPLICATION.'
    FROM app_role r WHERE r.rolreplication

    UNION ALL
    SELECT 'CRITICAL', 'ROLE_ATTRIBUTE_DRIFT', 'role', 'localoca_app.rolbypassrls',
           'false', r.rolbypassrls::text, 'Restore NOBYPASSRLS.'
    FROM app_role r WHERE r.rolbypassrls

    UNION ALL
    SELECT 'MEDIUM', 'ROLE_ATTRIBUTE_DRIFT', 'role', 'localoca_app.rolconnlimit',
           '-1', r.rolconnlimit::text, 'Review the connection limit and restore the approved value (-1) if the change was not intentional.'
    FROM app_role r WHERE r.rolconnlimit <> -1

    UNION ALL
    SELECT 'HIGH', 'ROLE_ATTRIBUTE_DRIFT', 'role', 'localoca_app.rolvaliduntil',
           'NULL (no unexpected expiry)', r.rolvaliduntil::text, 'Review the runtime credential lifecycle and restore the approved validity policy.'
    FROM app_role r WHERE r.rolvaliduntil IS NOT NULL

    UNION ALL
    SELECT 'HIGH', 'ROLE_CONFIGURATION_DRIFT', 'role', 'localoca_app.rolconfig',
           'No role-level session overrides', array_to_string(r.rolconfig, ', '),
           'Remove unreviewed ALTER ROLE ... SET overrides; check search_path and security-sensitive GUCs.'
    FROM app_role r WHERE r.rolconfig IS NOT NULL AND cardinality(r.rolconfig) > 0

    -- Any membership is unexpected: NOINHERIT does not prevent SET ROLE.
    UNION ALL
    SELECT 'CRITICAL', 'ROLE_MEMBERSHIP', 'role membership',
           'localoca_app is a member of ' || parent.rolname,
           'No memberships', 'roleid=' || parent.rolname || ', admin_option=' || m.admin_option::text,
           'Revoke the membership and investigate who granted it.'
    FROM app_role r
    JOIN pg_auth_members m ON m.member = r.oid
    JOIN pg_roles parent ON parent.oid = m.roleid

    UNION ALL
    SELECT 'HIGH', 'ROLE_MEMBERSHIP', 'role membership',
           'Role ' || member_role.rolname || ' is a member of localoca_app',
           'No roles may inherit or SET ROLE to localoca_app',
           'member=' || member_role.rolname || ', admin_option=' || m.admin_option::text,
           'Revoke the membership unless it is explicitly approved and documented.'
    FROM app_role r
    JOIN pg_auth_members m ON m.roleid = r.oid
    JOIN pg_roles member_role ON member_role.oid = m.member

    -- The runtime role must never own database objects or default ACL definitions.
    UNION ALL
    SELECT 'CRITICAL', 'OBJECT_OWNERSHIP', 'database', d.datname,
           'Database owned by an administrative/deployment role', 'Owned by localoca_app',
           'Transfer database ownership to the approved administrative owner.'
    FROM app_role r
    JOIN pg_database d ON d.datdba = r.oid
    WHERE d.datname = current_database()

    UNION ALL
    SELECT 'CRITICAL', 'OBJECT_OWNERSHIP', 'schema', n.nspname,
           'Schema owned by an administrative/deployment role', 'Owned by localoca_app',
           'Transfer schema ownership away from localoca_app.'
    FROM app_role r
    JOIN pg_namespace n ON n.nspowner = r.oid
    WHERE n.nspname NOT IN ('pg_catalog', 'information_schema')
      AND n.nspname NOT LIKE 'pg_toast%'

    UNION ALL
    SELECT 'CRITICAL', 'OBJECT_OWNERSHIP', 'relation', n.nspname || '.' || c.relname,
           'Relation owned by an administrative/deployment role', 'Owned by localoca_app',
           'Transfer relation ownership away from localoca_app; review all privileges inherited through ownership.'
    FROM app_role r
    JOIN pg_class c ON c.relowner = r.oid
    JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname NOT IN ('pg_catalog', 'information_schema')
      AND n.nspname NOT LIKE 'pg_toast%'

    UNION ALL
    SELECT 'CRITICAL', 'OBJECT_OWNERSHIP', 'function', n.nspname || '.' || p.proname,
           'Function owned by an administrative/deployment role', 'Owned by localoca_app',
           'Transfer function ownership away from localoca_app and review SECURITY DEFINER behavior.'
    FROM app_role r
    JOIN pg_proc p ON p.proowner = r.oid
    JOIN pg_namespace n ON n.oid = p.pronamespace
    WHERE n.nspname NOT IN ('pg_catalog', 'information_schema')
      AND n.nspname NOT LIKE 'pg_toast%'

    UNION ALL
    SELECT 'CRITICAL', 'OBJECT_OWNERSHIP', 'type', n.nspname || '.' || t.typname,
           'Type owned by an administrative/deployment role', 'Owned by localoca_app',
           'Transfer type ownership away from localoca_app.'
    FROM app_role r
    JOIN pg_type t ON t.typowner = r.oid
    JOIN pg_namespace n ON n.oid = t.typnamespace
    WHERE t.typrelid = 0
      AND n.nspname NOT IN ('pg_catalog', 'information_schema')
      AND n.nspname NOT LIKE 'pg_toast%'

    UNION ALL
    SELECT 'CRITICAL', 'OBJECT_OWNERSHIP', 'default privileges', COALESCE(n.nspname, '<all schemas>'),
           'Default ACLs owned by an administrative/deployment role', 'Default ACL owned by localoca_app',
           'Remove runtime-role ownership of default privileges and recreate them under the deployment owner.'
    FROM app_role r
    JOIN pg_default_acl d ON d.defaclrole = r.oid
    LEFT JOIN pg_namespace n ON n.oid = d.defaclnamespace

    -- Database: localoca_app is granted CONNECT only (not CREATE or TEMP directly).
    UNION ALL
    SELECT 'HIGH', 'DATABASE_PRIVILEGE_MISSING', 'database', current_database(),
           'Direct CONNECT grant to localoca_app', 'CONNECT grant missing',
           'Restore GRANT CONNECT ON DATABASE ... TO localoca_app.'
    FROM app_role r
    JOIN pg_database d ON d.datname = current_database()
    WHERE NOT EXISTS (
        SELECT 1
        FROM aclexplode(COALESCE(d.datacl, acldefault('d', d.datdba))) acl
        WHERE acl.grantee = r.oid AND acl.privilege_type = 'CONNECT'
    )

    UNION ALL
    SELECT 'CRITICAL', 'DATABASE_PRIVILEGE_EXTRA', 'database', current_database(),
           'CONNECT only', acl.privilege_type,
           'Revoke unexpected database privileges from localoca_app.'
    FROM app_role r
    JOIN pg_database d ON d.datname = current_database()
    CROSS JOIN LATERAL aclexplode(COALESCE(d.datacl, acldefault('d', d.datdba))) acl
    WHERE acl.grantee = r.oid
      AND acl.privilege_type <> 'CONNECT'

    UNION ALL
    SELECT 'CRITICAL', 'DATABASE_GRANT_OPTION', 'database', current_database(),
           'No grant options', acl.privilege_type || ' WITH GRANT OPTION',
           'Revoke the database grant option from localoca_app.'
    FROM app_role r
    JOIN pg_database d ON d.datname = current_database()
    CROSS JOIN LATERAL aclexplode(COALESCE(d.datacl, acldefault('d', d.datdba))) acl
    WHERE acl.grantee = r.oid AND acl.is_grantable

    UNION ALL
    SELECT 'CRITICAL', 'PUBLIC_DATABASE_PRIVILEGE', 'database', current_database(),
           'PUBLIC must not have CREATE', 'PUBLIC has ' || acl.privilege_type,
           'Revoke the unexpected database privilege from PUBLIC.'
    FROM pg_database d
    CROSS JOIN LATERAL aclexplode(COALESCE(d.datacl, acldefault('d', d.datdba))) acl
    WHERE d.datname = current_database()
      AND acl.grantee = 0
      AND acl.privilege_type = 'CREATE'
    UNION ALL
    SELECT 'HIGH', 'UNAPPROVED_ROLE_PRIVILEGE', 'database', current_database(),
           'Direct database grants only for the database owner and localoca_app',
           'Granted to ' || pg_get_userbyid(acl.grantee) || ': ' || acl.privilege_type,
           'Review why another login/group role has direct database privileges; revoke or explicitly allowlist it.'
    FROM pg_database d
    CROSS JOIN app_role r
    CROSS JOIN LATERAL aclexplode(COALESCE(d.datacl, acldefault('d', d.datdba))) acl
    WHERE d.datname = current_database()
      AND acl.grantee NOT IN (0, d.datdba, r.oid)

    -- Schema: usage on public is required; CREATE and all other schema access are forbidden.
    UNION ALL
    SELECT 'HIGH', 'SCHEMA_PRIVILEGE_MISSING', 'schema', 'public',
           'Direct USAGE grant to localoca_app', 'USAGE grant missing',
           'Restore GRANT USAGE ON SCHEMA public TO localoca_app.'
    FROM app_role r
    JOIN pg_namespace n ON n.nspname = 'public'
    WHERE NOT EXISTS (
        SELECT 1
        FROM aclexplode(COALESCE(n.nspacl, acldefault('n', n.nspowner))) acl
        WHERE acl.grantee = r.oid AND acl.privilege_type = 'USAGE'
    )

    UNION ALL
    SELECT 'CRITICAL', 'SCHEMA_PRIVILEGE_EXTRA', 'schema', n.nspname,
           CASE WHEN n.nspname = 'public' THEN 'USAGE only' ELSE 'No privileges' END,
           acl.privilege_type,
           'Revoke unexpected schema privileges from localoca_app.'
    FROM app_role r
    JOIN pg_namespace n ON true
    CROSS JOIN LATERAL aclexplode(COALESCE(n.nspacl, acldefault('n', n.nspowner))) acl
    WHERE n.nspname NOT IN ('pg_catalog', 'information_schema')
      AND n.nspname NOT LIKE 'pg_toast%'
      AND acl.grantee = r.oid
      AND NOT (n.nspname = 'public' AND acl.privilege_type = 'USAGE')

    UNION ALL
    SELECT 'CRITICAL', 'PUBLIC_SCHEMA_PRIVILEGE', 'schema', n.nspname,
           CASE WHEN n.nspname = 'public' THEN 'PUBLIC USAGE only' ELSE 'No PUBLIC privileges' END,
           'PUBLIC has ' || acl.privilege_type,
           'Revoke unexpected schema privileges from PUBLIC.'
    FROM pg_namespace n
    CROSS JOIN LATERAL aclexplode(COALESCE(n.nspacl, acldefault('n', n.nspowner))) acl
    WHERE n.nspname NOT IN ('pg_catalog', 'information_schema')
      AND n.nspname NOT LIKE 'pg_toast%'
      AND acl.grantee = 0
      AND NOT (n.nspname = 'public' AND acl.privilege_type = 'USAGE')

    UNION ALL
    SELECT 'HIGH', 'SCHEMA_PRIVILEGE_MISSING', 'schema', 'public',
           'Effective USAGE', 'USAGE is not effective',
           'Restore schema USAGE for localoca_app and check PUBLIC/schema ACL changes.'
    FROM app_role r
    JOIN pg_namespace n ON n.nspname = 'public'
    WHERE NOT has_schema_privilege(r.oid, n.oid, 'USAGE')
    UNION ALL
    SELECT 'HIGH', 'UNAPPROVED_ROLE_PRIVILEGE', 'schema', n.nspname,
           'Direct schema grants only for the schema owner and localoca_app (plus PUBLIC USAGE on public)',
           'Granted to ' || pg_get_userbyid(acl.grantee) || ': ' || acl.privilege_type,
           'Review this direct schema grant and revoke it or explicitly document the additional role.'
    FROM pg_namespace n
    CROSS JOIN app_role r
    CROSS JOIN LATERAL aclexplode(COALESCE(n.nspacl, acldefault('n', n.nspowner))) acl
    WHERE n.nspname NOT IN ('pg_catalog', 'information_schema')
      AND n.nspname NOT LIKE 'pg_toast%'
      AND acl.grantee NOT IN (0, n.nspowner, r.oid)

    UNION ALL
    SELECT 'CRITICAL', 'SCHEMA_GRANT_OPTION', 'schema', n.nspname,
           'No grant options', acl.privilege_type || ' WITH GRANT OPTION',
           'Revoke the schema grant option from localoca_app.'
    FROM app_role r
    JOIN pg_namespace n ON true
    CROSS JOIN LATERAL aclexplode(COALESCE(n.nspacl, acldefault('n', n.nspowner))) acl
    WHERE n.nspname NOT IN ('pg_catalog', 'information_schema')
      AND n.nspname NOT LIKE 'pg_toast%'
      AND acl.grantee = r.oid
      AND acl.is_grantable

    UNION ALL
    SELECT 'HIGH', 'ROW_LEVEL_SECURITY_DRIFT', 'table', n.nspname || '.' || c.relname,
           'No unreviewed RLS enablement in the current application baseline',
           CASE WHEN c.relforcerowsecurity THEN 'RLS enabled and forced' ELSE 'RLS enabled' END,
           'Review the RLS policy design and API access model; update the approved baseline only after security review.'
    FROM pg_class c
    JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname NOT IN ('pg_catalog', 'information_schema')
      AND n.nspname NOT LIKE 'pg_toast%'
      AND c.relkind IN ('r', 'p')
      AND (c.relrowsecurity OR c.relforcerowsecurity)

    UNION ALL
    SELECT 'HIGH', 'RLS_POLICY_DRIFT', 'policy', n.nspname || '.' || c.relname || '.' || p.polname,
           'No unreviewed row-level security policies in the current baseline',
           COALESCE(pg_get_expr(p.polqual, p.polrelid), '<none>') || COALESCE(' / ' || pg_get_expr(p.polwithcheck, p.polrelid), ''),
           'Review the policy expressions and role targets, then update the approved baseline intentionally.'
    FROM pg_policy p
    JOIN pg_class c ON c.oid = p.polrelid
    JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname NOT IN ('pg_catalog', 'information_schema')
      AND n.nspname NOT LIKE 'pg_toast%'

    -- Required application relations must exist.
    UNION ALL
    SELECT 'CRITICAL', 'EXPECTED_OBJECT_MISSING', 'table', e.schema_name || '.' || e.table_name,
           'Required application table', 'Relation not found',
           'Restore the expected schema using the reviewed database baseline/migrations.'
    FROM expected_tables e
    WHERE to_regclass(format('%I.%I', e.schema_name, e.table_name)) IS NULL

    -- Exact, direct table privileges. This also prevents accidental reliance on PUBLIC.
    UNION ALL
    SELECT 'HIGH', 'TABLE_PRIVILEGE_MISSING', 'table', e.schema_name || '.' || e.table_name,
           e.privilege_type, 'Direct grant missing',
           'Restore the exact table grant from database/security/05_create_app_role.sql or 06_update_runtime_permissions.sql.'
    FROM expected_table_privileges e
    CROSS JOIN app_role r
    WHERE to_regclass(format('%I.%I', e.schema_name, e.table_name)) IS NOT NULL
      AND NOT EXISTS (
          SELECT 1
          FROM actual_table_acl a
          WHERE a.schema_name = e.schema_name
            AND a.table_name = e.table_name
            AND a.privilege_type = e.privilege_type
      )

    UNION ALL
    SELECT 'CRITICAL', 'TABLE_PRIVILEGE_EXTRA', 'table', a.schema_name || '.' || a.table_name,
           'Only the approved table privileges', a.privilege_type,
           'Revoke the unapproved table privilege from localoca_app.'
    FROM actual_table_acl a
    WHERE NOT EXISTS (
        SELECT 1
        FROM expected_table_privileges e
        WHERE e.schema_name = a.schema_name
          AND e.table_name = a.table_name
          AND e.privilege_type = a.privilege_type
    )

    UNION ALL
    SELECT 'CRITICAL', 'GRANT_OPTION', 'table', a.schema_name || '.' || a.table_name,
           'No grant options', a.privilege_type || ' WITH GRANT OPTION',
           'Revoke the grant option and reapply the privilege without WITH GRANT OPTION.'
    FROM actual_table_acl a
    WHERE a.is_grantable
    UNION ALL
    SELECT 'HIGH', 'UNAPPROVED_ROLE_PRIVILEGE', 'table', n.nspname || '.' || c.relname,
           'Direct table grants only for the relation owner and localoca_app',
           'Granted to ' || pg_get_userbyid(acl.grantee) || ': ' || acl.privilege_type,
           'Review direct access granted to another database role; account for approved application/service roles explicitly.'
    FROM pg_class c
    JOIN pg_namespace n ON n.oid = c.relnamespace
    CROSS JOIN app_role r
    CROSS JOIN LATERAL aclexplode(COALESCE(c.relacl, acldefault('r', c.relowner))) acl
    WHERE n.nspname NOT IN ('pg_catalog', 'information_schema')
      AND n.nspname NOT LIKE 'pg_toast%'
      AND c.relkind IN ('r', 'p', 'v', 'm', 'f')
      AND acl.grantee NOT IN (0, c.relowner, r.oid)

    UNION ALL
    SELECT 'CRITICAL', 'PUBLIC_TABLE_PRIVILEGE', 'table', n.nspname || '.' || c.relname,
           'No table privileges for PUBLIC', 'PUBLIC has ' || acl.privilege_type,
           'Revoke the table privilege from PUBLIC; application data must not be anonymously accessible.'
    FROM pg_class c
    JOIN pg_namespace n ON n.oid = c.relnamespace
    CROSS JOIN LATERAL aclexplode(
        COALESCE(c.relacl, acldefault('r', c.relowner))
    ) acl
    WHERE n.nspname NOT IN ('pg_catalog', 'information_schema')
      AND n.nspname NOT LIKE 'pg_toast%'
      AND c.relkind IN ('r', 'p', 'v', 'm', 'f')
      AND acl.grantee = 0

    -- Column grants are intentionally limited to the three editable profile fields.
    UNION ALL
    SELECT 'HIGH', 'COLUMN_PRIVILEGE_MISSING', 'column',
           e.schema_name || '.' || e.table_name || '.' || e.column_name,
           e.privilege_type, 'Direct column grant missing',
           'Restore the approved column-level profile update grant.'
    FROM expected_column_privileges e
    CROSS JOIN app_role r
    WHERE NOT EXISTS (
        SELECT 1
        FROM actual_column_acl a
        WHERE a.schema_name = e.schema_name
          AND a.table_name = e.table_name
          AND a.column_name = e.column_name
          AND a.privilege_type = e.privilege_type
    )

    UNION ALL
    SELECT 'CRITICAL', 'COLUMN_PRIVILEGE_EXTRA', 'column',
           a.schema_name || '.' || a.table_name || '.' || a.column_name,
           'Only UPDATE on users.display_name, users.residence_country and users.residence_city',
           a.privilege_type,
           'Revoke the unapproved column-level privilege from localoca_app.'
    FROM actual_column_acl a
    WHERE NOT EXISTS (
        SELECT 1
        FROM expected_column_privileges e
        WHERE e.schema_name = a.schema_name
          AND e.table_name = a.table_name
          AND e.column_name = a.column_name
          AND e.privilege_type = a.privilege_type
    )

    UNION ALL
    SELECT 'CRITICAL', 'COLUMN_GRANT_OPTION', 'column',
           a.schema_name || '.' || a.table_name || '.' || a.column_name,
           'No grant options', a.privilege_type || ' WITH GRANT OPTION',
           'Revoke the column grant option.'
    FROM actual_column_acl a
    WHERE a.is_grantable
    UNION ALL
    SELECT 'HIGH', 'UNAPPROVED_ROLE_PRIVILEGE', 'column',
           n.nspname || '.' || c.relname || '.' || a.attname,
           'Direct column grants only for the relation owner and localoca_app',
           'Granted to ' || pg_get_userbyid(acl.grantee) || ': ' || acl.privilege_type,
           'Review the column-level privilege assigned to another database role.'
    FROM pg_class c
    JOIN pg_namespace n ON n.oid = c.relnamespace
    JOIN pg_attribute a ON a.attrelid = c.oid
    CROSS JOIN app_role r
    CROSS JOIN LATERAL aclexplode(a.attacl) acl
    WHERE n.nspname NOT IN ('pg_catalog', 'information_schema')
      AND n.nspname NOT LIKE 'pg_toast%'
      AND c.relkind IN ('r', 'p', 'v', 'm', 'f')
      AND a.attnum > 0
      AND NOT a.attisdropped
      AND acl.grantee NOT IN (0, c.relowner, r.oid)

    UNION ALL
    SELECT 'CRITICAL', 'PUBLIC_COLUMN_PRIVILEGE', 'column',
           n.nspname || '.' || c.relname || '.' || a.attname,
           'No column privileges for PUBLIC', 'PUBLIC has ' || acl.privilege_type,
           'Revoke the column privilege from PUBLIC.'
    FROM pg_class c
    JOIN pg_namespace n ON n.oid = c.relnamespace
    JOIN pg_attribute a ON a.attrelid = c.oid
    CROSS JOIN LATERAL aclexplode(a.attacl) acl
    WHERE n.nspname NOT IN ('pg_catalog', 'information_schema')
      AND n.nspname NOT LIKE 'pg_toast%'
      AND c.relkind IN ('r', 'p', 'v', 'm', 'f')
      AND a.attnum > 0
      AND NOT a.attisdropped
      AND acl.grantee = 0

    -- Exact sequence grants: USAGE only on identity sequences needed by INSERT.
    UNION ALL
    SELECT 'CRITICAL', 'EXPECTED_OBJECT_MISSING', 'sequence', e.schema_name || '.' || e.sequence_name,
           'Required identity sequence', 'Sequence not found',
           'Restore the expected identity column/sequence from the reviewed database schema.'
    FROM expected_sequences e
    WHERE to_regclass(format('%I.%I', e.schema_name, e.sequence_name)) IS NULL

    UNION ALL
    SELECT 'HIGH', 'SEQUENCE_PRIVILEGE_MISSING', 'sequence',
           e.schema_name || '.' || e.sequence_name,
           e.privilege_type, 'Direct grant missing',
           'Restore the exact USAGE grant on the approved identity sequence.'
    FROM expected_sequence_privileges e
    CROSS JOIN app_role r
    WHERE to_regclass(format('%I.%I', e.schema_name, e.sequence_name)) IS NOT NULL
      AND NOT EXISTS (
          SELECT 1
          FROM actual_sequence_acl a
          WHERE a.schema_name = e.schema_name
            AND a.sequence_name = e.sequence_name
            AND a.privilege_type = e.privilege_type
      )

    UNION ALL
    SELECT 'CRITICAL', 'SEQUENCE_PRIVILEGE_EXTRA', 'sequence',
           a.schema_name || '.' || a.sequence_name,
           'USAGE only on approved identity sequences', a.privilege_type,
           'Revoke the unapproved sequence privilege from localoca_app.'
    FROM actual_sequence_acl a
    WHERE NOT EXISTS (
        SELECT 1
        FROM expected_sequence_privileges e
        WHERE e.schema_name = a.schema_name
          AND e.sequence_name = a.sequence_name
          AND e.privilege_type = a.privilege_type
    )

    UNION ALL
    SELECT 'CRITICAL', 'SEQUENCE_GRANT_OPTION', 'sequence',
           a.schema_name || '.' || a.sequence_name,
           'No grant options', a.privilege_type || ' WITH GRANT OPTION',
           'Revoke the sequence grant option.'
    FROM actual_sequence_acl a
    WHERE a.is_grantable
    UNION ALL
    SELECT 'HIGH', 'UNAPPROVED_ROLE_PRIVILEGE', 'sequence',
           n.nspname || '.' || c.relname,
           'Direct sequence grants only for the sequence owner and localoca_app',
           'Granted to ' || pg_get_userbyid(acl.grantee) || ': ' || acl.privilege_type,
           'Review direct access to this sequence; sequence privileges can expose or alter generated identifiers.'
    FROM pg_class c
    JOIN pg_namespace n ON n.oid = c.relnamespace
    CROSS JOIN app_role r
    CROSS JOIN LATERAL aclexplode(COALESCE(c.relacl, acldefault('s', c.relowner))) acl
    WHERE n.nspname NOT IN ('pg_catalog', 'information_schema')
      AND n.nspname NOT LIKE 'pg_toast%'
      AND c.relkind = 'S'
      AND acl.grantee NOT IN (0, c.relowner, r.oid)

    UNION ALL
    SELECT 'CRITICAL', 'PUBLIC_SEQUENCE_PRIVILEGE', 'sequence',
           n.nspname || '.' || c.relname,
           'No sequence privileges for PUBLIC', 'PUBLIC has ' || acl.privilege_type,
           'Revoke the sequence privilege from PUBLIC.'
    FROM pg_class c
    JOIN pg_namespace n ON n.oid = c.relnamespace
    CROSS JOIN LATERAL aclexplode(
        COALESCE(c.relacl, acldefault('s', c.relowner))
    ) acl
    WHERE n.nspname NOT IN ('pg_catalog', 'information_schema')
      AND n.nspname NOT LIKE 'pg_toast%'
      AND c.relkind = 'S'
      AND acl.grantee = 0

    -- No runtime or PUBLIC grants may be introduced as default privileges.
    UNION ALL
    SELECT 'CRITICAL', 'DEFAULT_PRIVILEGE_DRIFT', 'default privileges',
           COALESCE(n.nspname, '<all schemas>') || ' / object type ' || d.defaclobjtype::text,
           'No default privileges for localoca_app or PUBLIC',
           CASE WHEN acl.grantee = 0 THEN 'PUBLIC: ' ELSE 'localoca_app: ' END || acl.privilege_type,
           'Revoke the unexpected ALTER DEFAULT PRIVILEGES grant and review the grantor role.'
    FROM pg_default_acl d
    JOIN app_role r ON true
    LEFT JOIN pg_namespace n ON n.oid = d.defaclnamespace
    CROSS JOIN LATERAL aclexplode(d.defaclacl) acl
    WHERE acl.grantee IN (0, r.oid)

    -- PUBLIC must not be able to execute functions in the application schema.
    UNION ALL
    SELECT 'HIGH', 'UNAPPROVED_DEFAULT_PRIVILEGE', 'default privileges',
           COALESCE(n.nspname, '<all schemas>') || ' / object type ' || d.defaclobjtype::text,
           'No unreviewed grants to third-party roles in default privileges',
           'Granted to ' || pg_get_userbyid(acl.grantee) || ': ' || acl.privilege_type,
           'Review this default privilege: it silently exposes future objects created by the grantor role.'
    FROM pg_default_acl d
    LEFT JOIN pg_namespace n ON n.oid = d.defaclnamespace
    CROSS JOIN LATERAL aclexplode(d.defaclacl) acl
    WHERE acl.grantee <> d.defaclrole
      AND acl.grantee <> 0
      AND acl.grantee <> COALESCE((SELECT oid FROM app_role), 0)

    -- PUBLIC must not be able to execute functions in the application schema.
    UNION ALL
    SELECT 'CRITICAL', 'PUBLIC_FUNCTION_PRIVILEGE', 'function',
           n.nspname || '.' || p.proname || '(' || pg_get_function_identity_arguments(p.oid) || ')',
           'No PUBLIC function privileges', 'PUBLIC has ' || acl.privilege_type,
           'Revoke PUBLIC EXECUTE and review whether the function should be exposed at all.'
    FROM pg_proc p
    JOIN pg_namespace n ON n.oid = p.pronamespace
    CROSS JOIN LATERAL aclexplode(
        COALESCE(p.proacl, acldefault('f', p.proowner))
    ) acl
    WHERE n.nspname NOT IN ('pg_catalog', 'information_schema')
      AND n.nspname NOT LIKE 'pg_toast%'
      AND acl.grantee = 0

    UNION ALL
    SELECT 'CRITICAL', 'FUNCTION_PRIVILEGE_EXTRA', 'function',
           n.nspname || '.' || p.proname || '(' || pg_get_function_identity_arguments(p.oid) || ')',
           'No function privileges for localoca_app', 'localoca_app has ' || acl.privilege_type,
           'Revoke the unexpected function privilege from localoca_app.'
    FROM pg_proc p
    JOIN pg_namespace n ON n.oid = p.pronamespace
    CROSS JOIN app_role r
    CROSS JOIN LATERAL aclexplode(
        COALESCE(p.proacl, acldefault('f', p.proowner))
    ) acl
    WHERE n.nspname NOT IN ('pg_catalog', 'information_schema')
      AND n.nspname NOT LIKE 'pg_toast%'
      AND acl.grantee = r.oid

    UNION ALL
    SELECT 'CRITICAL', 'FUNCTION_GRANT_OPTION', 'function',
           n.nspname || '.' || p.proname || '(' || pg_get_function_identity_arguments(p.oid) || ')',
           'No grant options', acl.privilege_type || ' WITH GRANT OPTION',
           'Revoke the function grant option.'
    FROM pg_proc p
    JOIN pg_namespace n ON n.oid = p.pronamespace
    CROSS JOIN app_role r
    CROSS JOIN LATERAL aclexplode(
        COALESCE(p.proacl, acldefault('f', p.proowner))
    ) acl
    WHERE n.nspname NOT IN ('pg_catalog', 'information_schema')
      AND n.nspname NOT LIKE 'pg_toast%'
      AND acl.grantee = r.oid
      AND acl.is_grantable
    UNION ALL
    SELECT 'HIGH', 'UNAPPROVED_ROLE_PRIVILEGE', 'function',
           n.nspname || '.' || p.proname || '(' || pg_get_function_identity_arguments(p.oid) || ')',
           'Direct function grants only for the function owner',
           'Granted to ' || pg_get_userbyid(acl.grantee) || ': ' || acl.privilege_type,
           'Review the role that can execute this function and its security context.'
    FROM pg_proc p
    JOIN pg_namespace n ON n.oid = p.pronamespace
    CROSS JOIN app_role r
    CROSS JOIN LATERAL aclexplode(COALESCE(p.proacl, acldefault('f', p.proowner))) acl
    WHERE n.nspname NOT IN ('pg_catalog', 'information_schema')
      AND n.nspname NOT LIKE 'pg_toast%'
      AND acl.grantee NOT IN (0, p.proowner, r.oid)

    UNION ALL
    SELECT 'CRITICAL', 'SECURITY_DEFINER_FUNCTION', 'function',
           n.nspname || '.' || p.proname || '(' || pg_get_function_identity_arguments(p.oid) || ')',
           'No unreviewed SECURITY DEFINER functions in application schemas',
           'SECURITY DEFINER; owner=' || pg_get_userbyid(p.proowner),
           'Review the function body, owner, search_path and EXECUTE grants. Keep only explicitly approved SECURITY DEFINER functions.'

    FROM pg_proc p
    JOIN pg_namespace n ON n.oid = p.pronamespace
    WHERE n.nspname NOT IN ('pg_catalog', 'information_schema')
      AND n.nspname NOT LIKE 'pg_toast%'
      AND p.prosecdef

    UNION ALL
    SELECT 'CRITICAL', 'LARGE_OBJECT_PRIVILEGE', 'large object', m.oid::text,
           'No large-object privileges for localoca_app or PUBLIC',
           CASE WHEN acl.grantee = 0 THEN 'PUBLIC: ' ELSE 'localoca_app: ' END || acl.privilege_type,
           'Revoke large-object privileges; LocaLoca stores image payloads in bytea columns and does not require PostgreSQL large objects.'
    FROM pg_largeobject_metadata m
    CROSS JOIN app_role r
    CROSS JOIN LATERAL aclexplode(
        COALESCE(m.lomacl, acldefault('L', m.lomowner))
    ) acl
    WHERE acl.grantee IN (0, r.oid)

    UNION ALL
    SELECT 'HIGH', 'UNAPPROVED_ROLE_PRIVILEGE', 'large object', m.oid::text,
           'Large-object grants only to the object owner',
           'Granted to ' || pg_get_userbyid(acl.grantee) || ': ' || acl.privilege_type,
           'Review the large-object ACL; application payloads are stored as bytea and should not need large objects.'
    FROM pg_largeobject_metadata m
    CROSS JOIN app_role r
    CROSS JOIN LATERAL aclexplode(COALESCE(m.lomacl, acldefault('L', m.lomowner))) acl
    WHERE acl.grantee NOT IN (0, m.lomowner, r.oid)

    UNION ALL
    SELECT 'CRITICAL', 'OBJECT_OWNERSHIP', 'sequence', n.nspname || '.' || c.relname,
           'Identity sequences owned by an administrative/deployment role',
           'Owned by localoca_app',
           'Transfer sequence ownership away from localoca_app.'
    FROM app_role r
    JOIN pg_class c ON c.relowner = r.oid
    JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE c.relkind = 'S'
      AND n.nspname NOT IN ('pg_catalog', 'information_schema')
      AND n.nspname NOT LIKE 'pg_toast%'

    UNION ALL
    SELECT 'CRITICAL', 'OBJECT_OWNERSHIP', 'large object', m.oid::text,
           'Large objects owned by an administrative/deployment role',
           'Owned by localoca_app',
           'Transfer large-object ownership away from localoca_app.'
    FROM app_role r
    JOIN pg_largeobject_metadata m ON m.lomowner = r.oid
)
SELECT
    severity,
    finding_code,
    object_type,
    object_name,
    expected,
    actual,
    recommendation
FROM findings
ORDER BY
    CASE severity
        WHEN 'CRITICAL' THEN 1
        WHEN 'HIGH' THEN 2
        WHEN 'MEDIUM' THEN 3
        ELSE 4
    END,
    finding_code,
    object_name;
