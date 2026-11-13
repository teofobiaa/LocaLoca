-- Persistent, per-account avatar storage with a server-generated thumbnail.
-- Apply after migration 012 on existing databases.
BEGIN;

CREATE TABLE IF NOT EXISTS public.user_avatars (
    user_id         integer PRIMARY KEY REFERENCES public.users (user_id) ON DELETE CASCADE,
    image_data      bytea NOT NULL,
    thumbnail_data  bytea NOT NULL,
    content_type    varchar(50) NOT NULL DEFAULT 'image/jpeg',
    updated_at      timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ck_user_avatars_image_size CHECK (octet_length(image_data) BETWEEN 1 AND 1048576),
    CONSTRAINT ck_user_avatars_thumbnail_size CHECK (octet_length(thumbnail_data) BETWEEN 1 AND 131072),
    CONSTRAINT ck_user_avatars_content_type CHECK (content_type = 'image/jpeg')
);

COMMENT ON TABLE public.user_avatars IS
    'One optimized JPEG avatar and a server-generated 96px thumbnail per user; upload quota is enforced by the API.';

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'localoca_app') THEN
        EXECUTE 'GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public.user_avatars TO localoca_app';
    END IF;
END $$;

INSERT INTO public.localoca_schema_migrations (version, name, source)
VALUES (13, 'user_avatars', 'migration')
ON CONFLICT (version) DO NOTHING;

COMMIT;
