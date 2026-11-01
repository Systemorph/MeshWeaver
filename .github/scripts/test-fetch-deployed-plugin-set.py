#!/usr/bin/env python3
"""Self-test for fetch-deployed-plugin-set.sh and github-api-get.py against a FAKE GitHub API.

Negative controls for the 2026-10-27 shape: a rate-limited lookup must end RED as NO VERDICT
(exit 3) and never as "the deployed plugin set is not available"; only a genuine 404 skips a
candidate; a 200 path still chooses and downloads the newest promoted complete set. The fake API is
a local HTTP server (GITHUB_API_URL) — the artifact downloads included: each `…/artifacts/<id>/zip`
answers a 302 to the server's own `/blob/<id>`, as GitHub's does to signed storage. A `gh` on PATH
fails loudly if anything still calls it (#7448: `gh run download` left a rate limit unclassified).
Nothing reaches GitHub.
"""

import http.server
import io
import json
import os
from pathlib import Path
import subprocess
import tempfile
import threading
import time
import unittest
import zipfile

HERE = Path(__file__).resolve().parent
SCRIPT = HERE / "fetch-deployed-plugin-set.sh"
HELPER = HERE / "github-api-get.py"
REPO = "Systemorph/MeshWeaver"
MODULES = ["MeshWeaver.AI", "MeshWeaver.Markdown.Collaboration", "MeshWeaver.Maps",
           "MeshWeaver.Payments.Stripe"]
# The sealed `plugins` publication carries far more than the four legacy modules (#7409) — the
# full set the ladder must link, as `published-module-set` hands it on (bundle file names).
PUBLICATION = ["AI.module.nupkg", "Essentials.module.nupkg", "Maps.module.nupkg", "Stripe.module.nupkg",
               "Mail.MicrosoftGraph.module.nupkg", "Teams.module.nupkg", "Mcp.module.nupkg"]
FULL_SET = "published-module-set"
RATE_LIMITED = (403, {"x-ratelimit-remaining": "0",
                      "x-ratelimit-reset": str(int(time.time()) + 3600)},
                {"message": "API rate limit exceeded for installation ID 1."})


def ok(body):
    return 200, {}, body


def not_found():
    return 404, {}, {"message": "Not Found"}


def run_json(run_id, path=".github/workflows/main-cd.yml"):
    return ok({"id": run_id, "path": path, "status": "completed", "head_sha": f"{run_id:040d}"})


def artifact_id(run_id, name):
    """A stable id per (run, artifact name) — what the download route is keyed on."""
    names = [f"module-bundle-{m}" for m in MODULES] + [FULL_SET]
    return run_id * 100 + names.index(name)


def complete_artifacts(run_id, full=True):
    names = [f"module-bundle-{m}" for m in MODULES] + ([FULL_SET] if full else [])
    return ok({"artifacts": [{"id": artifact_id(run_id, n), "name": n, "expired": False} for n in names]})


def zip_of(files):
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w") as z:
        for name, data in files.items():
            z.writestr(name, data)
    return buf.getvalue()


def download_routes(run_id, publication, delivered):
    """`…/artifacts/<id>/zip` → 302 → `/blob/<id>` carrying the artifact's files, per artifact."""
    routes = {}
    contents = {FULL_SET: {f"{FULL_SET}.index": "\n".join(publication) + "\n",
                           **{b: "" for b in delivered}}}
    for m in MODULES:
        contents[f"module-bundle-{m}"] = {f"{m}.1.0.0.module.nupkg": ""}
    for name, files in contents.items():
        aid = artifact_id(run_id, name)
        routes[f"/repos/{REPO}/actions/artifacts/{aid}/zip"] = (302, {"Location": f"/blob/{aid}"}, None)
        routes[f"/blob/{aid}"] = (200, {}, zip_of(files))
    return routes


def promoted():
    return ok({"jobs": [{"name": "Promote: the set", "conclusion": "success"}]})


def listing(*run_ids):
    return ok({"artifacts": [
        {"expired": False, "workflow_run": {"id": rid, "head_branch": "main"}} for rid in run_ids]})


def routes_for(*run_ids, overrides=None, full=True, publication=None, delivered=None):
    """Runs that hold the full `published-module-set` (full=True, every run after #7409) or only the
    four legacy per-module artifacts (full=False, a run from before it)."""
    routes = {
        f"/repos/{REPO}/actions/artifacts": listing(*run_ids),
        f"/repos/{REPO}/actions/artifacts?name={FULL_SET}": listing(*(run_ids if full else ())),
    }
    publication = PUBLICATION if publication is None else publication
    delivered = publication if delivered is None else delivered
    for rid in run_ids:
        routes[f"/repos/{REPO}/actions/runs/{rid}"] = run_json(rid)
        routes[f"/repos/{REPO}/actions/runs/{rid}/artifacts"] = complete_artifacts(rid, full)
        routes[f"/repos/{REPO}/actions/runs/{rid}/jobs"] = promoted()
        routes.update(download_routes(rid, publication, delivered))
    routes.update(overrides or {})
    return routes


