#!/usr/bin/env python3
"""Read-only GitHub Actions CI health analytics with explicit coverage guarantees."""

import collections
import datetime as dt
import io
import json
import math
import os
import time
import urllib.error
import urllib.parse
import urllib.request
import zipfile
import xml.etree.ElementTree as ET

# Keep import side-effect-free so pagination and reporting helpers can be unit-tested
# without injecting CI secrets into the test process.
REPO = os.getenv("GITHUB_REPOSITORY", "")
TOKEN = os.getenv("GITHUB_TOKEN", "")
API = "https://api.github.com"
DAYS = max(1, min(90, int(os.getenv("ANALYTICS_DAYS", "30"))))

PAGE_SIZE = 100
SEARCH_RESULT_LIMIT = 1_000
MAX_SPLIT_DEPTH = 32
MAX_API_RETRIES = 3
WORKFLOW_EVENTS = ("push", "pull_request", "merge_group")
TRANSIENT_HTTP_STATUSES = {429, 500, 502, 503, 504}
UTC = dt.timezone.utc

HEAD = {
    "Accept": "application/vnd.github+json",
    "X-GitHub-Api-Version": "2022-11-28",
}
if TOKEN:
    HEAD["Authorization"] = f"Bearer {TOKEN}"

API_REQUESTS = 0
API_RETRIES = 0
TRX_DIAGNOSTICS = collections.Counter()


def utc_second(value):
    """Normalize a datetime to UTC with the second precision supported by GitHub search."""
    if value.tzinfo is None:
        raise ValueError("Pagination boundaries must be timezone-aware.")
    return value.astimezone(UTC).replace(microsecond=0)


def github_timestamp(value):
    return utc_second(value).strftime("%Y-%m-%dT%H:%M:%SZ")


def get(path):
    """GET JSON from GitHub, retrying transient failures and rate limiting."""
    global API_REQUESTS, API_RETRIES

    url = API + path
    for attempt in range(MAX_API_RETRIES + 1):
        req = urllib.request.Request(url, headers=HEAD)
        API_REQUESTS += 1
        try:
            with urllib.request.urlopen(req, timeout=45) as response:
                return json.loads(response.read())
        except urllib.error.HTTPError as exc:
            remaining = exc.headers.get("X-RateLimit-Remaining")
            rate_limited = exc.code == 403 and remaining == "0"
            retryable = exc.code in TRANSIENT_HTTP_STATUSES or rate_limited
            if not retryable or attempt >= MAX_API_RETRIES:
                raise

            retry_after = exc.headers.get("Retry-After")
            reset_at = exc.headers.get("X-RateLimit-Reset")
            if retry_after:
                delay = float(retry_after)
            elif rate_limited and reset_at:
                delay = max(1.0, float(reset_at) - time.time())
            else:
                delay = min(2 ** attempt, 8)

            API_RETRIES += 1
            time.sleep(min(max(delay, 1.0), 60.0))
        except urllib.error.URLError:
            if attempt >= MAX_API_RETRIES:
                raise
            API_RETRIES += 1
            time.sleep(min(2 ** attempt, 8))

    raise RuntimeError("GitHub API request exhausted its retry budget.")


def paginated_items(path, key):
    """Read a standard GitHub collection until its final short page."""
    items = []
    page = 1
    while True:
        separator = "&" if "?" in path else "?"
        query = urllib.parse.urlencode({"per_page": PAGE_SIZE, "page": page})
        batch = get(f"{path}{separator}{query}").get(key, [])
        items.extend(batch)
        if len(batch) < PAGE_SIZE:
            return items
        page += 1


def _runs_query(start, end, event, page):
    """Fetch one page for an exact, inclusive GitHub created-at range."""
    if end <= start:
        raise ValueError("The workflow-run search interval must be non-empty.")

    # GitHub's created qualifier is inclusive at both ends. Our intervals are
    # [start, end), so subtract one second to keep adjacent partitions disjoint.
    end_inclusive = end - dt.timedelta(seconds=1)
    params = urllib.parse.urlencode({
        "created": f"{github_timestamp(start)}..{github_timestamp(end_inclusive)}",
        "event": event,
        "per_page": PAGE_SIZE,
        "page": page,
    })
    return get(f"/repos/{REPO}/actions/runs?{params}")


def _mark_incomplete(stats, event, start, end, reason, **details):
    entry = {
        "event": event,
        "since_utc": github_timestamp(start),
        "until_utc_exclusive": github_timestamp(end),
        "reason": reason,
    }
    entry.update(details)
    stats["incomplete_windows"].append(entry)


