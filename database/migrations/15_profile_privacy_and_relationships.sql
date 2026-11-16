-- Profile visibility and the minimal social graph used by audience checks.
-- Apply after migration 014 on existing databases. Safe to rerun.

BEGIN;

CREATE TABLE IF NOT EXISTS public.user_profile_visibility (
    user_id integer PRIMARY KEY REFERENCES public.users(user_id) ON DELETE CASCADE,
    display_name_visibility varchar(20) NOT NULL DEFAULT 'registered'
        CHECK (display_name_visibility IN ('private', 'friends', 'registered')),
    avatar_visibility varchar(20) NOT NULL DEFAULT 'registered'
        CHECK (avatar_visibility IN ('private', 'friends', 'registered')),
    residence_country_visibility varchar(20) NOT NULL DEFAULT 'private'
        CHECK (residence_country_visibility IN ('private', 'friends', 'registered')),
    residence_city_visibility varchar(20) NOT NULL DEFAULT 'private'
        CHECK (residence_city_visibility IN ('private', 'friends', 'registered')),
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS public.user_friendships (
    user_low_id integer NOT NULL REFERENCES public.users(user_id) ON DELETE CASCADE,
    user_high_id integer NOT NULL REFERENCES public.users(user_id) ON DELETE CASCADE,
    requested_by_user_id integer NOT NULL REFERENCES public.users(user_id) ON DELETE CASCADE,
    status varchar(12) NOT NULL DEFAULT 'pending'
        CHECK (status IN ('pending', 'accepted')),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT pk_user_friendships PRIMARY KEY (user_low_id, user_high_id),
    CONSTRAINT ck_user_friendships_order CHECK (user_low_id < user_high_id),
    CONSTRAINT ck_user_friendships_requester CHECK (
        requested_by_user_id = user_low_id OR requested_by_user_id = user_high_id
    )
);

CREATE TABLE IF NOT EXISTS public.user_blocks (
    blocker_user_id integer NOT NULL REFERENCES public.users(user_id) ON DELETE CASCADE,
    blocked_user_id integer NOT NULL REFERENCES public.users(user_id) ON DELETE CASCADE,
    created_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT pk_user_blocks PRIMARY KEY (blocker_user_id, blocked_user_id),
    CONSTRAINT ck_user_blocks_not_self CHECK (blocker_user_id <> blocked_user_id)
);

CREATE INDEX IF NOT EXISTS ix_user_friendships_low_status
    ON public.user_friendships(user_low_id, status);
CREATE INDEX IF NOT EXISTS ix_user_friendships_high_status
    ON public.user_friendships(user_high_id, status);
CREATE INDEX IF NOT EXISTS ix_user_blocks_blocked
    ON public.user_blocks(blocked_user_id);

CREATE OR REPLACE FUNCTION public.create_default_user_profile_visibility()
RETURNS trigger
LANGUAGE plpgsql
AS $function$
BEGIN
    INSERT INTO public.user_profile_visibility(user_id)
    VALUES (NEW.user_id)
    ON CONFLICT (user_id) DO NOTHING;
    RETURN NEW;
END;
$function$;

DROP TRIGGER IF EXISTS trg_users_default_profile_visibility ON public.users;
CREATE TRIGGER trg_users_default_profile_visibility
AFTER INSERT ON public.users
FOR EACH ROW
EXECUTE FUNCTION public.create_default_user_profile_visibility();

INSERT INTO public.user_profile_visibility(user_id)
SELECT user_id FROM public.users
ON CONFLICT (user_id) DO NOTHING;

COMMENT ON TABLE public.user_profile_visibility IS
    'Per-field profile visibility. Block relationships override every field setting.';
COMMENT ON TABLE public.user_friendships IS
    'A pending request or a mutually accepted friendship. User IDs are stored in canonical order.';
COMMENT ON TABLE public.user_blocks IS
    'Unilateral blocks. Either direction prevents profile access and removes friendship.';

DO $grant_profile_privacy$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'localoca_app') THEN
        EXECUTE 'GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public.user_profile_visibility TO localoca_app';
        EXECUTE 'GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public.user_friendships TO localoca_app';
        EXECUTE 'GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public.user_blocks TO localoca_app';
    END IF;
END;
$grant_profile_privacy$;

INSERT INTO public.localoca_schema_migrations (version, name, source)
VALUES (15, 'profile_privacy_and_relationships', 'migration')
ON CONFLICT (version) DO NOTHING;

COMMIT;
