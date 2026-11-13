-- Store the user's self-reported country and city of residence separately from visit history.
-- Existing accounts remain NULL; residence is never inferred from visited attractions.
BEGIN;

ALTER TABLE public.users
    ADD COLUMN IF NOT EXISTS residence_country varchar(100),
    ADD COLUMN IF NOT EXISTS residence_city varchar(100);

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conrelid = 'public.users'::regclass
          AND conname = 'ck_users_residence_city_requires_country'
    ) THEN
        ALTER TABLE public.users
            ADD CONSTRAINT ck_users_residence_city_requires_country CHECK (
                residence_city IS NULL OR (
                    residence_country IS NOT NULL
                    AND btrim(residence_country) <> ''
                    AND btrim(residence_city) <> ''
                )
            );
    END IF;

    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'localoca_app') THEN
        EXECUTE 'GRANT UPDATE (residence_country, residence_city) ON TABLE public.users TO localoca_app';
    END IF;
END $$;

COMMENT ON COLUMN public.users.residence_country IS
    'Optional self-reported country of residence; independent from visit history.';
COMMENT ON COLUMN public.users.residence_city IS
    'Optional self-reported city of residence; independent from visit history.';

INSERT INTO public.localoca_schema_migrations (version, name, source)
VALUES (11, 'user_residence_location', 'migration')
ON CONFLICT (version) DO NOTHING;

COMMIT;
