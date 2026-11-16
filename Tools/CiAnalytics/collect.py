#!/usr/bin/env python3
"""Read-only GitHub Actions CI health analytics."""
import collections, datetime as dt, io, json, os, urllib.request, zipfile
import xml.etree.ElementTree as ET

REPO=os.environ["GITHUB_REPOSITORY"]; TOKEN=os.environ["GITHUB_TOKEN"]
API="https://api.github.com"; DAYS=max(1,min(90,int(os.getenv("ANALYTICS_DAYS","30"))))
HEAD={"Accept":"application/vnd.github+json","Authorization":f"Bearer {TOKEN}","X-GitHub-Api-Version":"2022-11-28"}

def get(path):
    req=urllib.request.Request(API+path,headers=HEAD)
    with urllib.request.urlopen(req,timeout=45) as r: return json.loads(r.read())
def pages(path,key):
    out=[]
    for page in range(1,11):
        sep="&" if "?" in path else "?"
        batch=get(f"{path}{sep}per_page=100&page={page}").get(key,[])
        out.extend(batch)
        if len(batch)<100: break
    return out
def date(s): return dt.datetime.fromisoformat(s.replace("Z","+00:00")) if s else None
def pct(a,b): return round(100*a/b,1) if b else None
def trx(run_id):
    result=collections.defaultdict(set)
    try:
        for art in get(f"/repos/{REPO}/actions/runs/{run_id}/artifacts").get("artifacts",[]):
            if not art.get("name","").startswith("trx-") or art.get("expired"): continue
            req=urllib.request.Request(f"{API}/repos/{REPO}/actions/artifacts/{art['id']}/zip",headers=HEAD)
            with urllib.request.urlopen(req,timeout=60) as r: z=zipfile.ZipFile(io.BytesIO(r.read()))
            for name in z.namelist():
                if not name.lower().endswith(".trx"): continue
                root=ET.fromstring(z.read(name))
                for node in root.iter():
                    if node.tag.rsplit("}",1)[-1]=="UnitTestResult":
                        test=node.attrib.get("testName") or node.attrib.get("testId")
                        outcome=node.attrib.get("outcome","").lower()
                        if test and outcome in {"passed","failed"}: result[test].add(outcome)
    except Exception as exc:
        print(f"TRX artifact skipped for run {run_id}: {type(exc).__name__}")
    return result

