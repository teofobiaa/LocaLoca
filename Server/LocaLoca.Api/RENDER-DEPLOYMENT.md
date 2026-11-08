# Render deployment: LocaLoca API

The Render Blueprint and Docker configuration are now merged into the repository's
`main` branch. The Blueprint creates a free Docker Web Service, checks `/health`,
and deploys new commits from the branch selected when the Blueprint is created.

## Deploy

1. Sign in to Render and open [New + → Blueprint](https://dashboard.render.com/blueprints).
2. Connect GitHub if prompted, select `teofobiaa/LocaLoca`, and choose branch
   `main` (the branch containing `render.yaml`).
3. Review the Blueprint. It should show one Web Service named `localoca-api`,
   runtime Docker, Free plan, and health check `/health`. Apply it.
4. When Render prompts for `LOCA_DB_CONNECTION`, enter the Supabase PostgreSQL
   connection string. Use the dedicated application role
   `localoca_app.<project-ref>`, Supabase Session Pooler, and TLS. Never commit
   this value.
5. Set `LOCA_SESSION_SECRET` to a cryptographically random value of at least
   32 characters. Generate one locally with PowerShell:

   ```powershell
   [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(48))
   ```

   Keep the value only in Render environment settings; do not commit it.
6. Save the environment variables and let Render deploy the service. Open the
   service's Events and Logs tabs and wait for a successful deploy.
7. Copy the service's `onrender.com` URL from its Overview page and check
   `https://<render-service>.onrender.com/health` for API availability, then
   `https://<render-service>.onrender.com/ready` to verify database connectivity
   and application-role permissions.

Render provides the `PORT` environment variable. The API binds to
`0.0.0.0:$PORT` in the container; local development retains
`http://127.0.0.1:5181`.

## Photos: PostgreSQL storage

Photo upload and retrieval use PostgreSQL `bytea` columns in
`public.photos` and `public.user_map_place_photos`. The API stores image bytes
and MIME type in the same transaction as the photo metadata. Render's ephemeral
filesystem is no longer used for new uploads.

For an existing database, run `database/07_migrate_photo_storage.sql` in the
Supabase SQL Editor as the database administrator **before deploying this API
version**. The API readiness check returns HTTP 503 until both photo tables have
the required columns and the runtime role has the required table/column grants.

Existing photo rows from the old filesystem-based implementation are retained.
If their old files still exist on the API host, the API can serve them as a
compatibility fallback. This migration cannot recover files already lost from
Render's ephemeral filesystem. New uploads are durable database records and are
included in PostgreSQL backups. Each upload operation accepts up to 10 images
and 20 MiB in total (JPG, PNG, WEBP, BMP or GIF).

## Sessions

Bearer tokens are signed and expire after 30 days. In Render, the signing key is
read from `LOCA_SESSION_SECRET`; keep it stable across deploys or existing
sessions will become invalid. Local development generates an ephemeral key, so
local sessions are invalidated when the API restarts.

## Connect the Windows desktop client

After `/health` reports API availability and `/ready` confirms the database, run this in PowerShell on
the Windows PC that will use LocaLoca (replace the URL with the actual Render URL):

```powershell
[Environment]::SetEnvironmentVariable("LOCA_API_BASE_URL", "https://<service>.onrender.com", "User")
```

Close and reopen LocaLoca. The client uses the cloud endpoint whenever this
variable is set; if the remote endpoint is unavailable, it does not silently
start a local API. Remove the variable to return to local development:

```powershell
[Environment]::SetEnvironmentVariable("LOCA_API_BASE_URL", $null, "User")
```

## Local Docker smoke test

From the repository root:

```sh
docker build -f Server/LocaLoca.Api/Dockerfile -t localoca-api .
docker run --rm -p 10000:10000 \
  -e PORT=10000 \
  -e LOCA_DB_CONNECTION='(set a real connection string locally; do not commit it)' \
  -e LOCA_SESSION_SECRET='(set a random secret of at least 32 characters)' \
  localoca-api
```

Then request `http://localhost:10000/health` for API liveness and
`http://localhost:10000/ready` for the database and permission check.

A successful image build does not prove database permissions, session signing,
photo durability, or desktop-client connectivity. The free service may sleep
after inactivity, making its first subsequent request slower. This is a
deployment suitable for a learning project, not a production service.
