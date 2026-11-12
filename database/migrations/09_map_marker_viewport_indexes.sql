-- Supports bounded map-marker queries by the visible latitude/longitude rectangle.
-- The composite user index keeps personal markers scoped to the authenticated account.
CREATE INDEX IF NOT EXISTS ix_attractions_coordinates
    ON attractions (latitude, longitude);

CREATE INDEX IF NOT EXISTS ix_user_map_places_user_coordinates
    ON user_map_places (user_id, latitude, longitude);
