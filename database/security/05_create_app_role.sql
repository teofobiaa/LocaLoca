-- LocaLoca runtime login with least-privilege access.
-- Run once in Supabase SQL Editor as the database administrator after creating schema baseline 012,
-- or after applying the required existing-database migrations through 012.
-- Replace the password placeholder with a unique, randomly generated password.
-- Do not commit a real password or reuse the postgres account password.

SET password_encryption = 'scram-sha-256';

CREATE ROLE localoca_app
    WITH LOGIN
    PASSWORD 'REPLACE_WITH_A_LONG_RANDOM_PASSWORD'
    NOSUPERUSER
    NOCREATEDB
    NOCREATEROLE
    NOINHERIT
    NOREPLICATION
    NOBYPASSRLS;

GRANT CONNECT ON DATABASE postgres TO localoca_app;
GRANT USAGE ON SCHEMA public TO localoca_app;

-- Read-only reference/catalog data.
GRANT SELECT ON TABLE
    public.categories,
    public.countries,
    public.cities,
    public.attractions
TO localoca_app;

-- Application account registration and login.
GRANT SELECT, INSERT ON TABLE public.users TO localoca_app;
GRANT UPDATE (display_name, residence_country, residence_city) ON TABLE public.users TO localoca_app;

-- User-owned records.
GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public.visits TO localoca_app;
GRANT SELECT, INSERT, DELETE ON TABLE public.photos TO localoca_app;
GRANT SELECT, INSERT, DELETE ON TABLE public.wishlist TO localoca_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public.user_map_places TO localoca_app;
GRANT SELECT, INSERT, DELETE ON TABLE public.user_map_place_photos TO localoca_app;

-- Sessions are part of the current baseline. Keep this grant conditional
-- for older databases where migration 008 has not yet been applied.
DO $grant_session_permissions$
BEGIN
 IF to_regclass('public.user_sessions') IS NOT NULL THEN
  EXECUTE 'GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public.user_sessions TO localoca_app';
 END IF;
END;
$grant_session_permissions$;

DO $grant_rate_limit_permissions$
BEGIN
    IF to_regclass('public.auth_rate_limit_counters') IS NOT NULL THEN
        EXECUTE 'GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public.auth_rate_limit_counters TO localoca_app';
    END IF;
END;
$grant_rate_limit_permissions$;

-- Only identity sequences used by INSERT ... RETURNING.
GRANT USAGE ON SEQUENCE
    public.users_user_id_seq,
    public.visits_visit_id_seq,
    public.photos_photo_id_seq,
    public.user_map_places_user_map_place_id_seq,
    public.user_map_place_photos_photo_id_seq
TO localoca_app;

-- Verify role flags and required grants. Every boolean should be true.
SELECT
    rolname,
    rolcanlogin,
    rolsuper,
    rolcreatedb,
    rolcreaterole,
    rolbypassrls
FROM pg_roles
WHERE rolname = 'localoca_app';

SELECT
    (has_table_privilege('localoca_app', 'public.users', 'SELECT') AND has_table_privilege('localoca_app', 'public.users', 'INSERT')) AS users_ok,
    has_table_privilege('localoca_app', 'public.categories', 'SELECT') AS categories_ok,
    has_table_privilege('localoca_app', 'public.countries', 'SELECT') AS countries_ok,
    has_table_privilege('localoca_app', 'public.cities', 'SELECT') AS cities_ok,
    has_table_privilege('localoca_app', 'public.attractions', 'SELECT') AS attractions_ok,
    (has_table_privilege('localoca_app', 'public.visits', 'SELECT') AND has_table_privilege('localoca_app', 'public.visits', 'INSERT') AND has_table_privilege('localoca_app', 'public.visits', 'UPDATE') AND has_table_privilege('localoca_app', 'public.visits', 'DELETE')) AS visits_ok,
    (has_table_privilege('localoca_app', 'public.photos', 'SELECT') AND has_table_privilege('localoca_app', 'public.photos', 'INSERT') AND has_table_privilege('localoca_app', 'public.photos', 'DELETE')) AS photos_ok,
    (has_table_privilege('localoca_app', 'public.wishlist', 'SELECT') AND has_table_privilege('localoca_app', 'public.wishlist', 'INSERT') AND has_table_privilege('localoca_app', 'public.wishlist', 'DELETE')) AS wishlist_ok,
    (has_table_privilege('localoca_app', 'public.user_map_places', 'SELECT') AND has_table_privilege('localoca_app', 'public.user_map_places', 'INSERT') AND has_table_privilege('localoca_app', 'public.user_map_places', 'UPDATE') AND has_table_privilege('localoca_app', 'public.user_map_places', 'DELETE')) AS user_map_places_ok,
    (has_table_privilege('localoca_app', 'public.user_map_place_photos', 'SELECT') AND has_table_privilege('localoca_app', 'public.user_map_place_photos', 'INSERT') AND has_table_privilege('localoca_app', 'public.user_map_place_photos', 'DELETE')) AS user_map_place_photos_ok,
    (has_table_privilege('localoca_app', 'public.auth_rate_limit_counters', 'SELECT') AND has_table_privilege('localoca_app', 'public.auth_rate_limit_counters', 'INSERT') AND has_table_privilege('localoca_app', 'public.auth_rate_limit_counters', 'UPDATE') AND has_table_privilege('localoca_app', 'public.auth_rate_limit_counters', 'DELETE')) AS auth_rate_limit_counters_ok;
