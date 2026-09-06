#!/usr/bin/env python3
"""EXECUTE the two shell consumers of a sealed publication against a registry that REPUBLISHES.

WHY
---
`compose-sealed-modules.sh` is fetched from core at a pinned `platform-ref` and run by every
satellite's gate and publish-bake. It does **N+1 reads** of one sealed publication — the module-set
index, then each bundle that index names — of a directory the publisher unseals, rewrites and
re-seals underneath it. The window is roughly a minute and a half per target per publish, and the
`plugins` prefix has TWO writers (core CD's `plugins-bake` and the MeshWeaver.Plugins satellite's
own `publish-bake`), so they can land inside each other's.

MeshWeaver#3401 is what that looked like from a satellite: Manufacturing's gate 404'd on a bundle
its own index named, 35 minutes after the same identity served 114/114 elsewhere. #3409 gave the
BUNDLE lane a generation and made the window answer 503; the MODULE lane kept reading unpinned, and
a mix THERE is worse than a red — module bytes from two publications are the mvid mismatch that
DECLINES every NodeType assembly at adoption, invisibly, until a portal boots.

Nothing executed this script before. It is shell, it runs only in satellite repos, and its first
execution of any edit is in their CI.

HOW IT STAYS HONEST
-------------------
* The REAL script is run, never a copy — a copy passes while the real thing rots.
* The stub records whether `If-Match` was ever sent. A script that stopped pinning the generation
  would still compose two files and "pass" on the outputs alone, so that is asserted directly.
* Case 3 is a CONTROL: no reseal at all, and it must pass. A harness whose only cases are failures
  cannot tell "the script refuses correctly" from "the script always refuses".
* Case 4 pins the older-registry path: a registry publishing no generation must still be composed
  from, with no `If-Match` sent — that is what keeps a satellite on an older portal working.
* Case 5 runs the GATE's seed step, EXTRACTED from node-repo-gate.yml by its `id:` — the same lane
  that went red on Manufacturing. Extracted, never copied, and the harness fails red if the step
  cannot be found or has grown a `${{ }}` expression it cannot supply.
"""

from __future__ import annotations

import json
import os
import re
import subprocess
import sys
import tempfile
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

try:
    import yaml
except ImportError:  # pragma: no cover — the CI step installs PyYAML; locally `pip install pyyaml`
    print("::error::test-sealed-module-compose.py needs PyYAML to EXTRACT the gate's seed step "
          "(pip install pyyaml). Refusing rather than skipping that case: a harness that quietly "
          "tests less renders the same green tick as one that tests the lane.")
    raise SystemExit(1) from None

SCRIPT = Path(__file__).resolve().parent / "compose-sealed-modules.sh"
GATE = Path(__file__).resolve().parent.parent / "workflows" / "node-repo-gate.yml"
SEED_STEP_ID = "seed"
BUNDLES = ["Store.zip", "Export.zip"]
IDENTITY = "s0123456789abcdef0123456789abcdef"
SOURCE = "plugins"
MODULES = ["ai.module.nupkg", "essentials.module.nupkg"]
# The publish lands BETWEEN two reads: the first bundle is already fetched and written when the
# second is asked for, which is precisely the N+1 window.
RESEAL_ON_MODULE = "/modules/essentials.module.nupkg"
RESEAL_ON_BUNDLE = f"/{BUNDLES[1]}"


class Registry:
    """One sealed publication, and a publisher that can replace it mid-read."""

    def __init__(self, reseals: int, publish_generation: bool = True,
                 reseal_on: str = RESEAL_ON_MODULE):
        self.generation = 1
        self.reseals_left = reseals
        self.publish_generation = publish_generation
        self.reseal_on = reseal_on
        self.if_match_seen: list[str] = []
        self.unauthenticated = 0

    @property
    def token(self) -> str:
        return f"g{self.generation}"

    def marker(self) -> str:
        return f"gen{self.generation}"

    def maybe_reseal(self, path: str) -> None:
        if path.endswith(self.reseal_on) and self.reseals_left > 0:
            self.reseals_left -= 1
            self.generation += 1


