#!/usr/bin/env python3
"""EXECUTE the module publication lane's real steps against a stub registry. No credential, no cloud.

WHY
---
MeshWeaver#3878: the module registry hand-over used to run INSIDE `node-repo-module-pack.yml`'s
matrix leg, right after that module's own suite — while a sibling module's suite, the portal-host
shards, the NodeType compile-check and the Tests-area gate were still running. A module was being
served to every installation before the run that produced it had decided whether the source was
any good, and a red twenty minutes later changed nothing about what portals had adopted.

`node-repo-module-publish.yml` is where the hand-over went. Nothing on a core pull request calls
it — it is a `workflow_call` lane a satellite invokes — so the first execution of an edit to it
would otherwise be in a satellite's production publication. This harness executes it here.

BOTH DIRECTIONS, which is the acceptance criterion #3878 states:
  * green validation → the registry receives the EXACT staged bytes, once, at the right URL;
  * a later sibling / source failure → the registry receives NOTHING, even though the module was
    successfully packed and staged.

HOW IT STAYS HONEST
-------------------
* The steps are EXTRACTED FROM THE WORKFLOW by their `id:`, never copied here. A copy passes while
  the real thing rots. A step that cannot be found, or that grew a `${{ }}` expression this
  harness cannot supply, FAILS the harness rather than reporting a pass for something it did not
  run.
* The registry is a real HTTP server on localhost that RECORDS every request. "The registry was
  left untouched" is read off the request log, never off the script's own exit code.
* The MUTATIONS are run against the real files: a required dependency removed from the caller's
  `needs:`, and a status function added to the publishing job's `if:`. Both must be caught. A gate
  that has never been shown to fire is not a gate.
* `node-repo-module-pack.yml` is asserted structurally: under `publish-mode: staged` the in-leg
  hand-over must be unable to fire, and the staging step must be its exact complement. Without
  MeshWeaver#3878's change these assertions fail — which is the point.
"""

from __future__ import annotations

import http.server
import json
import os
import subprocess
import sys
import tempfile
import threading
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PUBLISH_WORKFLOW = ROOT / ".github/workflows/node-repo-module-publish.yml"
PACK_WORKFLOW = ROOT / ".github/workflows/node-repo-module-pack.yml"

failures: list[str] = []


def check(label: str, ok: bool, detail: str = ""):
    print(("  ok   " if ok else "  FAIL ") + label + (f"\n         {detail}" if detail and not ok else ""))
    if not ok:
        failures.append(label)


def die(msg: str):
    print(f"::error::{msg}")
    sys.exit(1)


# ── extraction ──────────────────────────────────────────────────────────────────────────────
def extract_step(workflow: Path, step_id: str) -> str:
    import yaml

    if not workflow.is_file():
        die(f"{workflow} does not exist — the publication lane this harness executes is missing, so "
            "there is nothing to prove. A harness must never report a pass for a lane it could not "
            "find (MeshWeaver#3878).")
    doc = yaml.safe_load(workflow.read_text(encoding="utf-8"))
    for job in (doc.get("jobs") or {}).values():
        for step in job.get("steps") or []:
            if isinstance(step, dict) and step.get("id") == step_id:
                body = step.get("run")
                if not body:
                    die(f"step id `{step_id}` in {workflow.name} has no `run:` — nothing to execute.")
                if "${{" in body:
                    die(f"step id `{step_id}`'s `run:` now contains a ${{{{ }}}} expression, which this "
                        "harness cannot supply. It refuses to execute a body it would have to "
                        "rewrite — pass the value through `env:` instead.")
                return body
    die(f"no step with `id: {step_id}` in {workflow.name}. It was renamed, moved or deleted — and "
        "this harness will not report a pass for a step it could not find.")
    return ""  # unreachable


