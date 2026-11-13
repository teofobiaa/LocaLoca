-- Speeds up the per-attraction visited EXISTS lookup used by map markers.
-- Multiple visits to the same attraction are valid, so this is intentionally non-unique.
CREATE INDEX IF NOT EXISTS ix_visits_user_attraction
    ON visits (user_id, attraction_id);
