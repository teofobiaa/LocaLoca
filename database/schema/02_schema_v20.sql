-- Loca Loca: схема базы данных (PostgreSQL 12+)
-- Перед запуском создайте базу отдельной командой:
--     CREATE DATABASE loca_loca ENCODING 'UTF8';
-- Затем подключитесь к loca_loca и выполните этот скрипт, после него — 02_seed.sql.

CREATE TABLE users (
    user_id        integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    login          varchar(50)  NOT NULL UNIQUE,
    password_hash  varchar(200) NOT NULL,
    display_name   varchar(100) NOT NULL,
    residence_country varchar(100),
    residence_city    varchar(100),
    created_at     timestamptz  NOT NULL DEFAULT now(),
    CONSTRAINT ck_users_residence_city_requires_country CHECK (
        residence_city IS NULL OR (
            residence_country IS NOT NULL
            AND btrim(residence_country) <> ''
            AND btrim(residence_city) <> ''
        )
    )
);

-- One normalized avatar and one small thumbnail per account. Input uploads are
-- normalized by the API; database constraints enforce the storage quota.
CREATE TABLE user_avatars (
    user_id         integer PRIMARY KEY REFERENCES users (user_id) ON DELETE CASCADE,
    image_data      bytea NOT NULL,
    thumbnail_data  bytea NOT NULL,
    content_type    varchar(50) NOT NULL DEFAULT 'image/jpeg',
    updated_at      timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ck_user_avatars_image_size CHECK (octet_length(image_data) BETWEEN 1 AND 1048576),
    CONSTRAINT ck_user_avatars_thumbnail_size CHECK (octet_length(thumbnail_data) BETWEEN 1 AND 131072),
    CONSTRAINT ck_user_avatars_content_type CHECK (content_type = 'image/jpeg')
);

CREATE TABLE categories (
    category_id    integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name           varchar(60) NOT NULL UNIQUE
);

CREATE TABLE countries (
    country_id     integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name           varchar(100) NOT NULL UNIQUE
);

CREATE TABLE cities (
    city_id        integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    country_id     integer      NOT NULL REFERENCES countries (country_id),
    name           varchar(100) NOT NULL,
    CONSTRAINT uq_cities_country_name UNIQUE (country_id, name)
);

CREATE TABLE attractions (
    attraction_id  integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name           varchar(150) NOT NULL,
    description    text         NOT NULL,
    history        text,
    latitude       numeric(9,6) NOT NULL CHECK (latitude  BETWEEN  -90 AND  90),
    longitude      numeric(9,6) NOT NULL CHECK (longitude BETWEEN -180 AND 180),
    category_id    integer      NOT NULL REFERENCES categories (category_id),
    city_id        integer      NOT NULL REFERENCES cities (city_id)
);

-- Каждое посещение — отдельная запись, поэтому пара (пользователь, достопримечательность)
-- намеренно НЕ уникальна: одно и то же место можно посетить несколько раз.
CREATE TABLE visits (
    visit_id       integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    user_id        integer     NOT NULL REFERENCES users (user_id) ON DELETE CASCADE,
    attraction_id  integer     NOT NULL REFERENCES attractions (attraction_id),
    visit_date     date        NOT NULL,
    rating         smallint    NOT NULL CHECK (rating BETWEEN 1 AND 5),
    impressions    text        NOT NULL DEFAULT '',
    notes          text        NOT NULL DEFAULT '',
    created_at     timestamptz NOT NULL DEFAULT now()
);

-- Метаданные и бинарные данные фотографий хранятся вместе в PostgreSQL (bytea).
CREATE TABLE photos (
    photo_id       integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    visit_id       integer      NOT NULL REFERENCES visits (visit_id) ON DELETE CASCADE,
    file_path      varchar(500) NOT NULL,
    caption        varchar(200) NOT NULL DEFAULT '',
    image_data     bytea,
    content_type   varchar(100),
    added_at       timestamptz  NOT NULL DEFAULT now(),
    CONSTRAINT ck_photos_image_payload CHECK (
        (image_data IS NULL AND content_type IS NULL) OR
        (image_data IS NOT NULL AND octet_length(image_data) BETWEEN 1 AND 20971520
            AND content_type IS NOT NULL AND content_type IN ('image/jpeg', 'image/png', 'image/webp', 'image/bmp', 'image/gif'))
    )
);

-- Список мест, которые пользователь хочет посетить.
-- Составной первичный ключ не позволяет добавить одно место в список дважды.
CREATE TABLE wishlist (
    user_id        integer     NOT NULL REFERENCES users (user_id) ON DELETE CASCADE,
    attraction_id  integer     NOT NULL REFERENCES attractions (attraction_id) ON DELETE CASCADE,
    added_at       timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (user_id, attraction_id)
);

