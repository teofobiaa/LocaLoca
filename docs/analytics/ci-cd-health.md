# CI/CD health analytics

The CI/CD analytics workflow runs every Monday and can also be started manually from the Actions tab. It reads GitHub Actions metadata and TRX test-result artifacts, then publishes a Markdown report in the workflow summary and a JSON artifact containing the collected metrics.

## Collected metrics

| Metric | Definition | Source |
|---|---|---|
| Build Success Rate | Successful completed push, pull-request and merge-queue workflow runs divided by all completed runs in the selected period | Actions workflow runs API |
| Test Success Rate | Successful completed Automated tests runs divided by all completed runs of that workflow | Actions workflow runs API |
| Flaky Tests | Tests with both passed and failed outcomes for the same commit SHA in the selected period | TRX artifacts from unit, API and database jobs |
| Average CI Duration | Mean duration of completed workflow runs | Workflow run start/update timestamps |
| Failed Job Distribution | Count of failed jobs grouped by job name | Actions jobs API |
| Regression Rate (proxy) | Failed Automated tests runs on main divided by all completed Automated tests runs on main | Actions workflow runs API |
| PR Validation Time | Mean time from PR creation to completion of its associated Automated tests run | Pull request and workflow run metadata |

## Interpretation and limitations

- The report is observational. It does not create a required status check, alter branch protection, or block merges.
- Regression Rate is deliberately labelled a proxy. It measures main-branch test failure frequency; it does not prove which merged PR caused a failure.
- Flaky-test detection is conservative and requires contradictory TRX outcomes for the same test on the same commit SHA. A one-off failure is not automatically labelled flaky.
- PR validation time starts at PR creation. It is not equivalent to time since the latest commit or time waiting for review.
- Missing/expired artifacts or insufficient run history result in incomplete samples. The report includes sample counts.
- The default window is 30 days. Manual runs accept a custom window from 1 to 90 days.
- Artifacts contain test names and outcomes, not test input payloads. No application user data, secrets, production telemetry, or user content is collected.

## Recommended rollout

Collect at least 4–6 weeks of baseline data before defining targets. Keep the report informational initially. If merge gates are later introduced, use stable, narrowly scoped checks rather than thresholds based on overall historical averages.