def run_step(body: str, env: dict[str, str], workspace: Path) -> subprocess.CompletedProcess:
    e = dict(os.environ)
    e.update(env)
    e["RUNNER_TEMP"] = str(workspace / "_temp")
    (workspace / "_temp").mkdir(exist_ok=True)
    e["GITHUB_STEP_SUMMARY"] = str(workspace / "_temp" / "summary.md")
    Path(e["GITHUB_STEP_SUMMARY"]).touch()
    e.setdefault("GITHUB_REPOSITORY", "Systemorph/Acme")
    e.setdefault("GITHUB_RUN_ID", "1000")
    # Belt and braces: no live credential may reach a step this harness executes.
    for cred in ("GH_TOKEN", "GITHUB_TOKEN", "MW_LEDGER_TOKEN"):
        e[cred] = ""
    return subprocess.run(["bash", "-c", body], cwd=workspace, env=e,
                          capture_output=True, text=True)


# ── the stub registry ───────────────────────────────────────────────────────────────────────
class Registry:
    """Records every request. 'The registry was left untouched' is read off THIS, never off an
    exit code — an assertion about the absence of a side effect has to observe the side effect."""

    def __init__(self, status: int = 200):
        self.requests: list[dict] = []
        self.status = status
        log = self.requests
        outer = self

        class Handler(http.server.BaseHTTPRequestHandler):
            def do_POST(self):  # noqa: N802
                length = int(self.headers.get("Content-Length") or 0)
                body = self.rfile.read(length)
                log.append({"path": self.path, "body": body,
                            "auth": self.headers.get("Authorization", "")})
                self.send_response(outer.status)
                self.send_header("Content-Type", "application/json")
                self.end_headers()
                self.wfile.write(b'{"ok":true}')

            def log_message(self, *args):  # silence
                pass

        self.server = http.server.HTTPServer(("127.0.0.1", 0), Handler)
        self.url = f"http://127.0.0.1:{self.server.server_port}"
        threading.Thread(target=self.server.serve_forever, daemon=True).start()

    def close(self):
        self.server.shutdown()


# ── fixtures ────────────────────────────────────────────────────────────────────────────────
IDENTITY = "mvid:8f2c19a4b7de4c2f9a1e6b3d5c7f0a12"
MODULES = [{"package": "AI", "module": "MeshWeaver.AI", "project": "src/MeshWeaver.AI/MeshWeaver.AI.csproj"}]
LANE = "floor-0123456789ab"


def stage_fixture(root: Path, lane: str = LANE, owed: bool = True) -> Path:
    """What `node-repo-module-pack.yml`'s `stage` step uploads, laid out the way
    download-artifact@v8 lays a `pattern:` download out: one directory per artifact."""
    d = root / "staged" / f"module-publication-{lane}-MeshWeaver.AI"
    d.mkdir(parents=True, exist_ok=True)
    name = "MeshWeaver.Plugin.AI.3.1.0.module.nupkg"
    bundle = d / name
    with zipfile.ZipFile(bundle, "w") as zf:
        zf.writestr("meshweaver/manifest.json", json.dumps(
            {"frameworkMvid": IDENTITY,
             "module": {"assemblyName": "MeshWeaver.AI", "assemblies": ["MeshWeaver.AI.dll"]}}))
        zf.writestr("meshweaver/modules/MeshWeaver.AI.dll", b"\x4d\x5a" + b"stub" * 64)
    import hashlib
    sha = hashlib.sha256(bundle.read_bytes()).hexdigest()
    (d / "MeshWeaver.AI.publication.json").write_text(json.dumps({
        "lane": lane, "package": "AI", "module": "MeshWeaver.AI", "version": "3.1.0",
        "registrySource": "Plugins", "frameworkIdentity": IDENTITY, "owed": owed,
        "reason": "" if owed else "no hand-over owed: the ledger already serves this key",
        "bundle": {"name": name, "sha256": sha, "size": bundle.stat().st_size},
        "ledger": {"key": "", "decision": "build"},
    }), encoding="utf-8")
    return root / "staged"


def workspace(root: Path) -> Path:
    """The lane's layout: the platform checkout lives under `meshweaver/`."""
    ws = root / "ws"
    ws.mkdir(exist_ok=True)
    link = ws / "meshweaver"
    if not link.exists():
        link.symlink_to(ROOT)
    return ws


