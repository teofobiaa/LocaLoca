-- Restrict the exposed application schema to the dedicated API database role.
-- The API uses its own authentication model and enforces per-user ownership in parameterized queries.
-- Do not grant BYPASSRLS: only localoca_app receives the policies below.
BEGIN;

DO $runtime_role$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='localoca_app') THEN
    RAISE EXCEPTION 'Required runtime role localoca_app is missing; create it before applying this migration.';
  END IF;
END
$runtime_role$;

-- Stop future public-table/sequence defaults from granting direct API access or broad runtime DML.
ALTER DEFAULT PRIVILEGES FOR ROLE postgres IN SCHEMA public
  REVOKE ALL ON TABLES FROM PUBLIC, anon, authenticated, localoca_app;
ALTER DEFAULT PRIVILEGES FOR ROLE postgres IN SCHEMA public
  REVOKE ALL ON SEQUENCES FROM PUBLIC, anon, authenticated, localoca_app;
ALTER DEFAULT PRIVILEGES FOR ROLE postgres IN SCHEMA public
  REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC, anon, authenticated;

-- Remove broad/default grants from the LocaLoca application surface.
REVOKE ALL ON TABLE
  public.users, public.categories, public.countries, public.cities, public.attractions,
  public.visits, public.photos, public.wishlist, public.user_map_places,
  public.user_map_place_photos, public.user_sessions, public.localoca_schema_migrations,
  public.auth_rate_limit_counters, public.user_avatars, public.birthday_contacts,
  public.user_profile_visibility, public.user_friendships, public.user_blocks,
  public.v_visit_details
FROM PUBLIC, anon, authenticated, localoca_app;

REVOKE ALL ON SEQUENCE
  public.users_user_id_seq, public.visits_visit_id_seq, public.photos_photo_id_seq,
  public.user_map_places_user_map_place_id_seq, public.user_map_place_photos_photo_id_seq,
  public.birthday_contacts_birthday_contact_id_seq
FROM PUBLIC, anon, authenticated, localoca_app;

-- Least-privilege table grants for the direct API role.
GRANT SELECT ON TABLE public.categories, public.countries, public.cities, public.attractions TO localoca_app;
GRANT SELECT, INSERT ON TABLE public.users TO localoca_app;
GRANT UPDATE (display_name, residence_country, residence_city, date_of_birth) ON TABLE public.users TO localoca_app;
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

-- Supabase enables RLS on public tables. The custom API authenticates users itself,
-- so only its dedicated DB login receives an application-wide policy; user scoping
-- remains enforced by parameterized owner checks in the API endpoints.
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
    EXECUTE format('CREATE POLICY localoca_app_runtime_access ON public.%I FOR ALL TO localoca_app USING (true) WITH CHECK (true)', relation_name);
  END LOOP;

  FOREACH relation_name IN ARRAY ARRAY['categories','countries','cities','attractions'] LOOP
    EXECUTE format('ALTER TABLE public.%I ENABLE ROW LEVEL SECURITY', relation_name);
    EXECUTE format('DROP POLICY IF EXISTS localoca_app_catalog_read ON public.%I', relation_name);
    EXECUTE format('CREATE POLICY localoca_app_catalog_read ON public.%I FOR SELECT TO localoca_app USING (true)', relation_name);
  END LOOP;
END
$rls_policies$;

-- The old view contains logins and visit history. Make its underlying table RLS apply
-- and keep it private to the database owner; the API uses owner-scoped endpoints instead.
ALTER VIEW public.v_visit_details SET (security_invoker = true);
REVOKE ALL ON TABLE public.v_visit_details FROM PUBLIC, anon, authenticated, localoca_app;

-- Event-trigger and row-trigger functions are not RPC entry points.
REVOKE ALL ON FUNCTION public.rls_auto_enable() FROM PUBLIC, anon, authenticated;
REVOKE ALL ON FUNCTION public.create_default_user_profile_visibility() FROM PUBLIC, anon, authenticated;

INSERT INTO public.localoca_schema_migrations(version,name,source)
VALUES(17,'runtime_role_rls_policies_and_grant_hardening','migration')
ON CONFLICT(version) DO NOTHING;
COMMIT;