def _fetch_event_window(start, end, event, stats, depth=0):
    """Fetch one event/window; split it if GitHub's 1,000-result search cap applies."""
    first_page = _runs_query(start, end, event, 1)
    stats["search_queries"] += 1
    stats["pages_fetched"] += 1

    total_count = int(first_page.get("total_count", len(first_page.get("workflow_runs", []))))
    first_batch = first_page.get("workflow_runs", [])

    if total_count > SEARCH_RESULT_LIMIT:
        duration_seconds = int((end - start).total_seconds())
        if duration_seconds <= 1 or depth >= MAX_SPLIT_DEPTH:
            _mark_incomplete(
                stats,
                event,
                start,
                end,
                "The smallest supported time window still exceeds GitHub's 1,000-result search cap.",
                reported_count=total_count,
                split_depth=depth,
            )
            return []

        midpoint = start + dt.timedelta(seconds=duration_seconds // 2)
        stats["time_windows_split"] += 1
        return (
            _fetch_event_window(start, midpoint, event, stats, depth + 1)
            + _fetch_event_window(midpoint, end, event, stats, depth + 1)
        )

    stats["reported_count_by_event"][event] += total_count
    if total_count == 0:
        stats["completed_windows_by_event"][event].append({
            "since_utc": github_timestamp(start),
            "until_utc_exclusive": github_timestamp(end),
        })
        return []

    expected_pages = math.ceil(total_count / PAGE_SIZE)
    collected = list(first_batch)

    for page in range(2, expected_pages + 1):
        payload = _runs_query(start, end, event, page)
        stats["pages_fetched"] += 1
        collected.extend(payload.get("workflow_runs", []))

    unique = {}
    for run in collected:
        run_id = run.get("id")
        if run_id is not None:
            unique[run_id] = run

    if len(unique) != total_count:
        _mark_incomplete(
            stats,
            event,
            start,
            end,
            "The paginated result count did not match GitHub's reported total.",
            reported_count=total_count,
            fetched_unique_count=len(unique),
        )
    else:
        stats["completed_windows_by_event"][event].append({
            "since_utc": github_timestamp(start),
            "until_utc_exclusive": github_timestamp(end),
        })

    return list(unique.values())


def fetch_workflow_runs(since, until):
    """Collect all eligible workflow runs across the complete requested interval.

    GitHub limits each workflow-run search to 1,000 results whenever a search
    qualifier such as created or event is used. Event filtering plus recursive
    time partitioning keeps every individual query below that limit.
    """
    since = utc_second(since)
    until = utc_second(until)
    if until <= since:
        raise ValueError("The analytics end time must be later than its start time.")

    stats = {
        "search_queries": 0,
        "pages_fetched": 0,
        "time_windows_split": 0,
        "incomplete_windows": [],
        "completed_windows_by_event": {event: [] for event in WORKFLOW_EVENTS},
        "reported_count_by_event": collections.Counter(),
    }
    by_id = {}

    for event in WORKFLOW_EVENTS:
        event_runs = _fetch_event_window(since, until, event, stats)
        for run in event_runs:
            # Keep a defensive client-side filter in case the API returns an
            # unexpected event, and deduplicate any overlap introduced upstream.
            if run.get("event") == event and run.get("id") is not None:
                by_id[run["id"]] = run

    runs = list(by_id.values())
    fetched_by_event = collections.Counter(run.get("event") for run in runs)
    complete = not stats["incomplete_windows"]

    coverage = {
        "requested_since_utc": github_timestamp(since),
        "requested_until_utc": github_timestamp(until),
        "coverage_status": "complete" if complete else "partial",
        "pagination_complete": complete,
        "coverage_start_utc": github_timestamp(since) if complete else None,
        "coverage_end_utc": github_timestamp(until) if complete else None,
        "workflow_runs_fetched": len(runs),
        "workflow_runs_reported_by_event": dict(stats["reported_count_by_event"]),
        "workflow_runs_fetched_by_event": dict(fetched_by_event),
        "pagination_search_queries": stats["search_queries"],
        "pagination_pages_fetched": stats["pages_fetched"],
        "pagination_time_windows_split": stats["time_windows_split"],
        "pagination_incomplete_windows": stats["incomplete_windows"],
        "pagination_completed_windows_by_event": stats["completed_windows_by_event"],
    }
    return runs, coverage


def date(value):
    return dt.datetime.fromisoformat(value.replace("Z", "+00:00")) if value else None


def pct(numerator, denominator):
    return round(100 * numerator / denominator, 1) if denominator else None


def download_artifact(artifact_id):
    """Download one artifact ZIP with the same transient-error retry policy."""
    global API_REQUESTS, API_RETRIES

    url = f"{API}/repos/{REPO}/actions/artifacts/{artifact_id}/zip"
    for attempt in range(MAX_API_RETRIES + 1):
        req = urllib.request.Request(url, headers=HEAD)
        API_REQUESTS += 1
        try:
            with urllib.request.urlopen(req, timeout=60) as response:
                return response.read()
        except urllib.error.HTTPError as exc:
            remaining = exc.headers.get("X-RateLimit-Remaining")
            rate_limited = exc.code == 403 and remaining == "0"
            retryable = exc.code in TRANSIENT_HTTP_STATUSES or rate_limited
            if not retryable or attempt >= MAX_API_RETRIES:
                raise
            retry_after = exc.headers.get("Retry-After")
            reset_at = exc.headers.get("X-RateLimit-Reset")
            if retry_after:
                delay = float(retry_after)
            elif rate_limited and reset_at:
                delay = max(1.0, float(reset_at) - time.time())
            else:
                delay = min(2 ** attempt, 8)
            API_RETRIES += 1
            time.sleep(min(max(delay, 1.0), 60.0))
        except urllib.error.URLError:
            if attempt >= MAX_API_RETRIES:
                raise
            API_RETRIES += 1
            time.sleep(min(2 ** attempt, 8))

    raise RuntimeError("Artifact download exhausted its retry budget.")


def trx(run_id):
    result = collections.defaultdict(set)
    TRX_DIAGNOSTICS["test_runs_examined"] += 1
    try:
        artifacts = paginated_items(
            f"/repos/{REPO}/actions/runs/{run_id}/artifacts",
            "artifacts",
        )
        trx_artifacts = [
            artifact for artifact in artifacts
            if artifact.get("name", "").startswith("trx-") and not artifact.get("expired")
        ]
        if not trx_artifacts:
            TRX_DIAGNOSTICS["test_runs_without_trx_artifact"] += 1
            return result

        TRX_DIAGNOSTICS["test_runs_with_trx_artifact"] += 1
        for artifact in trx_artifacts:
            try:
                archive = zipfile.ZipFile(io.BytesIO(download_artifact(artifact["id"])))
                TRX_DIAGNOSTICS["trx_artifacts_downloaded"] += 1
                for name in archive.namelist():
                    if not name.lower().endswith(".trx"):
                        continue
                    root = ET.fromstring(archive.read(name))
                    for node in root.iter():
                        if node.tag.rsplit("}", 1)[-1] == "UnitTestResult":
                            test_name = node.attrib.get("testName") or node.attrib.get("testId")
                            outcome = node.attrib.get("outcome", "").lower()
                            if test_name and outcome in {"passed", "failed"}:
                                result[test_name].add(outcome)
            except Exception as exc:
                TRX_DIAGNOSTICS["trx_artifact_errors"] += 1
                print(f"TRX artifact skipped for run {run_id}: {type(exc).__name__}")
    except Exception as exc:
        TRX_DIAGNOSTICS["artifact_listing_errors"] += 1
        print(f"TRX artifact listing skipped for run {run_id}: {type(exc).__name__}")
    return result


def main():
    global API_REQUESTS, API_RETRIES

    if not REPO or not TOKEN:
        raise RuntimeError("GITHUB_REPOSITORY and GITHUB_TOKEN are required to collect CI analytics.")

    generated_at = dt.datetime.now(UTC).replace(microsecond=0)
    since = generated_at - dt.timedelta(days=DAYS)
    runs, coverage = fetch_workflow_runs(since, generated_at)

    done = [run for run in runs if run.get("status") == "completed"]
    successful = [run for run in done if run.get("conclusion") == "success"]
    tests = [run for run in done if run.get("name") == "Automated tests"]
    successful_tests = [run for run in tests if run.get("conclusion") == "success"]
    main_tests = [run for run in tests if run.get("head_branch") == "main"]
    main_failed = [run for run in main_tests if run.get("conclusion") == "failure"]

    elapsed = [
        (date(run["updated_at"]) - date(run["run_started_at"])).total_seconds()
        for run in done
        if run.get("run_started_at")
        and run.get("updated_at")
        and date(run["updated_at"]) >= date(run["run_started_at"])
    ]

    job_failures = collections.Counter()
    test_signatures = collections.defaultdict(lambda: collections.defaultdict(set))
    pr_times = {}
    pr_created_cache = {}
    failed_workflow_runs_inspected = 0

    for run in done:
        # Successful workflows cannot contain a failed required job. Avoid an
        # unnecessary API request for every successful run in a high-volume repo.
        if run.get("conclusion") in {"failure", "timed_out", "cancelled"}:
            failed_workflow_runs_inspected += 1
            jobs = paginated_items(
                f"/repos/{REPO}/actions/runs/{run['id']}/jobs",
                "jobs",
            )
            for job in jobs:
                if job.get("conclusion") == "failure":
                    job_failures[job.get("name", "Unknown job")] += 1

        if run.get("name") != "Automated tests":
            continue

        for test_name, outcomes in trx(run["id"]).items():
            test_signatures[run.get("head_sha", "unknown")][test_name].update(outcomes)

        for pull_request in run.get("pull_requests", []):
            number = pull_request.get("number")
            if not number:
                continue
            try:
                if number not in pr_created_cache:
                    pr_created_cache[number] = date(get(f"/repos/{REPO}/pulls/{number}")["created_at"])
                created = pr_created_cache[number]
                finished = date(run["updated_at"])
                if created and finished >= created:
                    pr_times[number] = max(
                        pr_times.get(number, 0),
                        (finished - created).total_seconds(),
                    )
            except Exception as exc:
                print(f"PR validation timing skipped for #{number}: {type(exc).__name__}")

    flaky_tests = sorted(
        test_name
        for tests_by_sha in test_signatures.values()
        for test_name, outcomes in tests_by_sha.items()
        if {"passed", "failed"}.issubset(outcomes)
    )
    average_ci = round(sum(elapsed) / len(elapsed) / 60, 1) if elapsed else None
    average_pr = round(sum(pr_times.values()) / len(pr_times) / 60, 1) if pr_times else None

    trx_runs_with_artifact = TRX_DIAGNOSTICS["test_runs_with_trx_artifact"]
    trx_runs_examined = TRX_DIAGNOSTICS["test_runs_examined"]
    trx_artifact_coverage = (
        pct(trx_runs_with_artifact, trx_runs_examined) if trx_runs_examined else None
    )
    flaky_detection_complete = (
        trx_runs_examined > 0
        and TRX_DIAGNOSTICS["test_runs_without_trx_artifact"] == 0
        and TRX_DIAGNOSTICS["trx_artifact_errors"] == 0
        and TRX_DIAGNOSTICS["artifact_listing_errors"] == 0
    )

    metrics = {
        "period_days": DAYS,
        "generated_at_utc": github_timestamp(generated_at),
        **coverage,
        "api_requests_total": API_REQUESTS,
        "api_retries_total": API_RETRIES,
        "build_success_rate_percent": pct(len(successful), len(done)),
        "build_runs_completed": len(done),
        "test_success_rate_percent": pct(len(successful_tests), len(tests)),
        "test_runs_completed": len(tests),
        "flaky_tests": flaky_tests,
        "flaky_test_count": len(flaky_tests),
        "flaky_detection_complete": flaky_detection_complete,
        "trx_artifact_coverage_percent": trx_artifact_coverage,
        "trx_diagnostics": dict(TRX_DIAGNOSTICS),
        "average_ci_duration_minutes": average_ci,
        "failed_job_distribution": dict(job_failures.most_common()),
        "failed_job_total": sum(job_failures.values()),
        "failed_workflow_runs_inspected_for_jobs": failed_workflow_runs_inspected,
        "main_test_failure_rate_percent": pct(len(main_failed), len(main_tests)),
        "main_test_runs_completed": len(main_tests),
        "pr_validation_average_minutes": average_pr,
        "prs_with_completed_test_run": len(pr_times),
    }

    def show(value, suffix=""):
        return "n/a (insufficient data)" if value is None else f"{value}{suffix}"

    coverage_state = "COMPLETE" if coverage["pagination_complete"] else "PARTIAL — metrics are not representative"
    oldest_run = min((run.get("created_at", "") for run in runs if run.get("created_at")), default=None)
    newest_run = max((run.get("created_at", "") for run in runs if run.get("created_at")), default=None)
    metrics["oldest_workflow_run_created_at"] = oldest_run
    metrics["newest_workflow_run_created_at"] = newest_run

    lines = [
        "# CI/CD health report",
        "",
        f"Requested period: last {DAYS} days ({metrics['requested_since_utc']} to {metrics['requested_until_utc']}).",
        f"Collection status: **{coverage_state}**.",
        (
            f"Coverage: {metrics['workflow_runs_fetched']} workflow runs fetched; "
            f"{metrics['pagination_search_queries']} event/time-window searches; "
            f"{metrics['pagination_pages_fetched']} result pages; "
            f"{metrics['pagination_time_windows_split']} time-window splits."
        ),
        f"Oldest/newest returned run: {oldest_run or 'n/a'} / {newest_run or 'n/a'}.",
        "",
        "| Metric | Result | Sample / notes |",
        "|---|---:|---|",
        f"| Build Success Rate | {show(metrics['build_success_rate_percent'], '%')} | {len(successful)}/{len(done)} completed push/PR/merge-queue runs |",
        f"| Test Success Rate | {show(metrics['test_success_rate_percent'], '%')} | {len(successful_tests)}/{len(tests)} completed Automated tests runs |",
        f"| Flaky Tests | {len(flaky_tests)} detected | Contradictory TRX outcomes for the same test and commit SHA |",
        f"| TRX Artifact Coverage | {show(trx_artifact_coverage, '%')} | {trx_runs_with_artifact}/{trx_runs_examined} Automated tests runs had a readable artifact |",
        f"| Average CI Duration | {show(average_ci, ' min')} | Completed workflow runs |",
        f"| Failed Job Distribution | {sum(job_failures.values())} failures | Failed jobs within failure-like workflows; see JSON artifact |",
        f"| Regression Rate (proxy) | {show(metrics['main_test_failure_rate_percent'], '%')} | Failed main-branch test runs / completed main test runs |",
        f"| PR Validation Time | {show(average_pr, ' min')} average | PR creation to associated test completion; {len(pr_times)} PRs |",
        "",
        "## Collection diagnostics",
        "",
        f"- Pagination complete: {coverage['pagination_complete']}.",
        f"- Eligible runs fetched by event: {json.dumps(coverage['workflow_runs_fetched_by_event'], sort_keys=True)}.",
        f"- Fully covered intervals by event are recorded in metrics.json: "
        f"{json.dumps({event: len(windows) for event, windows in coverage['pagination_completed_windows_by_event'].items()}, sort_keys=True)}.",
        f"- API requests/retries: {API_REQUESTS}/{API_RETRIES}.",
        f"- Flaky-test detection complete: {flaky_detection_complete}. Missing or unreadable TRX artifacts reduce detection coverage.",
        "- Failed-job details are requested only for completed workflows with a failure-like conclusion to reduce API traffic.",
        "",
        "## Interpretation and limitations",
        "",
        "- Build success covers completed push, pull-request and merge-queue runs; scheduled/manual runs are excluded.",
        "- Test success covers the Automated tests workflow (unit, API and database jobs).",
        "- Flaky detection requires contradictory TRX outcomes for the same test on the same commit SHA.",
        "- Regression Rate is an operational proxy, not causal attribution to a specific PR; it is not a merge gate.",
        "- Failed-job distribution inspects only workflow runs whose overall conclusion is failure, timed out or cancelled; tolerated failures in successful workflows are not included.",
        "- PR validation time starts at PR creation, not the latest commit or review-ready time.",
        "- All metrics are observational. No metric in this report blocks merging.",
        "",
        "Failed jobs:",
    ]
    lines.extend(
        [f"- {name}: {count}" for name, count in job_failures.most_common()]
        or ["- None in the selected period."]
    )

    if coverage["incomplete_windows"]:
        lines.extend(["", "## Incomplete pagination windows", ""])
        for window in coverage["incomplete_windows"]:
            lines.append(
                f"- {window['event']}: {window['since_utc']} to "
                f"{window['until_utc_exclusive']} — {window['reason']}"
            )

    os.makedirs("ci-analytics", exist_ok=True)
    with open("ci-analytics/metrics.json", "w", encoding="utf-8") as output:
        json.dump(metrics, output, ensure_ascii=False, indent=2)
    with open("ci-analytics/report.md", "w", encoding="utf-8") as output:
        output.write("\n".join(lines) + "\n")

    print("\n".join(lines))

    if not coverage["pagination_complete"]:
        print(
            "::error::CI analytics collection is partial. "
            "The report was saved for diagnosis; its metrics must not be treated as complete."
        )
        raise SystemExit(2)


if __name__ == "__main__":
    main()
