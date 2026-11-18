-- Enforce one catalog entry per attraction name within a city.
-- Existing duplicates are reported and must be resolved explicitly; this migration never deletes catalog data.
BEGIN;

DO $attraction_uniqueness$
DECLARE
    duplicate_details text;
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conrelid = 'public.attractions'::regclass
          AND conname = 'uq_attractions_city_name'
    ) THEN
        SELECT string_agg(
                   format('%s / %s (%s записей)', ci.name, a.name, duplicate_count),
                   E'\\n' ORDER BY ci.name, a.name
               )
        INTO duplicate_details
        FROM (
            SELECT city_id, name, count(*) AS duplicate_count
            FROM public.attractions
            GROUP BY city_id, name
            HAVING count(*) > 1
        ) duplicates
        JOIN public.cities ci ON ci.city_id = duplicates.city_id;

        IF duplicate_details IS NOT NULL THEN
            RAISE EXCEPTION 'Невозможно включить уникальность достопримечательностей: в каталоге уже есть дубликаты.'
                USING DETAIL = duplicate_details,
                      HINT = 'Проверь перечисленные записи и вручную объедини или переименуй дубликаты, затем повтори миграцию.';
        END IF;

        ALTER TABLE public.attractions
            ADD CONSTRAINT uq_attractions_city_name UNIQUE (city_id, name);
    END IF;
END
$attraction_uniqueness$;

INSERT INTO public.localoca_schema_migrations (version, name, source)
VALUES (23, 'attraction_name_unique_per_city', 'migration')
ON CONFLICT (version) DO NOTHING;

COMMIT;
