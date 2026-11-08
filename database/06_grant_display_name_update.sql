-- Allow the LocaLoca runtime role to change only the public display name.
-- Run once in Supabase SQL Editor as the database administrator.
GRANT UPDATE (display_name) ON TABLE public.users TO localoca_app;

-- Verify the column-level permission.
SELECT has_column_privilege(
    'localoca_app',
    'public.users',
    'display_name',
    'UPDATE'
) AS can_update_display_name;
