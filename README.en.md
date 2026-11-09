# Loca Loca

**Loca Loca** is a Windows desktop application for keeping a personal travel journal. It lets users mark places on a map, save visit dates, ratings, notes, and photos, and maintain a list of places they want to visit.

[Русская версия](README.md) · **English**

## Features

- Account registration, sign-in, and personal account management.
- Searchable and filterable attractions catalog.
- **Visited** and **Want to Visit** lists.
- Visit dates, ratings, notes, and personal impressions.
- User-created places on the map.
- Photos attached to visits and personal places. Photos are stored in PostgreSQL and are available when signing in to the same account from another device.
- Travel map and statistics.
- Display-name changes and interface settings.
- A dedicated support page with links to project contacts, the repository, and releases.

## Installation

### Windows users

Loca Loca targets Windows 10 version 22H2 and Windows 11 (x64).

1. Open the [Releases](https://github.com/teofobiaa/LocaLoca/releases) page and download an installer if a current release is available.
2. Run `LocaLoca-Setup.exe` and complete the installation.
3. Launch the application and create an account, or sign in.

The installer configures the cloud API address and checks for the WebView2 Runtime. Users do not need to enter Supabase settings or a PostgreSQL password: the desktop client communicates with the server over HTTPS. If no installer is published in Releases, build one using the developer instructions below.

### Developer prerequisites

- Windows 10 version 22H2 or Windows 11 (x64).
- .NET SDK 10.0.100 or a later version in the supported update band.
- Visual Studio with the WinUI 3, Windows App SDK, and Windows SDK components.
- Microsoft Edge WebView2 Runtime.
- Internet access to restore NuGet packages.
- Access to Supabase PostgreSQL only when running the API against a real database.

Clone the repository:

```powershell
git clone https://github.com/teofobiaa/LocaLoca.git
cd LocaLoca
```

Install the client dependencies and configure the cloud API endpoint:

```powershell
.\tools\Install-CloudDependencies.ps1
```

Open `LocaLoca.sln`, wait for package restoration, select `Debug / x64 / Unpackaged`, and press F5 to run the application.

## Quick start

1. Create an account or sign in.
2. Find a place of interest in the attractions catalog.
3. Add it to **Visited** or **Want to Visit**.
4. For a visited place, add the visit date, rating, note, and photos.
5. Add a custom place to the map if it is missing from the catalog.
6. Open the map or statistics view to review your saved travel data.

Account data is synchronized through the shared cloud API and PostgreSQL database. To access it on another computer, sign in with the same account.

## Uninstalling

Uninstall Loca Loca through **Windows Settings → Apps → Installed apps** or by using the standard uninstaller.

The uninstaller removes the installed application and the local `%LOCALAPPDATA%\LocaLoca` directory, including local settings, temporary photo copies, and the map's WebView2 profile. The shared Microsoft Edge WebView2 Runtime is not removed.

Cloud accounts, visits, custom map places, and photos stored in Supabase PostgreSQL are retained. Removing the application from one computer does not delete the account's server-side data.

The `LOCA_API_BASE_URL` user environment variable is removed only if it still points to the official Loca Loca API. If the user has replaced it with a custom endpoint, the uninstaller preserves that value.

## Configuration

### API endpoint on the Windows client

The client uses the `LOCA_API_BASE_URL` user environment variable when it is set. The standard installer configures this variable automatically.

To set the endpoint manually:

```powershell
[Environment]::SetEnvironmentVariable(
    "LOCA_API_BASE_URL",
    "https://<render-service>.onrender.com",
    "User"
)
```

Restart the application after changing the variable.

To return to the local development mode, remove the variable:

```powershell
[Environment]::SetEnvironmentVariable("LOCA_API_BASE_URL", $null, "User")
```

When a cloud endpoint is configured, the client does not silently fall back to a local API if the remote server becomes unavailable.

### Server configuration

The hosted API reads secrets from protected Render environment variables:

- `LOCA_DB_CONNECTION` — Supabase PostgreSQL connection string using the Session Pooler and TLS.
- `LOCA_SESSION_SECRET` — session-signing secret of at least 32 characters.

Never put these values in source code, the desktop client, the installer, GitHub Issues, or commits.

During local development, the API can read configuration from `%LOCALAPPDATA%\LocaLoca\server-config.json`. The password is protected using DPAPI with the `CurrentUser` scope and is accessible only to the same Windows user account.

The database connection string is resolved in the following order:

1. `LOCA_DB_CONNECTION`.
2. `ConnectionStrings__LocaLoca`.
3. `ConnectionStrings:LocaLoca` from appsettings.
4. `%LOCALAPPDATA%\LocaLoca\server-config.json` for local development.

## Architecture and data storage

- **WinUI 3** — Windows desktop client.
- **ASP.NET Core API** — backend supporting Render cloud hosting and local development.
- **Supabase PostgreSQL** — shared application database. The API connects to it through Npgsql.
- **Photos** — binary data stored in `bytea` columns in the `photos` and `user_map_place_photos` tables. Photo storage does not depend on Render's temporary filesystem.

The application's `users` table manages Loca Loca accounts and is not Supabase Auth. SQL files are applied separately by a database administrator; the application does not run database migrations automatically.

## Project structure

| Directory / file | Purpose |
| --- | --- |
| `LocaLoca/` | WinUI desktop client |
| `LocaLoca/Assets/` | Application icon, mascot images, and UI resources |
| `Server/LocaLoca.Api/` | ASP.NET Core API and Dockerfile |
| `database/` | PostgreSQL schema, seed data, migrations, permissions, and diagnostic SQL |
| `Tests/` | Unit, API, and PostgreSQL integration tests |
| `installer/` | Inno Setup installer script |
| `tools/` | Dependency setup, build, and catalog export scripts |
| `site/` | Static project website for GitHub Pages |
| `render.yaml` | Render Blueprint |
| `LocaLoca.sln` | Main Windows/WinUI solution |
| `Tests/LocaLoca.Tests.sln` | Separate automated test solution |

## Development

### Build the application

Run the following command from the repository root in PowerShell:

```powershell
.\tools\Build-Windows.ps1
```

The output is published to `Builds\Windows`. Do not move `LocaLoca.exe` away from the `Server` directory: the client requires the API located alongside it.

### Build the installer

The build requires the .NET 10 SDK, Windows App SDK / Windows SDK components, and Inno Setup 6.3 or later.

```powershell
.\tools\Build-Installer.ps1
```

Output: `Builds\Installer\LocaLoca-Setup.exe`.

### Run tests

The test suite requires the .NET 10 SDK and Docker Desktop with the Linux container engine running.

```powershell
dotnet test Tests/LocaLoca.Tests.sln --configuration Release
```

For test layers, environment boundaries, and additional details, see [Tests/README.md](Tests/README.md), the [test plan](Tests/TEST_PLAN.md), and the [test case catalog](Tests/TEST_CASES.md).

### Contributing

1. Create a separate branch for each logically related change.
2. Do not commit secrets, real user data, local configuration files, or production database dumps.
3. Add or update executable tests for API and database changes. A documentation change alone does not mean that a scenario has been tested.
4. Run the relevant checks and report their results in the pull request description.
5. For WinUI or installer changes, also verify the Windows build and the relevant manual test scenarios.

## License

No `LICENSE` file is currently present in the repository root. Until a license is published, the terms for reusing, modifying, and distributing this code are not explicitly defined. A public repository should not be assumed to be open-source licensed by default.

## Contact and issue reporting

- Code-related suggestions and non-security bug reports: [GitHub Issues](https://github.com/teofobiaa/LocaLoca/issues).
- Source code and changes: [Loca Loca repository](https://github.com/teofobiaa/LocaLoca).
- Security vulnerabilities: follow the private reporting process in [SECURITY.md](SECURITY.md). Do not disclose vulnerability details in a public issue.
