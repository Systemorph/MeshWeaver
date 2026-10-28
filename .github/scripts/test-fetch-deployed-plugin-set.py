#!/usr/bin/env python3
"""Self-test for fetch-deployed-plugin-set.sh and github-api-get.py against a FAKE GitHub API.

Negative controls for the 2026-10-27 shape: a rate-limited lookup must end RED as NO VERDICT
(exit 3) and never as "the deployed plugin set is not available"; only a genuine 404 skips a
candidate; a 200 path still chooses and downloads the newest promoted complete set. The fake API is
a local HTTP server (GITHUB_API_URL), and `gh run download` is a stub on PATH — nothing reaches
GitHub.
"""

import http.server
import json
import os
from pathlib import Path
import subprocess
import tempfile
import threading
import time
import unittest

HERE = Path(__file__).resolve().parent
SCRIPT = HERE / "fetch-deployed-plugin-set.sh"
HELPER = HERE / "github-api-get.py"
REPO = "Systemorph/MeshWeaver"
MODULES = ["MeshWeaver.AI", "MeshWeaver.Markdown.Collaboration", "MeshWeaver.Maps",
           "MeshWeaver.Payments.Stripe"]
RATE_LIMITED = (403, {"x-ratelimit-remaining": "0",
                      "x-ratelimit-reset": str(int(time.time()) + 3600)},
                {"message": "API rate limit exceeded for installation ID 1."})


def ok(body):
    return 200, {}, body


def not_found():
    return 404, {}, {"message": "Not Found"}


def run_json(run_id, path=".github/workflows/main-cd.yml"):
    return ok({"id": run_id, "path": path, "status": "completed", "head_sha": f"{run_id:040d}"})


def complete_artifacts():
    return ok({"artifacts": [{"name": f"module-bundle-{m}", "expired": False} for m in MODULES]})


def promoted():
    return ok({"jobs": [{"name": "Promote: the set", "conclusion": "success"}]})


def routes_for(*run_ids, overrides=None):
    routes = {
        f"/repos/{REPO}/actions/artifacts": ok({"artifacts": [
            {"expired": False, "workflow_run": {"id": rid, "head_branch": "main"}} for rid in run_ids]}),
    }
    for rid in run_ids:
        routes[f"/repos/{REPO}/actions/runs/{rid}"] = run_json(rid)
        routes[f"/repos/{REPO}/actions/runs/{rid}/artifacts"] = complete_artifacts()
        routes[f"/repos/{REPO}/actions/runs/{rid}/jobs"] = promoted()
    routes.update(overrides or {})
    return routes


class FakeGitHub:
    def __init__(self, routes):
        self.routes = routes
        self.hits = []
        fake = self

        class Handler(http.server.BaseHTTPRequestHandler):
            def do_GET(self):  # noqa: N802 - http.server API
                path = self.path.split("?", 1)[0]
                fake.hits.append(path)
                status, headers, body = fake.routes.get(path, not_found())
                payload = json.dumps(body).encode("utf-8")
                self.send_response(status)
                for key, value in headers.items():
                    self.send_header(key, value)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(payload)))
                self.end_headers()
                self.wfile.write(payload)

            def log_message(self, *args):
                pass

        self.server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.url = f"http://127.0.0.1:{self.server.server_address[1]}"
        threading.Thread(target=self.server.serve_forever, daemon=True).start()

    def close(self):
        self.server.shutdown()
        self.server.server_close()


def run_fetch(routes, budget_seconds=20):
    fake = FakeGitHub(routes)
    try:
        with tempfile.TemporaryDirectory() as tmp:
            bin_dir = Path(tmp) / "bin"
            bin_dir.mkdir()
            gh = bin_dir / "gh"
            # `gh run download <run> -R <repo> -n module-bundle-<M> -D <dir>` writes one bundle.
            gh.write_text("#!/usr/bin/env bash\n"
                          "[ \"$1 $2\" = \"run download\" ] || { echo \"unexpected gh $*\" >&2; exit 9; }\n"
                          "while [ $# -gt 0 ]; do case \"$1\" in -n) n=$2; shift;; -D) d=$2; shift;; esac; shift; done\n"
                          "mkdir -p \"$d\" && : > \"$d/${n#module-bundle-}.1.0.0.module.nupkg\"\n")
            gh.chmod(0o755)
            env = {
                "PATH": f"{bin_dir}{os.pathsep}{os.environ['PATH']}",
                "HOME": tmp,
                "GITHUB_API_URL": fake.url,
                "GH_TOKEN": "fake-token",
                "GITHUB_API_DEADLINE": str(int(time.time()) + budget_seconds),
                "NO_PROXY": "*",
            }
            proc = subprocess.run(["bash", str(SCRIPT), str(Path(tmp) / "out"), REPO], env=env,
                                  capture_output=True, text=True, timeout=120)
            bundles = sorted(p.name for p in (Path(tmp) / "out").rglob("*.module.nupkg"))
            return proc, bundles, list(fake.hits)
    finally:
        fake.close()


