# Security Policy

## Supported Versions

Loca Loca is currently maintained as a single active development line. Security fixes are applied to the latest code on the `main` branch and, when releases are published, to the latest stable release.

| Version | Supported |
| --- | --- |
| Latest stable release | :white_check_mark: |
| Latest `main` branch | :white_check_mark: |
| Older releases and development snapshots | :x: |

There is currently no promise of backported security fixes for older releases. Users should update to the latest stable release available.

## Reporting a Vulnerability

**Please do not report security vulnerabilities through public GitHub Issues or pull requests.** Public disclosure before a fix is available may put users at risk.

Use GitHub's **Private vulnerability reporting** feature on this repository, if it is enabled (Security → Advisories → Report a vulnerability). If private reporting is unavailable, contact the repository maintainer privately using the contact details on their GitHub profile. Do not post exploit details publicly while arranging contact.

When reporting, include as much of the following information as possible:

- A concise description of the vulnerability and its potential impact.
- The affected version, commit, component, and relevant configuration.
- Clear reproduction steps and, where practical, a minimal proof of concept.
- Any prerequisites or user interaction required to exploit the issue.
- A suggested mitigation, if known.

For Loca Loca, reports may concern the Windows desktop client, the per-device local ASP.NET Core API, Supabase PostgreSQL access, data or photo handling, installer scripts, and the project's GitHub Actions workflows. Please also indicate whether the issue requires local access or can be triggered remotely.

The Supabase PostgreSQL connection string is a secret. It must never be committed to the repository or embedded in the installer. Store it in the per-user `%LOCALAPPDATA%\\LocaLoca\\server-config.json` file or provide it through a protected environment variable. Rotate any database password that has appeared in a commit, even if the current file no longer contains it. The desktop build currently connects directly to PostgreSQL for this educational project; this is not a production-safe secret distribution model.

### Response and Disclosure

- The maintainer aims to acknowledge a report within **5 business days**.
- The maintainer will investigate the report, assess its impact, and coordinate a fix and disclosure timeline with the reporter.
- If the issue is confirmed, the fix will be released as soon as reasonably practical. The reporter will be kept informed of material progress.
- If the report is not considered a vulnerability or is outside the project's scope, the maintainer will explain that decision where possible.
- Please allow reasonable time for investigation and remediation before making vulnerability details public. Coordinated disclosure timing should be agreed between the reporter and maintainer.

Loca Loca does not currently advertise a bug bounty program.
