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
