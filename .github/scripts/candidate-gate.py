#!/usr/bin/env python3
"""candidate-gate.py — GATE 1 of the Candidate Release Protocol.

Core has built an image. That image is a CANDIDATE, not a release. This script asks every node
repo "does your content still compile against THIS build?" and reports the answer for ALL of them,
so `main-cd.yml` can decide whether the candidate may be promoted to the tag installs self-update
from (`memex-portal-ai:<version>`) or must be published as a preview instead.

    .github/scripts/candidate-gate.py \
        --repos Systemorph/MeshWeaver.SocialMedia,Systemorph/MeshWeaver.Plugins,... \
        --workflow ci.yml --digest sha256:... --core-sha <full-sha> --candidate-id <id>

Exit code is ALWAYS 0 — this is a measurement, not the verdict. The verdict is `clean=true|false`
on $GITHUB_OUTPUT (plus a full markdown report on $GITHUB_STEP_SUMMARY and a JSON result file).
A script that exited non-zero on a broken dependent would stop the run before it could publish the
preview and name what broke, which is the one thing the protocol requires it to do.

────────────────────────────────────────────────────────────────────────────────────────────────
WHY THIS EXISTS (the gap it closes)

Core deleted `AddTracking` on MessageHubConfiguration (eafd353ed). Three NodeTypes in
MeshWeaver.SocialMedia still called it. EVERYTHING stayed green:

  * `dotnet build -c Release -warnaserror` — node source is <None> content, never compiled;
  * the test suite — nothing compiles in-mesh source;
  * the node repo's own "Compile every NodeType (vs core)" — it compiles against a PINNED core
    image digest that still had the method;
  * that job's triggers (push/pull_request/workflow_dispatch) — they fire when the PLUGIN
    changes, NEVER when core does.

Production then got `CompileError` → dependents `UpstreamFailed` → DynamicTypePreWarmer REFUSING
READINESS → hubs never activate → every request burns the 60 s activation budget.

🚨 THE PIN IS CORRECT AND STAYS. A moving `:latest` makes two runs of identical code disagree
(MeshWeaver.Plugins, 2026-08-04: main green on sha256:10462f9a, the same code red on
sha256:d8895c8a an hour later). The defect was never the pin — it is that NOTHING REBUILDS A
DEPENDENT WHEN THE THING IT IS PINNED TO MOVES. So this script does not touch anyone's pin; it
passes an OVERRIDE for one run, and the repo's committed pin remains its default.

────────────────────────────────────────────────────────────────────────────────────────────────
FAILURE SEMANTICS — breadth-complete, never stop at the first failure

Every repo is dispatched, every repo is waited for, every repo is reported. In the incident,
`SocialMedia/Post`, `Profile` and `PostsHub` each carried the identical broken call and only
`Post` was ever reported — the other two were `UpstreamFailed: blocked by SocialMedia/Post`, so
two of three bugs stayed invisible until the first was fixed. The same mistake at repo scale
("stop at the first red repo") would hide whole repositories, so it is structurally impossible
here: dispatch ALL, then poll ALL.

Each repo lands in exactly one outcome:

  compiled — its run went green against the candidate.
  failed   — its run went red; the report names the failing jobs and every diagnostic line the
             log yields (compile-check.py's regression list, mw-plugin-test's RED lines, CS errors).
  blocked  — it could not be attempted at all: the workflow does not accept the override inputs
             (not yet wired — see Doc/Architecture/CandidateReleaseGate), no access with this
             token, the run never appeared, or it timed out. NEVER treated as a pass.

A run is clean only when `failed` and `blocked` are both empty.
"""
from __future__ import annotations

import argparse
import io
import json
import os
import re
import sys
import time
import urllib.error
import urllib.request
import zipfile

API = "https://api.github.com"

