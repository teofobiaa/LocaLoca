-- Make the account birthday a field of the social profile and retire the manually maintained contacts list.
-- The full date remains owner-only; public profile views may expose month/day according to visibility.
BEGIN;

ALTER TABLE public.user_profile_visibility
    ADD COLUMN IF NOT EXISTS date_of_birth_visibility varchar(20) NOT NULL DEFAULT 'private';

DO $birthday_visibility$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conrelid = 'public.user_profile_visibility'::regclass
          AND conname = 'ck_user_profile_visibility_date_of_birth_visibility'
    ) THEN
        ALTER TABLE public.user_profile_visibility
            ADD CONSTRAINT ck_user_profile_visibility_date_of_birth_visibility
            CHECK (date_of_birth_visibility IN ('private', 'friends', 'registered'));
    END IF;
END
$birthday_visibility$;

DROP TABLE IF EXISTS public.birthday_contacts;

INSERT INTO public.localoca_schema_migrations (version, name, source)
VALUES (22, 'profile_birthday_visibility_and_remove_contacts', 'migration')
ON CONFLICT (version) DO NOTHING;

COMMIT;