CREATE TABLE user_map_places (
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

CREATE TABLE user_map_place_photos (
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

-- Persistent server-side sessions (schema migration 008).
CREATE TABLE user_sessions (
 session_id uuid PRIMARY KEY,
 user_id integer NOT NULL REFERENCES users(user_id) ON DELETE CASCADE,
 token_hash varchar(64) NOT NULL UNIQUE,
 created_at timestamptz NOT NULL DEFAULT now(),
 expires_at timestamptz NOT NULL,
 revoked_at timestamptz NULL,
 last_seen_at timestamptz NOT NULL DEFAULT now(),
 CONSTRAINT user_sessions_expiry_after_creation CHECK (expires_at > created_at)
);
CREATE INDEX ix_user_sessions_user_active ON user_sessions(user_id, created_at DESC) WHERE revoked_at IS NULL;
CREATE INDEX ix_user_sessions_expiry ON user_sessions(expires_at);

-- Shared, atomic fixed-window counters for login, registration and logout attempts.
CREATE TABLE auth_rate_limit_counters (
    policy_key          varchar(32) NOT NULL,
    client_ip           inet         NOT NULL,
    window_started_at   timestamptz  NOT NULL,
    request_count       bigint       NOT NULL,
    expires_at          timestamptz  NOT NULL,
    CONSTRAINT pk_auth_rate_limit_counters PRIMARY KEY (policy_key, client_ip),
    CONSTRAINT ck_auth_rate_limit_counters_request_count CHECK (request_count > 0),
    CONSTRAINT ck_auth_rate_limit_counters_expiry_after_window CHECK (expires_at > window_started_at)
);
CREATE INDEX ix_auth_rate_limit_counters_expires_at
    ON auth_rate_limit_counters(expires_at);

CREATE INDEX ix_attractions_city      ON attractions (city_id);
CREATE INDEX ix_attractions_category  ON attractions (category_id);
CREATE INDEX ix_attractions_coordinates ON attractions (latitude, longitude);
CREATE INDEX ix_visits_user_date      ON visits (user_id, visit_date DESC);
CREATE INDEX ix_visits_attraction     ON visits (attraction_id);
CREATE INDEX ix_visits_user_attraction ON visits (user_id, attraction_id);
CREATE INDEX ix_photos_visit          ON photos (visit_id);
CREATE INDEX ix_wishlist_attraction   ON wishlist (attraction_id);
CREATE INDEX ix_user_map_places_user  ON user_map_places (user_id, created_at DESC);
CREATE INDEX ix_user_map_places_user_coordinates ON user_map_places (user_id, latitude, longitude);
CREATE INDEX ix_user_map_place_photos ON user_map_place_photos (user_map_place_id);

-- Current baseline includes schema migrations 003, 004, 007, 008, 009, 010, 011, 012 and 013.
CREATE TABLE localoca_schema_migrations (
 version integer PRIMARY KEY CHECK (version > 0),
 name varchar(120) NOT NULL,
 applied_at timestamptz NOT NULL DEFAULT now(),
 source varchar(16) NOT NULL CHECK (source IN ('baseline', 'migration', 'adopted'))
);
INSERT INTO localoca_schema_migrations (version, name, source) VALUES
 (1, 'initial_schema', 'baseline'),
 (3, 'migrate_wishlist', 'baseline'),
 (4, 'migrate_user_map_places', 'baseline'),
 (7, 'migrate_photo_storage', 'baseline'),
 (8, 'server_sessions', 'baseline'),
 (9, 'map_marker_viewport_indexes', 'baseline'),
 (10, 'map_marker_visit_lookup_index', 'baseline'),
 (11, 'user_residence_location', 'baseline'),
 (12, 'distributed_auth_rate_limits', 'baseline'),
 (13, 'user_avatars', 'baseline');

-- Представление для отчётов и ручной проверки данных.
CREATE VIEW v_visit_details AS
SELECT v.visit_id,
       u.login,
       a.name        AS attraction,
       ci.name       AS city,
       co.name       AS country,
       v.visit_date,
       v.rating,
       (SELECT count(*) FROM photos p WHERE p.visit_id = v.visit_id) AS photo_count
FROM visits v
JOIN users u         ON u.user_id        = v.user_id
JOIN attractions a   ON a.attraction_id  = v.attraction_id
JOIN cities ci       ON ci.city_id       = a.city_id
JOIN countries co    ON co.country_id    = ci.country_id;

-- Schema additions through version 020. Runtime grants and RLS are finalized
-- after role creation by security/05_create_app_role.sql and
-- security/06_update_runtime_permissions.sql.

ALTER TABLE public.users
    ADD COLUMN date_of_birth date,
    ADD CONSTRAINT ck_users_date_of_birth_range
        CHECK (date_of_birth IS NULL OR date_of_birth >= DATE '1900-01-01');

ALTER TABLE public.attractions
    ADD COLUMN min_recommended_age smallint,
    ADD COLUMN max_recommended_age smallint,
    ADD CONSTRAINT ck_attractions_recommended_age_range CHECK (
        (min_recommended_age IS NULL OR min_recommended_age BETWEEN 0 AND 120)
        AND (max_recommended_age IS NULL OR max_recommended_age BETWEEN 0 AND 120)
        AND (min_recommended_age IS NULL OR max_recommended_age IS NULL
             OR min_recommended_age <= max_recommended_age)
    );

CREATE TABLE birthday_contacts (
    birthday_contact_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    user_id integer NOT NULL REFERENCES users(user_id) ON DELETE CASCADE,
    display_name varchar(80) NOT NULL,
    birth_date date NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ck_birthday_contacts_display_name CHECK (
        btrim(display_name) <> '' AND length(display_name) <= 80
    ),
    CONSTRAINT ck_birthday_contacts_birth_date CHECK (birth_date >= DATE '1900-01-01')
);
CREATE INDEX ix_birthday_contacts_user_birth_date ON birthday_contacts(user_id, birth_date);

COMMENT ON COLUMN users.date_of_birth IS
    'Private self-reported date of birth. Return only through owner-authenticated profile endpoints; never expose in public profile, search, or authentication DTOs.';
COMMENT ON TABLE birthday_contacts IS
    'Private per-user address book for friends birthdays. Contacts are visible only to their owner.';
COMMENT ON COLUMN attractions.min_recommended_age IS
    'Optional lower bound for age-based recommendations; NULL means no lower age restriction.';
COMMENT ON COLUMN attractions.max_recommended_age IS
    'Optional upper bound for age-based recommendations; NULL means no upper age restriction.';

CREATE TABLE user_profile_visibility (
    user_id integer PRIMARY KEY REFERENCES users(user_id) ON DELETE CASCADE,
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

CREATE TABLE user_friendships (
    user_low_id integer NOT NULL REFERENCES users(user_id) ON DELETE CASCADE,
    user_high_id integer NOT NULL REFERENCES users(user_id) ON DELETE CASCADE,
    requested_by_user_id integer NOT NULL REFERENCES users(user_id) ON DELETE CASCADE,
    status varchar(12) NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'accepted')),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT pk_user_friendships PRIMARY KEY (user_low_id, user_high_id),
    CONSTRAINT ck_user_friendships_order CHECK (user_low_id < user_high_id),
    CONSTRAINT ck_user_friendships_requester CHECK (
        requested_by_user_id = user_low_id OR requested_by_user_id = user_high_id
    )
);

CREATE TABLE user_blocks (
    blocker_user_id integer NOT NULL REFERENCES users(user_id) ON DELETE CASCADE,
    blocked_user_id integer NOT NULL REFERENCES users(user_id) ON DELETE CASCADE,
    created_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT pk_user_blocks PRIMARY KEY (blocker_user_id, blocked_user_id),
    CONSTRAINT ck_user_blocks_not_self CHECK (blocker_user_id <> blocked_user_id)
);

CREATE INDEX ix_user_friendships_low_status ON user_friendships(user_low_id, status);
CREATE INDEX ix_user_friendships_high_status ON user_friendships(user_high_id, status);
CREATE INDEX ix_user_friendships_requested_by_status ON user_friendships(requested_by_user_id, status);
CREATE INDEX ix_user_blocks_blocked ON user_blocks(blocked_user_id);

CREATE OR REPLACE FUNCTION public.create_default_user_profile_visibility()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog, public
AS $function$
BEGIN
    INSERT INTO public.user_profile_visibility(user_id)
    VALUES (NEW.user_id)
    ON CONFLICT (user_id) DO NOTHING;
    RETURN NEW;
END;
$function$;

CREATE TRIGGER trg_users_default_profile_visibility
AFTER INSERT ON public.users
FOR EACH ROW
EXECUTE FUNCTION public.create_default_user_profile_visibility();

INSERT INTO public.user_profile_visibility(user_id)
SELECT user_id FROM public.users
ON CONFLICT (user_id) DO NOTHING;

COMMENT ON TABLE user_profile_visibility IS
    'Per-field profile visibility. Block relationships override every field setting.';
COMMENT ON TABLE user_friendships IS
    'A pending request or a mutually accepted friendship. User IDs are stored in canonical order.';
COMMENT ON TABLE user_blocks IS
    'Unilateral blocks. Either direction prevents profile access and removes friendship.';

CREATE SCHEMA IF NOT EXISTS extensions;
CREATE EXTENSION IF NOT EXISTS pg_trgm WITH SCHEMA extensions;
CREATE INDEX ix_attractions_name_trgm ON public.attractions USING gin (name extensions.gin_trgm_ops);
CREATE INDEX ix_cities_name_trgm ON public.cities USING gin (name extensions.gin_trgm_ops);
CREATE INDEX ix_countries_name_trgm ON public.countries USING gin (name extensions.gin_trgm_ops);

ALTER VIEW public.v_visit_details SET (security_invoker = true);

-- The fresh-install snapshot contains the resulting schema through v020.
-- Runtime permissions/RLS are finalized by security/06 after role creation.
INSERT INTO public.localoca_schema_migrations (version, name, source) VALUES
 (14, 'user_birthdays_and_age_filtering', 'baseline'),
 (15, 'profile_privacy_and_relationships', 'baseline'),
 (16, 'catalog_search_trigram_indexes', 'baseline'),
 (17, 'runtime_role_rls_policies_and_grant_hardening', 'baseline'),
 (18, 'harden_profile_trigger_and_migration_ledger', 'baseline'),
 (19, 'restore_password_hash_update_permission', 'baseline'),
 (20, 'restrict_runtime_sequence_privileges', 'baseline');
