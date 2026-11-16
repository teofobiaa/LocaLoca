-- Trigram indexes for substring catalog search on attraction, city and country names.
-- Apply as an administrator: pg_trgm is a database extension and indexes are schema objects.
BEGIN;

CREATE EXTENSION IF NOT EXISTS pg_trgm;

DO $$
DECLARE
    trigram_schema text;
BEGIN
    SELECT namespace.nspname
    INTO trigram_schema
    FROM pg_extension AS extension
    JOIN pg_namespace AS namespace ON namespace.oid = extension.extnamespace
    WHERE extension.extname = 'pg_trgm';

    IF trigram_schema IS NULL THEN
        RAISE EXCEPTION 'The pg_trgm extension is not installed.';
    END IF;

    EXECUTE format(
        'CREATE INDEX IF NOT EXISTS ix_attractions_name_trgm ON public.attractions USING gin (name %I.gin_trgm_ops)',
        trigram_schema);
    EXECUTE format(
        'CREATE INDEX IF NOT EXISTS ix_cities_name_trgm ON public.cities USING gin (name %I.gin_trgm_ops)',
        trigram_schema);
    EXECUTE format(
        'CREATE INDEX IF NOT EXISTS ix_countries_name_trgm ON public.countries USING gin (name %I.gin_trgm_ops)',
        trigram_schema);
END
$$;

INSERT INTO public.localoca_schema_migrations (version, name, source)
VALUES (14, 'catalog_search_trigram_indexes', 'migration')
ON CONFLICT (version) DO NOTHING;

COMMIT;
