# Render deployment: LocaLoca API

This repository includes a Docker build and a Render Blueprint. They are deployment
infrastructure, not a declaration that the cloud migration is complete.

## Deploy

1. Push this branch to GitHub.
2. In Render, create a new Blueprint and select this repository and branch.
3. Set the secret `LOCA_DB_CONNECTION` in the Render service environment. Use the
   Supabase PostgreSQL connection string for the dedicated least-privilege
   `localoca_app` role and require TLS. Never commit this value.
4. Deploy and inspect the service logs.
5. Check `https://<render-service>.onrender.com/health`.

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
- **Sessions:** `SessionStore` currently keeps bearer tokens in process memory.
  A Render restart or instance replacement invalidates all sessions. Persist
  hashed session tokens with expiry in PostgreSQL (or replace them with a properly
  signed, expiring token design) before relying on cloud sessions.
- **Desktop client:** `LocaLocaApiClient` still targets localhost and starts the
  packaged local API process. Add a configurable HTTPS API base URL and stop
  requiring the local server for cloud mode. Keep a developer-only local override.
- **Map authentication:** the map currently places the bearer token in the query
  string. Replace this with a short-lived one-time exchange or another mechanism
  that does not expose the bearer token in URLs, browser history, or logs.
- **Linux validation:** verify every startup path is Linux-compatible. The
  Windows DPAPI configuration is for local desktop installations only and must
  never be required in the container.

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

A successful image build alone does not prove database permissions, Storage access,
session persistence, photo durability, or desktop-client connectivity.