def main():
    since=(dt.datetime.now(dt.timezone.utc)-dt.timedelta(days=DAYS)).date().isoformat()
    runs=pages(f"/repos/{REPO}/actions/runs?created=%3E%3D{since}","workflow_runs")
    runs=[r for r in runs if r.get("event") in {"push","pull_request","merge_group"} and r.get("name")!="CI/CD analytics"]
    done=[r for r in runs if r.get("status")=="completed"]
    ok=[r for r in done if r.get("conclusion")=="success"]
    tests=[r for r in done if r.get("name")=="Automated tests"]
    test_ok=[r for r in tests if r.get("conclusion")=="success"]
    main_tests=[r for r in tests if r.get("head_branch")=="main"]
    main_failed=[r for r in main_tests if r.get("conclusion")=="failure"]
    elapsed=[(date(r["updated_at"])-date(r["run_started_at"])).total_seconds() for r in done if r.get("run_started_at") and r.get("updated_at") and date(r["updated_at"])>=date(r["run_started_at"])]
    job_fail=collections.Counter(); signatures=collections.defaultdict(lambda:collections.defaultdict(set)); pr_times={}
    for run in done:
        jobs=get(f"/repos/{REPO}/actions/runs/{run['id']}/jobs?per_page=100").get("jobs",[])
        for job in jobs:
            if job.get("conclusion")=="failure": job_fail[job.get("name","Unknown job")]+=1
        if run.get("name")!="Automated tests": continue
        for name,outcomes in trx(run["id"]).items(): signatures[run.get("head_sha","unknown")][name].update(outcomes)
        for pr in run.get("pull_requests",[]):
            number=pr.get("number")
            if not number: continue
            try:
                created=date(get(f"/repos/{REPO}/pulls/{number}")["created_at"]); finished=date(run["updated_at"])
                if finished>=created: pr_times[number]=max(pr_times.get(number,0),(finished-created).total_seconds())
            except Exception: pass
    flaky=sorted(name for tests_by_sha in signatures.values() for name,values in tests_by_sha.items() if {"passed","failed"}.issubset(values))
    avg_ci=round(sum(elapsed)/len(elapsed)/60,1) if elapsed else None
    avg_pr=round(sum(pr_times.values())/len(pr_times)/60,1) if pr_times else None
    m={"period_days":DAYS,"generated_at_utc":dt.datetime.now(dt.timezone.utc).isoformat(),
       "build_success_rate_percent":pct(len(ok),len(done)),"build_runs_completed":len(done),
       "test_success_rate_percent":pct(len(test_ok),len(tests)),"test_runs_completed":len(tests),
       "flaky_tests":flaky,"flaky_test_count":len(flaky),"average_ci_duration_minutes":avg_ci,
       "failed_job_distribution":dict(job_fail.most_common()),"failed_job_total":sum(job_fail.values()),
       "main_test_failure_rate_percent":pct(len(main_failed),len(main_tests)),"main_test_runs_completed":len(main_tests),
       "pr_validation_average_minutes":avg_pr,"prs_with_completed_test_run":len(pr_times)}
    def show(v,s=""): return "n/a (insufficient data)" if v is None else f"{v}{s}"
    lines=["# CI/CD health report","",f"Period: last {DAYS} days. Generated: {m['generated_at_utc']}.","",
      "| Metric | Result | Sample / notes |","|---|---:|---|",
      f"| Build Success Rate | {show(m['build_success_rate_percent'],'%')} | {len(ok)}/{len(done)} completed push/PR/merge-queue runs |",
      f"| Test Success Rate | {show(m['test_success_rate_percent'],'%')} | {len(test_ok)}/{len(tests)} completed Automated tests runs |",
      f"| Flaky Tests | {len(flaky)} detected | Same test has pass and fail outcomes for the same commit SHA |",
      f"| Average CI Duration | {show(avg_ci,' min')} | Completed workflow runs |",
      f"| Failed Job Distribution | {sum(job_fail.values())} failures | Grouped by job name; see JSON artifact |",
      f"| Regression Rate (proxy) | {show(m['main_test_failure_rate_percent'],'%')} | Failed main-branch test runs / completed main test runs |",
      f"| PR Validation Time | {show(avg_pr,' min')} average | PR creation to associated test completion; {len(pr_times)} PRs |","",
      "## Interpretation and limitations","",
      "- Build success covers completed push, pull-request and merge-queue runs; scheduled/manual runs are excluded.",
      "- Test success covers the Automated tests workflow (unit, API and database jobs).",
      "- Flaky detection requires contradictory TRX outcomes for the same test on the same commit SHA. Missing/expired artifacts reduce detection.",
      "- Regression Rate is an operational proxy, not causal attribution to a specific PR; it is not a merge gate.",
      "- PR validation time starts at PR creation, not the latest commit or review-ready time.",
      "- All metrics are observational. No metric in this report blocks merging.","","Failed jobs:"]
    lines.extend([f"- {n}: {c}" for n,c in job_fail.most_common()] or ["- None in the selected period."])
    os.makedirs("ci-analytics",exist_ok=True)
    with open("ci-analytics/metrics.json","w",encoding="utf-8") as f: json.dump(m,f,ensure_ascii=False,indent=2)
    with open("ci-analytics/report.md","w",encoding="utf-8") as f: f.write("\n".join(lines)+"\n")
    print("\n".join(lines))
if __name__=="__main__": main()
