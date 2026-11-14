-- Read-only, administrator-run audience geography report.
-- Uses self-reported profile residence, never visit-history locations.
-- No login, user_id, precise address or coordinates are returned.
--
-- Privacy thresholds (k = 5):
--   * country totals are shown only when at least 5 profiles are present;
--   * a country's city breakdown is released only if EVERY city group has
--     at least 5 profiles and at least 5 profiles remain outside each city;
--   * the "no city supplied" complement must contain either zero profiles
--     or at least 5 profiles;
--   * if any city or residual group is smaller than 5, ALL city rows for
--     that country are suppressed (not just the small row).
--
-- This country-level all-or-nothing rule prevents subtraction of visible
-- city rows from a country total from revealing a suppressed city or
-- unassigned-residence group.
--
-- Run manually with a trusted administrative database role. Do not expose
-- this report through the public application API or grant it to localoca_app.

WITH normalized_profiles AS (
    SELECT
        lower(regexp_replace(btrim(residence_country), '\s+', ' ', 'g')) AS country_key,
        NULLIF(lower(regexp_replace(btrim(residence_city), '\s+', ' ', 'g')), '') AS city_key,
        btrim(residence_country) AS country_label,
        NULLIF(btrim(residence_city), '') AS city_label
    FROM public.users
    WHERE residence_country IS NOT NULL
      AND btrim(residence_country) <> ''
),
country_totals AS (
    SELECT
        country_key,
        min(country_label) AS country,
        count(*)::bigint AS profile_count
    FROM normalized_profiles
    GROUP BY country_key
),
city_totals AS (
    SELECT
        country_key,
        city_key,
        min(country_label) AS country,
        min(city_label) AS city,
        count(*)::bigint AS profile_count
    FROM normalized_profiles
    WHERE city_key IS NOT NULL
    GROUP BY country_key, city_key
),
no_city_totals AS (
    SELECT country_key, count(*)::bigint AS profile_count
    FROM normalized_profiles
    WHERE city_key IS NULL
    GROUP BY country_key
),
city_breakdown_safety AS (
    SELECT
        countries.country_key,
        count(cities.city_key)::bigint AS city_count,
        COALESCE(
            bool_and(
                cities.profile_count >= 5
                AND countries.profile_count - cities.profile_count >= 5
            ) FILTER (WHERE cities.city_key IS NOT NULL),
            false
        ) AS all_city_groups_safe,
        COALESCE(no_city.profile_count, 0) AS no_city_profile_count
    FROM country_totals countries
    LEFT JOIN city_totals cities USING (country_key)
    LEFT JOIN no_city_totals no_city USING (country_key)
    GROUP BY countries.country_key, countries.profile_count, no_city.profile_count
),
report_rows AS (
    SELECT
        'country'::text AS geography_level,
        country,
        NULL::text AS city,
        profile_count
    FROM country_totals
    WHERE profile_count >= 5

    UNION ALL

    SELECT
        'city'::text AS geography_level,
        cities.country,
        cities.city,
        cities.profile_count
    FROM city_totals cities
    JOIN city_breakdown_safety safety USING (country_key)
    WHERE safety.city_count >= 1
      AND safety.all_city_groups_safe
      AND (
          safety.no_city_profile_count = 0
          OR safety.no_city_profile_count >= 5
      )
)
SELECT geography_level, country, city, profile_count
FROM report_rows
ORDER BY country, geography_level DESC, city NULLS FIRST;
