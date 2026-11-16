-- Reconcile the reviewed LocaLoca runtime-role grants after schema migrations 014-019.
-- Run as the database administrator. Safe to rerun; this does not grant BYPASSRLS or role membership.
BEGIN;

DO $runtime_role$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'localoca_app') THEN
        RAISE EXCEPTION 'Required runtime role localoca_app does not exist.';
    END IF;
END
$runtime_role$;

ALTER DEFAULT PRIVILEGES FOR ROLE postgres IN SCHEMA public
    REVOKE ALL ON TABLES FROM PUBLIC, anon, authenticated, localoca_app;
ALTER DEFAULT PRIVILEGES FOR ROLE postgres IN SCHEMA public
    REVOKE ALL ON SEQUENCES FROM PUBLIC, anon, authenticated, localoca_app;
ALTER DEFAULT PRIVILEGES FOR ROLE postgres IN SCHEMA public
    REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC, anon, authenticated;

REVOKE ALL ON TABLE
    public.users, public.categories, public.countries, public.cities, public.attractions,
    public.visits, public.photos, public.wishlist, public.user_map_places,
    public.user_map_place_photos, public.user_sessions, public.auth_rate_limit_counters,
    public.user_avatars, public.birthday_contacts, public.user_profile_visibility,
    public.user_friendships, public.user_blocks, public.localoca_schema_migrations,
    public.v_visit_details
FROM PUBLIC, anon, authenticated, localoca_app;

REVOKE UPDATE (login, password_hash, display_name, created_at, residence_country, residence_city, date_of_birth)
    ON TABLE public.users FROM PUBLIC, anon, authenticated, localoca_app;

DO $revoke_public_sequences$
DECLARE
    sequence_row record;
BEGIN
    FOR sequence_row IN
        SELECT n.nspname AS schema_name, c.relname AS sequence_name
        FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = 'public' AND c.relkind = 'S'
    LOOP
        EXECUTE format(
            'REVOKE ALL ON SEQUENCE %I.%I FROM PUBLIC, anon, authenticated, localoca_app',
            sequence_row.schema_name, sequence_row.sequence_name);
    END LOOP;
END
$revoke_public_sequences$;

GRANT CONNECT ON DATABASE postgres TO localoca_app;
GRANT USAGE ON SCHEMA public TO localoca_app;
REVOKE CREATE ON SCHEMA public FROM localoca_app;

GRANT SELECT ON TABLE public.categories, public.countries, public.cities, public.attractions TO localoca_app;
GRANT SELECT, INSERT ON TABLE public.users TO localoca_app;
GRANT UPDATE (password_hash, display_name, residence_country, residence_city, date_of_birth)
    ON TABLE public.users TO localoca_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public.visits TO localoca_app;
GRANT SELECT, INSERT, DELETE ON TABLE public.photos, public.wishlist, public.user_map_place_photos TO localoca_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE
    public.user_map_places, public.user_sessions, public.auth_rate_limit_counters,
    public.user_avatars, public.birthday_contacts, public.user_profile_visibility,
    public.user_friendships, public.user_blocks
TO localoca_app;

GRANT USAGE ON SEQUENCE
    public.users_user_id_seq, public.visits_visit_id_seq, public.photos_photo_id_seq,
    public.user_map_places_user_map_place_id_seq, public.user_map_place_photos_photo_id_seq,
    public.birthday_contacts_birthday_contact_id_seq
TO localoca_app;

ALTER TABLE public.localoca_schema_migrations ENABLE ROW LEVEL SECURITY;

DO $rls_policies$
DECLARE
    relation_name text;
BEGIN
    FOREACH relation_name IN ARRAY ARRAY[
        'users','visits','photos','wishlist','user_map_places','user_map_place_photos',
        'user_sessions','auth_rate_limit_counters','user_avatars','birthday_contacts',
        'user_profile_visibility','user_friendships','user_blocks'
    ] LOOP
        EXECUTE format('ALTER TABLE public.%I ENABLE ROW LEVEL SECURITY', relation_name);
        EXECUTE format('DROP POLICY IF EXISTS localoca_app_runtime_access ON public.%I', relation_name);
        EXECUTE format(
            'CREATE POLICY localoca_app_runtime_access ON public.%I FOR ALL TO localoca_app USING (true) WITH CHECK (true)',
            relation_name);
    END LOOP;

    FOREACH relation_name IN ARRAY ARRAY['categories','countries','cities','attractions'] LOOP
        EXECUTE format('ALTER TABLE public.%I ENABLE ROW LEVEL SECURITY', relation_name);
        EXECUTE format('DROP POLICY IF EXISTS localoca_app_catalog_read ON public.%I', relation_name);
        EXECUTE format(
            'CREATE POLICY localoca_app_catalog_read ON public.%I FOR SELECT TO localoca_app USING (true)',
            relation_name);
    END LOOP;
END
$rls_policies$;

ALTER VIEW public.v_visit_details SET (security_invoker = true);
REVOKE ALL ON TABLE public.v_visit_details FROM PUBLIC, anon, authenticated, localoca_app;
REVOKE ALL ON FUNCTION public.create_default_user_profile_visibility() FROM PUBLIC, anon, authenticated;
REVOKE ALL ON FUNCTION public.rls_auto_enable() FROM PUBLIC, anon, authenticated;

SELECT
    (has_table_privilege('localoca_app', 'public.users', 'SELECT')
     AND has_table_privilege('localoca_app', 'public.users', 'INSERT')
     AND has_column_privilege('localoca_app', 'public.users', 'password_hash', 'UPDATE')) AS account_permissions_ok,
    has_table_privilege('localoca_app', 'public.birthday_contacts', 'SELECT') AS birthday_contacts_ok,
    has_table_privilege('localoca_app', 'public.user_profile_visibility', 'SELECT') AS profile_visibility_ok,
    has_table_privilege('localoca_app', 'public.user_friendships', 'SELECT') AS friendships_ok,
    has_table_privilege('localoca_app', 'public.user_blocks', 'SELECT') AS blocks_ok,
    (SELECT NOT (rolsuper OR rolcreatedb OR rolcreaterole OR rolbypassrls)
     FROM pg_roles WHERE rolname = 'localoca_app') AS role_is_restricted,
    NOT has_table_privilege('anon', 'public.users', 'SELECT') AS anon_is_blocked,
    NOT has_table_privilege('authenticated', 'public.users', 'SELECT') AS authenticated_is_blocked;

COMMIT;
