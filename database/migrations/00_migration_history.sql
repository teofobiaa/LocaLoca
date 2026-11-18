-- Create and backfill the migration ledger for an existing database.
-- This script does not alter application tables or data and can be rerun safely.
CREATE TABLE IF NOT EXISTS public.localoca_schema_migrations (
 version integer PRIMARY KEY CHECK (version > 0),
 name varchar(120) NOT NULL,
 applied_at timestamptz NOT NULL DEFAULT now(),
 source varchar(16) NOT NULL DEFAULT 'migration' CHECK (source IN ('baseline','migration','adopted'))
);

INSERT INTO public.localoca_schema_migrations (version,name,source)
SELECT 1,'initial_schema','adopted'
WHERE to_regclass('public.users') IS NOT NULL AND to_regclass('public.categories') IS NOT NULL
 AND to_regclass('public.countries') IS NOT NULL AND to_regclass('public.cities') IS NOT NULL
 AND to_regclass('public.attractions') IS NOT NULL AND to_regclass('public.visits') IS NOT NULL
 AND to_regclass('public.photos') IS NOT NULL
ON CONFLICT (version) DO NOTHING;

INSERT INTO public.localoca_schema_migrations (version,name,source)
SELECT 3,'migrate_wishlist','adopted'
WHERE (SELECT count(DISTINCT column_name)=3 FROM information_schema.columns
 WHERE table_schema='public' AND table_name='wishlist' AND column_name IN ('user_id','attraction_id','added_at'))
 AND EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid=to_regclass('public.wishlist') AND contype='p')
 AND to_regclass('public.ix_wishlist_attraction') IS NOT NULL
ON CONFLICT (version) DO NOTHING;

INSERT INTO public.localoca_schema_migrations (version,name,source)
SELECT 4,'migrate_user_map_places','adopted'
WHERE (SELECT count(DISTINCT column_name)=11 FROM information_schema.columns
 WHERE table_schema='public' AND table_name='user_map_places'
 AND column_name IN ('user_map_place_id','user_id','name','latitude','longitude','status','visit_date','rating','impressions','notes','created_at'))
 AND (SELECT count(DISTINCT column_name)=7 FROM information_schema.columns
 WHERE table_schema='public' AND table_name='user_map_place_photos'
 AND column_name IN ('photo_id','user_map_place_id','file_path','caption','image_data','content_type','added_at'))
 AND EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid=to_regclass('public.user_map_places') AND conname='ck_user_map_places_visit')
 AND EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid=to_regclass('public.user_map_place_photos') AND conname='ck_user_map_place_photos_image_payload')
 AND to_regclass('public.ix_user_map_places_user') IS NOT NULL
 AND to_regclass('public.ix_user_map_place_photos') IS NOT NULL
ON CONFLICT (version) DO NOTHING;

INSERT INTO public.localoca_schema_migrations (version,name,source)
SELECT 7,'migrate_photo_storage','adopted'
WHERE (SELECT count(DISTINCT column_name)=2 FROM information_schema.columns
 WHERE table_schema='public' AND table_name='photos' AND column_name IN ('image_data','content_type'))
 AND (SELECT count(DISTINCT column_name)=2 FROM information_schema.columns
 WHERE table_schema='public' AND table_name='user_map_place_photos' AND column_name IN ('image_data','content_type'))
 AND EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid=to_regclass('public.photos') AND conname='ck_photos_image_payload')
 AND EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid=to_regclass('public.user_map_place_photos') AND conname='ck_user_map_place_photos_image_payload')
ON CONFLICT (version) DO NOTHING;

INSERT INTO public.localoca_schema_migrations (version,name,source)
SELECT 8,'server_sessions','adopted'
WHERE (SELECT count(DISTINCT column_name)=7 FROM information_schema.columns
 WHERE table_schema='public' AND table_name='user_sessions'
 AND column_name IN ('session_id','user_id','token_hash','created_at','expires_at','revoked_at','last_seen_at'))
 AND EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid=to_regclass('public.user_sessions') AND conname='user_sessions_expiry_after_creation')
 AND to_regclass('public.ix_user_sessions_user_active') IS NOT NULL
 AND to_regclass('public.ix_user_sessions_expiry') IS NOT NULL
