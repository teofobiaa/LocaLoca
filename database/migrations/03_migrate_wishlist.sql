-- Безопасная миграция для ранее созданных баз Loca Loca.
-- Если wishlist уже существует, данные сохраняются без изменений.
CREATE TABLE IF NOT EXISTS wishlist (
    user_id       integer NOT NULL REFERENCES users (user_id) ON DELETE CASCADE,
    attraction_id integer NOT NULL REFERENCES attractions (attraction_id) ON DELETE CASCADE,
    added_at      timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (user_id, attraction_id)
);

CREATE INDEX IF NOT EXISTS ix_wishlist_attraction ON wishlist (attraction_id);

-- Record this migration after all schema changes above have succeeded.
INSERT INTO public.localoca_schema_migrations (version, name, source)
VALUES (3, 'migrate_wishlist', 'migration')
ON CONFLICT (version) DO NOTHING;
