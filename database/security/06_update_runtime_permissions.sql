-- Upgrade an existing LocaLoca runtime role to the current application schema.
-- Run as the database administrator after migrations/03 and /04; safe before or after migration/08.
-- Safe to execute more than once; it does not change ownership or grant admin privileges.

GRANT UPDATE (display_name) ON TABLE public.users TO localoca_app;

GRANT SELECT, INSERT, UPDATE, DELETE
    ON TABLE public.user_map_places
    TO localoca_app;

GRANT SELECT, INSERT, DELETE
    ON TABLE public.user_map_place_photos
    TO localoca_app;

GRANT USAGE ON SEQUENCE
    public.user_map_places_user_map_place_id_seq,
    public.user_map_place_photos_photo_id_seq
    TO localoca_app;

-- Every result must be true.
SELECT
    has_column_privilege('localoca_app', 'public.users', 'display_name', 'UPDATE') AS can_update_display_name,
    has_table_privilege('localoca_app', 'public.user_map_places', 'SELECT') AS map_places_select,
    has_table_privilege('localoca_app', 'public.user_map_places', 'INSERT') AS map_places_insert,
    has_table_privilege('localoca_app', 'public.user_map_places', 'UPDATE') AS map_places_update,
    has_table_privilege('localoca_app', 'public.user_map_places', 'DELETE') AS map_places_delete,
    has_table_privilege('localoca_app', 'public.user_map_place_photos', 'SELECT') AS map_photos_select,
    has_table_privilege('localoca_app', 'public.user_map_place_photos', 'INSERT') AS map_photos_insert,
    has_table_privilege('localoca_app', 'public.user_map_place_photos', 'DELETE') AS map_photos_delete,
    has_sequence_privilege('localoca_app', 'public.user_map_places_user_map_place_id_seq', 'USAGE') AS map_places_sequence,
    has_sequence_privilege('localoca_app', 'public.user_map_place_photos_photo_id_seq', 'USAGE') AS map_photos_sequence;

-- This script is also run before migration 08 on older installations.
-- Grant session access only when the table has already been created.
DO $grant_session_permissions$
BEGIN
    IF to_regclass('public.user_sessions') IS NOT NULL THEN
        EXECUTE 'GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public.user_sessions TO localoca_app';
    END IF;
END;
$grant_session_permissions$;
