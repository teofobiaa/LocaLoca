-- Password hash upgrades use compare-and-swap in AuthenticationEndpoints.
-- The permission is column-scoped; the runtime role still cannot change unrelated account fields.
BEGIN;
GRANT UPDATE (password_hash) ON TABLE public.users TO localoca_app;
INSERT INTO public.localoca_schema_migrations(version,name,source)
VALUES(19,'restore_password_hash_update_permission','migration')
ON CONFLICT(version) DO NOTHING;
COMMIT;
