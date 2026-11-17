-- Index the normalized login expression for bounded case-insensitive prefix search.
-- Apply after schema baseline 020. No extension or additional runtime grants are required.
BEGIN;

CREATE INDEX IF NOT EXISTS ix_users_login_prefix
    ON public.users (lower(login) text_pattern_ops);

INSERT INTO public.localoca_schema_migrations (version, name, source)
VALUES (21, 'user_login_prefix_search', 'migration')
ON CONFLICT (version) DO NOTHING;

COMMIT;
