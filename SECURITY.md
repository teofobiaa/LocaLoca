# Security Policy

The Loca Loca project takes reports of security vulnerabilities seriously. This document explains which versions are supported, how to report a vulnerability privately, what to include in a report, and how disclosure is coordinated.

## Supported Versions

Loca Loca is currently maintained as a single active development line. Security fixes are prepared for the latest code on `main`. When a stable release is published, the latest stable release is also supported.

| Version | Security support |
| --- | --- |
| Latest `main` | Supported |
| Latest stable release | Supported, when available |
| Older releases | Not supported |
| Development snapshots and forks | Not supported by the project |

Security fixes are not guaranteed to be backported to older releases. Users should update to the latest supported version. If you cannot update, include the exact version or commit you use in your report.

## Reporting a Vulnerability

**Do not report security vulnerabilities through public GitHub Issues, discussions, or pull requests.** Publicly posting exploit details before a fix is available can put users at risk.

Preferred reporting channel:

1. Open the repository's **Security** tab and use **Report a vulnerability** under **Advisories**, if GitHub Private Vulnerability Reporting is enabled for the repository.
2. If private vulnerability reporting is unavailable, contact the maintainer privately using the contact details on the maintainer's GitHub profile. Do not publish exploit details while arranging contact.

Please include as much of the following information as possible:

- A concise description of the issue and its potential security impact.
- The affected version, commit, component, operating system, and relevant configuration.
- Clear, step-by-step reproduction instructions and, where practical, a minimal proof of concept.
- Any prerequisites, required permissions, or user interaction needed to exploit the issue.
- The likely attack scenario and affected data or users.
- A suggested mitigation or fix, if you have one.

Do not include real users' personal data, production database contents, live credentials, session tokens, or other secrets in a report. Use synthetic examples and redact sensitive values. Do not access, modify, or download data that does not belong to you while validating a finding.

### Project-specific security context

The project includes several components that may be in scope for a report:

- The Windows WinUI client and its local configuration or data handling.
- The ASP.NET Core API, including authentication, authorization, session handling, and input validation.
- Supabase PostgreSQL access, database roles, grants, row-level security, and the handling of user data.
- Photo upload, storage, retrieval, and validation.
- Installer and PowerShell scripts.
- GitHub Actions workflows and the software supply chain.

The standard desktop client communicates with the hosted API over HTTPS. The PostgreSQL connection string and session-signing secret are server-side secrets and must be stored only in protected Render environment variables: `LOCA_DB_CONNECTION` and `LOCA_SESSION_SECRET`. They must not be committed or embedded in the Windows client or installer.

For local development, the API may read `%LOCALAPPDATA%\\LocaLoca\\server-config.json`. The database password in this file is protected with Windows DPAPI using the `CurrentUser` scope. This file is user-specific and must not be shared or committed.

The automated unit, API, and database tests are intended to use synthetic data and disposable PostgreSQL containers with credentials generated at runtime. They must not use Render environment variables, production Supabase connection strings, or production credentials.

The repository runs automated secret scanning on pull requests and pushes. Gitleaks scans checked-out Git history with secret redaction enabled. Narrow, documented allowlists cover specific non-secret XAML palette resource names containing hexadecimal color values and a historical `sb_publishable_` key format intended for public clients. These allowlists do not permit `sb_secret_`, `service_role`, database passwords, or arbitrary JWTs. Secret scanning reduces the risk of accidental disclosure; it cannot guarantee detection of every possible secret format.

A historical publishable key is not a database password or a service-role secret, and it is no longer used by the current desktop client. Its public nature does not make a misconfigured Supabase Data API safe: database grants and row-level security still require review. A read-only inspection query is available at [database/audits/check_supabase_data_api_access.sql](database/audits/check_supabase_data_api_access.sql).

If a database password, session secret, access token, or other credential has appeared in a commit, treat it as compromised and rotate or revoke it. Removing the value from the latest file does not invalidate a credential that was previously exposed.

## Response Timeline

- The maintainer aims to acknowledge a vulnerability report within **72 hours** of receipt.
- After the initial acknowledgement, the maintainer will review the report, attempt to reproduce the issue, and assess its severity and impact.
- The reporter will be informed when the issue has been triaged and, where practical, receive updates on material changes in status.
- Confirmed vulnerabilities will be prioritized according to severity and practical risk. A fix or mitigation will be prepared as soon as reasonably practical; no fixed remediation deadline is guaranteed.
- If a report is not considered a vulnerability or falls outside the project's scope, the maintainer will explain that decision where possible.

The 72-hour acknowledgement target is not a guarantee of a completed investigation or fix within that period.

## Disclosure Policy

Loca Loca follows a coordinated disclosure approach:

1. The reporter sends the details privately and allows the maintainer reasonable time to investigate.
2. The maintainer and reporter coordinate remediation and an appropriate disclosure date.
3. Vulnerability details should remain private while a fix or mitigation is being developed. The maintainer may publish an advisory after a fix or mitigation is available and users have had a reasonable opportunity to update.
4. If the vulnerability is being actively exploited or creates an immediate risk, the maintainer may need to accelerate the response and disclosure process. The reporter will be kept informed where possible.

Please do not publicly disclose the vulnerability, proof of concept, or affected users' data before coordinated disclosure has been agreed.

## Scope

Security reports are welcome for vulnerabilities in the project components listed above, including issues that could lead to:

- Unauthorized access to another user's account or data.
- Authentication or authorization bypass.
- Exposure of database credentials, session-signing secrets, access tokens, or other sensitive configuration.
- SQL injection, command injection, path traversal, or other injection vulnerabilities.
- Unsafe handling of uploaded files or images.
- Cross-user data access caused by incorrect database permissions or row-level security.
- A security weakness in the installer, update process, CI workflows, or dependencies that can be practically exploited against project users.

The following are generally not security vulnerabilities by themselves:

- Ordinary bugs or visual defects without a security impact.
- Feature requests or recommendations for hardening that do not identify an exploitable weakness.
- Findings that rely only on outdated software versions when no vulnerable behavior in the project is demonstrated.
- Reports based solely on automated scanner output without a reproducible security impact.

This list is illustrative, not exhaustive. A finding that does not fit these examples may still be in scope if you can demonstrate a realistic security impact. Do not perform denial-of-service testing, social engineering, or testing against other users' accounts or data without explicit authorization.

## Acknowledgements

We appreciate responsible security research and will acknowledge the reporter in a security advisory or project notes when a vulnerability is confirmed and the reporter agrees to be credited. Researchers may request to remain anonymous. No personal details will be published without permission.

Loca Loca does not currently operate a bug bounty program.
