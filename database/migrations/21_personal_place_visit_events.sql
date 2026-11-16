-- Preserve every visit to a personal map point as an independent event.
BEGIN;
CREATE TABLE IF NOT EXISTS public.personal_place_visits (
    personal_place_visit_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    user_id integer NOT NULL REFERENCES public.users(user_id) ON DELETE CASCADE,
    user_map_place_id integer REFERENCES public.user_map_places(user_map_place_id) ON DELETE SET NULL,
    place_key varchar(80) NOT NULL,
    place_name varchar(150) NOT NULL,
    latitude numeric(9,6) NOT NULL CHECK (latitude BETWEEN -90 AND 90),
    longitude numeric(9,6) NOT NULL CHECK (longitude BETWEEN -180 AND 180),
    visit_date date NOT NULL,
    rating smallint NOT NULL CHECK (rating BETWEEN 1 AND 5),
    category_name varchar(60) NOT NULL DEFAULT 'Личные точки',
    impressions text NOT NULL DEFAULT '',
    notes text NOT NULL DEFAULT '',
    created_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ck_personal_place_visits_place_key CHECK (place_key LIKE 'personal:%')
);
CREATE INDEX IF NOT EXISTS ix_personal_place_visits_user_date
    ON public.personal_place_visits(user_id, visit_date DESC, personal_place_visit_id DESC);
CREATE INDEX IF NOT EXISTS ix_personal_place_visits_user_place_date
    ON public.personal_place_visits(user_id, place_key, visit_date DESC);
COMMENT ON TABLE public.personal_place_visits IS
    'One row per visit to a personal map point. Snapshot fields and place_key preserve history if the source point is renamed or deleted.';
COMMENT ON COLUMN public.personal_place_visits.place_key IS
    'Stable per-user place identity used to count unique destinations across repeated events.';
COMMENT ON COLUMN public.personal_place_visits.category_name IS
    'Category snapshot for annual statistics; personal points use the explicit Personal Points group until a user-defined category exists.';

-- Backfill the single legacy visit stored on each already-visited map point.
INSERT INTO public.personal_place_visits (
    user_id, user_map_place_id, place_key, place_name, latitude, longitude,
    visit_date, rating, category_name, impressions, notes
)
SELECT point.user_id, point.user_map_place_id, 'personal:' || point.user_map_place_id::text,
       point.name, point.latitude, point.longitude, point.visit_date, point.rating,
       'Личные точки', point.impressions, point.notes
FROM public.user_map_places AS point
WHERE point.status = 'visited' AND point.visit_date IS NOT NULL AND point.rating IS NOT NULL
  AND NOT EXISTS (
      SELECT 1 FROM public.personal_place_visits AS event
      WHERE event.user_map_place_id = point.user_map_place_id
  );

