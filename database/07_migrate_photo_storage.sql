-- Store uploaded image bytes in PostgreSQL instead of the API container filesystem.
-- Run once in Supabase SQL Editor as the database administrator before deploying
-- the API version that writes photo data to these columns.
--
-- Existing rows remain valid with image_data/content_type = NULL. If their old
-- files still exist on the API host, the API can continue serving them as a
-- compatibility fallback. This migration cannot recover files already lost
-- from Render's ephemeral filesystem.

ALTER TABLE public.photos
    ADD COLUMN IF NOT EXISTS image_data bytea,
    ADD COLUMN IF NOT EXISTS content_type varchar(100);

ALTER TABLE public.user_map_place_photos
    ADD COLUMN IF NOT EXISTS image_data bytea,
    ADD COLUMN IF NOT EXISTS content_type varchar(100);

ALTER TABLE public.photos
    DROP CONSTRAINT IF EXISTS ck_photos_image_payload,
    ADD CONSTRAINT ck_photos_image_payload CHECK (
        (image_data IS NULL AND content_type IS NULL) OR
        (image_data IS NOT NULL
            AND octet_length(image_data) BETWEEN 1 AND 20971520
            AND content_type IS NOT NULL
            AND content_type IN ('image/jpeg', 'image/png', 'image/webp', 'image/bmp', 'image/gif'))
    );

ALTER TABLE public.user_map_place_photos
    DROP CONSTRAINT IF EXISTS ck_user_map_place_photos_image_payload,
    ADD CONSTRAINT ck_user_map_place_photos_image_payload CHECK (
        (image_data IS NULL AND content_type IS NULL) OR
        (image_data IS NOT NULL
            AND octet_length(image_data) BETWEEN 1 AND 20971520
            AND content_type IS NOT NULL
            AND content_type IN ('image/jpeg', 'image/png', 'image/webp', 'image/bmp', 'image/gif'))
    );

-- Re-apply the least-privilege table grants so the runtime role can access the new columns.
GRANT SELECT, INSERT, DELETE ON TABLE public.photos, public.user_map_place_photos TO localoca_app;

-- Every result must be true before deploying the new API.
SELECT
    has_column_privilege('localoca_app', 'public.photos', 'image_data', 'SELECT') AS visit_photo_read,
    has_column_privilege('localoca_app', 'public.photos', 'image_data', 'INSERT') AS visit_photo_insert,
    has_column_privilege('localoca_app', 'public.user_map_place_photos', 'image_data', 'SELECT') AS map_photo_read,
    has_column_privilege('localoca_app', 'public.user_map_place_photos', 'image_data', 'INSERT') AS map_photo_insert;
