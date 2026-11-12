-- Supports bounded map-marker queries by the visible latitude/longitude rectangle.
-- The composite user index keeps personal markers scoped to the authenticated account.
CREATE INDEX IF NOT EXISTS ix_attractions_coordinates
    ON attractions (latitude, longitude);

CREATE INDEX IF NOT EXISTS ix_user_map_places_user_coordinates
    ON user_map_places (user_id, latitude, longitude);

-- Record this migration after all schema changes above have succeeded.
INSERT INTO public.localoca_schema_migrations (version, name, source)
VALUES (9, 'map_marker_viewport_indexes', 'migration')
ON CONFLICT (version) DO NOTHING;
