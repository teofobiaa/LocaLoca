-- Close the remaining privilege-audit gaps after the RLS rollout.
BEGIN;
CREATE INDEX IF NOT EXISTS ix_user_friendships_requested_by_status
  ON public.user_friendships(requested_by_user_id,status);
CREATE OR REPLACE FUNCTION public.create_default_user_profile_visibility()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog, public
AS $function$
BEGIN
  INSERT INTO public.user_profile_visibility(user_id)
  VALUES (NEW.user_id)
  ON CONFLICT(user_id) DO NOTHING;
  RETURN NEW;
END;
$function$;
REVOKE ALL ON FUNCTION public.create_default_user_profile_visibility() FROM PUBLIC, anon, authenticated;
REVOKE ALL ON TABLE public.localoca_schema_migrations FROM PUBLIC, anon, authenticated, localoca_app;
INSERT INTO public.localoca_schema_migrations(version,name,source)
VALUES(18,'harden_profile_trigger_and_migration_ledger','migration')
ON CONFLICT(version) DO NOTHING;
COMMIT;
