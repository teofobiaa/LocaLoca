-- Shared fixed-window request counters for authentication endpoints.
-- Apply after migration 011 on existing databases.
BEGIN;

CREATE TABLE IF NOT EXISTS public.auth_rate_limit_counters (
    policy_key          varchar(32) NOT NULL,
    client_ip           inet         NOT NULL,
    window_started_at   timestamptz  NOT NULL,
    request_count       bigint       NOT NULL,
    expires_at          timestamptz  NOT NULL,
    CONSTRAINT pk_auth_rate_limit_counters PRIMARY KEY (policy_key, client_ip),
    CONSTRAINT ck_auth_rate_limit_counters_request_count CHECK (request_count > 0),
    CONSTRAINT ck_auth_rate_limit_counters_expiry_after_window CHECK (expires_at > window_started_at)
);

CREATE INDEX IF NOT EXISTS ix_auth_rate_limit_counters_expires_at
    ON public.auth_rate_limit_counters(expires_at);

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'localoca_app') THEN
        EXECUTE 'GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public.auth_rate_limit_counters TO localoca_app';
    END IF;
END $$;

INSERT INTO public.localoca_schema_migrations (version, name, source)
VALUES (12, 'distributed_auth_rate_limits', 'migration')
ON CONFLICT (version) DO NOTHING;

COMMIT;