ON CONFLICT (version) DO NOTHING;

INSERT INTO public.localoca_schema_migrations (version,name,source)
SELECT 9,'map_marker_viewport_indexes','adopted'
WHERE to_regclass('public.ix_attractions_coordinates') IS NOT NULL
 AND to_regclass('public.ix_user_map_places_user_coordinates') IS NOT NULL
ON CONFLICT (version) DO NOTHING;

INSERT INTO public.localoca_schema_migrations (version,name,source)
SELECT 10,'map_marker_visit_lookup_index','adopted'
WHERE to_regclass('public.ix_visits_user_attraction') IS NOT NULL
ON CONFLICT (version) DO NOTHING;

INSERT INTO public.localoca_schema_migrations (version,name,source)
SELECT 11,'user_residence_location','adopted'
WHERE (SELECT count(DISTINCT column_name)=2 FROM information_schema.columns
 WHERE table_schema='public' AND table_name='users'
 AND column_name IN ('residence_country','residence_city'))
 AND EXISTS (SELECT 1 FROM pg_constraint
             WHERE conrelid=to_regclass('public.users')
               AND conname='ck_users_residence_city_requires_country')
ON CONFLICT (version) DO NOTHING;


INSERT INTO public.localoca_schema_migrations (version,name,source)
SELECT 12,'distributed_auth_rate_limits','adopted'
WHERE (SELECT count(DISTINCT column_name)=5 FROM information_schema.columns
       WHERE table_schema='public' AND table_name='auth_rate_limit_counters'
         AND column_name IN ('policy_key','client_ip','window_started_at','request_count','expires_at'))
 AND EXISTS (SELECT 1 FROM pg_constraint
             WHERE conrelid=to_regclass('public.auth_rate_limit_counters')
               AND conname='pk_auth_rate_limit_counters')
 AND EXISTS (SELECT 1 FROM pg_constraint
             WHERE conrelid=to_regclass('public.auth_rate_limit_counters')
               AND conname='ck_auth_rate_limit_counters_expiry_after_window')
 AND to_regclass('public.ix_auth_rate_limit_counters_expires_at') IS NOT NULL
ON CONFLICT (version) DO NOTHING;

INSERT INTO public.localoca_schema_migrations (version,name,source)
SELECT 13,'user_avatars','adopted'
WHERE (SELECT count(DISTINCT column_name)=5 FROM information_schema.columns
       WHERE table_schema='public' AND table_name='user_avatars'
         AND column_name IN ('user_id','image_data','thumbnail_data','content_type','updated_at'))
 AND EXISTS (SELECT 1 FROM pg_constraint
             WHERE conrelid=to_regclass('public.user_avatars')
               AND conname='ck_user_avatars_image_size')
 AND EXISTS (SELECT 1 FROM pg_constraint
             WHERE conrelid=to_regclass('public.user_avatars')
               AND conname='ck_user_avatars_thumbnail_size')
ON CONFLICT (version) DO NOTHING;


INSERT INTO public.localoca_schema_migrations (version,name,source)
SELECT 14,'user_birthdays_and_age_filtering','adopted'
WHERE EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='users' AND column_name='date_of_birth' AND data_type='date')
 AND (SELECT count(DISTINCT column_name)=2 FROM information_schema.columns WHERE table_schema='public' AND table_name='attractions' AND column_name IN ('min_recommended_age','max_recommended_age'))
 AND (SELECT count(DISTINCT column_name)=6 FROM information_schema.columns WHERE table_schema='public' AND table_name='birthday_contacts' AND column_name IN ('birthday_contact_id','user_id','display_name','birth_date','created_at','updated_at'))
 AND EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid=to_regclass('public.users') AND conname='ck_users_date_of_birth_range')
 AND EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid=to_regclass('public.attractions') AND conname='ck_attractions_recommended_age_range')
 AND to_regclass('public.ix_birthday_contacts_user_birth_date') IS NOT NULL