# Lines worth lifting out of a failed node-repo log. Deliberately a SMALL, tolerant set: the point
# is to name what broke without becoming a parser for three tools' output formats. Whatever the
# patterns miss, the run URL in the report still carries — a report that says "red, here is where"
# beats a brittle extractor that says nothing when a format shifts.
DIAGNOSTIC_PATTERNS = [
    re.compile(r"NON-allowlisted NodeType\(s\) FAIL"),   # compile-check.py's regression header
    re.compile(r"CHANGED failure fingerprint"),          # compile-check.py's ratchet drift
    re.compile(r"now COMPILE CLEAN"),                    # compile-check.py's stale-allow ratchet
    re.compile(r"^\s+- \S+/\S+"),                        # the type names under those headers
    re.compile(r"error CS\d+"),                          # raw Roslyn diagnostics
    re.compile(r"^\s*\[?RED\]?\s"),                      # mw-plugin-test per-type verdict
    re.compile(r"GATE FAILED"),                          # mw-plugin-test verdict line
]

MAX_DIAGNOSTIC_LINES = 40  # per repo, in the rendered report


def api(path: str, token: str, method: str = "GET", body: dict | None = None):
    """One GitHub REST call. Returns (status, parsed-json-or-bytes). Raises only on transport."""
    url = path if path.startswith("http") else f"{API}{path}"
    data = json.dumps(body).encode() if body is not None else None
    request = urllib.request.Request(url, data=data, method=method)
    request.add_header("Authorization", f"Bearer {token}")
    request.add_header("Accept", "application/vnd.github+json")
    request.add_header("X-GitHub-Api-Version", "2022-11-28")
    if data is not None:
        request.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(request) as response:
            raw = response.read()
            if response.headers.get("Content-Type", "").startswith("application/json"):
                return response.status, json.loads(raw or b"null")
            return response.status, raw
    except urllib.error.HTTPError as error:
        raw = error.read()
        try:
            return error.code, json.loads(raw or b"null")
        except json.JSONDecodeError:
            return error.code, {"message": raw.decode(errors="replace")[:500]}


def latest_run_id(repo: str, workflow: str, token: str) -> int:
    """The newest run id for the workflow, or 0. The baseline half of run correlation."""
    status, payload = api(f"/repos/{repo}/actions/workflows/{workflow}/runs?per_page=1", token)
    if status != 200 or not isinstance(payload, dict):
        return 0
    runs = payload.get("workflow_runs") or []
    return int(runs[0]["id"]) if runs else 0


def dispatch(repo: str, workflow: str, ref: str, inputs: dict, token: str) -> str | None:
    """Fire the workflow. Returns None on success, else a human-readable refusal."""
    status, payload = api(
        f"/repos/{repo}/actions/workflows/{workflow}/dispatches", token, "POST",
        {"ref": ref, "inputs": inputs})
    if status == 204:
        return None
    message = (payload or {}).get("message", "") if isinstance(payload, dict) else str(payload)
    if status == 422 and "Unexpected inputs" in message:
        return (f"{repo} does not accept the candidate override inputs yet "
                f"({message}) — apply the node-repo patch in Doc/Architecture/CandidateReleaseGate")
    if status in (403, 404):
        return (f"{repo}: HTTP {status} dispatching {workflow} ({message or 'no access'}) — the "
                f"dispatch token needs `actions: write` + `contents: read` on this repository")
    return f"{repo}: HTTP {status} dispatching {workflow} ({message})"


def find_run(repo: str, workflow: str, baseline: int, candidate_id: str, token: str) -> dict | None:
    """The run our dispatch created.

    Correlated two ways, because either alone is weak: the run NAME carries the candidate id (the
    node repo's `run-name:` puts it there — exact), and the run id must exceed the pre-dispatch
    baseline (so a stale run carrying an older candidate id can never match). Falls back to the
    newest new dispatch run when `run-name` has not been wired yet, which keeps the gate working
    on a repo that applied only half the patch — the name match is preferred, never required.
    """
    status, payload = api(
        f"/repos/{repo}/actions/workflows/{workflow}/runs?event=workflow_dispatch&per_page=30",
        token)
    if status != 200 or not isinstance(payload, dict):
        return None
    fresh = [r for r in (payload.get("workflow_runs") or []) if int(r["id"]) > baseline]
    named = [r for r in fresh if candidate_id in (r.get("name") or "")]
    if named:
        return named[0]
    # `workflow_runs` is newest-first, so [0] is the newest run that did not exist before we
    # dispatched — the best available guess when the name carries no candidate id.
    return fresh[0] if fresh else None


