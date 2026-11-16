-- Private user birth dates, owner-scoped birthday reminders, and optional age suitability metadata.
-- Apply after migration 013 on existing databases. The API never exposes a user's full DOB publicly.
BEGIN;

ALTER TABLE public.users
    ADD COLUMN IF NOT EXISTS date_of_birth date;

DO $user_constraints$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conrelid = 'public.users'::regclass
          AND conname = 'ck_users_date_of_birth_range'
    ) THEN
        ALTER TABLE public.users
            ADD CONSTRAINT ck_users_date_of_birth_range
            CHECK (date_of_birth IS NULL OR date_of_birth >= DATE '1900-01-01');
    END IF;
END
$user_constraints$;

ALTER TABLE public.attractions
    ADD COLUMN IF NOT EXISTS min_recommended_age smallint,
    ADD COLUMN IF NOT EXISTS max_recommended_age smallint;

DO $constraints$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conrelid = 'public.attractions'::regclass
          AND conname = 'ck_attractions_recommended_age_range'
    ) THEN
        ALTER TABLE public.attractions
            ADD CONSTRAINT ck_attractions_recommended_age_range CHECK (
                (min_recommended_age IS NULL OR min_recommended_age BETWEEN 0 AND 120)
                AND (max_recommended_age IS NULL OR max_recommended_age BETWEEN 0 AND 120)
                AND (min_recommended_age IS NULL OR max_recommended_age IS NULL
                     OR min_recommended_age <= max_recommended_age)
            );
    END IF;
END
$constraints$;

CREATE TABLE IF NOT EXISTS public.birthday_contacts (
    birthday_contact_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    user_id             integer NOT NULL REFERENCES public.users(user_id) ON DELETE CASCADE,
    display_name        varchar(80) NOT NULL,
    birth_date          date NOT NULL,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ck_birthday_contacts_display_name CHECK (
        btrim(display_name) <> '' AND length(display_name) <= 80
    ),
    CONSTRAINT ck_birthday_contacts_birth_date CHECK (birth_date >= DATE '1900-01-01')
);

CREATE INDEX IF NOT EXISTS ix_birthday_contacts_user_birth_date
    ON public.birthday_contacts (user_id, birth_date);

COMMENT ON COLUMN public.users.date_of_birth IS
    'Private self-reported date of birth. Return only through owner-authenticated profile endpoints; never expose in public profile, search, or authentication DTOs.';
COMMENT ON TABLE public.birthday_contacts IS
    'Private per-user address book for friends birthdays. Contacts are visible only to their owner.';
COMMENT ON COLUMN public.attractions.min_recommended_age IS
    'Optional lower bound for age-based recommendations; NULL means no lower age restriction.';
COMMENT ON COLUMN public.attractions.max_recommended_age IS
    'Optional upper bound for age-based recommendations; NULL means no upper age restriction.';

-- Birthday contacts are an application-private address book, not a public Supabase Data API resource.
DO $public_access$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'anon') THEN
        EXECUTE 'REVOKE ALL ON TABLE public.birthday_contacts FROM anon';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'authenticated') THEN
        EXECUTE 'REVOKE ALL ON TABLE public.birthday_contacts FROM authenticated';
    END IF;
    EXECUTE 'REVOKE ALL ON TABLE public.birthday_contacts FROM PUBLIC';
END
$public_access$;

DO $permissions$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'localoca_app') THEN
        EXECUTE 'GRANT UPDATE (date_of_birth) ON TABLE public.users TO localoca_app';
        EXECUTE 'GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public.birthday_contacts TO localoca_app';
        IF to_regclass('public.birthday_contacts_birthday_contact_id_seq') IS NOT NULL THEN
            EXECUTE 'GRANT USAGE ON SEQUENCE public.birthday_contacts_birthday_contact_id_seq TO localoca_app';
        END IF;
    END IF;
END
$permissions$;

INSERT INTO public.localoca_schema_migrations (version, name, source)
VALUES (14, 'user_birthdays_and_age_filtering', 'migration')
ON CONFLICT (version) DO NOTHING;

COMMIT;