class FetchDeployedPluginSetTest(unittest.TestCase):
    def test_200_chooses_the_newest_promoted_complete_set(self):
        proc, bundles, _ = run_fetch(routes_for(101, 102))
        self.assertEqual(proc.returncode, 0, proc.stderr + proc.stdout)
        self.assertIn("deployed plugin set: main-cd run 102", proc.stdout)
        self.assertEqual(len(bundles), 4)

    def test_404_on_a_candidate_skips_it_and_the_next_one_is_chosen(self):
        proc, bundles, _ = run_fetch(routes_for(101, 102, overrides={
            f"/repos/{REPO}/actions/runs/102": not_found()}))
        self.assertEqual(proc.returncode, 0, proc.stderr + proc.stdout)
        self.assertIn("deployed plugin set: main-cd run 101", proc.stdout)

    def test_rate_limited_candidate_is_no_verdict_never_a_skip(self):
        # The pre-fix `|| continue` would have skipped 102 and chosen 101 — or, with every
        # candidate limited, reported "not available". Both are verdicts over evidence never read.
        for target in ("", "/artifacts", "/jobs"):
            with self.subTest(lookup=target or "run"):
                proc, bundles, _ = run_fetch(routes_for(101, 102, overrides={
                    f"/repos/{REPO}/actions/runs/102{target}": RATE_LIMITED}))
                self.assertEqual(proc.returncode, 3, proc.stderr + proc.stdout)
                self.assertIn("NO VERDICT — GitHub API rate-limited until", proc.stderr)
                self.assertNotIn("deployed plugin set is not available", proc.stderr + proc.stdout)
                self.assertEqual(bundles, [])

    def test_rate_limited_listing_is_no_verdict(self):
        proc, _, hits = run_fetch(routes_for(101, overrides={
            f"/repos/{REPO}/actions/artifacts": RATE_LIMITED}))
        self.assertEqual(proc.returncode, 3, proc.stderr + proc.stdout)
        self.assertIn("NO VERDICT", proc.stderr)
        # A reset an hour away is beyond the budget: it is reported at once, not slept on.
        self.assertEqual(hits, [f"/repos/{REPO}/actions/artifacts"])

    def test_5xx_past_the_budget_is_no_verdict(self):
        proc, _, _ = run_fetch(routes_for(101, overrides={
            f"/repos/{REPO}/actions/runs/101/jobs": (502, {}, {"message": "Bad Gateway"})}),
            budget_seconds=1)
        self.assertEqual(proc.returncode, 3, proc.stderr + proc.stdout)
        self.assertIn("NO VERDICT — GitHub API unavailable (HTTP 502", proc.stderr)

    def test_a_deliberate_refusal_is_red_but_not_no_verdict(self):
        proc, _, _ = run_fetch(routes_for(101, overrides={
            f"/repos/{REPO}/actions/runs/101": (401, {}, {"message": "Bad credentials"})}))
        self.assertEqual(proc.returncode, 1, proc.stderr + proc.stdout)
        self.assertIn("HTTP 401", proc.stderr)

    def test_no_promoted_set_is_still_red_as_before(self):
        proc, _, _ = run_fetch(routes_for(101, overrides={
            f"/repos/{REPO}/actions/runs/101/jobs": ok({"jobs": [
                {"name": "Promote: the set", "conclusion": "failure"}]})}))
        self.assertEqual(proc.returncode, 1, proc.stderr + proc.stdout)
        self.assertIn("the deployed plugin set is not available", proc.stdout + proc.stderr)


if __name__ == "__main__":
    unittest.main()
