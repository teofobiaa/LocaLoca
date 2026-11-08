-- Loca Loca: схема базы данных (PostgreSQL 12+)
-- Перед запуском создайте базу отдельной командой:
--     CREATE DATABASE loca_loca ENCODING 'UTF8';
-- Затем подключитесь к loca_loca и выполните этот скрипт, после него — 02_seed.sql.

CREATE TABLE users (
    user_id        integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    login          varchar(50)  NOT NULL UNIQUE,
    password_hash  varchar(200) NOT NULL,
    display_name   varchar(100) NOT NULL,
    created_at     timestamptz  NOT NULL DEFAULT now()
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

CREATE INDEX ix_attractions_city      ON attractions (city_id);
CREATE INDEX ix_attractions_category  ON attractions (category_id);
CREATE INDEX ix_visits_user_date      ON visits (user_id, visit_date DESC);
CREATE INDEX ix_visits_attraction     ON visits (attraction_id);
CREATE INDEX ix_photos_visit          ON photos (visit_id);
CREATE INDEX ix_wishlist_attraction   ON wishlist (attraction_id);
CREATE INDEX ix_user_map_places_user  ON user_map_places (user_id, created_at DESC);
CREATE INDEX ix_user_map_place_photos ON user_map_place_photos (user_map_place_id);

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