CREATE OR REPLACE FUNCTION public.sync_personal_place_visit_snapshot()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog, public
AS $function$
DECLARE latest_event_id integer;
BEGIN
    IF NEW.status <> 'visited' THEN RETURN NEW; END IF;
    IF TG_OP = 'INSERT' THEN
        INSERT INTO public.personal_place_visits (
            user_id, user_map_place_id, place_key, place_name, latitude, longitude,
            visit_date, rating, category_name, impressions, notes
        ) VALUES (NEW.user_id, NEW.user_map_place_id, 'personal:' || NEW.user_map_place_id::text,
            NEW.name, NEW.latitude, NEW.longitude, NEW.visit_date, NEW.rating,
            'Личные точки', NEW.impressions, NEW.notes);
        RETURN NEW;
    END IF;
    IF OLD.status <> 'visited' THEN
        -- The repeat-visit API inserts its event before updating the legacy point fields.
        -- Do not let this trigger create a duplicate of that already-persisted event.
        IF EXISTS (
            SELECT 1
            FROM public.personal_place_visits AS event
            WHERE event.user_id = NEW.user_id
              AND event.user_map_place_id = NEW.user_map_place_id
              AND event.visit_date = NEW.visit_date
              AND event.rating = NEW.rating
              AND event.place_name = NEW.name
              AND event.impressions = NEW.impressions
              AND event.notes = NEW.notes
        ) THEN
            RETURN NEW;
        END IF;
        INSERT INTO public.personal_place_visits (
            user_id, user_map_place_id, place_key, place_name, latitude, longitude,
            visit_date, rating, category_name, impressions, notes
        ) VALUES (NEW.user_id, NEW.user_map_place_id, 'personal:' || NEW.user_map_place_id::text,
            NEW.name, NEW.latitude, NEW.longitude, NEW.visit_date, NEW.rating,
            'Личные точки', NEW.impressions, NEW.notes);
        RETURN NEW;
    END IF;
    SELECT event.personal_place_visit_id INTO latest_event_id
    FROM public.personal_place_visits AS event
    WHERE event.user_id = NEW.user_id AND event.user_map_place_id = NEW.user_map_place_id
    ORDER BY event.personal_place_visit_id DESC LIMIT 1;
    IF latest_event_id IS NULL THEN
        INSERT INTO public.personal_place_visits (
            user_id, user_map_place_id, place_key, place_name, latitude, longitude,
            visit_date, rating, category_name, impressions, notes
        ) VALUES (NEW.user_id, NEW.user_map_place_id, 'personal:' || NEW.user_map_place_id::text,
            NEW.name, NEW.latitude, NEW.longitude, NEW.visit_date, NEW.rating,
            'Личные точки', NEW.impressions, NEW.notes);
    ELSE
        -- Legacy point editing updates the latest visit; the repeat-visit API inserts a new row.
        UPDATE public.personal_place_visits
        SET place_name = NEW.name, latitude = NEW.latitude, longitude = NEW.longitude,
            visit_date = NEW.visit_date, rating = NEW.rating,
            impressions = NEW.impressions, notes = NEW.notes
        WHERE personal_place_visit_id = latest_event_id;
    END IF;
    RETURN NEW;
END;
$function$;

DROP TRIGGER IF EXISTS trg_user_map_places_sync_visit_snapshot ON public.user_map_places;
CREATE TRIGGER trg_user_map_places_sync_visit_snapshot
AFTER INSERT OR UPDATE OF name, latitude, longitude, status, visit_date, rating, impressions, notes
ON public.user_map_places
FOR EACH ROW EXECUTE FUNCTION public.sync_personal_place_visit_snapshot();

REVOKE ALL ON TABLE public.personal_place_visits FROM PUBLIC;
ALTER TABLE public.personal_place_visits ENABLE ROW LEVEL SECURITY;
REVOKE ALL ON FUNCTION public.sync_personal_place_visit_snapshot() FROM PUBLIC;
DO $permissions$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'anon') THEN
        EXECUTE 'REVOKE ALL ON TABLE public.personal_place_visits FROM anon';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'authenticated') THEN
        EXECUTE 'REVOKE ALL ON TABLE public.personal_place_visits FROM authenticated';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'localoca_app') THEN
        EXECUTE 'GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public.personal_place_visits TO localoca_app';
        EXECUTE 'GRANT USAGE ON SEQUENCE public.personal_place_visits_personal_place_visit_id_seq TO localoca_app';
        EXECUTE 'DROP POLICY IF EXISTS localoca_app_runtime_access ON public.personal_place_visits';
        EXECUTE 'CREATE POLICY localoca_app_runtime_access ON public.personal_place_visits FOR ALL TO localoca_app USING (true) WITH CHECK (true)';
    END IF;
END
$permissions$;

INSERT INTO public.localoca_schema_migrations(version, name, source)
VALUES (21, 'personal_place_visit_events', 'migration')
ON CONFLICT (version) DO NOTHING;
COMMIT;