def failure_detail(repo: str, run: dict, token: str) -> tuple[list[str], list[str]]:
    """(failed job names, diagnostic lines) for a red run."""
    jobs: list[str] = []
    status, payload = api(f"/repos/{repo}/actions/runs/{run['id']}/jobs?per_page=100", token)
    if status == 200 and isinstance(payload, dict):
        jobs = [j["name"] for j in payload.get("jobs", [])
                if j.get("conclusion") not in (None, "success", "skipped")]

    lines: list[str] = []
    status, payload = api(f"/repos/{repo}/actions/runs/{run['id']}/logs", token)
    if status == 200 and isinstance(payload, (bytes, bytearray)):
        try:
            with zipfile.ZipFile(io.BytesIO(payload)) as archive:
                for name in archive.namelist():
                    if not name.endswith(".txt"):
                        continue
                    for raw in archive.read(name).decode(errors="replace").splitlines():
                        # Strip the ISO timestamp Actions prefixes onto every log line.
                        line = re.sub(r"^\S+Z\s", "", raw).rstrip()
                        if line and any(p.search(line) for p in DIAGNOSTIC_PATTERNS):
                            if line not in lines:
                                lines.append(line)
        except zipfile.BadZipFile:
            lines.append("(could not read the run's logs — open the run to see the failure)")
    return jobs, lines


def verify(repo: str, workflow: str, ref: str, inputs: dict, candidate_id: str,
           deadline: float, token: str) -> dict:
    """Dispatch is split from polling by the caller: dispatch ALL repos, then poll ALL repos."""
    baseline = latest_run_id(repo, workflow, token)
    refusal = dispatch(repo, workflow, ref, inputs, token)
    if refusal:
        return {"repo": repo, "outcome": "blocked", "reason": refusal}
    return {"repo": repo, "outcome": "pending", "baseline": baseline,
            "candidate_id": candidate_id, "deadline": deadline}


def poll(state: dict, workflow: str, token: str, poll_seconds: int) -> dict:
    """Block until this repo's run completes, its deadline passes, or it never appears."""
    repo, baseline = state["repo"], state["baseline"]
    run = None
    while True:
        # `or run` keeps the last sighting when a listing call hiccups — losing the run to one
        # flaky read would report a green repo as "never appeared", the worst kind of wrong.
        run = find_run(repo, workflow, baseline, state["candidate_id"], token) or run
        if run and run.get("status") == "completed":
            break
        if time.time() >= state["deadline"]:
            break
        time.sleep(poll_seconds)
    if run is None:
        return {"repo": repo, "outcome": "blocked",
                "reason": f"{repo}: the dispatched {workflow} run never appeared within the budget"}
    url = run.get("html_url", "")
    if run.get("status") != "completed":
        return {"repo": repo, "outcome": "blocked", "run": url,
                "reason": f"{repo}: its run did not finish inside the gate's budget"}
    conclusion = run.get("conclusion")
    if conclusion == "success":
        return {"repo": repo, "outcome": "compiled", "run": url}
    if conclusion in ("cancelled", "skipped"):
        return {"repo": repo, "outcome": "blocked", "run": url,
                "reason": f"{repo}: its run was {conclusion} — the candidate was never verified"}
    jobs, lines = failure_detail(repo, run, token)
    return {"repo": repo, "outcome": "failed", "run": url, "jobs": jobs, "diagnostics": lines}


def render(results: list[dict], digest: str, core_sha: str) -> str:
    """The report. Every repo appears, in every outcome — that is the whole point."""
    compiled = [r for r in results if r["outcome"] == "compiled"]
    failed = [r for r in results if r["outcome"] == "failed"]
    blocked = [r for r in results if r["outcome"] == "blocked"]
    clean = not failed and not blocked

    out = [f"## Candidate gate — core `{core_sha[:7]}`",
           "",
           f"Candidate framework image: `{digest}`",
           "",
           f"**{'CLEAN — the candidate may be promoted' if clean else 'BROKEN — preview only'}**"
           f"  ·  {len(compiled)} compiled · {len(failed)} failed · {len(blocked)} blocked",
           "",
           "| Node repo | Outcome | Run |",
           "|---|---|---|"]
    for result in results:
        icon = {"compiled": "✅ compiled", "failed": "❌ failed", "blocked": "🚧 blocked"}[result["outcome"]]
        run = f"[run]({result['run']})" if result.get("run") else "—"
        out.append(f"| `{result['repo']}` | {icon} | {run} |")

    for result in failed:
        out += ["", f"### ❌ {result['repo']}"]
        if result.get("jobs"):
            out.append(f"Failed jobs: {', '.join('`' + j + '`' for j in result['jobs'])}")
        if result.get("diagnostics"):
            out += ["", "```", *result["diagnostics"][:MAX_DIAGNOSTIC_LINES], "```"]
            if len(result["diagnostics"]) > MAX_DIAGNOSTIC_LINES:
                out.append(f"_…{len(result['diagnostics']) - MAX_DIAGNOSTIC_LINES} more lines "
                           f"in the run log._")
        else:
            out.append("No diagnostic lines matched — open the run for the full log.")

    for result in blocked:
        out += ["", f"### 🚧 {result['repo']} — NOT VERIFIED", "", result["reason"],
                "", "A blocked repo is never a pass: the candidate was not proven against it."]
    return "\n".join(out) + "\n"


