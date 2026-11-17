"""Regression tests for CI analytics collection and pagination."""

import datetime as dt
import email.message
import json
import os
import tempfile
import urllib.error
import unittest
from unittest import mock
from urllib.parse import parse_qs, urlparse

import collect


UTC = dt.timezone.utc


def make_run(run_id, event, created_at):
    return {
        "id": run_id,
        "event": event,
        "created_at": created_at.strftime("%Y-%m-%dT%H:%M:%SZ"),
        "status": "completed",
        "conclusion": "success",
        "name": "Build",
    }


class WorkflowPaginationTests(unittest.TestCase):
    def setUp(self):
        collect.REPO = "example/loca"
        collect.TOKEN = "test-token"
        collect.API_REQUESTS = 0
        collect.API_RETRIES = 0
        collect.TRX_DIAGNOSTICS.clear()

    def api_stub_for(self, dataset):
        def fake_get(path):
            parsed = urlparse(path)
            params = parse_qs(parsed.query)
            event = params["event"][0]
            start_text, end_text = params["created"][0].split("..", 1)
            start = dt.datetime.fromisoformat(start_text.replace("Z", "+00:00"))
            end = dt.datetime.fromisoformat(end_text.replace("Z", "+00:00"))
            page = int(params["page"][0])

            matching = [
                run for run in dataset
                if run["event"] == event
                and start <= dt.datetime.fromisoformat(run["created_at"].replace("Z", "+00:00")) <= end
            ]
            offset = (page - 1) * collect.PAGE_SIZE
            return {
                "total_count": len(matching),
                "workflow_runs": matching[offset:offset + collect.PAGE_SIZE],
            }

        return fake_get

    def test_collects_more_than_one_thousand_mixed_event_runs(self):
        since = dt.datetime(2026, 10, 1, tzinfo=UTC)
        until = dt.datetime(2026, 10, 31, tzinfo=UTC)
        dataset = []

        for index in range(700):
            dataset.append(make_run(
                f"push-{index}",
                "push",
                since + dt.timedelta(minutes=index),
            ))
        for index in range(500):
            dataset.append(make_run(
                f"pr-{index}",
                "pull_request",
                since + dt.timedelta(hours=12, minutes=index),
            ))
        for index in range(3):
            dataset.append(make_run(
                f"merge-{index}",
                "merge_group",
                since + dt.timedelta(days=2, minutes=index),
            ))
        dataset.append(make_run("schedule-ignored", "schedule", since))

        with mock.patch.object(collect, "get", side_effect=self.api_stub_for(dataset)):
            runs, coverage = collect.fetch_workflow_runs(since, until)

        self.assertEqual(1_203, len(runs))
        self.assertTrue(coverage["pagination_complete"])
        self.assertEqual("complete", coverage["coverage_status"])
        self.assertEqual(
            {"push": 700, "pull_request": 500, "merge_group": 3},
            coverage["workflow_runs_fetched_by_event"],
        )
        self.assertEqual(
            {"push": 700, "pull_request": 500, "merge_group": 3},
            coverage["workflow_runs_reported_by_event"],
        )
        self.assertIn("push-0", {run["id"] for run in runs})
        self.assertNotIn("schedule-ignored", {run["id"] for run in runs})
        self.assertEqual(3, coverage["pagination_search_queries"])
        self.assertEqual(13, coverage["pagination_pages_fetched"])
        self.assertEqual(
            {"push": 1, "pull_request": 1, "merge_group": 1},
            {
                event: len(windows)
                for event, windows in coverage["pagination_completed_windows_by_event"].items()
            },
        )

    def test_90_day_window_is_not_silently_truncated(self):
        since = dt.datetime(2026, 1, 1, tzinfo=UTC)
        until = since + dt.timedelta(days=90)
        dataset = []

        for index in range(600):
            dataset.append(make_run(
                f"quarter-push-{index}",
                "push",
                since + dt.timedelta(hours=index * 3),
            ))
        for index in range(500):
            dataset.append(make_run(
                f"quarter-pr-{index}",
                "pull_request",
                since + dt.timedelta(hours=12 + index * 4),
            ))
        dataset.append(make_run("quarter-merge", "merge_group", since + dt.timedelta(days=89)))

        with mock.patch.object(collect, "get", side_effect=self.api_stub_for(dataset)):
            runs, coverage = collect.fetch_workflow_runs(since, until)

        self.assertEqual(1_101, len(runs))
        self.assertTrue(coverage["pagination_complete"])
        self.assertEqual(collect.github_timestamp(since), coverage["coverage_start_utc"])
        self.assertEqual(collect.github_timestamp(until), coverage["coverage_end_utc"])
        self.assertEqual(
            {"push": 600, "pull_request": 500, "merge_group": 1},
            coverage["workflow_runs_fetched_by_event"],
        )

    def test_splits_a_single_event_window_above_github_search_limit(self):
        since = dt.datetime(2026, 10, 1, tzinfo=UTC)
        until = since + dt.timedelta(days=2)
        midpoint = since + dt.timedelta(days=1)
        dataset = []

        for index in range(600):
            dataset.append(make_run(
                f"dense-left-{index}",
                "push",
                since + dt.timedelta(minutes=index),
            ))
        for index in range(601):
            dataset.append(make_run(
                f"dense-right-{index}",
                "push",
                midpoint + dt.timedelta(minutes=index),
            ))

        with mock.patch.object(collect, "get", side_effect=self.api_stub_for(dataset)):
            runs, coverage = collect.fetch_workflow_runs(since, until)

        self.assertEqual(1_201, len(runs))
        self.assertTrue(coverage["pagination_complete"])
        self.assertEqual(1, coverage["pagination_time_windows_split"])
        self.assertEqual(1_201, coverage["workflow_runs_reported_by_event"]["push"])
        self.assertIn("dense-left-0", {run["id"] for run in runs})
        self.assertIn("dense-right-600", {run["id"] for run in runs})
        self.assertEqual(2, len(coverage["pagination_completed_windows_by_event"]["push"]))

    def test_marks_irreducibly_dense_one_second_window_as_partial(self):
        since = dt.datetime(2026, 10, 1, tzinfo=UTC)
        until = since + dt.timedelta(seconds=2)
        dataset = [
            make_run(f"same-second-{index}", "push", since)
            for index in range(1_001)
        ]

        with mock.patch.object(collect, "get", side_effect=self.api_stub_for(dataset)):
            runs, coverage = collect.fetch_workflow_runs(since, until)

        self.assertEqual([], runs)
        self.assertFalse(coverage["pagination_complete"])
        self.assertEqual("partial", coverage["coverage_status"])
        self.assertEqual(1, len(coverage["pagination_incomplete_windows"]))
        self.assertEqual("push", coverage["pagination_incomplete_windows"][0]["event"])
        self.assertEqual(
            1_001,
            coverage["pagination_incomplete_windows"][0]["reported_count"],
        )
        self.assertEqual(1, len(coverage["pagination_completed_windows_by_event"]["push"]))

    def test_partial_collection_writes_diagnostic_report_and_fails(self):
        since = dt.datetime(2026, 10, 1, tzinfo=UTC)
        until = since + dt.timedelta(days=30)
        coverage = {
            "requested_since_utc": collect.github_timestamp(since),
            "requested_until_utc": collect.github_timestamp(until),
            "coverage_status": "partial",
            "pagination_complete": False,
            "coverage_start_utc": None,
            "coverage_end_utc": None,
            "workflow_runs_fetched": 0,
            "workflow_runs_reported_by_event": {},
            "workflow_runs_fetched_by_event": {},
            "pagination_search_queries": 4,
            "pagination_pages_fetched": 4,
            "pagination_time_windows_split": 1,
            "pagination_incomplete_windows": [{
                "event": "push",
                "since_utc": collect.github_timestamp(since),
                "until_utc_exclusive": collect.github_timestamp(since + dt.timedelta(seconds=1)),
                "reason": "Synthetic over-cap test window.",
                "reported_count": 1_001,
            }],
            "pagination_completed_windows_by_event": {
                "push": [],
                "pull_request": [],
                "merge_group": [],
            },
        }

        with tempfile.TemporaryDirectory() as temp_dir:
            original_directory = os.getcwd()
            try:
                os.chdir(temp_dir)
                with mock.patch.object(
                    collect,
                    "fetch_workflow_runs",
                    return_value=([], coverage),
                ):
                    with self.assertRaises(SystemExit) as raised:
                        collect.main()

                self.assertEqual(2, raised.exception.code)
                metrics_path = os.path.join(temp_dir, "ci-analytics", "metrics.json")
                report_path = os.path.join(temp_dir, "ci-analytics", "report.md")
                with open(metrics_path, encoding="utf-8") as source:
                    metrics = json.load(source)
                with open(report_path, encoding="utf-8") as source:
                    report = source.read()

                self.assertFalse(metrics["pagination_complete"])
                self.assertEqual("partial", metrics["coverage_status"])
                self.assertIn("PARTIAL", report)
                self.assertIn("Synthetic over-cap test window.", report)
            finally:
                os.chdir(original_directory)

    def test_retries_transient_api_response(self):
        headers = email.message.Message()
        headers["Retry-After"] = "0"
        error = urllib.error.HTTPError(
            "https://api.github.com/test",
            503,
            "Service Unavailable",
            headers,
            None,
        )

        class FakeResponse:
            def __enter__(self):
                return self

            def __exit__(self, *_args):
                return False

            def read(self):
                return json.dumps({"ok": True}).encode("utf-8")

        with (
            mock.patch.object(collect.urllib.request, "urlopen", side_effect=[error, FakeResponse()]),
            mock.patch.object(collect.time, "sleep") as sleep,
        ):
            self.assertEqual({"ok": True}, collect.get("/test"))

        self.assertEqual(2, collect.API_REQUESTS)
        self.assertEqual(1, collect.API_RETRIES)
        sleep.assert_called_once_with(1.0)

    def test_generic_job_and_artifact_pagination_reads_every_page(self):
        first_page = [{"id": index} for index in range(collect.PAGE_SIZE)]
        second_page = [{"id": collect.PAGE_SIZE}]

        with mock.patch.object(
            collect,
            "get",
            side_effect=[
                {"jobs": first_page},
                {"jobs": second_page},
            ],
        ) as get:
            items = collect.paginated_items("/jobs", "jobs")

        self.assertEqual(101, len(items))
        self.assertEqual(2, get.call_count)


if __name__ == "__main__":
    unittest.main()
