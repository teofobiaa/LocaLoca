-- Restrict runtime sequence privileges to sequences used by application INSERT operations.
-- Supabase had granted USAGE on catalog identity sequences which the API only reads.
BEGIN;

ALTER DEFAULT PRIVILEGES FOR ROLE postgres IN SCHEMA public
    REVOKE ALL ON SEQUENCES FROM PUBLIC, anon, authenticated, localoca_app;

DO $sequence_acl$
DECLARE
    sequence_row record;
BEGIN
    FOR sequence_row IN
        SELECT n.nspname AS schema_name, c.relname AS sequence_name
        FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = 'public'
          AND c.relkind = 'S'
    LOOP
        EXECUTE format(
            'REVOKE ALL ON SEQUENCE %I.%I FROM PUBLIC, anon, authenticated, localoca_app',
            sequence_row.schema_name,
            sequence_row.sequence_name
        );
    END LOOP;
END
$sequence_acl$;

GRANT USAGE ON SEQUENCE
    public.users_user_id_seq,
    public.visits_visit_id_seq,
    public.photos_photo_id_seq,
    public.user_map_places_user_map_place_id_seq,
    public.user_map_place_photos_photo_id_seq,
    public.birthday_contacts_birthday_contact_id_seq
TO localoca_app;

INSERT INTO public.localoca_schema_migrations (version, name, source)
VALUES (20, 'restrict_runtime_sequence_privileges', 'migration')
ON CONFLICT (version) DO NOTHING;

COMMIT;