class FakeGitHub:
    def __init__(self, routes):
        self.routes = routes
        self.hits = []
        self.auth = {}      # path -> the Authorization header it was asked with ("" when none)
        fake = self

        class Handler(http.server.BaseHTTPRequestHandler):
            def do_GET(self):  # noqa: N802 - http.server API
                path, _, query = self.path.partition("?")
                fake.hits.append(path)
                fake.auth[path] = self.headers.get("Authorization") or ""
                # A route may name the `name=` filter of an artifact listing; otherwise path only.
                name = next((q[5:] for q in query.split("&") if q.startswith("name=")), None)
                status, headers, body = fake.routes.get(f"{path}?name={name}", None) or \
                    fake.routes.get(path, not_found())
                is_bytes = isinstance(body, bytes)
                payload = body if is_bytes else (b"" if body is None else json.dumps(body).encode("utf-8"))
                self.send_response(status)
                for key, value in headers.items():
                    self.send_header(key, value)
                self.send_header("Content-Type", "application/zip" if is_bytes else "application/json")
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


def run_fetch(routes, budget_seconds=20, with_fake=None):
    """`routes` carry the downloads too (routes_for's `publication` is the index the full set
    carries, `delivered` the bundles its download really yields; they differ only to prove the
    ladder refuses a missing module). `with_fake` receives the fake server after the run."""
    fake = FakeGitHub(routes)
    try:
        with tempfile.TemporaryDirectory() as tmp:
            bin_dir = Path(tmp) / "bin"
            bin_dir.mkdir()
            gh = bin_dir / "gh"
            # Every read goes through github-api-get.py now; a `gh` call is a regression (#7448).
            gh.write_text("#!/usr/bin/env bash\necho \"unexpected gh $*\" >&2\nexit 9\n")
            gh.chmod(0o755)
            env = {
                "PATH": f"{bin_dir}{os.pathsep}{os.environ['PATH']}",
                "HOME": tmp,
                "GITHUB_API_URL": fake.url,
                "GH_TOKEN": "fake-token",
                "GITHUB_API_DEADLINE": str(int(time.time()) + budget_seconds),
                "NO_PROXY": "*",
                "TMPDIR": tmp,
            }
            proc = subprocess.run(["bash", str(SCRIPT), str(Path(tmp) / "out"), REPO], env=env,
                                  capture_output=True, text=True, timeout=120)
            bundles = sorted(p.name for p in (Path(tmp) / "out").rglob("*.module.nupkg"))
            if with_fake:
                with_fake(fake)
            return proc, bundles, list(fake.hits)
    finally:
        fake.close()


