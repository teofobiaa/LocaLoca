# Code coverage reports

## What CI measures

The `Automated tests` workflow collects .NET code coverage from:

- `LocaLoca.Tests` — unit tests.
- `LocaLoca.Api.Tests` — API integration tests.

These suites exercise the API project and the C# components referenced by the unit tests. Their Cobertura reports are merged into one report by ReportGenerator.

`LocaLoca.Database.Tests` is intentionally not included in the C# coverage percentage. It validates PostgreSQL schema, migrations, constraints and runtime permissions; its result is a database-test status, not a meaningful measure of executed application C# code.

## Where to find the results

For each workflow run, open **Actions → Automated tests**:

1. The **Aggregate code coverage report** job prints the merged line and branch coverage summary to its log and the workflow run summary.
2. The `code-coverage-report` artifact contains the HTML report, the merged Cobertura XML and the text summary.
3. The `coverage-unit` and `coverage-api` artifacts contain the raw Cobertura reports from each test suite.

Artifacts are retained for 30 days. The report job still runs when a unit or API test job fails, so coverage gathered before a failure can be inspected.

## Merge policy

No minimum coverage percentage is enforced yet. Coverage is collected for observation only and does not make a pull request fail because of its percentage.

Before enabling a merge gate:

1. Collect several successful reports from `main` to establish a representative baseline.
2. Choose an explicit metric. Line coverage is the practical initial gate; branch coverage should remain visible as a secondary metric.
3. Set a minimum only after reviewing the baseline and the actual test scope. Avoid selecting a percentage from a single run.
4. Add the threshold check as a separate required CI status, then enable it in branch protection after confirming that the check is stable.

The combined report is the reference for setting the future threshold. Keep the unit, API and database test results separate when interpreting failures.
