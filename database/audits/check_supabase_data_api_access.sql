-- Read-only audit of Supabase Data API privileges and row-level security.
-- Run in Supabase SQL Editor as an administrator.
-- This script does not change grants, policies, table data or configuration.
--
-- The current Loca Loca desktop client talks to the Render API using Npgsql.
-- A Supabase publishable (formerly anon) key is not a database password, but
-- direct Data API access must still be limited by grants and RLS policies.
--
-- Review every row where grantee is anon, authenticated or PUBLIC.
-- "RLS disabled" together with SELECT/INSERT/UPDATE/DELETE grants requires
-- investigation before assuming that the data is private.

SELECT
    n.nspname AS schema_name,
    c.relname AS table_name,
    c.relrowsecurity AS rls_enabled,
    c.relforcerowsecurity AS rls_forced,
    COALESCE(p.grantee, '(no direct grant found)') AS grantee,
    COALESCE(p.privilege_type, '(none)') AS privilege_type
FROM pg_class AS c
JOIN pg_namespace AS n
    ON n.oid = c.relnamespace
LEFT JOIN information_schema.table_privileges AS p
    ON p.table_schema = n.nspname
   AND p.table_name = c.relname
   AND p.grantee IN ('anon', 'authenticated', 'PUBLIC')
WHERE n.nspname = 'public'
  AND c.relkind IN ('r', 'p', 'v', 'm')
ORDER BY
    c.relname,
    p.grantee NULLS FIRST,
    p.privilege_type NULLS FIRST;

-- Check whether default privileges will grant access to future tables.
-- Existing table grants are not automatically removed when defaults change.
SELECT
    owner_role.rolname AS owner_role,
    COALESCE(schema_name.nspname, '(all schemas)') AS schema_name,
    default_acl.defaclobjtype AS object_type,
    acl.grantee,
    acl.privilege_type,
    acl.is_grantable
FROM pg_default_acl AS default_acl
JOIN pg_roles AS owner_role
    ON owner_role.oid = default_acl.defaclrole
LEFT JOIN pg_namespace AS schema_name
    ON schema_name.oid = default_acl.defaclnamespace
CROSS JOIN LATERAL aclexplode(default_acl.defaclacl) AS acl
WHERE acl.grantee = 0
   OR acl.grantee IN (
       SELECT oid
       FROM pg_roles
       WHERE rolname IN ('anon', 'authenticated')
   )
ORDER BY owner_role.rolname, schema_name.nspname, object_type, acl.grantee;

-- Show row-level security policies. A policy is not required for a table
-- without grants to anon/authenticated, but every exposed table with RLS
-- enabled should have policies matching the intended user model.
SELECT
    schemaname,
    tablename,
    policyname,
    roles,
    cmd,
    qual AS using_expression,
    with_check AS check_expression
FROM pg_policies
WHERE schemaname = 'public'
ORDER BY tablename, policyname;