def main() -> int:
    parser = argparse.ArgumentParser(description="Gate 1 of the Candidate Release Protocol.")
    parser.add_argument("--repos", required=True, help="comma-separated owner/name list")
    parser.add_argument("--workflow", default="ci.yml", help="workflow file name in each node repo")
    parser.add_argument("--ref", default="main", help="ref to run in each node repo")
    parser.add_argument("--digest", required=True, help="candidate framework image digest")
    parser.add_argument("--core-sha", required=True, help="the core commit this candidate is")
    parser.add_argument("--candidate-id", required=True, help="correlation id for this candidate")
    parser.add_argument("--timeout-minutes", type=int, default=45)
    parser.add_argument("--poll-seconds", type=int, default=20)
    parser.add_argument("--result-json", default="candidate-gate.json")
    parser.add_argument("--report-md", default="candidate-gate.md",
                        help="the rendered report; `preview` posts this file onto the ci-failure issue")
    args = parser.parse_args()

    token = os.environ.get("GH_TOKEN", "")
    if not token:
        print("::error::GH_TOKEN is empty — candidate-gate.py cannot dispatch anything")
        return 1

    repos = [r.strip() for r in args.repos.split(",") if r.strip()]
    inputs = {
        # The framework build to verify against, overriding the repo's committed MW_IMAGE_DIGEST
        # pin for THIS run only. The pin stays the repo's default for its own PRs (gate 2).
        "core_image_digest": args.digest,
        # For a repo that builds core from source instead of unpacking the image (SocialMedia).
        "core_ref": args.core_sha,
        # Correlation: the node repo puts it in `run-name:` and in its `concurrency:` group, so
        # this run is findable AND cannot be cancelled by an unrelated push to that repo's main.
        "candidate_id": args.candidate_id,
    }

    deadline = time.time() + args.timeout_minutes * 60
    print(f"Dispatching {args.workflow} to {len(repos)} node repo(s) against {args.digest}")
    states = [verify(repo, args.workflow, args.ref, inputs, args.candidate_id, deadline, token)
              for repo in repos]

    results = []
    for state in states:
        if state["outcome"] != "pending":
            print(f"  {state['repo']}: {state['outcome']} — {state.get('reason', '')}")
            results.append(state)
            continue
        print(f"  {state['repo']}: waiting for its run…")
        result = poll(state, args.workflow, token, args.poll_seconds)
        print(f"  {state['repo']}: {result['outcome']} {result.get('run', '')}")
        results.append(result)

    report = render(results, args.digest, args.core_sha)
    print(report)
    clean = all(r["outcome"] == "compiled" for r in results)

    with open(args.result_json, "w", encoding="utf-8") as handle:
        json.dump({"clean": clean, "digest": args.digest, "coreSha": args.core_sha,
                   "results": results}, handle, indent=2)
    with open(args.report_md, "w", encoding="utf-8") as handle:
        handle.write(report)
    if summary := os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(summary, "a", encoding="utf-8") as handle:
            handle.write(report)
    if output := os.environ.get("GITHUB_OUTPUT"):
        with open(output, "a", encoding="utf-8") as handle:
            handle.write(f"clean={'true' if clean else 'false'}\n")
    # 0 even when the closure is broken — see the module docstring. `promote` reads `clean`.
    return 0


if __name__ == "__main__":
    sys.exit(main())