ON CONFLICT (version) DO NOTHING;

DO $adopt_profile_privacy$
BEGIN
    -- Keep references to v015 tables inside the guarded branch. PostgreSQL resolves
    -- relation names before evaluating a top-level WHERE condition.
    IF to_regclass('public.user_profile_visibility') IS NOT NULL
       AND to_regclass('public.user_friendships') IS NOT NULL
       AND to_regclass('public.user_blocks') IS NOT NULL
       AND to_regprocedure('public.create_default_user_profile_visibility()') IS NOT NULL
       AND EXISTS (
           SELECT 1 FROM pg_trigger
           WHERE tgrelid = to_regclass('public.users')
             AND tgname = 'trg_users_default_profile_visibility'
             AND NOT tgisinternal
       )
       AND to_regclass('public.ix_user_friendships_low_status') IS NOT NULL
       AND to_regclass('public.ix_user_friendships_high_status') IS NOT NULL
       AND to_regclass('public.ix_user_blocks_blocked') IS NOT NULL
    THEN
        IF NOT EXISTS (
            SELECT 1 FROM public.users u
            LEFT JOIN public.user_profile_visibility v ON v.user_id = u.user_id
            WHERE v.user_id IS NULL
        ) THEN
            INSERT INTO public.localoca_schema_migrations(version, name, source)
            VALUES (15, 'profile_privacy_and_relationships', 'adopted')
            ON CONFLICT (version) DO NOTHING;
        END IF;
    END IF;
END
$adopt_profile_privacy$;

INSERT INTO public.localoca_schema_migrations (version,name,source)
SELECT 16,'catalog_search_trigram_indexes','adopted'
WHERE EXISTS (SELECT 1 FROM pg_extension WHERE extname='pg_trgm')
 AND to_regclass('public.ix_attractions_name_trgm') IS NOT NULL
 AND to_regclass('public.ix_cities_name_trgm') IS NOT NULL
 AND to_regclass('public.ix_countries_name_trgm') IS NOT NULL
ON CONFLICT (version) DO NOTHING;

INSERT INTO public.localoca_schema_migrations (version,name,source)
SELECT 17,'runtime_role_rls_policies_and_grant_hardening','adopted'
WHERE EXISTS (SELECT 1 FROM pg_roles WHERE rolname='localoca_app' AND NOT rolbypassrls)
 AND (SELECT count(*)=17 FROM pg_policies WHERE schemaname='public'
      AND policyname IN ('localoca_app_runtime_access','localoca_app_catalog_read')
      AND 'localoca_app'=ANY(roles))
 AND CASE WHEN to_regrole('anon') IS NOT NULL THEN NOT has_table_privilege('anon','public.users','SELECT') ELSE true END
 AND CASE WHEN to_regrole('authenticated') IS NOT NULL THEN NOT has_table_privilege('authenticated','public.users','SELECT') ELSE true END
 AND (SELECT COALESCE(reloptions,ARRAY[]::text[]) @> ARRAY['security_invoker=true']
      FROM pg_class WHERE oid=to_regclass('public.v_visit_details'))
ON CONFLICT (version) DO NOTHING;

INSERT INTO public.localoca_schema_migrations (version,name,source)
SELECT 18,'harden_profile_trigger_and_migration_ledger','adopted'
WHERE to_regclass('public.ix_user_friendships_requested_by_status') IS NOT NULL
 AND EXISTS (SELECT 1 FROM pg_proc WHERE oid=to_regprocedure('public.create_default_user_profile_visibility()')
             AND proconfig @> ARRAY['search_path=pg_catalog, public'])
 AND CASE WHEN to_regrole('localoca_app') IS NOT NULL THEN NOT has_table_privilege('localoca_app','public.localoca_schema_migrations','SELECT') ELSE false END
