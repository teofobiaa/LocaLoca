-- Read-only aggregate age-band report.
-- If any non-empty band has fewer than 5 profiles, no age-band counts are returned.
-- Exact dates and account identifiers are never selected.
WITH ages AS (
    SELECT
        CASE
            WHEN u.date_of_birth IS NULL THEN 'Не указана'
            WHEN EXTRACT(YEAR FROM age(CURRENT_DATE, u.date_of_birth)) < 13 THEN '0–12'
            WHEN EXTRACT(YEAR FROM age(CURRENT_DATE, u.date_of_birth)) < 18 THEN '13–17'
            WHEN EXTRACT(YEAR FROM age(CURRENT_DATE, u.date_of_birth)) < 25 THEN '18–24'
            WHEN EXTRACT(YEAR FROM age(CURRENT_DATE, u.date_of_birth)) < 35 THEN '25–34'
            WHEN EXTRACT(YEAR FROM age(CURRENT_DATE, u.date_of_birth)) < 45 THEN '35–44'
            WHEN EXTRACT(YEAR FROM age(CURRENT_DATE, u.date_of_birth)) < 55 THEN '45–54'
            ELSE '55+'
        END AS age_group
    FROM public.users u
),
counts AS (
    SELECT age_group, count(*)::bigint AS profile_count
    FROM ages
    GROUP BY age_group
),
publication_guard AS (
    SELECT COALESCE(bool_and(profile_count >= 5), true) AS may_publish
    FROM counts
)
SELECT c.age_group, c.profile_count
FROM counts c
CROSS JOIN publication_guard g
WHERE g.may_publish
ORDER BY CASE c.age_group
    WHEN '0–12' THEN 1
    WHEN '13–17' THEN 2
    WHEN '18–24' THEN 3
    WHEN '25–34' THEN 4
    WHEN '35–44' THEN 5
    WHEN '45–54' THEN 6
    WHEN '55+' THEN 7
    ELSE 8
END;
