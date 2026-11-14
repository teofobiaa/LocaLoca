-- Read-only, administrator-run audience geography report.
-- Uses self-reported profile residence, never visit-history locations.
-- No login, user_id, precise address or coordinates are returned.
--
-- Privacy thresholds:
--   * country and city cohorts must each contain at least 5 profiles;
--   * a displayed city cohort must leave at least 5 profiles outside that
--     city in the same country, preventing a one-city total from revealing
--     the entire country cohort by subtraction;
--   * suppressed cohorts are not combined into an "other" row.
--
-- Run manually with a trusted administrative database role. Do not expose
-- this report through the public application API or grant it to localoca_app.

WITH normalized_profiles AS (
    SELECT
        user_id,
        lower(regexp_replace(btrim(residence_country), '\s+', ' ', 'g')) AS country_key,
        lower(regexp_replace(btrim(residence_city), '\s+', ' ', 'g')) AS city_key,
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
        city_totals.country,
        city_totals.city,
        city_totals.profile_count
    FROM city_totals
    JOIN country_totals USING (country_key)
    WHERE city_totals.profile_count >= 5
      AND country_totals.profile_count - city_totals.profile_count >= 5
)
SELECT geography_level, country, city, profile_count
FROM report_rows
ORDER BY country, geography_level DESC, city NULLS FIRST;