ON CONFLICT (version) DO NOTHING;

INSERT INTO public.localoca_schema_migrations (version,name,source)
SELECT 19,'restore_password_hash_update_permission','adopted'
WHERE CASE WHEN to_regrole('localoca_app') IS NOT NULL THEN has_column_privilege('localoca_app','public.users','password_hash','UPDATE') ELSE false END
ON CONFLICT (version) DO NOTHING;


DO $adopt_runtime_sequence_privileges$
DECLARE
    app_role_oid oid;
    anon_role_oid oid;
    authenticated_role_oid oid;
BEGIN
    SELECT oid INTO app_role_oid FROM pg_roles WHERE rolname = 'localoca_app';
    SELECT oid INTO anon_role_oid FROM pg_roles WHERE rolname = 'anon';
    SELECT oid INTO authenticated_role_oid FROM pg_roles WHERE rolname = 'authenticated';

    IF app_role_oid IS NOT NULL
       AND to_regclass('public.users_user_id_seq') IS NOT NULL
       AND to_regclass('public.visits_visit_id_seq') IS NOT NULL
       AND to_regclass('public.photos_photo_id_seq') IS NOT NULL
       AND to_regclass('public.user_map_places_user_map_place_id_seq') IS NOT NULL
       AND to_regclass('public.user_map_place_photos_photo_id_seq') IS NOT NULL
       AND to_regclass('public.birthday_contacts_birthday_contact_id_seq') IS NOT NULL
       AND has_sequence_privilege(app_role_oid, 'public.users_user_id_seq', 'USAGE')
       AND has_sequence_privilege(app_role_oid, 'public.visits_visit_id_seq', 'USAGE')
       AND has_sequence_privilege(app_role_oid, 'public.photos_photo_id_seq', 'USAGE')
       AND has_sequence_privilege(app_role_oid, 'public.user_map_places_user_map_place_id_seq', 'USAGE')
       AND has_sequence_privilege(app_role_oid, 'public.user_map_place_photos_photo_id_seq', 'USAGE')
       AND has_sequence_privilege(app_role_oid, 'public.birthday_contacts_birthday_contact_id_seq', 'USAGE')
       AND NOT EXISTS (
           SELECT 1
           FROM pg_class c
           JOIN pg_namespace n ON n.oid = c.relnamespace
           CROSS JOIN LATERAL aclexplode(COALESCE(c.relacl, acldefault('s', c.relowner))) acl
           WHERE n.nspname = 'public'
             AND c.relkind = 'S'
             AND (
                 acl.grantee = 0
                 OR acl.grantee = anon_role_oid
                 OR acl.grantee = authenticated_role_oid
                 OR (
                     acl.grantee = app_role_oid
                     AND NOT (
                         c.relname IN (
                             'users_user_id_seq',
                             'visits_visit_id_seq',
                             'photos_photo_id_seq',
                             'user_map_places_user_map_place_id_seq',
                             'user_map_place_photos_photo_id_seq',
                             'birthday_contacts_birthday_contact_id_seq'
                         )
                         AND acl.privilege_type = 'USAGE'
                         AND NOT acl.is_grantable
                     )
                 )
             )
       )
    THEN
        INSERT INTO public.localoca_schema_migrations(version, name, source)
        VALUES (20, 'restrict_runtime_sequence_privileges', 'adopted')
        ON CONFLICT (version) DO NOTHING;
    END IF;
END
$adopt_runtime_sequence_privileges$;

INSERT INTO public.localoca_schema_migrations (version,name,source)
SELECT 23,'attraction_name_unique_per_city','adopted'
WHERE EXISTS (
    SELECT 1
    FROM pg_constraint
    WHERE conrelid = to_regclass('public.attractions')
      AND conname = 'uq_attractions_city_name'
      AND contype = 'u'
)
ON CONFLICT (version) DO NOTHING;
