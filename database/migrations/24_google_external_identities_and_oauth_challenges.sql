-- Google identities are separate from local login credentials.
-- OAuth challenges are one-time, short-lived and stored server-side for replay protection.
BEGIN;

CREATE TABLE IF NOT EXISTS public.user_external_identities (
    external_identity_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    user_id integer NOT NULL REFERENCES public.users(user_id) ON DELETE CASCADE,
    provider varchar(32) NOT NULL,
    provider_subject varchar(255) NOT NULL,
    email varchar(320),
    email_verified boolean NOT NULL DEFAULT false,
    created_at timestamptz NOT NULL DEFAULT now(),
    last_used_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ck_user_external_identities_provider CHECK (provider IN ('google')),
    CONSTRAINT uq_user_external_identities_provider_subject UNIQUE (provider, provider_subject),
    CONSTRAINT uq_user_external_identities_user_provider UNIQUE (user_id, provider)
);

CREATE TABLE IF NOT EXISTS public.google_oauth_challenges (
    challenge_hash char(64) PRIMARY KEY,
    expires_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_google_oauth_challenges_expires_at
    ON public.google_oauth_challenges (expires_at);

DO $runtime_role$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'localoca_app') THEN
        CREATE ROLE localoca_app NOLOGIN;
    END IF;
END
$runtime_role$;

REVOKE ALL ON TABLE public.user_external_identities, public.google_oauth_challenges
    FROM PUBLIC, anon, authenticated;
GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE
    public.user_external_identities, public.google_oauth_challenges
    TO localoca_app;
GRANT USAGE ON SEQUENCE public.user_external_identities_external_identity_id_seq
    TO localoca_app;

ALTER TABLE public.user_external_identities ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS localoca_app_runtime_access ON public.user_external_identities;
CREATE POLICY localoca_app_runtime_access ON public.user_external_identities
    FOR ALL TO localoca_app USING (true) WITH CHECK (true);

ALTER TABLE public.google_oauth_challenges ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS localoca_app_runtime_access ON public.google_oauth_challenges;
CREATE POLICY localoca_app_runtime_access ON public.google_oauth_challenges
    FOR ALL TO localoca_app USING (true) WITH CHECK (true);

INSERT INTO public.localoca_schema_migrations(version, name, source)
VALUES (24, 'google_external_identities_and_oauth_challenges', 'migration')
ON CONFLICT (version) DO NOTHING;

COMMIT;
