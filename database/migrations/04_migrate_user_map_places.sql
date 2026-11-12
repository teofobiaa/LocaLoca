-- Персональные точки, созданные пользователем на карте, и их фотографии.
CREATE TABLE IF NOT EXISTS user_map_places (
	user_map_place_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
	user_id           integer      NOT NULL REFERENCES users (user_id) ON DELETE CASCADE,
	name              varchar(150) NOT NULL,
	latitude          numeric(9,6) NOT NULL CHECK (latitude BETWEEN -90 AND 90),
	longitude         numeric(9,6) NOT NULL CHECK (longitude BETWEEN -180 AND 180),
	status            varchar(10)  NOT NULL DEFAULT 'planned' CHECK (status IN ('planned', 'visited')),
	visit_date        date,
	rating            smallint,
	impressions       text         NOT NULL DEFAULT '',
	notes             text         NOT NULL DEFAULT '',
	created_at        timestamptz  NOT NULL DEFAULT now(),
	CONSTRAINT ck_user_map_places_visit CHECK (
		(status = 'planned' AND visit_date IS NULL AND rating IS NULL) OR
		(status = 'visited' AND visit_date IS NOT NULL AND rating IS NOT NULL AND rating BETWEEN 1 AND 5)
	)
);

CREATE TABLE IF NOT EXISTS user_map_place_photos (
	photo_id          integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
	user_map_place_id integer      NOT NULL REFERENCES user_map_places (user_map_place_id) ON DELETE CASCADE,
	file_path         varchar(500) NOT NULL,
	caption           varchar(200) NOT NULL DEFAULT '',
	image_data        bytea,
	content_type      varchar(100),
	added_at          timestamptz  NOT NULL DEFAULT now(),
	CONSTRAINT ck_user_map_place_photos_image_payload CHECK (
		(image_data IS NULL AND content_type IS NULL) OR
		(image_data IS NOT NULL AND octet_length(image_data) BETWEEN 1 AND 20971520
			AND content_type IS NOT NULL AND content_type IN ('image/jpeg', 'image/png', 'image/webp', 'image/bmp', 'image/gif'))
	)
);

CREATE INDEX IF NOT EXISTS ix_user_map_places_user ON user_map_places (user_id, created_at DESC);
CREATE INDEX IF NOT EXISTS ix_user_map_place_photos ON user_map_place_photos (user_map_place_id);

-- Record this migration after all schema changes above have succeeded.
INSERT INTO public.localoca_schema_migrations (version, name, source)
VALUES (4, 'migrate_user_map_places', 'migration')
ON CONFLICT (version) DO NOTHING;
