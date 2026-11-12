# Password hashing and gradual migration

## Current cost and deployment context

The API currently uses PBKDF2-HMAC-SHA256 with 100,000 iterations, a cryptographically random 16-byte salt, and a 32-byte derived key. The database contains legacy values in this representation:

`iterations.saltBase64.hashBase64`

Password derivation runs in the ASP.NET Core API process, not in the WinUI client. Consequently, performance selection must be made on the production API hosting plan/container class, including its CPU quota and memory limit, rather than on a developer workstation.

OWASP currently lists 600,000 iterations for PBKDF2-HMAC-SHA256 and Argon2id minimum settings of 19 MiB / 2 passes / 1 lane. The existing 100,000-iteration default is below that PBKDF2 recommendation. It remains unchanged until production-equivalent measurements are captured; do not set the production environment variable based on a workstation benchmark alone.

Reference: https://cheatsheetseries.owasp.org/cheatsheets/Password_Storage_Cheat_Sheet.html

## Versioned format and migration

New hashes use a self-describing representation:

`pbkdf2-sha256$iterations$saltBase64$hashBase64`

The verifier accepts both the old three-part representation and the new versioned representation. After a successful password verification, the login endpoint upgrades legacy hashes and PBKDF2 hashes below the configured target. The update is compare-and-swap on both user ID and old hash, so a concurrent password reset cannot be overwritten. If another login updates the row first, the endpoint checks that the current record still verifies the submitted password before issuing a session. Failed logins never change password hashes.

No database schema migration is required. Gradual migration happens as users sign in; inactive accounts retain their old representation until their next successful login.

## Configurable PBKDF2 cost

The API supports the environment variable `LOCA_PASSWORD_PBKDF2_ITERATIONS`, with an accepted range of 10,000–2,000,000. It controls new registrations and the target for login-time upgrades. The default remains 100,000 until target-host benchmarks are reviewed. Stronger existing hashes are not downgraded.

## Target-host benchmark

The isolated tool under `Tools/PasswordHashBenchmarks` compares:

| Candidate | Settings |
| --- | --- |
| PBKDF2-HMAC-SHA256 | 100k, 300k, 600k, 1m iterations |
| Argon2id | m=19 MiB, t=2, p=1 |
| Argon2id | m=46 MiB, t=1, p=1 |
| Argon2id | m=64 MiB, t=3, p=1 |

It reports runtime, OS, architecture, processor count, p50/p95 latency and two-way concurrent latency for PBKDF2 at 600k and Argon2id at 19 MiB / 2 passes. Argon2id memory is allocated per in-flight derivation, so test concurrency and container memory pressure in addition to single-request latency.

Run the benchmark inside the same container/resource class as the production API, preferably during a controlled maintenance or staging window:

```sh
dotnet run --configuration Release --project Tools/PasswordHashBenchmarks -- 20
```

Select a production cost only after reviewing:
1. p50 and p95 derivation latency under representative CPU limits;
2. memory use per concurrent Argon2id derivation and container memory headroom;
3. login throughput and tail latency with expected simultaneous logins;
4. CPU throttling and impact on normal API endpoints.

A reasonable target is to keep a single password derivation comfortably below one second, while retaining enough CPU and memory capacity under concurrent logins. Record the measured host class and output before changing `LOCA_PASSWORD_PBKDF2_ITERATIONS` or introducing an Argon2id production dependency.

## Argon2id decision

Argon2id is benchmarked by a tooling-only dependency (`BouncyCastle.Cryptography`); it is not included in the production API dependency graph by this change. No algorithm switch is made until results from the target host are available. If Argon2id is selected later, add its versioned verifier and route it through the same successful-login migration mechanism. Do not bulk-reset passwords or attempt to reverse existing hashes.