class FetchDeployedPluginSetTest(unittest.TestCase):
    def test_200_chooses_the_newest_promoted_complete_set(self):
        proc, bundles, _ = run_fetch(routes_for(101, 102))
        self.assertEqual(proc.returncode, 0, proc.stderr + proc.stdout)
        self.assertIn("deployed plugin set: main-cd run 102", proc.stdout)
        # The WHOLE publication, not the four legacy modules (#7409).
        self.assertEqual(bundles, sorted(PUBLICATION))
        self.assertIn(f"ladder coverage: {len(PUBLICATION)} of {len(PUBLICATION)}", proc.stdout)

    def test_a_module_in_the_publication_but_not_on_the_ladder_is_red(self):
        # #7409: the ladder linked four modules and reported green over every other one. A module
        # the publication's index lists and no bundle provides must now turn the ladder RED.
        proc, bundles, _ = run_fetch(routes_for(101, delivered=PUBLICATION[:-1]))
        self.assertEqual(proc.returncode, 1, proc.stderr + proc.stdout)
        self.assertIn(f"'{PUBLICATION[-1]}' is in the sealed publication but NOT on the ladder",
                      proc.stdout + proc.stderr)
        self.assertNotIn("deployed plugin set: main-cd run", proc.stdout)

    def test_a_bundle_the_publication_never_sealed_is_red(self):
        proc, _, _ = run_fetch(routes_for(101, delivered=PUBLICATION + ["Stray.module.nupkg"]))
        self.assertEqual(proc.returncode, 1, proc.stderr + proc.stdout)
        self.assertIn("'Stray.module.nupkg' is on the ladder but NOT in the sealed publication",
                      proc.stdout + proc.stderr)

    def test_an_empty_publication_index_is_red_never_a_pass_over_nothing(self):
        proc, _, _ = run_fetch(routes_for(101, publication=[], delivered=[]))
        self.assertEqual(proc.returncode, 1, proc.stderr + proc.stdout)
        self.assertIn("lists no module", proc.stdout + proc.stderr)

    def test_before_any_full_set_exists_the_legacy_four_are_linked_and_the_gap_is_said(self):
        proc, bundles, _ = run_fetch(routes_for(101, full=False))
        self.assertEqual(proc.returncode, 0, proc.stderr + proc.stdout)
        self.assertEqual(len(bundles), 4)
        self.assertIn("LADDER COVERAGE NOT MEASURED", proc.stdout + proc.stderr)

    def test_once_a_full_set_exists_a_legacy_only_run_is_never_chosen(self):
        # 102 is newer and holds only the legacy four; 101 holds the full set. The full set wins.
        routes = routes_for(101, 102)
        routes[f"/repos/{REPO}/actions/artifacts?name={FULL_SET}"] = listing(101)
        routes[f"/repos/{REPO}/actions/runs/102/artifacts"] = complete_artifacts(102, full=False)
        proc, bundles, _ = run_fetch(routes)
        self.assertEqual(proc.returncode, 0, proc.stderr + proc.stdout)
        self.assertIn("deployed plugin set: main-cd run 101", proc.stdout)
        self.assertEqual(bundles, sorted(PUBLICATION))
        self.assertNotIn("LADDER COVERAGE NOT MEASURED", proc.stdout + proc.stderr)

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
            f"/repos/{REPO}/actions/artifacts?name={FULL_SET}": RATE_LIMITED}))
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

    def test_rate_limited_download_is_no_verdict_naming_the_reset(self):
        # #7448: `gh run download` answered a rate limit with a bare non-zero exit and the script
        # printed "could not be downloaded — if gh printed a rate limit above, this is NO VERDICT",
        # leaving the classification to the reader. Now the download says NO VERDICT, and until when.
        aid = artifact_id(102, FULL_SET)
        proc, bundles, _ = run_fetch(routes_for(101, 102, overrides={
            f"/repos/{REPO}/actions/artifacts/{aid}/zip": RATE_LIMITED}))
        self.assertEqual(proc.returncode, 3, proc.stderr + proc.stdout)
        self.assertIn("NO VERDICT — GitHub API rate-limited until", proc.stderr)
        self.assertIn("HTTP 403 rate limit", proc.stderr)
        self.assertIn(f"downloading {FULL_SET} (artifact {aid}) of run 102", proc.stderr)
        self.assertNotIn("could not be downloaded", proc.stderr + proc.stdout)
        self.assertEqual(bundles, [])

    def test_rate_limited_legacy_download_is_no_verdict(self):
        aid = artifact_id(101, f"module-bundle-{MODULES[2]}")
        proc, _, _ = run_fetch(routes_for(101, full=False, overrides={
            f"/repos/{REPO}/actions/artifacts/{aid}/zip": (429, {"retry-after": "3600"},
                                                          {"message": "secondary rate limit"})}))
        self.assertEqual(proc.returncode, 3, proc.stderr + proc.stdout)
        self.assertIn("NO VERDICT", proc.stderr)
        self.assertIn("HTTP 429", proc.stderr)

    def test_5xx_on_the_download_past_the_budget_is_no_verdict(self):
        aid = artifact_id(101, FULL_SET)
        proc, _, _ = run_fetch(routes_for(101, overrides={
            f"/blob/{aid}": (503, {}, {"message": "Service Unavailable"})}), budget_seconds=1)
        self.assertEqual(proc.returncode, 3, proc.stderr + proc.stdout)
        self.assertIn("NO VERDICT — GitHub API unavailable (HTTP 503", proc.stderr)

    def test_an_artifact_gone_at_download_is_red_but_not_no_verdict(self):
        # Negative control: a 404 is a real answer (it expired between the listing and the download).
        aid = artifact_id(101, FULL_SET)
        proc, _, _ = run_fetch(routes_for(101, overrides={
            f"/repos/{REPO}/actions/artifacts/{aid}/zip": not_found()}))
        self.assertEqual(proc.returncode, 1, proc.stderr + proc.stdout)
        self.assertIn("answered 404 for its download", proc.stderr)
        self.assertNotIn("NO VERDICT", proc.stderr)

    def test_the_token_never_follows_the_redirect_to_storage(self):
        seen = {}
        aid = artifact_id(101, FULL_SET)
        proc, _, _ = run_fetch(routes_for(101), with_fake=lambda f: seen.update(f.auth))
        self.assertEqual(proc.returncode, 0, proc.stderr + proc.stdout)
        self.assertEqual(seen.get(f"/repos/{REPO}/actions/artifacts/{aid}/zip"), "Bearer fake-token")
        self.assertEqual(seen.get(f"/blob/{aid}"), "")

    def test_no_promoted_set_is_still_red_as_before(self):
        proc, _, _ = run_fetch(routes_for(101, overrides={
            f"/repos/{REPO}/actions/runs/101/jobs": ok({"jobs": [
                {"name": "Promote: the set", "conclusion": "failure"}]})}))
        self.assertEqual(proc.returncode, 1, proc.stderr + proc.stdout)
        self.assertIn("the deployed plugin set is not available", proc.stdout + proc.stderr)


if __name__ == "__main__":
    unittest.main()
