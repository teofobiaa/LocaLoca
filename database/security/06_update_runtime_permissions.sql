-- Upgrade an existing LocaLoca runtime role to the current application schema.
-- Run as the database administrator after migrations/03, /04, /11 and /12; safe to rerun.
-- Safe to execute more than once; it does not change ownership or grant admin privileges.

GRANT UPDATE (display_name, residence_country, residence_city) ON TABLE public.users TO localoca_app;

GRANT SELECT, INSERT, UPDATE, DELETE
    ON TABLE public.auth_rate_limit_counters
    TO localoca_app;

GRANT SELECT, INSERT, UPDATE, DELETE
    ON TABLE public.user_map_places
    TO localoca_app;

GRANT SELECT, INSERT, DELETE
    ON TABLE public.user_map_place_photos
    TO localoca_app;

DO $
BEGIN
    IF to_regclass('public.user_avatars') IS NOT NULL THEN
        EXECUTE 'GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public.user_avatars TO localoca_app';
    END IF;
END $;

GRANT USAGE ON SEQUENCE
    public.user_map_places_user_map_place_id_seq,
    public.user_map_place_photos_photo_id_seq
    TO localoca_app;

-- Every result must be true.
SELECT
    has_column_privilege('localoca_app', 'public.users', 'display_name', 'UPDATE') AS can_update_display_name,
    has_column_privilege('localoca_app', 'public.users', 'residence_country', 'UPDATE') AS can_update_residence_country,
    has_column_privilege('localoca_app', 'public.users', 'residence_city', 'UPDATE') AS can_update_residence_city,
    has_table_privilege('localoca_app', 'public.user_map_places', 'SELECT') AS map_places_select,
    has_table_privilege('localoca_app', 'public.user_map_places', 'INSERT') AS map_places_insert,
    has_table_privilege('localoca_app', 'public.user_map_places', 'UPDATE') AS map_places_update,
    has_table_privilege('localoca_app', 'public.user_map_places', 'DELETE') AS map_places_delete,
    has_table_privilege('localoca_app', 'public.user_map_place_photos', 'SELECT') AS map_photos_select,
    has_table_privilege('localoca_app', 'public.user_map_place_photos', 'INSERT') AS map_photos_insert,
    has_table_privilege('localoca_app', 'public.user_map_place_photos', 'DELETE') AS map_photos_delete,
    CASE WHEN to_regclass('public.user_avatars') IS NOT NULL THEN
        has_table_privilege('localoca_app', 'public.user_avatars', 'SELECT')
        AND has_table_privilege('localoca_app', 'public.user_avatars', 'INSERT')
        AND has_table_privilege('localoca_app', 'public.user_avatars', 'UPDATE')
        AND has_table_privilege('localoca_app', 'public.user_avatars', 'DELETE')
    ELSE false END AS user_avatars_permissions,
    has_sequence_privilege('localoca_app', 'public.user_map_places_user_map_place_id_seq', 'USAGE') AS map_places_sequence,
    has_sequence_privilege('localoca_app', 'public.user_map_place_photos_photo_id_seq', 'USAGE') AS map_photos_sequence,
    has_table_privilege('localoca_app', 'public.auth_rate_limit_counters', 'SELECT') AS auth_rate_limit_select,
    has_table_privilege('localoca_app', 'public.auth_rate_limit_counters', 'INSERT') AS auth_rate_limit_insert,
    has_table_privilege('localoca_app', 'public.auth_rate_limit_counters', 'UPDATE') AS auth_rate_limit_update,
    has_table_privilege('localoca_app', 'public.auth_rate_limit_counters', 'DELETE') AS auth_rate_limit_delete;

-- Residence fields and the shared limiter are introduced by migrations 011 and 012.
-- Run this script after migration 012. Session access remains conditional for legacy installations.
DO $grant_session_permissions$
BEGIN
    IF to_regclass('public.user_sessions') IS NOT NULL THEN
        EXECUTE 'GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public.user_sessions TO localoca_app';
    END IF;
END;
$grant_session_permissions$;
