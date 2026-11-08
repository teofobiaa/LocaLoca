# Render deployment: LocaLoca API

This repository includes a Docker build and a Render Blueprint. The Blueprint creates a free Docker-based Web Service, checks `/health`, and enables automatic deploys from the branch used to create the Blueprint.

## Deploy

1. Push `feature/render-cloud-server` to GitHub.
2. Sign in to Render and open [New + → Blueprint](https://dashboard.render.com/blueprints).
3. Connect GitHub if prompted, select `teofobiaa/LocaLoca`, and choose branch
   `feature/render-cloud-server` (the branch containing this `render.yaml`).
4. Review the Blueprint. It should show one Web Service named `localoca-api`,
   runtime Docker, Free plan, and health check `/health`. Apply it.
5. When Render prompts for `LOCA_DB_CONNECTION`, enter the Supabase PostgreSQL
   connection string. Use a dedicated least-privilege application role and require
   TLS. Never commit this value.
6. Set `LOCA_SESSION_SECRET` to a cryptographically random value of at least 32 characters. Generate one locally with PowerShell:
   ```powershell
   [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(48))
   ```
   Keep the value only in Render environment settings; do not commit it.
7. Save the environment variables and let Render deploy the service. Open the
   service's Events and Logs tabs and wait for a successful deploy.
8. Copy the service's `onrender.com` URL from its Overview page and check
   `https://<render-service>.onrender.com/health`. A healthy response must report
   both API configuration and database connectivity.

Render provides the `PORT` environment variable. The API binds to
`0.0.0.0:$PORT` in the container; local development retains
`http://127.0.0.1:5181`.

## Important: migration is not complete yet

Do not distribute a cloud-connected client or treat this service as production-ready
until all items below are implemented and tested:

- **Photo storage:** current API photo endpoints still write to the local filesystem.
  Render Free has an ephemeral filesystem. Move both visit photos and personal-map
  place photos to a private Supabase Storage bucket, including upload, download,
  and delete paths. Preserve per-user authorization. Existing database
  `file_path` values need a migration/compatibility strategy.
- **Sessions:** bearer tokens are now signed, expiring tokens (30-day lifetime).
  In Render, the signing key is read from `LOCA_SESSION_SECRET`; keep it stable
  across deploys and rotations. Local development generates an ephemeral key,
  so local sessions are intentionally invalidated when the API restarts.
- **Desktop client:** the client now accepts `LOCA_API_BASE_URL` as an HTTPS
  endpoint. When configured, it uses the remote API and does not start the local
  API as a fallback. Set it on a Windows client after Render assigns the service URL:
  ```powershell
  [Environment]::SetEnvironmentVariable("LOCA_API_BASE_URL", "https://<service>.onrender.com", "User")
  ```
  Restart the desktop application after changing the variable. Without it, local
  development continues to use `127.0.0.1:5181`.
- **Map authentication:** the desktop client now places the bearer token in the URL
  fragment. The map reads it, stores it in session storage, and removes it from the
  visible URL. Fragments are not sent in the HTTP request or ordinary server access logs.
- **Linux validation:** verify every startup path is Linux-compatible. The
  Windows DPAPI configuration is for local desktop installations only and must
  never be required in the container.

## Connect the Windows desktop client

After `/health` reports a healthy API and database, run this in PowerShell on the
Windows PC that will use LocaLoca (replace the URL with the actual Render URL):

```powershell
[Environment]::SetEnvironmentVariable("LOCA_API_BASE_URL", "https://<service>.onrender.com", "User")
```

Close and reopen LocaLoca. The client uses the cloud endpoint whenever this
variable is set; if the remote endpoint is unavailable, it does not silently start
a local API. Remove the variable to return to local development:

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
  localoca-api
```

Then request `http://localhost:10000/health`.

A successful image build alone does not prove database permissions, session signing,
photo durability, or desktop-client connectivity. This project intentionally keeps
photo storage on the API filesystem for this deployment iteration; Render Free does
not provide durable local storage, so uploaded photos can disappear after restarts.