def make_handler(state: Registry):
    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *_args):        # keep the harness output readable
            pass

        def _json(self, code: int, payload: dict) -> None:
            body = json.dumps(payload).encode()
            self.send_response(code)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def do_GET(self):                     # noqa: N802 — BaseHTTPRequestHandler's contract
            if not (self.headers.get("Authorization") or "").startswith("Bearer "):
                state.unauthenticated += 1
                self._json(401, {"error": "a registered instance key is required"})
                return
            base = f"/api/plugins/bundles/prebuilt/{IDENTITY}/{SOURCE}"
            prefix = base + "/modules"
            if self.path == prefix:
                payload = {"identity": IDENTITY, "source": SOURCE, "modules": MODULES}
                if state.publish_generation:
                    payload["generation"] = state.token
                self._json(200, payload)
                return
            if self.path.startswith(prefix + "/"):
                name = self.path[len(prefix) + 1:]
                state.maybe_reseal(self.path)
                held = self.headers.get("If-Match")
                if held:
                    state.if_match_seen.append(held.strip('"'))
                    if held.strip('"') != state.token:
                        self._json(412, {"error": "the publication was resealed after you read "
                                                  "its index", "held": held, "current": state.token})
                        return
                if name not in MODULES:
                    self._json(404, {"error": "no such bundle in this publication"})
                    return
                body = f"{state.marker()}:{name}".encode()
                self.send_response(200)
                self.send_header("Content-Type", "application/zip")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)
                return
            if self.path == base:
                payload = {"identity": IDENTITY, "source": SOURCE, "bundles": BUNDLES}
                if state.publish_generation:
                    payload["generation"] = state.token
                self._json(200, payload)
                return
            if self.path.startswith(base + "/"):
                name = self.path[len(base) + 1:]
                state.maybe_reseal(self.path)
                held = self.headers.get("If-Match")
                if held:
                    state.if_match_seen.append(held.strip('"'))
                    if held.strip('"') != state.token:
                        self._json(412, {"error": "the publication was resealed after you read "
                                                  "its index", "held": held, "current": state.token})
                        return
                if name not in BUNDLES:
                    self._json(404, {"error": "no such bundle in this publication"})
                    return
                body = f"{state.marker()}:{name}".encode()
                self.send_response(200)
                self.send_header("Content-Type", "application/zip")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)
                return
            self._json(404, {"error": "not a route this stub serves"})

    return Handler


def compose(state: Registry, out: Path) -> subprocess.CompletedProcess[str]:
    server = ThreadingHTTPServer(("127.0.0.1", 0), make_handler(state))
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        return subprocess.run(
            [str(SCRIPT),
             "--identity", IDENTITY, "--packages", "AI Essentials",
             "--upstreams", SOURCE, "--out", str(out),
             "--registry-url", f"http://127.0.0.1:{server.server_port}",
             "--registry-key", "mwi_stub"],
            capture_output=True, text=True, timeout=180, check=False)
    finally:
        server.shutdown()
        server.server_close()


def gate_seed_step() -> str:
    """The gate's seed step, EXTRACTED from node-repo-gate.yml by its `id:` — never copied.

    Fails RED rather than skipping when the step cannot be found, has grown an expression this
    harness cannot supply, or has stopped issuing the request being stubbed. A harness that quietly
    tested nothing would render the same green tick as one that tested the lane.
    """
    if not GATE.is_file():
        raise SystemExit(f"FAIL: {GATE} is missing — this harness runs the REAL lane, never a copy.")
    workflow = yaml.safe_load(GATE.read_text())
    for job in (workflow.get("jobs") or {}).values():
        for step in job.get("steps") or []:
            if step.get("id") != SEED_STEP_ID:
                continue
            run = step["run"]
            if re.search(r"\$\{\{", run):
                raise SystemExit(
                    f"FAIL: the '{SEED_STEP_ID}' step now carries a workflow expression this "
                    "harness cannot supply — teach it, never skip it.")
            if "curl" not in run:
                raise SystemExit(
                    f"FAIL: the '{SEED_STEP_ID}' step no longer issues the curl this harness stubs "
                    "— it would pass having exercised nothing.")
            return run
    raise SystemExit(f"FAIL: no step with id '{SEED_STEP_ID}' in {GATE}.")


def seed(state: Registry, runner_temp: Path, script: Path) -> subprocess.CompletedProcess[str]:
    server = ThreadingHTTPServer(("127.0.0.1", 0), make_handler(state))
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        env = dict(os.environ)
        env.update(
            UPSTREAMS=SOURCE, IDENTITY=IDENTITY, TARGETS="",
            REGISTRY_URL=f"http://127.0.0.1:{server.server_port}", REGISTRY_KEY="mwi_stub",
            RUNNER_TEMP=str(runner_temp), GITHUB_OUTPUT=str(runner_temp / "outputs"))
        return subprocess.run(["bash", str(script)], capture_output=True, text=True,
                              timeout=180, check=False, env=env)
    finally:
        server.shutdown()
        server.server_close()


def fail(case: str, why: str, proc: subprocess.CompletedProcess[str] | None = None) -> None:
    print(f"FAIL [{case}] {why}")
    if proc is not None:
        print(f"  exit={proc.returncode}\n  stdout:\n{proc.stdout}\n  stderr:\n{proc.stderr}")
    sys.exit(1)