CALLER_GOOD = """
name: ci
on: { push: { branches: [main] } }
jobs:
  validate:
    runs-on: ubuntu-latest
    timeout-minutes: 10
    steps: [{ run: echo }]
  module-pack:
    needs: [validate]
    uses: Systemorph/MeshWeaver/.github/workflows/node-repo-module-pack.yml@0123456789012345678901234567890123456789
  compile-check:
    needs: [module-pack]
    runs-on: ubuntu-latest
    timeout-minutes: 10
    steps: [{ run: echo }]
  publish-modules:
    if: github.event_name == 'push' && github.ref == 'refs/heads/main'
    needs: [validate, module-pack, compile-check]
    uses: Systemorph/MeshWeaver/.github/workflows/node-repo-module-publish.yml@0123456789012345678901234567890123456789
    with:
      verdicts: ${{ toJSON(needs) }}
      required-jobs: validate module-pack compile-check
      lane: ${{ needs.module-pack.outputs.lane }}
      selected: ${{ needs.module-pack.outputs.selected }}
      modules: '[]'
      platform-ref: 0123456789012345678901234567890123456789
"""


def main() -> int:  # noqa: C901
    verdict_body = extract_step(PUBLISH_WORKFLOW, "verdict")
    caller_body = extract_step(PUBLISH_WORKFLOW, "caller-gate")
    publish_body = extract_step(PUBLISH_WORKFLOW, "publish")
    print(f"extracted 3 step(s) from {PUBLISH_WORKFLOW.name}\n")

    with tempfile.TemporaryDirectory() as td:
        root = Path(td)
        ws = workspace(root)
        staged = stage_fixture(root)
        registry = Registry()
        try:
            base = {
                "LANE": LANE, "SELECTED": "MeshWeaver.AI",
                "DECLARED": json.dumps(MODULES),
                "REGISTRY": registry.url, "REGISTRY_SOURCE": "Plugins",
                "LEDGER": "off", "MW_PUBLISH_TOKEN": "tok_test",
            }

            def do_publish(**over):
                env = dict(base)
                env.update(over)
                # download-artifact@v8 puts the matched artifacts under $RUNNER_TEMP/staged; the
                # fixture is copied there so the step's own path expression is what resolves it.
                dest = ws / "_temp" / "staged"
                if dest.exists():
                    import shutil
                    shutil.rmtree(dest)
                if over.pop("_no_staged", False) is not True:
                    import shutil
                    (ws / "_temp").mkdir(exist_ok=True)
                    shutil.copytree(staged, dest)
                return run_step(publish_body, env, ws)

            def do_verdict(verdicts: dict, required="validate module-pack compile-check"):
                return run_step(verdict_body, {"VERDICTS": json.dumps(verdicts), "REQUIRED": required}, ws)

            green = {"validate": {"result": "success"}, "module-pack": {"result": "success"},
                     "compile-check": {"result": "success"}}

            print("== DIRECTION 1: green validation publishes the exact validated bytes")
            r = do_verdict(green)
            check("the verdict step passes on an all-success needs context", r.returncode == 0,
                  r.stdout + r.stderr)
            r = do_publish()
            check("the hand-over step runs and exits 0", r.returncode == 0, r.stdout + r.stderr)
            check("the registry received exactly ONE request", len(registry.requests) == 1,
                  json.dumps([q["path"] for q in registry.requests]))
            if registry.requests:
                q = registry.requests[0]
                want = (staged / f"module-publication-{LANE}-MeshWeaver.AI"
                        / "MeshWeaver.Plugin.AI.3.1.0.module.nupkg").read_bytes()
                check("…carrying the EXACT staged bytes", q["body"] == want,
                      f"{len(q['body'])} bytes received, {len(want)} staged")
                check("…to /api/plugins/bundles/AI with the version and packagePath",
                      q["path"] == "/api/plugins/bundles/AI?version=3.1.0&packagePath=Plugins/AI",
                      q["path"])
                check("…with the publish token as a bearer credential",
                      q["auth"] == "Bearer tok_test", q["auth"])

            print("\n== DIRECTION 2: a later sibling/source failure leaves the registry untouched")
            for job, result in (("compile-check", "failure"), ("module-pack", "cancelled"),
                                ("validate", "skipped")):
                before = len(registry.requests)
                ctx = dict(green)
                ctx[job] = {"result": result}
                r = do_verdict(ctx)
                ran_publish = r.returncode == 0
                check(f"a `{result}` {job} refuses BEFORE the hand-over — and names it",
                      r.returncode == 1 and job in (r.stdout + r.stderr), r.stdout + r.stderr)
                # GitHub would not run the later step at all; the harness models that literally.
                if ran_publish:
                    do_publish()
                check(f"…and the registry received nothing more ({result} {job})",
                      len(registry.requests) == before,
                      f"{len(registry.requests) - before} extra request(s)")

            before = len(registry.requests)
            r = do_verdict({"validate": {"result": "success"}, "module-pack": {"result": "success"}})
            check("a required job MISSING from `needs:` refuses (it never reported at all)",
                  r.returncode == 1 and "compile-check" in (r.stdout + r.stderr), r.stdout + r.stderr)
            r = do_verdict(green, required="")
            check("an EMPTY required-jobs list refuses — it would assert nothing",
                  r.returncode == 1, r.stdout + r.stderr)
            r = do_verdict({}, required="validate")
            check("an EMPTY needs context refuses — a publication that waited for nothing",
                  r.returncode == 1, r.stdout + r.stderr)
            check("nothing above reached the registry", len(registry.requests) == before)

            print("\n== The staged evidence must be THIS call's, and unaltered")
            before = len(registry.requests)
            r = do_publish(LANE="rest-fedcba987654")
            check("a FOREIGN lane's staged record refuses", r.returncode == 1,
                  r.stdout + r.stderr)
            r = do_publish(SELECTED="MeshWeaver.AI MeshWeaver.Mcp")
            check("a SELECTED module with no staged record refuses (its leg never staged)",
                  r.returncode == 1 and "MeshWeaver.Mcp" in (r.stdout + r.stderr), r.stdout + r.stderr)
            swapped = staged / f"module-publication-{LANE}-MeshWeaver.AI" / "MeshWeaver.Plugin.AI.3.1.0.module.nupkg"
            keep = swapped.read_bytes()
            swapped.write_bytes(keep + b"tampered")
            r = do_publish()
            check("SUBSTITUTED bytes refuse — the sha256 on the record is the validated one",
                  r.returncode == 1, r.stdout + r.stderr)
            swapped.write_bytes(keep)
            r = do_publish(MW_PUBLISH_TOKEN="")
            check("an EMPTY publish token FAILS the job rather than publishing nothing quietly",
                  r.returncode == 1, r.stdout + r.stderr)
            check("none of those reached the registry", len(registry.requests) == before,
                  f"{len(registry.requests) - before} extra request(s)")

            print("\n== A registry refusal is a RED, and is not recorded as published")
            refusing = Registry(status=409)
            try:
                r = do_publish(REGISTRY=refusing.url)
                check("a non-2xx answer fails the job", r.returncode == 1, r.stdout + r.stderr)
                check("…after actually asking the registry", len(refusing.requests) == 1)
            finally:
                refusing.close()

            print("\n== MUTATIONS of the caller's graph (the half the runtime verdict cannot see)")
            caller_dir = ws / "caller" / ".github" / "workflows"
            caller_dir.mkdir(parents=True, exist_ok=True)
            wf = caller_dir / "ci.yml"

            def do_caller(text: str, required="validate module-pack compile-check", unrelated=""):
                wf.write_text(text, encoding="utf-8")
                return run_step(caller_body, {
                    "WORKFLOW_REF": "Systemorph/Acme/.github/workflows/ci.yml@refs/heads/main",
                    "REQUIRED": required, "UNRELATED": unrelated}, ws)

            r = do_caller(CALLER_GOOD)
            check("a caller that waits for its whole graph passes", r.returncode == 0,
                  r.stdout + r.stderr)
            r = do_caller(CALLER_GOOD.replace("needs: [validate, module-pack, compile-check]",
                                              "needs: [validate, module-pack]"),
                          required="validate module-pack")
            check("MUTATION: a required dependency removed from `needs:` AND `required-jobs` is caught",
                  r.returncode == 1 and "compile-check" in (r.stdout + r.stderr), r.stdout + r.stderr)
            r = do_caller(CALLER_GOOD.replace(
                "if: github.event_name == 'push' && github.ref == 'refs/heads/main'",
                "if: ${{ !cancelled() }}"))
            check("MUTATION: a status function on the publishing job's `if:` is caught",
                  r.returncode == 1 and "status function" in (r.stdout + r.stderr), r.stdout + r.stderr)
            r = do_caller(CALLER_GOOD.replace("verdicts: ${{ toJSON(needs) }}",
                                              "verdicts: '{\"validate\":{\"result\":\"success\"}}'"))
            check("MUTATION: a curated `verdicts` literal is caught", r.returncode == 1,
                  r.stdout + r.stderr)
        finally:
            registry.close()

    print("\n== node-repo-module-pack.yml stages instead of publishing under `publish-mode: staged`")
    import yaml

    pack = yaml.safe_load(PACK_WORKFLOW.read_text(encoding="utf-8"))
    inputs = ((pack.get(True) or pack.get("on") or {}).get("workflow_call") or {}).get("inputs") or {}
    check("the lane declares `publish-mode`", "publish-mode" in inputs)
    check("…defaulting to `direct`, so an unconverted caller is unchanged",
          (inputs.get("publish-mode") or {}).get("default") == "direct")
    outs = ((pack.get(True) or pack.get("on") or {}).get("workflow_call") or {}).get("outputs") or {}
    check("the lane publishes its `lane` key so one publication can only reach one call's evidence",
          "lane" in outs)
    check("…and its `selected` set, so a module that never staged is a NAMED refusal", "selected" in outs)
    # The matrix the call was GIVEN, so the publisher's set-aside check reads the one catalog the pack
    # call was handed instead of a second copy of it in the caller (Plugins refuses a second
    # `modules:` list in its ci.yml, #3732 — and a copy drifts).
    check("…and the matrix it was GIVEN (`declared`), routed through `select`",
          "declared" in outs and str((outs.get("declared") or {}).get("value", "")).replace(" ", "")
          == "${{jobs.select.outputs.declared}}", str(outs.get("declared")))
    select_outs = pack["jobs"]["select"].get("outputs") or {}
    check("…fed VERBATIM from the `modules` input, never a derived or filtered list",
          str(select_outs.get("declared", "")).replace(" ", "") == "${{inputs.modules}}",
          str(select_outs.get("declared")))

    steps = {s.get("id") or s.get("name"): s for s in (pack["jobs"]["pack"]["steps"])}
    handover = steps.get("publish")
    stage = steps.get("stage")
    check("the in-leg hand-over still exists (`direct` is untouched)", handover is not None)
    check("…and cannot fire under `publish-mode: staged`",
          handover is not None and "publish-mode == 'direct'" in str(handover.get("if")),
          str(handover.get("if")) if handover else "")
    check("a staging step exists as its exact complement", stage is not None
          and "publish-mode == 'staged'" in str(stage.get("if")),
          str(stage.get("if")) if stage else "")
    check("…and it stages the SAME bytes the hand-over would have posted",
          stage is not None and handover is not None
          and str(stage.get("env", {}).get("BUNDLE")) == str(handover.get("env", {}).get("BUNDLE")))
    ledger_published = [s for s in pack["jobs"]["pack"]["steps"] if s.get("name") == "Ledger — Published"]
    check("a staged leg records NO `Published` ledger transition — a staged artifact is not a "
          "publication", len(ledger_published) == 1
          and "steps.publish.outcome == 'success'" in str(ledger_published[0].get("if")),
          str(ledger_published[0].get("if")) if ledger_published else "")

    pub = yaml.safe_load(PUBLISH_WORKFLOW.read_text(encoding="utf-8"))
    job = pub["jobs"]["publish"]
    check("the publication job carries NO `if:` of its own — a caller that weakened its gate is "
          "caught by the verdict, never skipped past it", "if" not in job)
    ids = [s.get("id") for s in job["steps"] if s.get("id")]
    check("the verdict runs before the hand-over", ids.index("verdict") < ids.index("publish"))
    check("…and so does the caller-graph check", ids.index("caller-gate") < ids.index("publish"))

    print()
    if failures:
        print(f"::error::{len(failures)} case(s) FAILED: " + "; ".join(failures))
        return 1
    print("test-module-publication: both directions, the staged-evidence refusals, the caller "
          "mutations and the pack lane's staged arm — all green")
    return 0


if __name__ == "__main__":
    sys.exit(main())