def main() -> int:
    if not SCRIPT.is_file():
        print(f"FAIL: {SCRIPT} is missing — this harness runs the REAL lane script, never a copy.")
        return 1
    os.environ.setdefault("PATH", "/usr/bin:/bin")

    with tempfile.TemporaryDirectory() as tmp:
        # ── 1. the CONTROL: a settled publication composes, and pins the generation it read ─────
        out = Path(tmp, "settled"); out.mkdir()
        state = Registry(reseals=0)
        proc = compose(state, out)
        if proc.returncode != 0:
            fail("settled", "a publication nobody touched must compose", proc)
        if sorted(p.name for p in out.iterdir()) != sorted(MODULES):
            fail("settled", f"composed {[p.name for p in out.iterdir()]}, wanted {MODULES}", proc)
        if state.if_match_seen != ["g1", "g1"]:
            fail("settled",
                 "every bundle fetch must pin the generation its index came from (MeshWeaver#3401); "
                 f"If-Match values seen: {state.if_match_seen}", proc)

        # ── 2. THE DEFECT: a reseal between the two module reads ────────────────────────────────
        out = Path(tmp, "resealed"); out.mkdir()
        state = Registry(reseals=1)
        proc = compose(state, out)
        if proc.returncode != 0:
            fail("resealed", "one reseal must be survivable by reading the publication that now "
                             "applies — not by refusing", proc)
        markers = {p.name: p.read_text().split(":")[0] for p in sorted(out.iterdir())}
        if len(set(markers.values())) != 1:
            fail("resealed",
                 f"the composition mixed publications: {markers}. A composition reads ONE "
                 "publication or none (MeshWeaver#3401).", proc)
        if "412" not in "".join(state.if_match_seen) and "g1" not in state.if_match_seen:
            fail("resealed", "the stale generation was never sent, so nothing could refuse it", proc)
        if "re-reading the publication that now applies" not in proc.stdout:
            fail("resealed", "the move must be REPORTED, not absorbed silently", proc)

        # ── 3. a publisher that reseals under every attempt: RED, and nothing staged ────────────
        out = Path(tmp, "storm"); out.mkdir()
        state = Registry(reseals=99)
        proc = compose(state, out)
        if proc.returncode == 0:
            fail("storm", "a publisher resealing under every attempt must go RED, never spin", proc)
        if "MeshWeaver#3401" not in proc.stdout + proc.stderr:
            fail("storm", "the refusal must name itself so a satellite is not blamed for it", proc)
        if list(out.iterdir()):
            fail("storm",
                 f"a refused composition staged {[p.name for p in out.iterdir()]} — bytes from a "
                 "publication this run rejected must not reach the compile surface", proc)

        # ── 4. a registry that predates #3401 publishes no generation: unchanged behaviour ──────
        out = Path(tmp, "older"); out.mkdir()
        state = Registry(reseals=0, publish_generation=False)
        proc = compose(state, out)
        if proc.returncode != 0:
            fail("older-registry", "a registry publishing no generation must still be composed "
                                   "from — the precondition is opt-in", proc)
        if state.if_match_seen:
            fail("older-registry",
                 f"no If-Match may be sent when the index carries no generation: {state.if_match_seen}",
                 proc)
        if state.unauthenticated:
            fail("older-registry", "every request must carry the instance key", proc)

        # ── 5. THE GATE'S OWN SEED LOOP — the lane that went red on Manufacturing ───────────────
        # Extracted from node-repo-gate.yml by its `id:`, run against the same stub. This is the
        # N+1 read that #3401 was reported on: index, then each bundle it names.
        script = Path(tmp, "seed-step.sh")
        script.write_text(gate_seed_step())
        runner = Path(tmp, "gate"); runner.mkdir()
        state = Registry(reseals=1, reseal_on=RESEAL_ON_BUNDLE)
        proc = seed(state, runner, script)
        if proc.returncode != 0:
            fail("gate-seed", "one reseal must be survivable by reading the publication that now "
                              "applies — not by failing the satellite's gate", proc)
        seeded = runner / "upstream-seed"
        markers = {name: (seeded / name).read_text().split(":")[0] for name in BUNDLES}
        if len(set(markers.values())) != 1:
            fail("gate-seed",
                 f"the seed mixed publications: {markers}. A seed reads ONE publication or none.",
                 proc)
        if not (seeded / "framework-mvid.txt").is_file():
            fail("gate-seed", "the seed must be addressable — framework-mvid.txt is missing", proc)
        if "dir=" not in (runner / "outputs").read_text():
            fail("gate-seed", "the step must publish its seed directory as an output", proc)
        if "g1" not in state.if_match_seen:
            fail("gate-seed",
                 f"the bundle fetches did not pin a generation: {state.if_match_seen}", proc)

        # …and a publisher resealing under every attempt still fails the gate, naming itself.
        runner = Path(tmp, "gate-storm"); runner.mkdir()
        state = Registry(reseals=99, reseal_on=RESEAL_ON_BUNDLE)
        proc = seed(state, runner, script)
        if proc.returncode == 0:
            fail("gate-storm", "a publisher resealing under every attempt must red the gate", proc)
        if "MeshWeaver#3401" not in proc.stdout + proc.stderr:
            fail("gate-storm", "the refusal must name itself so a pin-move PR is not blamed", proc)

    print("sealed-publication reads: 6/6 cases pass — compose-sealed-modules.sh "
          "(settled, resealed-once, reseal-storm, registry-without-generation) and "
          "node-repo-gate.yml's seed step (resealed-once, reseal-storm)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
