#!/usr/bin/env python3
"""build-run.py — the standing build host's runner: claim a run, build and test it in fresh processes.

The HOST half of the standing build hosts (MeshWeaver.Plugins `Hosting/StandingBuildHosts` §2 and
§4.3; platform reference Doc/Architecture/BuildHosts). One warm Deployment per release channel
(`build-latest`, `build-stable`, chart `deploy/helm/templates/build-host/` in Systemorph/MeshWeaver.Deploy) runs this as its main
process:

    build-run.py --host          the loop: refresh the plugins layer → claim → run → repeat
    build-run.py --once          one cycle of that loop, then exit (diagnostics)
    build-run.py --self-test     the parsers, the refresh-before-claim ordering, the stage runner

THE LOOP, and the order that matters:
  1. `GET {api}/hosts/inputs` → the sealed plugins publication the fleet's modules are built from.
     When its digest differs from the one this host has staged, the new publication is pulled
     (ORAS, by DIGEST) into the content-addressed cache `/cache/plugins/<hex>/` BEFORE anything is
     claimed. A host never claims on a plugins layer it has not staged: an unreadable `inputs` or a
     failed pull skips the claim for this cycle (said, then retried) — because `claim` stamps the
     host's `pluginsDigest` on the run, and a run must record exactly what compiled it.
     With MW_REGISTRY_URL set (H8) the layer is the registry's BUNDLE INDEX instead (read with the
     host's mwi_ instance key, a host-only file), keyed by the digest of its sorted
     `package@version[@hash]` list, changed bundles fetched with core's `module-fetch`.
  2. `POST {api}/claim` with the host's channel, platform set, framework identity, plugins digest
     or key, and a fresh `claimId` (the SAME one on a network-error retry). 204 = nothing
     claimable → sleep. 200 = a run: `{path, repo, sha, refs, isTrunk, publish, stages}` — `path`
     is authoritative (a non-latest run's node id is `<sha>-<channel>`). The host then posts the
     PLAN (`{"kind":"plan","stages":[…]}`, host-emitted only) and keeps the claim's lease with a
     batch at least every 45 s (an empty batch is the heartbeat); every stage route's batches are
     numbered (`seq`) and a retried batch keeps its number. No claim survives a host restart.
  3. The HOST checks the run out at `sha` into a FRESH work directory through the shared git
     mirror `<cache>/git/<repo>.git` (cloned `--mirror` on first use, fetched before every
     checkout; the run's one-repository, contents:read installation token stays in the host
     process), and every stage the repository declares in
     `.github/build-stages.json` runs in an ENFORCED SANDBOX: `bwrap` with user, PID, mount, IPC
     and UTS namespaces, `--die-with-parent`, a fresh /proc, `--clearenv` + an explicit allow-list
     (the worker token is a mounted FILE read into the host process and never in any environment),
     the system and every cache bound READ-ONLY, and only the run's checkout, its outputs directory
     and a tmpfs scratch writable. When the stage ends its whole process tree ends with it; the host
     checks no process survives in another pid namespace before it claims again, and EXITS if one
     does. If the node cannot create the namespaces, the host refuses to START, naming why — it
     never runs a stage unsandboxed. It also refuses to start when, from INSIDE a sandbox, the
     instance metadata service, a cluster-private address or the Kubernetes API ANSWERS (the
     boundary self-check): the NetworkPolicy, not the sandbox, is the network boundary. `MW_BUILD_PUBLISH=true|false` (from the claim) is
     informational: a host holds no publication credential and mounts no bake share; publication
     is the publish lane's, on the aggregate check `build`.
  4. The child's output is translated into PROGRESS EVENTS (§7a) and POSTed in batches to
     `{api}/{repo}/{sha}/stages/{stage}/events?channel=<c>`: compiler `file(line,col): error CODE:
     message` → `diagnostic`; test results (vstest `Passed/Failed/Skipped` and xunit `[FAIL]`) →
     `test-finished`; a structured `::mw-event::{json}` line → that event; plus the host's own
     `line`, `stage-finished` and `run-finished`. Everything else the child prints stays in a local
     tail and is forwarded ONLY when the stage failed with nothing else to explain it ("CI logs
     warn/error + verdicts only"). A stage writes outputs to `$MW_BUILD_OUTPUTS/<content key>/`
     with a `.cache-key.json`; the HOST promotes them into the shared cache (`<cache>/outputs/<key>/`)
     ONLY for a succeeded BUILD stage of a trunk run, and ONLY when it recomputes the key itself
     (module: core's module-build-key.py; NodeType: sources + reference set + compile-check options
     + compiler). Never a test verdict; never a pull request's output. A stage needing PostgreSQL
     starts a disposable one inside its sandbox (sandbox-postgres.sh: tmpfs, random password, Unix
     socket only).
  5. 409 on an event POST = the run is claimed by another host: the child is killed, nothing more
     is posted for that run.

`.github/build-stages.json` (declared by each repository; validated here):
    {"stages": [{"name": "compile-check", "kind": "build", "command": "python3 …",
                 "timeoutMinutes": 40}, …]}
  * `name` is ONE path segment ([a-z0-9][a-z0-9._-]*); its Check Run is `build/<name>`
    on latest and `build-<channel>/<name>` otherwise; the run's AGGREGATE check is `build` /
    `build-<channel>` (no slash), so no stage — not even one named `verdict` — can publish under it.
  * `kind` is `build` or `test`; `timeoutMinutes` defaults to 45.
  * stages run in declaration order, fail-fast: after a failed stage the rest are finished
    `failed` with "not run" — never left open, never silently skipped. When the claim names
    `stages`, only those run, and a claimed stage the repository does not declare is RED.

Environment (rendered by the chart from `buildHosts.*`):
  BUILD_API                    https://<operations instance>/api/builds              required
  BUILD_TOKEN_FILE             the mounted worker token (Hosting:Builds:WorkerToken)  default /run/build-worker/token
                               (BUILD_TOKEN in the environment is REFUSED)
  MW_BUILD_CHANNEL             latest | stable                                        required
  MW_BUILD_PLATFORM_VERSION    the set this host runs (3.x.y-ci.N / 3.x.N)            required
  MW_BUILD_FRAMEWORK_IDENTITY  or MW_BUILD_IDENTITY_FILE (first token of the file)    required
  MW_BUILD_HOST                the claiming host's name (default: hostname)
  MW_BUILD_CACHE               the shared cache volume                    default /cache
  MW_BUILD_WORK                the per-run work root                       default /work
  MW_BUILD_READY_FILE          written once the host is staged (readiness) default /tmp/build-host-ready
  MW_BUILD_GIT_BASE            clone base                                  default https://github.com/Systemorph
  MW_BUILD_IDLE_SECONDS        sleep after a 204                           default 15
  MW_BUILD_RUN_DEADLINE_SECONDS  the whole run's deadline (= the pod's grace) default 3600
  BWRAP                        the bubblewrap binary                       default bwrap
  MW_PLUGINS_REGISTRY          registry host the plugins publication is pulled from
  MW_PLUGINS_SOURCE            the publication's source name               default plugins
  AZURE_FEDERATED_TOKEN_FILE   (projected by Azure workload identity) — the hosts' ONE credential,
  AZURE_CLIENT_ID, _TENANT_ID   AcrPull only, read in the host and never bound into a sandbox;
                               absent ⇒ the publication is pulled anonymously
  ORAS                         the oras binary                             default oras
"""
from __future__ import annotations

import argparse
import base64
import json
import os
import queue
import re
import shutil
import signal
import socket
import subprocess
import sys
import tempfile
import threading
import time
import uuid
import hashlib
import urllib.error
import urllib.parse
import urllib.request
from typing import Callable, Iterable, Optional

STAGE_NAME = re.compile(r"^[a-z0-9][a-z0-9._-]*$")
CHANNEL = re.compile(r"^[a-z0-9][a-z0-9._-]*$")
DIGEST = re.compile(r"^sha256:[0-9a-f]{64}$")
SHA = re.compile(r"^[0-9a-f]{40}$")
REPO = re.compile(r"^[A-Za-z0-9._-]+$")
KINDS = {"build", "test"}
DEFAULT_STAGE_TIMEOUT_MINUTES = 45
MAX_LINE = 2000
BATCH_EVENTS = 200
BATCH_SECONDS = 2.0
MAX_DIAGNOSTICS_PER_STAGE = 500
TAIL_LINES = 60
# Event kinds a CHILD may emit with `::mw-event::`. The verdicts (stage-finished, run-finished)
# are the HOST's: a child that could declare its own stage green would make the exit code moot.
CHILD_KINDS = {"unit-started", "unit-finished", "diagnostic", "test-finished", "count", "line"}
SECRET_ENV = re.compile(r"(TOKEN|SECRET|PASSWORD|PASSWD|CREDENTIAL|API_KEY|APIKEY)", re.I)


# ───────────────────────────────── output parsers ─────────────────────────────────────────

# Roslyn / MSBuild: `path/File.cs(41,13): error CS0246: The type … [/work/x/Proj.csproj]`
MSBUILD = re.compile(
    r"^\s*(?P<file>[^\s(][^(]*?)\((?P<line>\d+)(?:,(?P<col>\d+))?(?:,\d+,\d+)?\)\s*:\s*"
    r"(?P<sev>error|warning)\s+(?P<code>[A-Za-z]+\d+)\s*:\s*(?P<msg>.*?)(?:\s+\[[^\]]+\])?\s*$")
# A GitHub workflow command a child already printed: `::error file=a.cs,line=3::CS0246 msg`
WFCMD = re.compile(r"^::(?P<sev>error|warning)(?:\s+(?P<props>[^:]*))?::(?P<msg>.*)$")
CODE_IN_MSG = re.compile(r"^(?P<code>[A-Z]{2,}\d{2,})\b[:\s]*(?P<rest>.*)$")
# vstest console: `  Passed Ns.Class.Test [12 ms]` / `  Failed …` / `  Skipped …`
VSTEST = re.compile(r"^\s*(?P<o>Passed|Failed|Skipped)\s+(?P<test>[^\s\[][^\[]*?)\s*(?:\[(?P<d>[^\]]*)\])?\s*$")
# xunit (v2/v3 console): `    Ns.Class.Test [FAIL]`
XUNIT = re.compile(r"^\s*(?P<test>\S[^\[]*?)\s+\[(?P<o>FAIL|PASS|SKIP)\]\s*$")
SUMMARY = re.compile(r"^\s*((Passed|Failed|Skipped)!\s+-|Total tests:|=== TEST EXECUTION SUMMARY ===|Test Run (Successful|Failed|Aborted))")
TEST_RUN_FOR = re.compile(r"Test run for (?P<dll>\S+?\.dll)")
STACK_FILE = re.compile(r"\bin (?P<file>\S+\.(?:cs|fs|vb|razor)):line (?P<line>\d+)")
MW_EVENT = re.compile(r"^::mw-event::(?P<json>.*)$")
OUTCOME = {"Passed": "passed", "Failed": "failed", "Skipped": "skipped",
           "PASS": "passed", "FAIL": "failed", "SKIP": "skipped"}


def parse_duration_ms(text: Optional[str]) -> int:
    """`12 ms`, `1 s`, `< 1 ms`, `2m 3s`, `1.5 s` → milliseconds (0 when unreadable)."""
    if not text:
        return 0
    t = text.strip().lstrip("<").strip()
    total = 0.0
    for num, unit in re.findall(r"([\d.]+)\s*(ms|s|m|h)\b", t):
        try:
            v = float(num)
        except ValueError:
            continue
        total += v * {"ms": 1, "s": 1000, "m": 60000, "h": 3600000}[unit]
    return int(total)


class OutputParser:
    """Translates one stage's child output into §7a progress events. Stateful (a failed test's
    message and file follow it on later lines); feed every line, then call finish()."""

    def __init__(self, workdir: str, stage: str) -> None:
        self.workdir = os.path.realpath(workdir).rstrip("/") + "/" if workdir else ""
        self.stage = stage
        self.suite = stage
        self.pending: Optional[dict] = None
        self.pending_state = ""        # "", "message", "stack"
        self.pending_lines = 0
        self.seen_diag: set[tuple] = set()
        self.diag_count = 0
        self.diag_dropped = 0
        self.errors = 0
        self.warnings = 0
        self.tests = {"passed": 0, "failed": 0, "skipped": 0}
        self.tail: list[str] = []

    # -- helpers ---------------------------------------------------------------------------
    def rel(self, path: str) -> str:
        p = path.strip().replace("\\", "/")
        if self.workdir and p.startswith(self.workdir):
            return p[len(self.workdir):]
        if self.workdir:
            real = self.workdir.rstrip("/")
            # a path relative to a project dir stays as printed; an absolute one under a symlinked
            # work root is normalised too
            try:
                rp = os.path.realpath(p)
                if rp.startswith(real + "/"):
                    return rp[len(real) + 1:]
            except (OSError, ValueError):
                pass
        return p

    def _diag(self, sev: str, file: str, line: int, code: str, message: str) -> list[dict]:
        key = (sev, file, line, code, message)
        if key in self.seen_diag:
            return []           # MSBuild repeats every diagnostic in its closing summary
        self.seen_diag.add(key)
        if sev == "error":
            self.errors += 1
        else:
            self.warnings += 1
        if self.diag_count >= MAX_DIAGNOSTICS_PER_STAGE:
            self.diag_dropped += 1
            return []
        self.diag_count += 1
        return [{"kind": "diagnostic", "severity": sev, "file": file, "line": line,
                 "code": code, "message": message[:MAX_LINE]}]

    def _flush_pending(self) -> list[dict]:
        if self.pending is None:
            return []
        ev, self.pending = self.pending, None
        self.pending_state = ""
        self.pending_lines = 0
        return [ev]

    def _test(self, outcome: str, test: str, dur: Optional[str]) -> list[dict]:
        out = self._flush_pending()
        outcome = OUTCOME[outcome]
        self.tests[outcome] += 1
        test = test.strip()
        suite = self.suite if self.suite != self.stage else (test.rsplit(".", 1)[0] if "." in test else self.stage)
        ev = {"kind": "test-finished", "suite": suite, "test": test, "outcome": outcome,
              "durationMs": parse_duration_ms(dur), "message": "", "file": "", "line": 0}
        if outcome == "failed":
            self.pending = ev           # its message and source line follow
        else:
            out.append(ev)
        return out

    def _child_event(self, raw: str) -> list[dict]:
        """A stage's own `::mw-event::{json}` — parsed TOTALLY: a malformed one is dropped with a
        line saying so, never an exception. Only the kinds in CHILD_KINDS are accepted, each field
        coerced to its type; `stage-finished` / `run-finished` are the HOST's alone (decided from
        the sandbox's exit status), so a child naming them is dropped and said."""
        def dropped(why: str) -> list[dict]:
            return [{"kind": "line", "text": f"malformed ::mw-event:: dropped ({why})"}]
        try:
            ev = json.loads(raw)
        except ValueError:
            return dropped("not JSON")
        if not isinstance(ev, dict):
            return dropped("not an object")
        kind = ev.get("kind")
        if kind in ("stage-finished", "run-finished", "plan"):
            return [{"kind": "line", "text": f"a stage tried to emit `{kind}` — refused: only the host decides a verdict"}]
        if kind not in CHILD_KINDS:
            return dropped(f"unknown kind {str(kind)[:40]!r}")

        def text(k: str, limit: int = MAX_LINE) -> str:
            v = ev.get(k)
            return v[:limit] if isinstance(v, str) else ""

        def num(k: str) -> int:
            v = ev.get(k)
            if isinstance(v, bool) or not isinstance(v, (int, float)) or v < 0 or v > 2**31:
                raise ValueError(k)
            return int(v)

        def opt_num(k: str) -> int:
            return num(k) if ev.get(k) is not None else 0
        try:
            if kind == "diagnostic":
                sev = text("severity") or "error"
                if sev not in ("error", "warning"):
                    return dropped(f"severity {sev!r}")
                return self._diag(sev, self.rel(text("file")), opt_num("line"), text("code", 40), text("message"))
            if kind == "test-finished":
                outcome = text("outcome")
                if outcome not in self.tests:
                    return dropped(f"outcome {outcome!r}")
                self.tests[outcome] += 1
                return [{"kind": kind, "suite": text("suite", 300) or self.stage, "test": text("test", 500),
                         "outcome": outcome, "durationMs": opt_num("durationMs"), "message": text("message"),
                         "file": self.rel(text("file")), "line": opt_num("line")}]
            if kind == "unit-started":
                return [{"kind": kind, "unit": text("unit", 300)}] if text("unit") else dropped("no unit")
            if kind == "unit-finished":
                outcome = text("outcome")
                if not text("unit") or outcome not in ("compiled", "cached", "failed"):
                    return dropped("unit/outcome")
                return [{"kind": kind, "unit": text("unit", 300), "outcome": outcome, "durationMs": opt_num("durationMs")}]
            if kind == "count":
                return [{"kind": kind, "unitsTotal": opt_num("unitsTotal"), "testsTotal": opt_num("testsTotal")}]
            return [{"kind": "line", "text": text("text")}] if text("text") else dropped("no text")
        except ValueError as e:
            return dropped(f"field {e}")

    # -- the feed --------------------------------------------------------------------------
    def feed(self, raw: str) -> list[dict]:
        """Never raises: a parser fault on one line is reported as a line, and the stage goes on."""
        try:
            return self._feed(raw)
        except Exception as e:      # the parser is total by contract; this is its seatbelt
            return [{"kind": "line", "text": f"output line not parsed ({type(e).__name__})"}]

    def _feed(self, raw: str) -> list[dict]:
        line = raw.rstrip("\r\n")
        self.tail.append(line[:MAX_LINE])
        if len(self.tail) > TAIL_LINES:
            del self.tail[0]

        m = MW_EVENT.match(line)
        if m:
            return self._child_event(m.group("json"))

        m = TEST_RUN_FOR.search(line)
        if m:
            out = self._flush_pending()
            self.suite = os.path.basename(m.group("dll"))[:-4]
            return out

        m = VSTEST.match(line)
        if m and not SUMMARY.match(line):
            return self._test(m.group("o"), m.group("test"), m.group("d"))
        m = XUNIT.match(line)
        if m:
            return self._test(m.group("o"), m.group("test"), None)

        if self.pending is not None:
            if SUMMARY.match(line):
                return self._flush_pending()
            self.pending_lines += 1
            stripped = line.strip()
            if stripped.startswith("Error Message:"):
                self.pending_state = "message"
            elif stripped.startswith("Stack Trace:"):
                self.pending_state = "stack"
            elif stripped:
                if not self.pending["message"] and self.pending_state in ("", "message") \
                        and not stripped.startswith("at "):
                    self.pending["message"] = stripped[:MAX_LINE]
                sm = STACK_FILE.search(line)
                if sm and not self.pending["file"]:
                    f = self.rel(sm.group("file"))
                    if not f.startswith("/"):       # a repository file, not a framework frame
                        self.pending["file"] = f
                        self.pending["line"] = int(sm.group("line"))
            if self.pending_lines > 50:
                return self._flush_pending()
            return []

        m = MSBUILD.match(line)
        if m:
            return self._diag(m.group("sev"), self.rel(m.group("file")), int(m.group("line")),
                              m.group("code"), m.group("msg").strip())
        m = WFCMD.match(line)
        if m:
            props = dict(p.split("=", 1) for p in (m.group("props") or "").split(",") if "=" in p)
            msg = m.group("msg").strip()
            code = ""
            cm = CODE_IN_MSG.match(msg)
            if cm:
                code, msg = cm.group("code"), cm.group("rest").strip()
            try:
                ln = int(props.get("line", "0") or 0)
            except ValueError:
                ln = 0
            return self._diag(m.group("sev"), self.rel(props.get("file", "")), ln, code, msg)
        return []

    def finish(self) -> list[dict]:
        out = self._flush_pending()
        if self.diag_dropped:
            out.append({"kind": "line", "text": f"{self.diag_dropped} further diagnostic(s) not forwarded "
                                                f"(cap {MAX_DIAGNOSTICS_PER_STAGE} per stage)"})
        return out

    def summary(self, rc: Optional[int], seconds: float, note: str = "") -> str:
        t = self.tests
        parts = [f"exit {rc}" if rc is not None else "no exit code",
                 f"{self.errors} error(s)", f"{self.warnings} warning(s)"]
        if sum(t.values()):
            parts.append(f"tests {t['passed']} passed, {t['failed']} failed, {t['skipped']} skipped")
        parts.append(_hms(seconds))
        if note:
            parts.append(note)
        return " · ".join(parts)


def _hms(seconds: float) -> str:
    s = int(seconds)
    return f"{s // 60}m{s % 60:02d}s"


# ───────────────────────────────── stage declaration ──────────────────────────────────────

class Refusal(Exception):
    """A run that cannot proceed, with the reason it is red."""


def load_stages(path: str) -> list[dict]:
    try:
        with open(path) as fh:
            doc = json.load(fh)
    except FileNotFoundError:
        raise Refusal(f"the repository declares no build stages ({os.path.basename(os.path.dirname(path))}/"
                      f"build-stages.json is absent) — a run with nothing to run is not a green one")
    except (OSError, ValueError) as e:
        raise Refusal(f".github/build-stages.json is unreadable: {e}")
    return validate_stages(doc)


# The shared-cache entries a stage may DECLARE it reads (`needs`), each bound read-only into its
# sandbox: the plugins layer, its repository's own compile outputs, the NuGet package cache.
STAGE_NEEDS = {"plugins", "outputs", "nuget"}


def validate_stages(doc: object) -> list[dict]:
    stages = doc.get("stages") if isinstance(doc, dict) else None
    if not isinstance(stages, list) or not stages:
        raise Refusal(".github/build-stages.json must be {\"stages\": [ … ]} with at least one stage")
    seen: set[str] = set()
    out = []
    for i, s in enumerate(stages):
        if not isinstance(s, dict):
            raise Refusal(f"stage #{i} is not an object")
        name = str(s.get("name") or "")
        if not STAGE_NAME.match(name):
            raise Refusal(f"stage #{i} name {name!r} is not one path segment [a-z0-9][a-z0-9._-]* "
                          f"(its Check Run is build/<name>, or build-<channel>/<name>)")
        if name in seen:
            raise Refusal(f"stage {name!r} is declared twice")
        seen.add(name)
        kind = str(s.get("kind") or "")
        if kind not in KINDS:
            raise Refusal(f"stage {name!r} kind {kind!r} must be one of {sorted(KINDS)}")
        cmd = s.get("command")
        if not isinstance(cmd, str) or not cmd.strip():
            raise Refusal(f"stage {name!r} has no command")
        tmo = s.get("timeoutMinutes", DEFAULT_STAGE_TIMEOUT_MINUTES)
        if not isinstance(tmo, (int, float)) or not (0 < tmo <= DEFAULT_STAGE_TIMEOUT_MINUTES):
            raise Refusal(f"stage {name!r} timeoutMinutes {tmo!r} must be in (0, {DEFAULT_STAGE_TIMEOUT_MINUTES}]")
        needs = s.get("needs", [])
        if not isinstance(needs, list) or not all(isinstance(n, str) and n in STAGE_NEEDS for n in needs):
            raise Refusal(f"stage {name!r} needs {needs!r} must be a list drawn from {sorted(STAGE_NEEDS)} — "
                          f"the shared-cache entries the stage may read (its own repository's mirror always)")
        out.append({"name": name, "kind": kind, "command": cmd, "timeoutMinutes": float(tmo), "needs": list(needs)})
    return out


CHECK_PREFIX = re.compile(r"^build(-[a-z0-9][a-z0-9._-]*)?/")


def bare_stage(name: str) -> str:
    """A stage from its check name: `build/<stage>` (latest) or `build-<channel>/<stage>`."""
    return CHECK_PREFIX.sub("", name, count=1)


def select_stages(declared: list[dict], claimed: object) -> list[dict]:
    """The stages this run executes: all declared, or — when the claim names stages — those, in
    declaration order. A claimed stage the repository does not declare is a Refusal."""
    names = []
    for c in claimed or []:
        n = c.get("name") if isinstance(c, dict) else c
        if isinstance(n, str) and n:
            names.append(bare_stage(n))
    if not names:
        return declared
    known = {s["name"] for s in declared}
    missing = [n for n in names if n not in known]
    if missing:
        raise Refusal(f"the claim names stage(s) {missing} that .github/build-stages.json does not declare")
    want = set(names)
    return [s for s in declared if s["name"] in want]


# ───────────────────────────────── the build API ──────────────────────────────────────────

class Lost(Exception):
    """409: the run is claimed by another host. Stop everything for it."""


class Api:
    def __init__(self, base: str, token: str) -> None:
        self.base = base.rstrip("/")
        self.token = token

    def call(self, method: str, path: str, body: Optional[dict] = None,
             query: Optional[dict] = None) -> tuple[int, object]:
        url = self.base + path + (("?" + urllib.parse.urlencode(query)) if query else "")
        data = json.dumps(body).encode() if body is not None else None
        req = urllib.request.Request(url, data=data, method=method, headers={
            "Authorization": f"Bearer {self.token}", "Accept": "application/json",
            **({"Content-Type": "application/json"} if data is not None else {})})
        try:
            with urllib.request.urlopen(req, timeout=60) as r:
                raw = r.read().decode()
                return r.status, (json.loads(raw) if raw.strip() else None)
        except urllib.error.HTTPError as e:
            # The BODY of a refusal is part of the answer: a 409's `error` tells `stale-set` (drain)
            # from `stale-claim-id` (claim again with a fresh id). Kept beside its status code.
            try:
                raw = e.read().decode()
                return e.code, (json.loads(raw) if raw.strip() else None)
            except (OSError, ValueError):
                return e.code, None
        except (urllib.error.URLError, OSError, ValueError) as e:
            return 0, str(e)


def events_path(repo: str, sha: str, stage: str) -> str:
    return f"/{urllib.parse.quote(repo, safe='')}/{sha}/stages/{urllib.parse.quote(stage, safe='')}/events"


# THE RUN BUDGET. A run's deadline is the HOST's (MW_BUILD_RUN_DEADLINE_SECONDS, default 120 min,
# at most 720 — the contract's `budgetMinutes` range): it must cover the stages a repository
# declares, which run SEQUENTIALLY (MeshWeaver.Plugins: compile-check 40 + gate 45 = 85 min). Each
# stage's `timeoutMinutes` is a PER-STAGE CAP inside it. The run's EFFECTIVE budget is the smaller of
# the deadline and the sum of the selected stages' caps (+ RUN_MARGIN_SECONDS for checkout, merge and
# hand-over); it is DECLARED in the plan (`budgetMinutes`, so the instance's attempt deadline is
# claim time + budget + 15 min). When the sum exceeds the deadline the run still starts — stages
# often finish well inside their caps — and the stages the deadline does not reach are finished
# `failed` with "not run — the run deadline passed": red, never skipped.
# The 45-minute GitHub job cap bounds a FOLLOW job, not a run: a follower that reaches its window
# with its stage's verdict in exits with that verdict (build-follow.py); a run longer than a
# follower's window is judged on the instance's live Check Runs.
MIN_RUN_DEADLINE = 600
MAX_RUN_DEADLINE = 720 * 60
DEFAULT_RUN_DEADLINE = 7200
RUN_MARGIN_SECONDS = 600


def run_budget_seconds(stages: list[dict], deadline: float) -> int:
    """The run's effective budget: the selected stages' summed caps + a margin, never past the deadline."""
    total = sum(float(st.get("timeoutMinutes", DEFAULT_STAGE_TIMEOUT_MINUTES)) * 60 for st in stages) + RUN_MARGIN_SECONDS
    return int(min(deadline, total))


def budget_minutes(seconds: float) -> int:
    """`budgetMinutes` for the plan: whole minutes, rounded UP, within the contract's 10..720."""
    return max(10, min(720, int(-(-seconds // 60))))

HEARTBEAT_SECONDS = 45     # < 60: the instance's lease lasts 10 min past the host's last batch


class EventSink:
    """Batches one stage's events and POSTs them as `{"host","seq","events"}`. Every batch is
    numbered 1, 2, … per stage (`seq`) and a RETRIED batch keeps its number, so the instance can
    drop a duplicate. An EMPTY batch is the heartbeat that keeps the claim's lease (`beat`). 409
    raises Lost; other failures are retried. Thread-safe: the run's heartbeat thread beats the
    sink the stage is writing to."""

    def __init__(self, api: Api, host: str, repo: str, sha: str, stage: str, channel: str,
                 log: Callable[[str], None], sleep: Callable[[float], None] = time.sleep,
                 clock: Callable[[], float] = time.monotonic, claim_id: str = "", probe: Optional[str] = None) -> None:
        self.api, self.host, self.log, self.sleep, self.clock = api, host, log, sleep, clock
        self.claim_id = claim_id   # the assignment every batch belongs to
        self.path = events_path(repo, sha, stage)
        # A probe run's identity is (repo, sha, channel, probe): its id rides on every callback.
        self.query = {"channel": channel, **({"probe": probe} if probe else {})}
        self.buf: list[dict] = []
        self.last = clock()
        self.dropped = 0
        self.seq = 0               # the last batch number ACKNOWLEDGED
        self.inflight: Optional[list[dict]] = None    # the batch sent as seq+1, frozen until acknowledged
        self.lock = threading.RLock()

    def add(self, events: Iterable[dict]) -> None:
        with self.lock:
            for e in events:
                if e.get("kind") == "line":
                    e = dict(e, text=str(e.get("text", ""))[:MAX_LINE])
                self.buf.append(e)
            if len(self.buf) > 20 * BATCH_EVENTS:      # the instance is unreachable for a long time
                over = len(self.buf) - 20 * BATCH_EVENTS
                del self.buf[:over]
                self.dropped += over
            if len(self.buf) >= BATCH_EVENTS or (self.buf and self.clock() - self.last >= BATCH_SECONDS):
                self.flush(attempts=1)
            elif self.clock() - self.last >= HEARTBEAT_SECONDS:
                self.beat()

    def _send_inflight(self) -> int:
        """POST the ONE in-flight batch. It is frozen when first sent and re-sent byte-identical
        under the same `seq` until acknowledged — events added meanwhile wait for the NEXT seq, so a
        receiver that committed a batch whose acknowledgement was lost can never be handed a
        different payload under a number it already holds."""
        status, _ = self.api.call("POST", self.path, {"host": self.host, "claimId": self.claim_id,
                                                      "seq": self.seq + 1, "events": self.inflight}, self.query)
        if status == 409:
            # Not held by this host, or a batch of an EARLIER assignment (`stale-claim`): either way
            # the run is no longer this host's — stop, kill the child, post nothing more.
            raise Lost(f"{self.path}: the run is not this host's claim any more (409)")
        # 503 = the instance cannot ingest right now (e.g. it states no redaction terms — ingestion
        # fails closed): retryable, the SAME batch with the SAME seq, never a run failure.
        if 200 <= status < 300:
            self.seq += 1
            self.inflight = None
            self.last = self.clock()
        return status

    def beat(self) -> bool:
        """The heartbeat: when nothing was posted for HEARTBEAT_SECONDS, re-send the in-flight batch,
        else flush what is buffered, else send an EMPTY batch (a batch of its own, with its own seq)."""
        with self.lock:
            if self.clock() - self.last < HEARTBEAT_SECONDS:
                return True
            if self.inflight is None and self.buf:
                return self.flush(attempts=1)
            if self.inflight is None:
                self.inflight = []
            return 200 <= self._send_inflight() < 300

    def flush(self, attempts: int = 4) -> bool:
        """POST everything buffered, one frozen batch at a time. Only FAILED posts consume
        `attempts` — a long buffer that drains in many successful batches never runs out of retries.
        Returns True when nothing is held (no in-flight batch, nothing buffered)."""
        with self.lock:
            if self.inflight is None and not self.buf:
                return True
            if self.dropped:
                self.buf.insert(0, {"kind": "line", "text": f"{self.dropped} progress event(s) were dropped "
                                                            f"while the build API was unreachable"})
                self.dropped = 0
            delay = 2.0
            failures = 0
            while (self.inflight is not None or self.buf) and failures < attempts:
                if self.inflight is None:
                    self.inflight = self.buf[:BATCH_EVENTS]
                    del self.buf[:len(self.inflight)]
                status = self._send_inflight()
                if 200 <= status < 300:
                    continue
                failures += 1
                self.log(f"events POST {self.path} → {status}; batch {self.seq + 1} held, "
                         f"{len(self.buf)} more buffered (failure {failures}/{attempts})")
                if failures < attempts:
                    self.sleep(delay)
                    delay = min(delay * 2, 60.0)
            return self.inflight is None and not self.buf


class Heartbeat:
    """Keeps the claim's lease for the whole run — checkout included — by beating whichever sink
    the run currently writes to (`target`), every `period` seconds, from its own thread."""

    def __init__(self, period: float = 15.0) -> None:
        self.target: Optional[EventSink] = None
        self.period = period
        self.stop = threading.Event()
        self.thread = threading.Thread(target=self._loop, daemon=True)

    def _loop(self) -> None:
        while not self.stop.wait(self.period):
            t = self.target
            if t is None:
                continue
            try:
                t.beat()
            except Exception:       # Lost is seen by the run's own next post; a beat never kills the run
                pass

    def __enter__(self) -> "Heartbeat":
        self.thread.start()
        return self

    def __exit__(self, *exc) -> None:
        self.stop.set()
        self.thread.join(timeout=5)


# ───────────────────────────────── the plugins layer ──────────────────────────────────────

def stage_plugins(plugins: dict, cache: str, env: dict,
                  pull: Callable[[list[str]], int], post: Callable[[str, dict], dict] = None) -> str:
    """Stage the plugins publication named by `plugins.digest` into `<cache>/plugins/<hex>/`
    (content-addressed: an existing complete directory is reused, never re-pulled). Returns the
    staged directory; raises Refusal when it cannot be staged."""
    digest = str(plugins.get("digest") or "")
    if not DIGEST.match(digest):
        raise Refusal(f"hosts/inputs names a plugins digest {digest!r} that is not sha256:<64 hex>")
    root = os.path.join(cache, "plugins")
    final = os.path.join(root, digest.split(":", 1)[1])
    if os.path.isfile(os.path.join(final, ".complete")):
        return final
    registry = env.get("MW_PLUGINS_REGISTRY", "").strip()
    if not registry:
        raise Refusal("hosts/inputs names a plugins publication but MW_PLUGINS_REGISTRY is not set — "
                      "the host cannot stage it, so it will not claim")
    source = env.get("MW_PLUGINS_SOURCE", "plugins").strip() or "plugins"
    os.makedirs(root, exist_ok=True)
    tmp = tempfile.mkdtemp(prefix=".staging-", dir=root)
    cmd = [env.get("ORAS", "oras"), "pull", f"{registry}/plugins/{source}@{digest}", "-o", tmp]
    auth_dir = tempfile.mkdtemp(prefix=".auth-", dir=root)
    try:
        config = acr_registry_config(registry, env, post or _post_form, auth_dir)
        if config:
            cmd += ["--registry-config", config]
        rc = pull(cmd)
    except Refusal:
        shutil.rmtree(tmp, ignore_errors=True)
        raise
    finally:
        shutil.rmtree(auth_dir, ignore_errors=True)      # the refresh token never outlives the pull
    if rc != 0:
        shutil.rmtree(tmp, ignore_errors=True)
        raise Refusal(f"pulling the plugins publication {digest} failed (oras exit {rc})")
    with open(os.path.join(tmp, ".complete"), "w") as fh:
        json.dump({k: plugins.get(k) for k in ("digest", "identity", "version", "sha")}, fh)
    try:
        os.rename(tmp, final)
    except OSError:
        # another replica staged the same digest first; its directory is the same content
        shutil.rmtree(tmp, ignore_errors=True)
        if not os.path.isfile(os.path.join(final, ".complete")):
            raise Refusal(f"could not move the staged plugins publication into {final}")
    return final


# ───────────────────────────────── the plugins layer, H8: the registry's bundle index ───────

BUNDLE_HASH_FIELDS = ("bundleHash", "hash", "sha256", "contentHash")


def bundle_lines(index: dict) -> list[str]:
    """The index as the sorted `package@version[@bundle-hash]` lines the plugins KEY is the digest
    of. An entry without a package or a version is a Refusal: a key over a partial list would name
    a layer the host does not hold."""
    entries = index.get("bundles") if isinstance(index, dict) else None
    if entries is None and isinstance(index, dict):
        entries = index.get("Bundles")
    if not isinstance(entries, list):
        raise Refusal("the bundle index carries no `bundles` list")
    lines = []
    for e in entries:
        if not isinstance(e, dict):
            raise Refusal("a bundle index entry is not an object")
        pkg = e.get("plugin") or e.get("Plugin") or e.get("package")
        ver = e.get("version") or e.get("Version")
        if not isinstance(pkg, str) or not REPO.match(pkg) or not isinstance(ver, str) or not ver.strip() or "@" in ver:
            raise Refusal(f"a bundle index entry names no usable package@version ({str(e)[:120]})")
        h = next((e[f] for f in BUNDLE_HASH_FIELDS if isinstance(e.get(f), str) and e[f]), None)
        lines.append(f"{pkg}@{ver.strip()}" + (f"@{h}" if h else ""))
    return sorted(set(lines))


def plugins_key_of(lines: list[str]) -> str:
    return "sha256:" + hashlib.sha256("\n".join(lines).encode()).hexdigest()


def stage_bundle_set(lines: list[str], cache: str, fetch: Callable[[str, str, str], int]) -> str:
    """Every bundle the index names, in the content-addressed cache `<cache>/bundles/<h>/<package>/`
    (h = the line's digest, so a re-published version with a new hash is a new entry and an unchanged
    one is never fetched again), and the SET as `<cache>/plugin-sets/<key-hex>/<package>` links.
    `fetch(package, version, out)` is `module-fetch`; returns the set directory."""
    root = os.path.join(cache, "bundles")
    os.makedirs(root, exist_ok=True)
    places = {}
    for line in lines:
        pkg, ver = line.split("@")[0], line.split("@")[1]
        final = os.path.join(root, hashlib.sha256(line.encode()).hexdigest()[:32])
        if not os.path.isfile(os.path.join(final, ".complete")):
            tmp = tempfile.mkdtemp(prefix=".fetch-", dir=root)
            rc = fetch(pkg, ver, tmp)
            if rc != 0 or not os.path.isdir(os.path.join(tmp, pkg)):
                shutil.rmtree(tmp, ignore_errors=True)
                raise Refusal(f"module-fetch {pkg}@{ver} failed (exit {rc}) — not claiming on a partial plugins layer")
            with open(os.path.join(tmp, ".complete"), "w") as fh:
                fh.write(line + "\n")
            try:
                os.rename(tmp, final)
            except OSError:
                shutil.rmtree(tmp, ignore_errors=True)
                if not os.path.isfile(os.path.join(final, ".complete")):
                    raise Refusal(f"could not move the fetched bundle {line} into the cache")
        places[pkg] = os.path.join(final, pkg)
    key = plugins_key_of(lines)
    sets = os.path.join(cache, "plugin-sets")
    os.makedirs(sets, exist_ok=True)
    set_dir = os.path.join(sets, key.split(":", 1)[1])
    if not os.path.isfile(os.path.join(set_dir, ".complete")):
        tmp = tempfile.mkdtemp(prefix=".set-", dir=sets)
        for pkg, place in places.items():
            os.symlink(place, os.path.join(tmp, pkg))
        with open(os.path.join(tmp, ".complete"), "w") as fh:
            fh.write("\n".join(lines) + "\n")
        try:
            os.rename(tmp, set_dir)
        except OSError:
            shutil.rmtree(tmp, ignore_errors=True)
    return set_dir


# ───────────────────────────────── the host ───────────────────────────────────────────────

class Fatal(Exception):
    """The HOST is no longer fit to claim (no sandbox, a namespace that will not empty). Exit
    non-zero; Kubernetes replaces the container. Never caught into another claim."""


def api_url_acceptable(url: str) -> bool:
    """https anywhere; plain http only to a cluster-local host (no dot, *.svc, *.cluster.local,
    localhost) — the same rule the chart's `memex.buildHosts` gate applies."""
    p = urllib.parse.urlparse(url or "")
    host = (p.hostname or "").lower()
    if p.scheme == "https" and host:
        return True
    return p.scheme == "http" and bool(host) and (
        "." not in host or host.endswith(".svc") or host.endswith(".cluster.local"))


def read_worker_token(env: dict) -> str:
    """The worker token, from the mounted Secret FILE (`BUILD_TOKEN_FILE`). Never from the
    environment: an env var is inherited by anything the host starts, and a file is not."""
    if env.get("BUILD_TOKEN"):
        raise Refusal("BUILD_TOKEN is set in the host's environment — the worker token must be a mounted "
                      "file (BUILD_TOKEN_FILE), never an environment variable a process could inherit")
    path = env.get("BUILD_TOKEN_FILE", "/run/build-worker/token")
    try:
        with open(path) as fh:
            tok = fh.read().strip()
    except OSError as e:
        raise Refusal(f"the worker token file {path} is unreadable: {e}")
    if not tok:
        raise Refusal(f"the worker token file {path} is empty")
    return tok


# What a stage process may see of the host's environment. EVERYTHING else is cleared
# (`bwrap --clearenv`); the run's own facts are added explicitly by the host.
ENV_ALLOW = ("PATH", "LANG", "LC_ALL", "TZ", "DOTNET_ROOT", "DOTNET_CLI_TELEMETRY_OPTOUT", "DOTNET_NOLOGO",
             "DOTNET_SKIP_FIRST_TIME_EXPERIENCE", "SSL_CERT_FILE", "SSL_CERT_DIR", "MeshWeaverRefs",
             "MW_BUILD_GATE_HOST", "MW_BUILD_PORTAL_DOTNET")
SCRATCH = "/scratch"


def sandbox_env(host_env: dict, extra: dict, cache: str) -> dict:
    """The explicit allow-list a stage gets. A key that looks like a credential is refused even when
    the host itself adds it — the worker token can never reach a child, by construction."""
    env = {k: host_env[k] for k in ENV_ALLOW if host_env.get(k)}
    env.update({"HOME": f"{SCRATCH}/home", "TMPDIR": f"{SCRATCH}/tmp",
                # NuGet writes its package folder; the shared cache is READ-ONLY, so it is the
                # fallback folder and the writable folder is the stage's own scratch.
                "NUGET_PACKAGES": f"{SCRATCH}/nuget", "NUGET_FALLBACK_PACKAGES": os.path.join(cache, "nuget")})
    env.update(extra)
    leaked = [k for k in env if SECRET_ENV.search(k)]
    if leaked:
        raise Refusal(f"refusing to hand a stage credential-shaped variables: {sorted(leaked)}")
    return env


class Sandbox:
    """`bwrap` per stage: user, PID, mount, IPC and UTS namespaces; `--die-with-parent`; a fresh
    /proc; `--clearenv` + the allow-list; the system and the caches bound READ-ONLY; writable only
    the run's checkout, its outputs directory and a tmpfs scratch. No network namespace — a build
    restores packages and fetches sources; the NetworkPolicy bounds where it can go."""

    SYSTEM_RO = ("/usr", "/bin", "/sbin", "/lib", "/lib32", "/lib64", "/libx32", "/etc", "/opt")

    def __init__(self, bwrap: str, readonly: Iterable[str]) -> None:
        self.bwrap = bwrap
        self.readonly = tuple(readonly)

    def argv(self, command: str, checkout: str, outputs: str, env: dict,
             readonly: Iterable[str] = (), writable: Iterable[str] = ()) -> list[str]:
        """`readonly` are the cache entries THIS stage may read (its own repository's mirror and the
        dependency artifacts it declares) — never the cache root; `writable` the run's extra output
        directories (artifacts) beside the checkout and the outputs."""
        a = [self.bwrap, "--die-with-parent", "--new-session",
             "--unshare-user", "--unshare-pid", "--unshare-ipc", "--unshare-uts", "--unshare-cgroup-try",
             "--clearenv"]
        for k in sorted(env):
            a += ["--setenv", k, env[k]]
        for p in self.SYSTEM_RO + self.readonly + tuple(readonly):
            a += ["--ro-bind-try", p, p]
        a += ["--proc", "/proc", "--dev", "/dev", "--tmpfs", "/tmp",
              "--tmpfs", SCRATCH, "--dir", f"{SCRATCH}/home", "--dir", f"{SCRATCH}/tmp", "--dir", f"{SCRATCH}/nuget",
              "--bind", checkout, checkout, "--bind", outputs, outputs]
        for w in writable:
            a += ["--bind", w, w]
        a += ["--chdir", checkout, "--", "/bin/bash", "-c", command]
        return a

    def probe(self, run: Callable[[list[str]], tuple[int, str]]) -> None:
        """Create the namespaces once at startup. If the node does not allow them, the host REFUSES
        to start, naming why — it never runs a stage unsandboxed."""
        d = tempfile.mkdtemp(prefix="bwrap-probe-")
        try:
            rc, err = run(self.argv("test \"$$\" -le 3 && test ! -e /run/build-worker", d, d, {"PATH": "/usr/bin:/bin"}))
        except OSError as e:
            rc, err = 127, str(e)
        finally:
            shutil.rmtree(d, ignore_errors=True)
        if rc != 0:
            raise Fatal(f"the sandbox cannot be created on this node (bwrap exit {rc}: {err.strip()[-300:]}) — "
                        f"user/PID/mount namespaces are required; the host refuses to run any stage unsandboxed")


# The places PR code must NOT reach from a stage, probed from INSIDE a sandbox at host start
# (Doc/Architecture/BuildHosts §2): the instance metadata service (node-identity tokens), a
# cluster-private address, and the Kubernetes API. Any of them ANSWERING — any HTTP status, even a
# 401/403 — is a hole in the NetworkPolicy, and the host refuses to start naming it.
BOUNDARY_PROBES = (
    ("instance metadata (169.254.169.254)", "http://169.254.169.254/metadata/instance?api-version=2021-02-01", {"Metadata": "true"}),
    ("a cluster-private address", None, {}),          # MW_BUILD_BOUNDARY_PRIVATE_URL, default below
    ("the Kubernetes API (kubernetes.default.svc)", "https://kubernetes.default.svc/version", {}),
)
DEFAULT_PRIVATE_PROBE = "http://10.0.0.1/"
BOUNDARY_SCRIPT = r"""
import json, ssl, sys, urllib.request, urllib.error
out = {}
for name, url, headers in json.loads(sys.argv[1]):
    try:
        ctx = ssl.create_default_context(); ctx.check_hostname = False; ctx.verify_mode = ssl.CERT_NONE
        urllib.request.urlopen(urllib.request.Request(url, headers=headers), timeout=4, context=ctx)
        out[name] = "answered"
    except urllib.error.HTTPError as e:
        out[name] = "answered (HTTP %d)" % e.code
    except Exception as e:
        out[name] = "blocked (%s)" % type(e).__name__
print(json.dumps(out))
"""


def boundary_problems(results: dict) -> list[str]:
    """The probes that ANSWERED — each one a boundary the NetworkPolicy does not hold."""
    return sorted(f"{name}: {r}" for name, r in results.items() if str(r).startswith("answered"))


def boundary_check(sandbox, env: dict, run: Callable[[list[str]], tuple[int, str]]) -> None:
    """Probe the boundary from inside a sandbox (the place a stage would try from). The host REFUSES
    TO START when any probe answers, or when the probe itself cannot report — a check that could not
    run is not a check that passed."""
    probes = [(name, url or env.get("MW_BUILD_BOUNDARY_PRIVATE_URL") or DEFAULT_PRIVATE_PROBE, headers)
              for name, url, headers in BOUNDARY_PROBES]
    d = tempfile.mkdtemp(prefix="boundary-")
    try:
        argv = sandbox.argv("python3 -c " + shlex_quote(BOUNDARY_SCRIPT) + " " + shlex_quote(json.dumps(probes)),
                            d, d, {"PATH": "/usr/bin:/bin"})
        rc, out = run(argv)
    finally:
        shutil.rmtree(d, ignore_errors=True)
    try:
        results = json.loads(out.strip().splitlines()[-1]) if rc == 0 and out.strip() else None
    except ValueError:
        results = None
    if not isinstance(results, dict) or len(results) != len(probes):
        raise Fatal(f"the boundary self-check could not run inside the sandbox (exit {rc}): {out.strip()[-300:]} — "
                    f"the host refuses to start on an unmeasured boundary")
    holes = boundary_problems(results)
    if holes:
        raise Fatal("the network boundary does not hold — from inside a stage's sandbox these ANSWERED: "
                    + "; ".join(holes) + ". The host refuses to start: fix the build-host NetworkPolicy "
                    "(169.254.0.0/16, 168.63.129.16 and the private ranges must be unreachable)")


def scoping_check(host, run: Callable[[list[str]], tuple[int, str]]) -> None:
    """A PUBLIC repository's sandbox must not be able to read a PRIVATE repository's mirror or
    cache entry — nor the cache root. Seeded entries for two scratch repositories are created in the
    real cache, a sandbox is built for the public one exactly as for a stage that declares every
    `needs`, and it must read its own entry and fail to read the other's and to list the root. Any
    other outcome — including a check that cannot run — and the host refuses to start."""
    cache = host.cache
    pub, priv = ".boundary-public", ".boundary-private"
    entries = {
        "pub-mirror": os.path.join(cache, "git", f"{pub}.git", "PUBLIC"),
        "priv-mirror": os.path.join(cache, "git", f"{priv}.git", "SECRET"),
        "priv-outputs": os.path.join(outputs_root(cache, priv), "SECRET"),
        "root-canary": os.path.join(cache, ".boundary-root-canary"),
    }
    for f in entries.values():
        os.makedirs(os.path.dirname(f), exist_ok=True)
        with open(f, "w") as fh:
            fh.write("boundary\n")
    d = tempfile.mkdtemp(prefix="scoping-", dir=host.work if os.path.isdir(host.work) else None)
    try:
        ro = host.stage_readonly(pub, {"needs": sorted(STAGE_NEEDS)})
        if any(os.path.realpath(p) in (os.path.realpath(cache), os.path.realpath(os.path.join(cache, "outputs")),
                                       os.path.realpath(os.path.join(cache, "git"))) for p in ro):
            raise Fatal(f"a stage would be bound a cache ROOT ({ro}) — the host refuses to start")
        script = (f"cat {shlex_quote(entries['pub-mirror'])} >/dev/null || {{ echo own-unreadable; exit 3; }}; "
                  f"if cat {shlex_quote(entries['priv-mirror'])} >/dev/null 2>&1; then echo private-mirror-readable; exit 4; fi; "
                  f"if cat {shlex_quote(entries['priv-outputs'])} >/dev/null 2>&1; then echo private-outputs-readable; exit 5; fi; "
                  f"if cat {shlex_quote(entries['root-canary'])} >/dev/null 2>&1; then echo cache-root-readable; exit 6; fi; echo scoped")
        rc, out = run(host.sandbox.argv(script, d, d, {"PATH": "/usr/bin:/bin"}, readonly=ro))
    finally:
        shutil.rmtree(d, ignore_errors=True)
    if rc != 0 or "scoped" not in out:
        reason = {3: "its own mirror is not readable", 4: "a PRIVATE repository's mirror is readable",
                  5: "a PRIVATE repository's compile outputs are readable", 6: "a file at the cache ROOT is readable"}.get(rc, out.strip()[-200:])
        raise Fatal(f"the cache-scoping self-check failed: from a public repository's sandbox, {reason} — "
                    f"the host refuses to start")


def shlex_quote(v: str) -> str:
    import shlex
    return shlex.quote(v)


def pid_namespace_leftovers(proc_root: str = "/proc") -> list[int]:
    """Processes visible to the host that live in ANOTHER pid namespace than the host's — what a
    finished stage left behind. The host runs one run at a time and starts nothing else in a
    namespace, so after a stage this must be empty."""
    try:
        mine = os.readlink(os.path.join(proc_root, "self", "ns", "pid"))
    except OSError:
        return []
    out = []
    for name in os.listdir(proc_root):
        if not name.isdigit():
            continue
        try:
            ns = os.readlink(os.path.join(proc_root, name, "ns", "pid"))
        except OSError:
            continue
        if ns != mine and not _is_zombie(proc_root, name):
            out.append(int(name))
    return sorted(out)


def _is_zombie(proc_root: str, pid: str) -> bool:
    """A zombie has already exited: it runs nothing and holds nothing but its process-table slot.
    A stage's sandbox processes are reparented to the host when bwrap exits, and the host runs as
    the container's PID 1 (`exec python3 build-run.py`), so they stay ZOMBIES until it reaps them
    (measured on the user-namespace pool, 2026-10-27: `bwrap` State Z, PPid 1, after the startup
    probe). Counting them as survivors made the host refuse to claim on every start."""
    try:
        # errors="replace": a task name may hold non-UTF-8 bytes, and the name is read before State
        # — a decode error must never abort cleanup (review of #7287).
        with open(os.path.join(proc_root, pid, "status"), encoding="utf-8", errors="replace") as fh:
            for line in fh:
                if line.startswith("State:"):
                    return line.split()[1:2] == ["Z"]
    except OSError:
        return False
    return False


def reap_children() -> int:
    """Reap every exited child without blocking — what PID 1 must do for the processes reparented
    to it. Returns how many were reaped."""
    reaped = 0
    while True:
        try:
            pid, _ = os.waitpid(-1, os.WNOHANG)
        except ChildProcessError:
            return reaped
        if pid == 0:
            return reaped
        reaped += 1


CONTENT_KEY = re.compile(r"^[0-9a-f]{16,128}$")


CACHE_KEY_RECIPE = 1
CACHE_KEY_FILE = ".cache-key.json"


def _sha256_file(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def _inside(root: str, rel: str) -> str:
    """`rel` resolved under `root`, refusing an absolute path, a `..` escape or a symlink out."""
    if not isinstance(rel, str) or not rel or os.path.isabs(rel):
        raise Refusal(f"{rel!r} is not a repository-relative path")
    full = os.path.realpath(os.path.join(root, rel))
    if not full.startswith(os.path.realpath(root).rstrip("/") + "/"):
        raise Refusal(f"{rel!r} escapes the checkout")
    if not os.path.isfile(full):
        raise Refusal(f"{rel!r} is not a file in the checkout")
    return full


def nodetype_cache_key(checkout: str, spec: dict, env: dict) -> str:
    """A NodeType's cross-run compile key: its DECLARED source set (each file's content hash), the
    reference-set identity (framework identity, portal digest, the plugins layer's key), the
    canonical compile-check OPTIONS and allow-list, and the compiler (the tester digest). An input
    the host cannot read is a Refusal — never a key over less than was compiled."""
    sources = spec.get("sources")
    if not isinstance(sources, list) or not sources:
        raise Refusal("a nodetype key names its sources")
    refset = {"frameworkIdentity": env.get("MW_BUILD_FRAMEWORK_IDENTITY", ""),
              "portalDigest": env.get("MW_BUILD_PORTAL_DIGEST", ""),
              "pluginsKey": env.get("MW_PLUGINS_KEY", "")}
    compiler = env.get("MW_BUILD_TESTER_DIGEST", "")
    if not refset["frameworkIdentity"] or not DIGEST.match(refset["portalDigest"]) or not DIGEST.match(compiler):
        raise Refusal("the reference-set identity (framework identity, portal and tester digests) is not known")
    options = spec.get("options", "")
    if not isinstance(options, str):
        raise Refusal("a nodetype key's options are the canonical compile-check option string")
    allow = spec.get("allowList")
    doc = {"kind": "nodetype", "recipe": CACHE_KEY_RECIPE,
           "sources": {rel: _sha256_file(_inside(checkout, rel)) for rel in sorted(set(sources))},
           "refset": refset, "options": options, "compiler": compiler,
           "allowList": {allow: _sha256_file(_inside(checkout, allow))} if allow else None}
    return hashlib.sha256(json.dumps(doc, sort_keys=True, separators=(",", ":")).encode()).hexdigest()


def module_cache_key(checkout: str, spec: dict, env: dict,
                     run: Callable[[list[str]], tuple[int, str]] = None) -> str:
    """A module's cross-run key: core's own `module-build-key.py` (sources, the dependency closure,
    Directory.Build.props/.targets, Directory.Packages.props, the project file, the toolchain
    identity), run with the HOST's tester and portal digests — never a key the stage asserts."""
    entry = spec.get("entry")
    if not isinstance(entry, dict):
        raise Refusal("a module key names its matrix entry")
    script = os.path.join(os.path.dirname(os.path.abspath(__file__)), "module-build-key.py")
    argv = [sys.executable, script, "--root", checkout, "--entry", json.dumps(entry, sort_keys=True),
            "--tester-digest", env.get("MW_BUILD_TESTER_DIGEST", ""),
            "--platform-digest", env.get("MW_BUILD_PORTAL_DIGEST", ""),
            "--platform-ref", env.get("MW_BUILD_PLATFORM_REF", "")]
    if run is None:
        def run(a):
            r = subprocess.run(a, capture_output=True, text=True, timeout=300)
            return r.returncode, (r.stdout or "") if r.returncode == 0 else (r.stderr or "")
    rc, out = run(argv)
    key = out.strip().splitlines()[-1].strip() if rc == 0 and out.strip() else ""
    if rc != 0 or not CONTENT_KEY.match(key):
        raise Refusal(f"module-build-key.py could not key the module (exit {rc}): {out.strip()[-200:]}")
    return key


def cache_key_of(checkout: str, spec: object, env: dict, run=None) -> str:
    if not isinstance(spec, dict):
        raise Refusal(f"{CACHE_KEY_FILE} is not an object")
    if spec.get("kind") == "nodetype":
        return nodetype_cache_key(checkout, spec, env)
    if spec.get("kind") == "module":
        return module_cache_key(checkout, spec, env, run)
    raise Refusal(f"{CACHE_KEY_FILE} kind must be nodetype or module (got {spec.get('kind')!r})")


def refset_key(tester_digest: str, portal_digest: str) -> str:
    """The staged reference set's key: BOTH images' digests — the compiler (tester) and the
    reference assemblies (portal) — never one of them alone."""
    return hashlib.sha256(f"tester={tester_digest}\nportal={portal_digest}".encode()).hexdigest()[:40]


def outputs_root(cache: str, repo: str) -> str:
    """A repository's OWN compile-output cache: outputs are scoped by repository, so a stage of one
    repository can never be bound another's compiled bytes (a private repo's into a public run)."""
    return os.path.join(cache, "outputs", repo)


def promote_outputs(outputs: str, cache: str, log: Callable[[str], None],
                    verify: Callable[[str], Optional[str]] = None, repo: str = "") -> tuple[int, int, list[str]]:
    """Copy a TRUNK stage's content-addressed outputs (`<outputs>/<key>/…`) into the shared cache
    (`<cache>/outputs/<key>/`). A key already present is kept (same key ⇒ same content); a name
    that is not a content key is not promoted. Symlinks are copied as links, never followed.
    Returns (promoted, kept, refused names)."""
    root = outputs_root(cache, repo) if repo else os.path.join(cache, "outputs")
    promoted = kept = 0
    refused: list[str] = []
    if not os.path.isdir(outputs):
        return 0, 0, []
    for name in sorted(os.listdir(outputs)):
        src = os.path.join(outputs, name)
        if not CONTENT_KEY.match(name) or os.path.islink(src) or not os.path.isdir(src):
            refused.append(name)
            continue
        problem = verify(src) if verify else None
        if problem:
            log(f"cache: {name} NOT promoted — {problem}")
            refused.append(name)
            continue
        dst = os.path.join(root, name)
        if os.path.isdir(dst):
            kept += 1
            continue
        os.makedirs(root, exist_ok=True)
        tmp = tempfile.mkdtemp(prefix=f".promote-{name[:12]}-", dir=root)
        shutil.copytree(src, os.path.join(tmp, "c"), symlinks=True)
        try:
            os.rename(os.path.join(tmp, "c"), dst)
            promoted += 1
        except OSError:
            kept += 1          # another host promoted the same key first
        finally:
            shutil.rmtree(tmp, ignore_errors=True)
    return promoted, kept, refused


def git_auth_env(token: Optional[str]) -> dict:
    """Git credentials through the ENVIRONMENT (never argv, never a URL, never the checkout)."""
    env = {"GIT_TERMINAL_PROMPT": "0"}
    if token:
        basic = base64.b64encode(f"x-access-token:{token}".encode()).decode()
        env.update({"GIT_CONFIG_COUNT": "1", "GIT_CONFIG_KEY_0": "http.https://github.com/.extraheader",
                    "GIT_CONFIG_VALUE_0": f"AUTHORIZATION: basic {basic}"})
    return env


def default_checkout(repo: str, sha: str, refs: list, dest: str, token: Optional[str],
                     env: dict, log: Callable[[str], None], git: Callable[..., None] = None,
                     deadline: Optional[float] = None) -> None:
    """The HOST checks out (the stage never holds the installation token), through the shared git
    MIRROR `<cache>/git/<repo>.git`: `git clone --mirror` on first use, `git fetch` before every
    checkout, then a clone `--reference`-ing the mirror into the run's directory, so a run fetches
    only what the mirror lacks. The mirror is written by the host alone — it is bound READ-ONLY into
    every sandbox — and only from GitHub. Several replicas share it: an exclusive file lock
    serialises its updates."""
    base = env.get("MW_BUILD_GIT_BASE", "https://github.com/Systemorph").rstrip("/")
    url = f"{base}/{repo}.git"
    mirror_root = os.path.join(env.get("MW_BUILD_CACHE", "/cache"), "git")
    mirror = os.path.join(mirror_root, f"{repo}.git")
    genv = {**{k: env[k] for k in ("PATH", "LANG", "SSL_CERT_FILE", "SSL_CERT_DIR") if env.get(k)},
            "HOME": os.path.dirname(dest), **git_auth_env(token)}

    def run_git(*args: str) -> None:
        left = None if deadline is None else deadline - time.monotonic()
        if left is not None and left <= 0:
            raise Refusal("the run's deadline passed during the checkout")
        try:
            # subprocess.run kills and reaps the child when the timeout expires
            r = subprocess.run(["git", *args], env=genv, capture_output=True, text=True, timeout=left)
        except subprocess.TimeoutExpired:
            raise Refusal(f"git {args[0] if args[0] != '-C' else args[2]} did not finish inside the run's deadline — killed")
        if r.returncode != 0:
            raise Refusal(f"git {args[0] if args[0] != '-C' else args[2]} failed (exit {r.returncode}): "
                          f"{(r.stderr or '').strip()[-400:]}")
    git = git or run_git
    wanted = [sha] + [r for r in (refs or []) if isinstance(r, str) and r]

    os.makedirs(mirror_root, exist_ok=True)
    with open(os.path.join(mirror_root, f".{repo}.lock"), "w") as lock:
        _flock(lock)
        if not os.path.isfile(os.path.join(mirror, "HEAD")):
            tmp = tempfile.mkdtemp(prefix=f".{repo}-", dir=mirror_root)
            try:
                git("clone", "--mirror", "--quiet", url, os.path.join(tmp, "m"))
                os.rename(os.path.join(tmp, "m"), mirror)
                log(f"git mirror created: {mirror}")
            finally:
                shutil.rmtree(tmp, ignore_errors=True)
        else:
            git("-C", mirror, "fetch", "--quiet", "--prune", "origin")
        # A PR head (refs/pull/N/head) or the exact sha may not be on a mirrored ref yet.
        git("-C", mirror, "fetch", "--quiet", "origin", *wanted)
    git("clone", "--no-checkout", "--quiet", "--reference", mirror, url, dest)
    git("-C", dest, "fetch", "--quiet", "origin", *wanted)
    git("-C", dest, "checkout", "--quiet", "--detach", sha)


def _flock(fh) -> None:
    try:
        import fcntl
        fcntl.flock(fh.fileno(), fcntl.LOCK_EX)
    except (ImportError, OSError):
        pass


# ───────────────────────────────── the one credential: AcrPull by workload identity ───────

def acr_registry_config(registry: str, env: dict, post: Callable[[str, dict], dict], dest_dir: str) -> Optional[str]:
    """A docker config.json authorising ORAS to PULL from `registry`, from the host's Azure
    WORKLOAD IDENTITY (the ServiceAccount's federated token, AcrPull only) — the hosts' ONE
    credential, read-only. Returns None when no workload identity is projected (an anonymous pull).

    federated token (AZURE_FEDERATED_TOKEN_FILE) → Entra access token (client-credentials with a
    client assertion) → ACR refresh token (`/oauth2/exchange`) → `identitytoken` in a config file
    written into the HOST's private directory. The token file is mounted into the host container
    only and is never bound into a sandbox; the config is deleted after the pull."""
    token_file = env.get("AZURE_FEDERATED_TOKEN_FILE", "")
    client = env.get("AZURE_CLIENT_ID", "")
    tenant = env.get("AZURE_TENANT_ID", "")
    if not token_file:
        return None
    if not client or not tenant:
        raise Refusal("a workload-identity token is projected but AZURE_CLIENT_ID / AZURE_TENANT_ID are not — "
                      "the ServiceAccount's azure.workload.identity/client-id annotation is missing")
    try:
        with open(token_file) as fh:
            assertion = fh.read().strip()
    except OSError as e:
        raise Refusal(f"the workload-identity token {token_file} is unreadable: {e}")
    authority = (env.get("AZURE_AUTHORITY_HOST") or "https://login.microsoftonline.com/").rstrip("/")
    aad = post(f"{authority}/{tenant}/oauth2/v2.0/token", {
        "grant_type": "client_credentials", "client_id": client,
        "scope": "https://management.azure.com/.default",   # the audience ACR's /oauth2/exchange accepts
        "client_assertion_type": "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
        "client_assertion": assertion})
    access = aad.get("access_token") if isinstance(aad, dict) else None
    if not access:
        raise Refusal("Entra refused the workload-identity exchange (no access_token) — is the federated "
                      "credential registered for this ServiceAccount?")
    acr = post(f"https://{registry}/oauth2/exchange", {
        "grant_type": "access_token", "service": registry, "tenant": tenant, "access_token": access})
    refresh = acr.get("refresh_token") if isinstance(acr, dict) else None
    if not refresh:
        raise Refusal(f"{registry} refused the token exchange (no refresh_token) — does the identity hold AcrPull?")
    os.makedirs(dest_dir, mode=0o700, exist_ok=True)
    path = os.path.join(dest_dir, "config.json")
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(fd, "w") as fh:
        json.dump({"auths": {registry: {"identitytoken": refresh}}}, fh)
    return path


def _registry_get(url: str, key: str) -> tuple[int, object]:
    req = urllib.request.Request(url, headers={"Authorization": f"Bearer {key}", "Accept": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            return r.status, json.loads(r.read().decode() or "null")
    except urllib.error.HTTPError as e:
        return e.code, None
    except (urllib.error.URLError, OSError, ValueError) as e:
        return 0, str(e)


def _post_form(url: str, form: dict) -> dict:
    data = urllib.parse.urlencode(form).encode()
    req = urllib.request.Request(url, data=data, method="POST",
                                 headers={"Content-Type": "application/x-www-form-urlencoded", "Accept": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=30) as r:
            return json.loads(r.read().decode() or "{}")
    except urllib.error.HTTPError as e:
        return {"error": f"HTTP {e.code}"}
    except (urllib.error.URLError, OSError, ValueError) as e:
        return {"error": str(e)}


def _git_run(args: list[str], env: dict) -> tuple[int, str]:
    r = subprocess.run(["git", *args], env=env, capture_output=True, text=True, timeout=900)
    return r.returncode, (r.stdout or "") + (r.stderr or "")


def _run_capture_stdout(argv: list[str], timeout: float = 1800) -> tuple[int, str]:
    """A HOST helper (ci-artifact-store.py): no credential but the Azure workload identity's.
    `timeout` is the caller's remaining RUN budget; past it the helper is killed (TimeoutExpired)."""
    r = subprocess.run(argv, capture_output=True, text=True, timeout=max(1.0, timeout),
                       env={k: v for k, v in os.environ.items() if not SECRET_ENV.search(k) or k.startswith("AZURE_")})
    return r.returncode, (r.stdout or "") + ("\n" + r.stderr if r.returncode else "")


def _run_capture(argv: list[str]) -> tuple[int, str]:
    r = subprocess.run(argv, capture_output=True, text=True, env={"PATH": os.environ.get("PATH", "/usr/bin:/bin")},
                       timeout=120)
    return r.returncode, (r.stderr or "") + (r.stdout or "")      # stdout LAST: callers read its final line


class Host:
    def __init__(self, env: dict, api: Api, *, log: Callable[[str], None] = None,
                 pull: Callable[[list[str]], int] = None, checkout: Callable = None,
                 sandbox=None, leftovers: Callable[[], list[int]] = None, reap: Callable[[], int] = None,
                 registry_get: Callable[[str, str], tuple[int, object]] = None,
                 fetch_runner: Callable[[list[str], dict], int] = None,
                 store_runner: Callable[..., tuple[int, str]] = None,
                 git_runner: Callable[[list[str], dict], tuple[int, str]] = None,
                 sleep: Callable[[float], None] = time.sleep,
                 clock: Callable[[], float] = time.monotonic) -> None:
        self.env = env
        self.api = api
        self.log = log or (lambda s: print(f"build-run: {s}", flush=True))
        self.pull = pull or (lambda cmd: subprocess.run(cmd).returncode)
        self.checkout = checkout or default_checkout
        self.cache = env.get("MW_BUILD_CACHE", "/cache")
        self.work = env.get("MW_BUILD_WORK", "/work")
        # NEVER the cache root: what a stage may read of the shared cache is bound per stage
        # (stage_readonly) — its own repository's mirror and the dependency artifacts it declares.
        self.sandbox = sandbox or Sandbox(env.get("BWRAP", "bwrap"), [
            p for p in (env.get("MW_BUILD_GATE_HOST"), env.get("MeshWeaverRefs"), "/portal", "/tester", "/tools") if p])
        self.leftovers = leftovers or pid_namespace_leftovers
        self.reap = reap or reap_children
        self.registry_get = registry_get or _registry_get
        self.store_runner = store_runner or _run_capture_stdout
        self.git_runner = git_runner or _git_run
        self.fetch_runner = fetch_runner or (lambda argv, env: subprocess.run(argv, env=env, timeout=600).returncode)
        self.sleep = sleep
        self.clock = clock
        self.stopping = False
        self.host = env.get("MW_BUILD_HOST") or socket.gethostname()
        self.channel = env.get("MW_BUILD_CHANNEL", "").strip()
        self.platform_version = env.get("MW_BUILD_PLATFORM_VERSION", "").strip()
        self.identity = env.get("MW_BUILD_FRAMEWORK_IDENTITY", "").strip()
        if not self.identity and env.get("MW_BUILD_IDENTITY_FILE"):
            try:
                with open(env["MW_BUILD_IDENTITY_FILE"]) as fh:
                    self.identity = (fh.read().split() or [""])[0]
            except OSError:
                self.identity = ""
        self.ready_file = env.get("MW_BUILD_READY_FILE", "/tmp/build-host-ready")
        try:
            self.run_deadline = float(env.get("MW_BUILD_RUN_DEADLINE_SECONDS", str(DEFAULT_RUN_DEADLINE)) or DEFAULT_RUN_DEADLINE)
        except ValueError:
            self.run_deadline = float(DEFAULT_RUN_DEADLINE)
        self.plugins_digest: Optional[str] = None
        self.plugins_dir: Optional[str] = None
        self.plugins_known = False
        self.held: list[EventSink] = []
        self.artifacts_dir = ""
        self.plugins_key: Optional[str] = None
        self.heartbeat: Optional[Heartbeat] = None
        self.run_sinks: dict[str, EventSink] = {}
        self.claim_id = ""
        self.probe: Optional[str] = None

    def preflight(self) -> None:
        missing = []
        if not api_url_acceptable(self.api.base):
            missing.append("BUILD_API (the operations instance's /api/builds: https, or http to a "
                           "cluster-local host — the worker token rides on it)")
        if not self.api.token:
            missing.append("the worker token (BUILD_TOKEN_FILE, the mounted Hosting:Builds:WorkerToken)")
        if not CHANNEL.match(self.channel):
            missing.append("MW_BUILD_CHANNEL (latest | stable)")
        if not self.platform_version:
            missing.append("MW_BUILD_PLATFORM_VERSION (the set this host runs)")
        if not self.identity:
            missing.append("MW_BUILD_FRAMEWORK_IDENTITY or a readable MW_BUILD_IDENTITY_FILE")
        if not (MIN_RUN_DEADLINE <= self.run_deadline <= MAX_RUN_DEADLINE):
            missing.append(f"MW_BUILD_RUN_DEADLINE_SECONDS within [{MIN_RUN_DEADLINE}, {MAX_RUN_DEADLINE}] — the RUN budget "
                           f"(the contract's budgetMinutes is 10..720), which must cover the stages a repository declares")
        if missing:
            raise Refusal("the host cannot start — provide: " + "; ".join(missing))

    # -- the namespace must be empty before anything is claimed -----------------------------
    def assert_clean(self) -> None:
        self.reap()
        left = self.leftovers()
        if not left:
            return
        for pid in left:
            try:
                os.kill(pid, signal.SIGKILL)
            except (OSError, ProcessLookupError):
                pass
        self.sleep(1.0)
        self.reap()
        left = self.leftovers()
        if left:
            raise Fatal(f"processes of a finished stage survive in another pid namespace ({left[:10]}) — "
                        f"the host will not claim again; the container is replaced")

    # -- step 1: the plugins layer, refreshed BEFORE any claim -------------------------------
    def refresh_inputs(self) -> bool:
        if self.env.get("MW_REGISTRY_URL"):
            return self.refresh_bundles()
        return self.refresh_seal()

    def refresh_bundles(self) -> bool:
        """H8: the plugins layer from the REGISTRY's bundle index — what an instance on this framework
        identity would install — read before every claim with the host's read-only `mwi_` instance
        key (a file in the host container, never bound into a sandbox)."""
        url = self.env["MW_REGISTRY_URL"].rstrip("/")
        key_file = self.env.get("MW_REGISTRY_KEY_FILE", "/run/registry-key/key")
        try:
            with open(key_file) as fh:
                instance_key = fh.read().strip()
        except OSError as e:
            self.log(f"the registry instance key {key_file} is unreadable ({e}) — NOT claiming")
            return False
        status, index = self.registry_get(f"{url}/api/plugins/bundles/index.json", instance_key)
        if status != 200 or not isinstance(index, dict):
            self.log(f"the registry's bundle index is unreadable (HTTP {status}) — NOT claiming this cycle")
            return False
        # The index states the framework it serves (`frameworkMvid`, the identity a bundle is built
        # against). MISSING is a refusal as much as DIFFERENT: an index that cannot say which
        # framework its bundles are for cannot be staged for this host's.
        served_for = index.get("frameworkMvid") or index.get("frameworkIdentity") or index.get("identity")
        if not served_for:
            self.log("the registry's bundle index names no framework identity (frameworkMvid) — NOT claiming")
            return False
        if served_for != self.identity:
            self.log(f"the registry serves bundles for identity {served_for}, this host runs {self.identity} — NOT claiming")
            return False
        try:
            lines = bundle_lines(index)
            key = plugins_key_of(lines)
            if key != self.plugins_key or not self.plugins_dir:
                d = stage_bundle_set(lines, self.cache, lambda pkg, ver, out: self.module_fetch(url, instance_key, pkg, ver, out))
                self.log(f"plugins layer staged from the registry index: {key[:19]}… ({len(lines)} bundle(s))")
                self.plugins_dir = d
        except Refusal as r:
            self.log(f"{r} — NOT claiming this cycle")
            return False
        self.plugins_key, self.plugins_digest, self.plugins_known = key, None, True
        return True

    def module_fetch(self, registry: str, instance_key: str, package: str, version: str, out: str) -> int:
        """core's `meshweaver-plugin-build module-fetch` — the one implementation that validates a
        bundle's paths before writing. The key reaches it through the environment only (`--key-env`),
        never argv; it is a HOST helper, never run inside a stage's sandbox."""
        cmd = (self.env.get("MW_PLUGIN_BUILD") or "dotnet /opt/build-host/plugin-build/MeshWeaver.Plugin.Build.dll").split()
        env = {k: self.env[k] for k in ("PATH", "HOME", "DOTNET_ROOT", "SSL_CERT_FILE", "SSL_CERT_DIR") if self.env.get(k)}
        env.update({"MW_REGISTRY_INSTANCE_KEY": instance_key, "DOTNET_CLI_TELEMETRY_OPTOUT": "1", "DOTNET_NOLOGO": "1"})
        return self.fetch_runner([*cmd, "module-fetch", package, "--registry", registry,
                                  "--key-env", "MW_REGISTRY_INSTANCE_KEY", "--version", version, "--out", out], env)

    def refresh_seal(self) -> bool:
        status, body = self.api.call("GET", "/hosts/inputs")
        if status != 200 or not isinstance(body, dict):
            self.log(f"hosts/inputs unreadable (HTTP {status}) — NOT claiming this cycle: a run must "
                     f"record the plugins layer that compiled it")
            return False
        plugins = body.get("plugins")
        if plugins is None:
            self.plugins_digest, self.plugins_dir, self.plugins_known = None, None, True
            return True
        if not isinstance(plugins, dict):
            self.log("hosts/inputs answered a plugins value that is not an object — NOT claiming")
            return False
        digest = str(plugins.get("digest") or "")
        if digest == self.plugins_digest and self.plugins_dir and self.plugins_known:
            return True
        try:
            d = stage_plugins(plugins, self.cache, self.env, self.pull)
        except Refusal as r:
            self.log(f"{r} — NOT claiming this cycle")
            return False
        if self.plugins_digest != digest:
            self.log(f"plugins layer staged: {digest} (version {plugins.get('version')}, sha "
                     f"{str(plugins.get('sha') or '')[:12]})")
        self.plugins_digest, self.plugins_dir, self.plugins_known = digest, d, True
        return True

    def mark_ready(self) -> None:
        try:
            with open(self.ready_file, "w") as fh:
                fh.write(f"{self.platform_version} {self.plugins_digest or '-'}\n")
        except OSError as e:
            self.log(f"could not write the readiness file {self.ready_file}: {e}")

    # -- step 2: claim ------------------------------------------------------------------------
    def claim(self) -> Optional[dict]:
        """`POST /claim` with a FRESH `claimId` per claim attempt. A network error (or a 5xx) is
        retried with the SAME claimId: the instance then answers the assignment it already made, so a
        lost answer never strands a run claimed for a host that does not know it. A host keeps no
        claim across a restart — after one it claims afresh, never resumes an old claim.

        409 answers, by `error`:
          * `stale-claim-id` — that id is SPENT (its reservation names a run no longer held under this
            host and id) and nothing was handed out: mint a FRESH id and claim again, ONCE — never a
            retry of the spent id, never a loop on it;
          * `stale-set` — this host's platform set is no longer its channel's: DRAIN (stop claiming;
            the run in hand, if any, finishes; the replacement replica runs the new set)."""
        for fresh in range(2):
            body = {"host": self.host, "channel": self.channel, "platformVersion": self.platform_version,
                    "frameworkIdentity": self.identity, "pluginsDigest": self.plugins_digest,
                    "pluginsKey": self.plugins_key, "claimId": str(uuid.uuid4())}
            self.claim_id = body["claimId"]
            delay = 2.0
            status, answer = 0, None
            for attempt in range(4):
                status, answer = self.api.call("POST", "/claim", body)
                if status == 204:
                    return None
                if status == 200 and isinstance(answer, dict):
                    return answer
                if status != 0 and status < 500:
                    break
                self.log(f"claim {body['claimId']} answered HTTP {status} — retrying with the SAME claimId")
                self.sleep(delay)
                delay *= 2
            error = str(answer.get("error") or "") if status == 409 and isinstance(answer, dict) else ""
            if error.startswith("stale-claim-id"):
                if fresh == 0:
                    self.log(f"claim id {body['claimId']} is spent (stale-claim-id) — claiming again with a FRESH id")
                    continue
                self.log("a fresh claim id was answered stale-claim-id too — not claiming again this cycle")
                return None
            if error.startswith("stale-set"):
                self.log(f"the instance answers stale-set: this host's set {self.platform_version} is no longer "
                         f"channel {self.channel}'s — DRAINING (no further claims)")
                self.stopping = True
                return None
            self.log(f"claim answered HTTP {status} — retrying after a pause")
            return None
        return None

    def cycle(self) -> str:
        """One loop turn: 'stopping', 'no-inputs', 'idle', 'ran' or 'lost'. Raises Fatal."""
        if self.stopping:
            return "stopping"
        self.assert_clean()
        if not self.refresh_inputs():
            return "no-inputs"
        self.mark_ready()
        if self.stopping:
            return "stopping"
        claim = self.claim()
        if claim is None:
            return "idle"
        return self.run(claim)

    # -- step 3/4: run ----------------------------------------------------------------------
    def run(self, claim: dict) -> str:
        repo = str(claim.get("repo") or "")
        sha = str(claim.get("sha") or "").lower()
        path = str(claim.get("path") or "")
        publish = claim.get("publish") is True
        trunk = claim.get("isTrunk") is True
        if not REPO.match(repo) or not SHA.match(sha):
            self.log(f"claim {path!r} carries an unusable repo/sha ({repo!r}, {sha!r}) — not running it")
            return "idle"
        started = self.clock()
        self.sweep_workspaces()
        workdir = tempfile.mkdtemp(prefix=f"{repo}-{sha[:12]}-", dir=self.work)
        checkout_dir = os.path.join(workdir, "src")
        outputs_dir = os.path.join(workdir, "out")
        self.artifacts_dir = os.path.join(workdir, "artifacts")
        stages: list[dict] = []
        self.run_sinks: dict[str, EventSink] = {}       # seq restarts at 1 for every assignment
        probe_id = claim.get("probe")
        self.probe = str(probe_id) if isinstance(probe_id, str) and probe_id.strip() else None
        if claim.get("claimId"):
            self.claim_id = str(claim["claimId"])
        with Heartbeat() as hb:
            self.heartbeat = hb
            outcome = "lost"
            try:
                outcome = self._run(claim, repo, sha, path, publish, trunk, started, workdir, checkout_dir,
                                    outputs_dir, stages)
                return outcome
            finally:
                self.heartbeat = None
                if outcome == "undelivered" and os.listdir(self.artifacts_dir or workdir):
                    # H9: the workspace goes only after every receipt was ACKNOWLEDGED. It is kept
                    # (and named) for the instance's re-queue, and swept once older than two deadlines.
                    self.log(f"{path}: receipts not acknowledged — keeping the workspace {workdir}")
                else:
                    shutil.rmtree(workdir, ignore_errors=True)

    def _sink_for(self, repo: str, sha: str, stage: str) -> EventSink:
        """ONE sink per stage route for the whole run, so its batch numbers are one sequence."""
        sink = self.run_sinks.get(stage)
        if sink is None:
            sink = self.run_sinks[stage] = self.sink(repo, sha, stage)
        if self.heartbeat is not None:
            self.heartbeat.target = sink
        return sink

    def _run(self, claim, repo, sha, path, publish, trunk, started, workdir, checkout_dir, outputs_dir, stages) -> str:
        artifacts_dir = self.artifacts_dir
        try:
            fallback = [c.get("name") if isinstance(c, dict) else c for c in (claim.get("stages") or [])]
            last_stage = next((bare_stage(s) for s in fallback if isinstance(s, str) and s), "verdict")
            # The lease is kept from the first second — a first clone into an empty mirror can take
            # longer than the instance's lease, and the checkout posts nothing else.
            self._sink_for(repo, sha, last_stage)
            self.log(f"claimed {path} ({repo}@{sha[:12]}, channel {self.channel}, trunk={trunk}, publish={publish})")
            token = self.installation_token(claim)
            try:
                # The checkout is bounded by the run's deadline: a stalled clone or fetch ends the run
                # RED inside the pod's grace period, never holds the claim indefinitely.
                self.checkout(repo, sha, claim.get("refs") or [], checkout_dir, token, self.env, self.log,
                              deadline=time.monotonic() + max(1.0, self.run_deadline - (self.clock() - started)))
                # ONE merged tree per attempt (policy suites-test-fresh-merge): a pull-request run's
                # head is merged onto ONE base — the claim's `mergeBaseSha`, or main's tip resolved
                # ONCE from the mirror when the claim names none — and every stage runs on that tree.
                # The STAGE DECLARATION is read from the MERGED tree, so a command, dependency or
                # timeout the merge brings in is the one that runs. Trunk and probe runs are not
                # merged. The base is named in the plan; the instance refuses a PR success whose plan
                # names another sha, or none.
                merge_base = None
                probe = claim.get("isProbe") is True or str(claim.get("kind") or "").lower() == "probe"
                if not trunk and not probe:
                    merge_base = self.merge_onto_base(repo, checkout_dir, claim.get("mergeBaseSha"), token)
                declared = load_stages(os.path.join(checkout_dir, ".github", "build-stages.json"))
                stages = select_stages(declared, claim.get("stages"))
                if probe and claim.get("requestedStages"):
                    # A PROBE runs EXACTLY the stages it asked for (`requestedStages`; empty = every
                    # declared stage) and plans only those; one the repository does not declare is red.
                    stages = select_stages(declared, claim.get("requestedStages"))
            except Refusal as r:
                sink = self._sink_for(repo, sha, last_stage)
                sink.add([{"kind": "line", "text": f"run refused before any stage: {r}"},
                          {"kind": "stage-finished", "state": "failed", "summary": str(r)[:MAX_LINE]},
                          {"kind": "run-finished", "succeeded": False, "error": str(r)[:MAX_LINE]}])
                return "ran" if sink.flush(attempts=12) else "undelivered"
            # The PLAN, host-emitted and never a child's: the instance refuses a host-run success
            # unless every planned stage reported success.
            plan = self._sink_for(repo, sha, stages[0]["name"])
            # The run's declared execution budget: the instance sets the attempt's deadline to claim
            # time + budget + 15 min. The host enforces the same number (`budget`, below).
            budget = run_budget_seconds(stages, self.run_deadline - (self.clock() - started))
            plan_event = {"kind": "plan", "stages": [st["name"] for st in stages], "budgetMinutes": budget_minutes(budget)}
            if merge_base:
                plan_event["mergeBase"] = merge_base
            plan.add([plan_event])
            self.deliver(plan)
            os.makedirs(outputs_dir, exist_ok=True)
            os.makedirs(artifacts_dir, exist_ok=True)
            # MW_BUILD_PUBLISH is INFORMATIONAL: a host holds no publication credential, so a stage
            # cannot publish whatever it says; publication is the publish lane's, on the aggregate check `build`.
            extra = {
                "MW_BUILD_PUBLISH": "true" if publish else "false",
                "MW_BUILD_CHANNEL": self.channel, "MW_BUILD_REPO": repo, "MW_BUILD_SHA": sha,
                "MW_BUILD_RUN_PATH": path, "MW_BUILD_IS_TRUNK": "true" if trunk else "false",
                "MW_BUILD_PLATFORM_VERSION": self.platform_version,
                "MW_BUILD_FRAMEWORK_IDENTITY": self.identity,
                "MW_PLUGINS_DIR": self.plugins_dir or "", "MW_PLUGINS_DIGEST": self.plugins_digest or "",
                "MW_BUILD_CACHE_OUTPUTS": outputs_root(self.cache, repo), "MW_BUILD_ARTIFACTS": artifacts_dir,
                "MW_BUILD_OUTPUTS": outputs_dir,
                # What a stage needs to compute the cross-run cache keys the host will recompute.
                "MW_BUILD_TESTER_DIGEST": self.env.get("MW_BUILD_TESTER_DIGEST", ""),
                "MW_BUILD_PORTAL_DIGEST": self.env.get("MW_BUILD_PORTAL_DIGEST", ""),
                "MW_BUILD_PLATFORM_REF": self.env.get("MW_BUILD_PLATFORM_REF", ""),
                "MW_PLUGINS_KEY": self.effective_plugins_key(),
                "MW_BUILD_REFSET_KEY": self.env.get("MW_BUILD_REFSET_KEY", ""),
            }
            failed_at = None
            why = ""
            run_end = self.clock() + budget
            for st in stages:
                last_stage = st["name"]
                sink = self._sink_for(repo, sha, st["name"])
                remaining = run_end - self.clock()
                if failed_at is None and remaining <= 0:
                    failed_at, why = st["name"], f"the run deadline ({budget} s) passed"
                if failed_at is not None:
                    sink.add([{"kind": "stage-finished", "state": "failed",
                               "summary": f"not run — {why or 'stage ' + failed_at + ' failed first'}"}])
                    self.deliver(sink)
                    continue
                self.drain_held(attempts=2)
                ok = self.run_stage(dict(st, timeoutMinutes=min(st["timeoutMinutes"], remaining / 60)),
                                    checkout_dir, outputs_dir, sink, {**extra, "MW_BUILD_STAGE": st["name"]},
                                    readonly=self.stage_readonly(repo, st), writable=[artifacts_dir],
                                    hand_over=lambda: self.hand_over_artifacts(repo, sha, st["name"], artifacts_dir,
                                                                               deadline=run_end))
                self.assert_clean()
                if not ok:
                    failed_at = st["name"]
                elif trunk and st["kind"] == "build":
                    # Only COMPILATION is reused across runs — never a test verdict — and only a key
                    # the host can RECOMPUTE from the inputs the output names (cache_key_problem).
                    promoted, kept, refused = promote_outputs(outputs_dir, self.cache, self.log,
                                                              verify=lambda d: self.cache_key_problem(d, checkout_dir),
                                                              repo=repo)
                    if promoted or kept or refused:
                        sink.add([{"kind": "line", "text": f"cache: {promoted} output(s) promoted, {kept} already "
                                                           f"present" + (f", NOT promoted (not a content key): "
                                                                         f"{refused[:5]}" if refused else "")}])
                        self.deliver(sink)
            # run-finished is the LAST word: never before every stage-finished was acknowledged —
            # a run verdict over a missing stage verdict is a contradiction the instance cannot heal.
            for _ in range(10):
                if self.drain_held(attempts=6):
                    break
                self.sleep(30.0)
            if self.held:
                self.log(f"{path}: {len(self.held)} stage verdict(s) could not be delivered — NOT posting run-finished; "
                         f"the instance's adoption re-queues the run")
                self.held = []
                return "undelivered"
            sink = self._sink_for(repo, sha, last_stage)
            sink.add([{"kind": "run-finished", "succeeded": failed_at is None,
                       "error": None if failed_at is None else (why or f"stage {failed_at} failed")}])
            if not sink.flush(attempts=12):
                self.log(f"{path}: run-finished could not be delivered — the instance's adoption re-queues the run")
                return "undelivered"
            self.log(f"{path}: {'succeeded' if failed_at is None else 'failed at ' + failed_at} in "
                     f"{_hms(self.clock() - started)}")
            return "ran"
        except Lost as e:
            self.log(f"{path}: {e} — abandoning the run")
            return "lost"

    def sink(self, repo: str, sha: str, stage: str) -> EventSink:
        return EventSink(self.api, self.host, repo, sha, stage, self.channel, self.log,
                         sleep=self.sleep, clock=self.clock, claim_id=self.claim_id, probe=self.probe)

    def installation_token(self, claim: dict) -> Optional[str]:
        """A one-hour installation token for the CHECKOUT only (`POST /token`, Build service §1.3).
        It stays in the host process; no stage ever sees it. None ⇒ clone anonymously."""
        status, body = self.api.call("POST", "/token", {"host": self.host, "path": claim.get("path"),
                                                        "repo": claim.get("repo"), "sha": claim.get("sha")},
                                     {"channel": self.channel, **({"probe": self.probe} if self.probe else {})})
        if status == 200 and isinstance(body, dict) and body.get("token"):
            return str(body["token"])
        self.log(f"no installation token for {claim.get('repo')} (HTTP {status}) — cloning anonymously")
        return None

    def stage_refset(self) -> Optional[str]:
        """The composed gate host (the portal's /app with the tester beside it) staged into the
        shared cache keyed by BOTH digests — `<cache>/refsets/<sha256(tester|portal)>/host` — so a
        reference set is never reused across a change of either image. Rebinds MW_BUILD_GATE_HOST
        (and the sandbox's read-only bind) to the staged copy. None when there is nothing to stage."""
        src = self.env.get("MW_BUILD_GATE_HOST", "")
        tester, portal = self.env.get("MW_BUILD_TESTER_DIGEST", ""), self.env.get("MW_BUILD_PORTAL_DIGEST", "")
        if not src or not os.path.isdir(src):
            return None
        if not DIGEST.match(tester) or not DIGEST.match(portal):
            raise Fatal("the reference set cannot be keyed: MW_BUILD_TESTER_DIGEST / MW_BUILD_PORTAL_DIGEST are not both sha256 digests")
        key = refset_key(tester, portal)
        root = os.path.join(self.cache, "refsets")
        final = os.path.join(root, key)
        if not os.path.isfile(os.path.join(final, ".complete")):
            os.makedirs(root, exist_ok=True)
            tmp = tempfile.mkdtemp(prefix=".refset-", dir=root)
            shutil.copytree(src, os.path.join(tmp, "host"), symlinks=True)
            with open(os.path.join(tmp, ".complete"), "w") as fh:
                fh.write(f"tester {tester}\nportal {portal}\n")
            try:
                os.rename(tmp, final)
            except OSError:
                shutil.rmtree(tmp, ignore_errors=True)
        staged = os.path.join(final, "host")
        self.env["MW_BUILD_GATE_HOST"] = staged
        self.env["MW_BUILD_REFSET_KEY"] = key
        if isinstance(self.sandbox, Sandbox):
            self.sandbox.readonly = tuple(p for p in self.sandbox.readonly if p != src) + (staged,)
        return staged

    def merge_onto_base(self, repo: str, checkout: str, claimed: object, token: Optional[str]) -> str:
        """Merge the checked-out head onto ONE base and return that base's sha. `claimed` is the
        claim's `mergeBaseSha`: used VERBATIM (fetched if absent) when given; when null, main's tip
        is resolved ONCE from the repository's mirror (its HEAD — the default branch, fetched just
        before the checkout). The merge happens once, in the run's checkout, so every stage tests the
        same merged tree; a conflict is a Refusal naming the base."""
        genv = {**{k: self.env[k] for k in ("PATH", "LANG", "SSL_CERT_FILE", "SSL_CERT_DIR") if self.env.get(k)},
                "HOME": os.path.dirname(checkout), **git_auth_env(token),
                "GIT_AUTHOR_NAME": "build host", "GIT_AUTHOR_EMAIL": "build-host@invalid",
                "GIT_COMMITTER_NAME": "build host", "GIT_COMMITTER_EMAIL": "build-host@invalid"}
        if claimed is not None:
            base = str(claimed).strip().lower()
            if not SHA.match(base):
                raise Refusal(f"the claim's mergeBaseSha {claimed!r} is not a full commit")
        else:
            mirror = os.path.join(self.cache, "git", f"{repo}.git")
            rc, out = self.git_runner(["--git-dir", mirror, "rev-parse", "--verify", "HEAD^{commit}"], genv)
            base = out.strip().splitlines()[-1].strip().lower() if rc == 0 and out.strip() else ""
            if not SHA.match(base):
                raise Refusal(f"main's tip could not be resolved from the mirror (exit {rc}): {out.strip()[-160:]}")
        rc, out = self.git_runner(["-C", checkout, "fetch", "--quiet", "origin", base], genv)
        if rc != 0:
            raise Refusal(f"the merge base {base[:12]} could not be fetched (exit {rc}): {out.strip()[-160:]}")
        rc, out = self.git_runner(["-C", checkout, "merge", "--no-ff", "--no-edit", "-m",
                                   f"build host: merge onto {base}", base], genv)
        if rc != 0:
            self.git_runner(["-C", checkout, "merge", "--abort"], genv)
            raise Refusal(f"the head does not merge onto {base[:12]} (exit {rc}) — a conflict is red, never a test of the unmerged head: {out.strip()[-200:]}")
        self.log(f"merged the head onto {base[:12]} — every stage of this attempt tests that tree")
        return base

    def sweep_workspaces(self, now: Optional[float] = None) -> list[str]:
        """Remove kept workspaces older than two run deadlines (their receipts were never
        acknowledged; the instance re-queued the run long since). Returns what was removed."""
        now = time.time() if now is None else now
        gone = []
        try:
            names = os.listdir(self.work)
        except OSError:
            return gone
        for n in names:
            full = os.path.join(self.work, n)
            try:
                if os.path.isdir(full) and not os.path.islink(full) and now - os.path.getmtime(full) > 2 * self.run_deadline:
                    shutil.rmtree(full, ignore_errors=True)
                    gone.append(n)
            except OSError:
                continue
        return gone

    def stage_readonly(self, repo: str, st: dict) -> list[str]:
        """What of the SHARED cache this stage may read, read-only: its own repository's git mirror
        (the checkout's alternates point there) and what the stage DECLARES (`needs`) — never the
        cache root, never another repository's mirror or outputs."""
        ro = [os.path.join(self.cache, "git", f"{repo}.git")]
        needs = st.get("needs") or []
        if "plugins" in needs and self.plugins_dir:
            ro.append(self.plugins_dir)
            for name in sorted(os.listdir(self.plugins_dir)) if os.path.isdir(self.plugins_dir) else []:
                link = os.path.join(self.plugins_dir, name)
                if os.path.islink(link):
                    ro.append(os.path.realpath(link))     # a plugin set's links point into /cache/bundles
        if "outputs" in needs:
            ro.append(outputs_root(self.cache, repo))
        if "nuget" in needs:
            ro.append(os.path.join(self.cache, "nuget"))
        return ro

    def run_stage(self, st: dict, checkout: str, outputs: str, sink: EventSink, extra: dict,
                  readonly: Iterable[str] = (), writable: Iterable[str] = (),
                  hand_over: Callable[[], tuple[list[dict], Optional[str]]] = None) -> bool:
        """Run ONE stage in a fresh sandboxed process tree; post its events; return success."""
        name, started = st["name"], self.clock()
        parser = OutputParser(checkout, name)
        sink.add([{"kind": "line", "text": f"[00:00:00] {st['kind']}/{name} on build-{self.channel} "
                                           f"({self.platform_version}, plugins {(self.plugins_digest or '-')[:19]})"}])
        try:
            argv = self.sandbox.argv(st["command"], checkout, outputs, sandbox_env(self.env, extra, self.cache),
                                     readonly=readonly, writable=writable)
            proc = subprocess.Popen(argv, cwd=checkout, env={"PATH": self.env.get("PATH", "/usr/bin:/bin")},
                                    stdout=subprocess.PIPE, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL,
                                    start_new_session=True, text=True, errors="replace", bufsize=1)
        except (OSError, Refusal) as e:
            sink.add([{"kind": "stage-finished", "state": "failed", "summary": f"could not start: {e}"}])
            sink.flush(attempts=6)
            return False
        lines: "queue.Queue[Optional[str]]" = queue.Queue()

        def reader() -> None:
            assert proc.stdout is not None
            for ln in proc.stdout:
                lines.put(ln)
            lines.put(None)

        threading.Thread(target=reader, daemon=True).start()
        deadline = started + st["timeoutMinutes"] * 60
        timed_out = False
        try:
            while True:
                try:
                    ln = lines.get(timeout=1.0)
                except queue.Empty:
                    if self.clock() > deadline and not timed_out:
                        timed_out = True
                        _kill(proc)
                    sink.add([])
                    continue
                if ln is None:
                    break
                sink.add(parser.feed(ln))
                if self.clock() > deadline and not timed_out:
                    timed_out = True
                    _kill(proc)
            # EOF is NOT exit: a stage can close its stdout and keep running. The deadline holds
            # until the process tree is gone, and the events keep flowing meanwhile.
            while True:
                try:
                    rc = proc.wait(timeout=1.0)
                    break
                except subprocess.TimeoutExpired:
                    sink.add([])
                    if self.clock() > deadline and not timed_out:
                        timed_out = True
                        _kill(proc)
        except Lost:
            _kill(proc)
            raise
        except Exception as e:
            # Nothing a stage does may take the host down without a verdict: kill, reap, report.
            _kill(proc)
            sink.add([{"kind": "stage-finished", "state": "failed",
                       "summary": f"the host failed while running the stage ({type(e).__name__}: {str(e)[:200]})"}])
            self.deliver(sink)
            return False
        events = parser.finish()
        ok = rc == 0 and not timed_out
        if not ok and parser.errors == 0 and parser.tests["failed"] == 0:
            # nothing structured explains the failure — the tail is the evidence
            events += [{"kind": "line", "text": "│ " + t} for t in parser.tail if t.strip()]
        note = f"timed out after {st['timeoutMinutes']:g} min" if timed_out else ""
        if ok and hand_over is not None:
            # H9: every artifact the stage produced is in the shared CI store, its stored hash re-read,
            # BEFORE the stage can succeed — a receipt the instance holds is a byte the store holds.
            receipts, problem = hand_over()
            events += receipts
            if problem:
                ok = False
                note = (note + "; " if note else "") + f"artifact hand-over failed: {problem}"
        events.append({"kind": "stage-finished", "state": "succeeded" if ok else "failed",
                       "summary": parser.summary(rc, self.clock() - started, note)})
        sink.add(events)
        self.deliver(sink)
        return ok

    def effective_plugins_key(self) -> str:
        """The plugins layer's identity in a cache key, in EITHER mode: the registry index's key
        (H8), else the sealed publication's digest — so a changed plugins layer is always a new key."""
        return self.plugins_key or self.plugins_digest or ""

    def cache_key_problem(self, output_dir: str, checkout: str) -> Optional[str]:
        """None when the output's directory name IS the key the host recomputes from the inputs its
        `.cache-key.json` names; otherwise the reason it is not promoted."""
        try:
            with open(os.path.join(output_dir, CACHE_KEY_FILE)) as fh:
                spec = json.load(fh)
        except (OSError, ValueError) as e:
            return f"no readable {CACHE_KEY_FILE} ({type(e).__name__})"
        env = dict(self.env, MW_BUILD_FRAMEWORK_IDENTITY=self.identity, MW_PLUGINS_KEY=self.effective_plugins_key())
        try:
            key = cache_key_of(checkout, spec, env)
        except Refusal as r:
            return str(r)
        return None if key == os.path.basename(output_dir.rstrip("/")) else f"the recomputed key is {key[:16]}…, not the directory's name"

    def hand_over_artifacts(self, repo: str, sha: str, stage: str, artifacts_dir: str,
                            deadline: Optional[float] = None) -> tuple[list[dict], Optional[str]]:
        """H9: upload every file a stage left under `$MW_BUILD_ARTIFACTS/<unit>/` (a module bundle,
        its publication record, the gate bake) to the shared CI artifact store with core's
        `ci-artifact-store.py` (content-addressed, SHA-256), READ IT BACK and re-hash it, and only
        then return its receipt `{"kind":"artifact","unit","key","sha256","bytes"}`. Returns
        (receipts, the first problem or None); a stage with artifacts and no store is a problem.
        The hand-over is INSIDE the run budget: every store call gets the time left until `deadline`
        (the run's absolute end, on self.clock) and no more, so a stage finishing near the limit
        cannot hold its claim past the follower's window or the pod's grace; when the budget runs
        out the hand-over is a problem and the stage fails."""
        files = []
        for root, _dirs, names in os.walk(artifacts_dir):
            for n in sorted(names):
                full = os.path.join(root, n)
                if os.path.islink(full) or not os.path.isfile(full):
                    return [], f"{os.path.relpath(full, artifacts_dir)} is not a regular file"
                files.append(full)
        if not files:
            return [], None
        store_url = self.env.get("MW_BUILD_ARTIFACT_STORE", "").strip()
        if not store_url:
            return [], "the stage produced artifacts but no artifact store is configured (MW_BUILD_ARTIFACT_STORE)"
        script = os.path.join(os.path.dirname(os.path.abspath(__file__)), "ci-artifact-store.py")
        receipts = []

        def left() -> float:
            return float("inf") if deadline is None else deadline - self.clock()

        def store(argv: list[str]) -> tuple[int, str]:
            budget = left()
            if budget <= 0:
                raise subprocess.TimeoutExpired(argv, 0)
            return self.store_runner(argv, timeout=min(1800.0, budget))

        for full in sorted(files):
            unit = os.path.relpath(full, artifacts_dir).replace(os.sep, "/")
            if "/" not in unit:
                return receipts, f"{unit} is not under a unit directory ($MW_BUILD_ARTIFACTS/<unit>/<file>)"
            digest, size = _sha256_file(full), os.path.getsize(full)
            # The DIGEST is in the key: a later attempt, probe or platform-set build of the same
            # commit/channel/stage that produces OTHER bytes writes a different object, so an earlier
            # acknowledged receipt's object is never replaced under it.
            key = f"build-hosts/{repo}/{sha}/{self.channel}/{stage}/{digest}/{unit}"
            try:
                rc, out = store([sys.executable, script, "put", "--store", store_url, "--key", key, "--file", full])
            except subprocess.TimeoutExpired:
                return receipts, f"the run deadline passed during the hand-over of {unit} (put)"
            locator = next((l.split("=", 1)[1] for l in out.splitlines() if l.startswith("locator=")), "")
            if rc != 0 or not locator:
                return receipts, f"ci-artifact-store put {unit} failed (exit {rc}): {out.strip()[-200:]}"
            if not locator.endswith(f"#sha256={digest}"):
                return receipts, f"the store's locator for {unit} names another hash ({locator[-80:]})"
            readback = tempfile.mkdtemp(prefix=".readback-", dir=self.work)
            try:
                back = os.path.join(readback, "f")
                try:
                    rc, out = store([sys.executable, script, "get", "--store", store_url, "--locator", locator, "--out", back])
                except subprocess.TimeoutExpired:
                    return receipts, f"the run deadline passed during the hand-over of {unit} (read-back)"
                if rc != 0 or not os.path.isfile(back) or _sha256_file(back) != digest:
                    return receipts, f"the stored {unit} did not read back as sha256 {digest[:12]}… (exit {rc}): {out.strip()[-160:]}"
            finally:
                shutil.rmtree(readback, ignore_errors=True)
            receipts.append({"kind": "artifact", "unit": unit, "key": locator, "sha256": digest, "bytes": size})
        return receipts, None

    # -- terminal events are never lost -----------------------------------------------------
    def deliver(self, sink: EventSink) -> bool:
        """Flush a sink; one that could not be emptied (the API is down) is HELD, with its
        stage-finished, and retried before every later post — nothing is discarded with it."""
        if sink.flush(attempts=6):
            return True
        self.held.append(sink)
        return False

    def drain_held(self, attempts: int = 6) -> bool:
        still = []
        for sink in self.held:
            if not sink.flush(attempts=attempts):
                still.append(sink)
        self.held = still
        return not still

    def loop(self) -> int:
        idle = float(self.env.get("MW_BUILD_IDLE_SECONDS", "15") or 15)
        while not self.stopping:
            what = self.cycle()
            if what in ("idle", "no-inputs"):
                self.sleep(idle if what == "idle" else max(idle, 30))
        self.log("stopping: no further claims (the run in hand, if any, has finished)")
        return 0


def _kill(proc: subprocess.Popen) -> None:
    """End the stage's whole tree: bwrap is the session leader, and --die-with-parent plus the PID
    namespace take everything under it down with it."""
    try:
        os.killpg(proc.pid, signal.SIGTERM)
    except (OSError, ProcessLookupError):
        return
    for _ in range(100):
        if proc.poll() is not None:
            return
        time.sleep(0.1)
    try:
        os.killpg(proc.pid, signal.SIGKILL)
    except (OSError, ProcessLookupError):
        pass


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    g = ap.add_mutually_exclusive_group(required=True)
    g.add_argument("--host", action="store_true")
    g.add_argument("--once", action="store_true")
    g.add_argument("--self-test", action="store_true")
    g.add_argument("--cache-key", metavar="SPEC.json",
                   help="print the cross-run cache key of the output SPEC describes (a stage's "
                        ".cache-key.json), computed exactly as the host recomputes it; cwd = the checkout")
    a = ap.parse_args(argv)
    if a.self_test:
        return self_test()
    if a.cache_key:
        try:
            with (sys.stdin if a.cache_key == "-" else open(a.cache_key)) as fh:
                print(cache_key_of(os.getcwd(), json.load(fh), dict(os.environ)))
            return 0
        except (Refusal, OSError, ValueError) as e:
            print(f"build-run: {e}", file=sys.stderr)
            return 1
    env = dict(os.environ)
    try:
        host = Host(env, Api(env.get("BUILD_API", ""), read_worker_token(env)))
        host.preflight()
        host.sandbox.probe(_run_capture)
        boundary_check(host.sandbox, env, _run_capture)
        host.stage_refset()
        scoping_check(host, _run_capture)
    except (Refusal, Fatal) as r:
        print(f"build-run: {r}", file=sys.stderr, flush=True)
        return 1

    def stop(_sig, _frm) -> None:
        # The old replica of a roll (or a scale-in): stop claiming, finish the run in hand, exit.
        host.stopping = True
    signal.signal(signal.SIGTERM, stop)
    signal.signal(signal.SIGINT, stop)
    try:
        if a.once:
            print(f"build-run: {host.cycle()}", flush=True)
            return 0
        return host.loop()
    except Fatal as f:
        print(f"build-run: {f}", file=sys.stderr, flush=True)
        return 2


# ───────────────────────────────── self-test ──────────────────────────────────────────────

class _FakeApi(Api):
    def __init__(self, script: dict) -> None:
        super().__init__("https://control.example.com/api/builds", "worker")
        self.script = script
        self.calls: list[tuple] = []

    def call(self, method, path, body=None, query=None):
        self.calls.append((method, path, json.loads(json.dumps(body)) if body is not None else None,
                           dict(query or {})))
        for (m, p), answers in self.script.items():
            if m == method and path.startswith(p):
                if callable(answers):
                    return answers(path, body)
                return answers.pop(0) if len(answers) > 1 else answers[0]
        return 404, None


class _EnvSandbox:
    """The self-test's stand-in for bwrap on a machine without it: `env -i` gives the same
    clear-env + allow-list semantics, so what a stage SEES is still what the host chose."""

    last_readonly: list = []

    def argv(self, command, checkout, outputs, env, readonly=(), writable=()):
        _EnvSandbox.last_readonly = list(readonly)
        return ["/usr/bin/env", "-i", *[f"{k}={v}" for k, v in sorted(env.items())], "/bin/bash", "-c", command]


def self_test() -> int:
    failures: list[str] = []

    def check(name: str, cond: bool, detail: object = "") -> None:
        if not cond:
            failures.append(f"{name}: {detail}")

    # ── 1. compiler diagnostics ─────────────────────────────────────────────────────────────
    p = OutputParser("/work/run1/src", "compile-check")
    ev = p.feed("/work/run1/src/Hosting/Build/Source/BuildApi.cs(41,13): error CS0246: The type or "
                "namespace name 'X' could not be found [/work/run1/src/Hosting/Hosting.csproj]\n")
    check("msbuild/error", ev == [{"kind": "diagnostic", "severity": "error", "file": "Hosting/Build/Source/BuildApi.cs",
                                   "line": 41, "code": "CS0246",
                                   "message": "The type or namespace name 'X' could not be found"}], ev)
    ev = p.feed("src/A.cs(7): warning CS8602: Dereference of a possibly null reference.")
    check("msbuild/warning no column", ev and ev[0]["severity"] == "warning" and ev[0]["line"] == 7
          and ev[0]["file"] == "src/A.cs", ev)
    again = p.feed("/work/run1/src/Hosting/Build/Source/BuildApi.cs(41,13): error CS0246: The type or "
                   "namespace name 'X' could not be found [/work/run1/src/Hosting/Hosting.csproj]")
    check("msbuild/dedup of the closing summary", again == [] and p.errors == 1, again)
    ev = p.feed("::error file=Store/Plugin/Source/Gate.cs,line=12::CS1061 'Foo' does not contain 'Bar'")
    check("wfcmd/diagnostic", ev and ev[0]["code"] == "CS1061" and ev[0]["line"] == 12
          and ev[0]["file"] == "Store/Plugin/Source/Gate.cs" and ev[0]["message"].startswith("'Foo'"), ev)
    check("chatter/dropped", p.feed("  Restored /work/run1/src/x.csproj (in 1.2 s).") == [])
    check("chatter/kept in tail", any("Restored" in t for t in p.tail), p.tail)
    check("summary/counts", "2 error(s)" in p.summary(1, 75) and "1 warning(s)" in p.summary(1, 75)
          and "1m15s" in p.summary(1, 75), p.summary(1, 75))

    # ── 2. test results ─────────────────────────────────────────────────────────────────────
    t = OutputParser("/work/run2/src", "portal-hosts")
    out: list[dict] = []
    for ln in [
        "Test run for /work/run2/src/bin/MeshWeaver.Hosting.Test.dll (.NETCoreApp,Version=v10.0)",
        "  Passed MeshWeaver.Hosting.Test.QueueTest.Claims [12 ms]",
        "  Failed MeshWeaver.Hosting.Test.QueueTest.Orders [1 s]",
        "  Error Message:",
        "   Assert.Equal() Failure: Values differ",
        "  Stack Trace:",
        "     at Xunit.Assert.Equal(Int32 expected, Int32 actual) in /_/src/Assert.cs:line 9",
        "     at MeshWeaver.Hosting.Test.QueueTest.Orders() in /work/run2/src/test/QueueTest.cs:line 42",
        "  Skipped MeshWeaver.Hosting.Test.QueueTest.Later [< 1 ms]",
        "Failed!  - Failed:     1, Passed:     1, Skipped:     1, Total:     3",
    ]:
        out += t.feed(ln)
    out += t.finish()
    tests = [e for e in out if e["kind"] == "test-finished"]
    check("vstest/three results", [e["outcome"] for e in tests] == ["passed", "failed", "skipped"], tests)
    f = next((e for e in tests if e["outcome"] == "failed"), {})
    check("vstest/suite from the dll", f.get("suite") == "MeshWeaver.Hosting.Test", f)
    check("vstest/failure message", f.get("message") == "Assert.Equal() Failure: Values differ", f)
    check("vstest/failure source line is the repo's, not the framework's",
          f.get("file") == "test/QueueTest.cs" and f.get("line") == 42, f)
    check("vstest/duration", tests[0]["durationMs"] == 12 and f.get("durationMs") == 1000, tests)
    check("vstest/summary not a result", t.tests == {"passed": 1, "failed": 1, "skipped": 1}, t.tests)
    x = OutputParser("/w", "tests")
    xo = x.feed("    MeshWeaver.Store.Test.GateTest.Refuses [FAIL]") + x.feed("      Expected refusal, got grant") + x.finish()
    check("xunit/fail with message", xo and xo[0]["outcome"] == "failed"
          and xo[0]["message"] == "Expected refusal, got grant" and xo[0]["suite"] == "MeshWeaver.Store.Test.GateTest", xo)
    s = OutputParser("/w", "modules")
    check("mw-event/unit", s.feed('::mw-event::{"kind":"unit-finished","unit":"Store","outcome":"cached","durationMs":3}')
          == [{"kind": "unit-finished", "unit": "Store", "outcome": "cached", "durationMs": 3}])
    refused = s.feed('::mw-event::{"kind":"stage-finished","state":"succeeded"}')
    check("mw-event/a child cannot declare its stage green",
          [e["kind"] for e in refused] == ["line"] and "only the host decides" in refused[0]["text"], refused)
    refused = s.feed('::mw-event::{"kind":"run-finished","succeeded":true}')
    check("mw-event/a child cannot declare the run green", [e["kind"] for e in refused] == ["line"], refused)
    for bad in ('{not json}', '[1]', '{"kind":"diagnostic","line":"oops"}', '{"kind":"diagnostic","line":-1}',
                '{"kind":"diagnostic","severity":"fatal"}', '{"kind":"test-finished","outcome":"maybe"}',
                '{"kind":"count","unitsTotal":true}', '{"kind":"unit-finished","unit":"A","outcome":"won"}',
                '{"kind":"nope"}', '{"kind":"line"}'):
        got = s.feed("::mw-event::" + bad)
        check(f"mw-event/malformed {bad} is dropped and said, never raised",
              len(got) == 1 and got[0]["kind"] == "line" and "dropped" in got[0]["text"], got)
    ok_diag = s.feed('::mw-event::{"kind":"diagnostic","severity":"warning","file":"/w/a.cs","line":3,"code":"CS1","message":"m"}')
    check("mw-event/a well-formed diagnostic passes, typed", ok_diag and ok_diag[0]["line"] == 3 and ok_diag[0]["file"] == "a.cs", ok_diag)

    # ── 3. stage declaration ───────────────────────────────────────────────────────────────
    good = {"stages": [{"name": "compile-check", "kind": "build", "command": "true"},
                       {"name": "gate", "kind": "test", "command": "true", "timeoutMinutes": 30}]}
    check("stages/valid", [x["name"] for x in validate_stages(good)] == ["compile-check", "gate"])
    for bad in ({"stages": []}, {"stages": [{"name": "build/x", "kind": "build", "command": "t"}]},
                {"stages": [{"name": "a", "kind": "deploy", "command": "t"}]},
                {"stages": [{"name": "a", "kind": "build", "command": ""}]},
                {"stages": [{"name": "a", "kind": "build", "command": "t", "timeoutMinutes": 90}]},
                {"stages": [{"name": "a", "kind": "build", "command": "t"}] * 2}):
        try:
            validate_stages(bad)
            check(f"stages/refuses {bad}", False, "accepted")
        except Refusal:
            pass
    d = validate_stages(good)
    check("stages/claim selects", [x["name"] for x in select_stages(d, ["build/gate"])] == ["gate"])
    check("stages/a stable stage check selects its stage", [x["name"] for x in select_stages(d, ["build-stable/gate"])] == ["gate"])
    try:
        select_stages(d, ["portal-hosts"])
        check("stages/undeclared claim is red", False, "accepted")
    except Refusal:
        pass

    # ── 4. the sandbox: the bwrap argv, the env allow-list, the token file ─────────────────
    sb = Sandbox("/usr/bin/bwrap", ["/cache", "/host"])
    av = sb.argv("make test", "/work/r/src", "/work/r/out", {"PATH": "/usr/bin", "MW_BUILD_PUBLISH": "false"})
    joined = " ".join(av)
    for flag in ("--die-with-parent", "--unshare-user", "--unshare-pid", "--unshare-ipc", "--clearenv",
                 "--new-session"):
        check(f"bwrap/{flag}", flag in av, av)
    check("bwrap/fresh /proc", "--proc /proc" in joined, joined)
    check("bwrap/caches READ-ONLY", "--ro-bind-try /cache /cache" in joined and "--bind /cache" not in joined, joined)
    check("bwrap/writable: only checkout, outputs, scratch",
          [av[i + 1] for i, a in enumerate(av) if a == "--bind"] == ["/work/r/src", "/work/r/out"]
          and "--tmpfs /scratch" in joined, joined)
    check("bwrap/env only via --setenv", av.count("--setenv") == 2 and av.index("--clearenv") < av.index("--setenv"), av)
    check("bwrap/command last", av[-4:] == ["--", "/bin/bash", "-c", "make test"], av[-4:])
    check("bwrap/no network namespace (builds fetch packages)", "--unshare-net" not in av and "--unshare-all" not in av)
    se = sandbox_env({"PATH": "/bin", "BUILD_TOKEN_FILE": "/run/build-worker/token", "AZURE_CLIENT_SECRET": "s",
                      "BUILD_API": "https://x", "KUBERNETES_SERVICE_HOST": "10.0.0.1", "MeshWeaverRefs": "/portal/app"},
                     {"MW_BUILD_PUBLISH": "false"}, "/cache")
    check("env/allow-list only", set(se) == {"PATH", "MeshWeaverRefs", "HOME", "TMPDIR", "NUGET_PACKAGES",
                                             "NUGET_FALLBACK_PACKAGES", "MW_BUILD_PUBLISH"}, sorted(se))
    try:
        sandbox_env({}, {"GITHUB_TOKEN": "t"}, "/cache")
        check("env/credential-shaped extra refused", False, "accepted")
    except Refusal:
        pass
    probe_calls: list[list[str]] = []

    def probe_fails(argv):
        probe_calls.append(argv)
        return 1, "bwrap: No permissions to creating new namespace, likely because the kernel does not allow non-privileged user namespaces"
    try:
        sb.probe(probe_fails)
        check("probe/no namespaces is FATAL at startup", False, "accepted")
    except Fatal as e:
        check("probe/names why", "No permissions" in str(e) and "unsandboxed" in str(e), e)
    check("probe/uses the same sandbox argv", probe_calls and probe_calls[0][0] == "/usr/bin/bwrap"
          and "--unshare-pid" in probe_calls[0], probe_calls)
    sb.probe(lambda argv: (0, ""))      # a node that allows them starts

    # the boundary self-check, with injected results
    sbx = Sandbox("/usr/bin/bwrap", [])
    seen_argv: list[list[str]] = []

    def boundary_run(results):
        def run(argv):
            seen_argv.append(argv)
            return 0, "bwrap noise\n" + json.dumps(results)
        return run
    blocked = {n: "blocked (URLError)" for n, _, _ in BOUNDARY_PROBES}
    boundary_check(sbx, {}, boundary_run(blocked))
    check("boundary/probed from INSIDE a sandbox", seen_argv and seen_argv[0][0] == "/usr/bin/bwrap"
          and "--unshare-pid" in seen_argv[0] and "169.254.169.254" in seen_argv[0][-1], seen_argv[:1])
    check("boundary/the metadata probe sends Metadata: true", '"Metadata": "true"' in seen_argv[0][-1])
    for name, _, _ in BOUNDARY_PROBES:
        for answer in ("answered", "answered (HTTP 403)"):
            try:
                boundary_check(sbx, {}, boundary_run(dict(blocked, **{name: answer})))
                check(f"boundary/{name} {answer} refuses to start", False, "started")
            except Fatal as e:
                check(f"boundary/the refusal names {name}", name in str(e), e)
    for broken in ((1, "python3: not found"), (0, "not json"), (0, json.dumps({"only": "one"}))):
        try:
            boundary_check(sbx, {}, lambda argv, b=broken: b)
            check(f"boundary/an unmeasured boundary {broken} refuses to start", False, "started")
        except Fatal:
            pass
    seen_argv.clear()
    boundary_check(sbx, {"MW_BUILD_BOUNDARY_PRIVATE_URL": "http://10.1.2.3/"}, boundary_run(blocked))
    check("boundary/the private probe address is configurable", "10.1.2.3" in seen_argv[0][-1], seen_argv[0][-1][-200:])

    tmp = tempfile.mkdtemp(prefix="build-run-selftest-")
    try:
        tf = os.path.join(tmp, "token")
        with open(tf, "w") as fh:
            fh.write("worker-secret\n")
        check("token/from the file", read_worker_token({"BUILD_TOKEN_FILE": tf}) == "worker-secret")
        for bad_env in ({"BUILD_TOKEN": "x", "BUILD_TOKEN_FILE": tf}, {"BUILD_TOKEN_FILE": os.path.join(tmp, "absent")}):
            try:
                read_worker_token(bad_env)
                check(f"token/refuses {sorted(bad_env)}", False, "accepted")
            except Refusal:
                pass

        # namespace leftovers: a fake /proc with one process in a foreign pid namespace
        fake = os.path.join(tmp, "proc")
        for pid, ns in (("self", "pid:[1]"), ("10", "pid:[1]"), ("11", "pid:[2]")):
            os.makedirs(os.path.join(fake, pid, "ns"))
            os.symlink(ns, os.path.join(fake, pid, "ns", "pid"))
        check("leftovers/foreign namespace found", pid_namespace_leftovers(fake) == [11], pid_namespace_leftovers(fake))
        # A ZOMBIE in a foreign namespace has exited: it is not a survivor (the user-namespace pool,
        # 2026-10-27 — the startup probe's bwrap, State Z, PPid 1, made every start refuse to claim).
        os.makedirs(os.path.join(fake, "12", "ns"))
        os.symlink("pid:[3]", os.path.join(fake, "12", "ns", "pid"))
        with open(os.path.join(fake, "12", "status"), "w") as fh:
            fh.write("Name:\tbwrap\nState:\tZ (zombie)\nPPid:\t1\n")
        with open(os.path.join(fake, "11", "status"), "w") as fh:
            fh.write("Name:\tpython3\nState:\tS (sleeping)\nPPid:\t1\n")
        check("leftovers/a zombie is not a survivor, a sleeping process is", pid_namespace_leftovers(fake) == [11],
              pid_namespace_leftovers(fake))
        with open(os.path.join(fake, "12", "status"), "wb") as fh:
            fh.write(b"Name:\tbw\xff\xferap\nState:\tZ (zombie)\nPPid:\t1\n")
        check("leftovers/a non-UTF-8 task name neither raises nor hides a zombie", pid_namespace_leftovers(fake) == [11],
              pid_namespace_leftovers(fake))
        # reap_children really reaps: a child that exited is collected without blocking.
        child = subprocess.Popen(["true"])
        # Wait for the child to EXIT without consuming its status (Popen waits only for exec), so the
        # reaper is what collects it — never a fixed sleep that a loaded runner can outrun (review of #7287).
        if hasattr(os, "waitid") and hasattr(os, "WNOWAIT"):
            os.waitid(os.P_PID, child.pid, os.WEXITED | os.WNOWAIT)
            reaped = reap_children()
        else:  # no waitid (macOS): retry the reaper until the child has exited, bounded
            reaped = 0
            for _ in range(200):
                reaped += reap_children()
                if reaped:
                    break
                time.sleep(0.05)
        child.returncode = 0  # reaped behind Popen's back; keep its finalizer quiet
        gone = True
        try:
            os.kill(child.pid, 0)
            gone = False
        except OSError:
            pass
        check("leftovers/reap_children collects an exited child", reaped >= 1 and gone, f"reaped={reaped} gone={gone}")
        # assert_clean reaps BEFORE it decides, and again after the kill.
        calls: list[str] = []
        hz = Host({"MW_BUILD_CHANNEL": "latest"}, _FakeApi({}), log=lambda s: None, sandbox=_EnvSandbox(),
                  leftovers=lambda: (calls.append("look"), [])[1], reap=lambda: (calls.append("reap"), 0)[1],
                  sleep=lambda s: None)
        hz.assert_clean()
        check("leftovers/assert_clean reaps before looking", calls[:2] == ["reap", "look"], calls)
        hl = Host({"MW_BUILD_CHANNEL": "latest"}, _FakeApi({}), log=lambda s: None, sandbox=_EnvSandbox(),
                  leftovers=lambda: [999999999], sleep=lambda s: None)
        try:
            hl.cycle()
            check("leftovers/host refuses to claim", False, "claimed")
        except Fatal:
            check("leftovers/no claim was attempted", not any(c[1] == "/claim" for c in hl.api.calls), hl.api.calls)

        # ── 5. cache promotion: trunk only, content keys only, links not followed ──────────
        outd = os.path.join(tmp, "o")
        key = "ab" * 16
        os.makedirs(os.path.join(outd, key))
        with open(os.path.join(outd, key, "X.dll"), "w") as fh:
            fh.write("x")
        os.makedirs(os.path.join(outd, "not-a-key"))
        os.symlink("/etc", os.path.join(outd, "cd" * 16))
        cache = os.path.join(tmp, "cache")
        pr, kp, rf = promote_outputs(outd, cache, lambda s: None)
        check("promote/one promoted", pr == 1 and os.path.isfile(os.path.join(cache, "outputs", key, "X.dll")), (pr, kp, rf))
        check("promote/non-key and symlink refused", sorted(rf) == sorted(["not-a-key", "cd" * 16]), rf)
        pr2, kp2, _ = promote_outputs(outd, cache, lambda s: None)
        check("promote/same key kept", pr2 == 0 and kp2 == 1, (pr2, kp2))

        # ── 5b. the git mirror: clone --mirror on first use, fetch before every checkout ───
        calls: list[tuple] = []

        def fake_git(*args):
            calls.append(args)
            if args[0] == "clone" and args[1] == "--mirror":
                os.makedirs(args[-1])
                open(os.path.join(args[-1], "HEAD"), "w").close()
        genv = {"MW_BUILD_CACHE": os.path.join(tmp, "gitcache"), "PATH": "/usr/bin:/bin"}
        default_checkout("MeshWeaver.Plugins", "d" * 40, ["refs/pull/7/head"], os.path.join(tmp, "r1", "src"),
                         None, genv, lambda s: None, git=fake_git)
        mirror = os.path.join(tmp, "gitcache", "git", "MeshWeaver.Plugins.git")
        first = [c[:2] for c in calls]
        check("mirror/first use clones --mirror, then fetches the wanted refs, then a --reference clone",
              first[0] == ("clone", "--mirror") and ("-C", mirror) in first
              and any(c[0] == "clone" and "--reference" in c and mirror in c for c in calls), calls)
        check("mirror/the PR ref and the sha are fetched into the mirror",
              any(c[:3] == ("-C", mirror, "fetch") and "refs/pull/7/head" in c and "d" * 40 in c for c in calls), calls)
        calls.clear()
        default_checkout("MeshWeaver.Plugins", "d" * 40, [], os.path.join(tmp, "r2", "src"),
                         None, genv, lambda s: None, git=fake_git)
        check("mirror/later use fetches --prune, never re-clones the mirror",
              not any(c[:2] == ("clone", "--mirror") for c in calls)
              and any(c[:4] == ("-C", mirror, "fetch", "--quiet") and "--prune" in c for c in calls), calls)
        check("mirror/the checkout is detached at the sha", calls[-1][-2:] == ("--detach", "d" * 40), calls[-1])

        # ── 5c. the ONE credential: AcrPull by workload identity, host-only ──────────────
        fed = os.path.join(tmp, "azure-identity-token")
        with open(fed, "w") as fh:
            fh.write("federated-jwt")
        posts: list[tuple] = []

        def fake_post(url, form):
            posts.append((url, dict(form)))
            if "login.microsoftonline.com" in url:
                return {"access_token": "aad-token"}
            return {"refresh_token": "acr-refresh"}
        wi = {"AZURE_FEDERATED_TOKEN_FILE": fed, "AZURE_CLIENT_ID": "cid", "AZURE_TENANT_ID": "tid"}
        cfg = acr_registry_config("cr.example.com", wi, fake_post, os.path.join(tmp, "auth"))
        check("wi/the federated token is the client assertion",
              posts[0][1].get("client_assertion") == "federated-jwt" and posts[0][0].endswith("/tid/oauth2/v2.0/token"), posts)
        check("wi/the Entra token is exchanged at the registry",
              posts[1][0] == "https://cr.example.com/oauth2/exchange" and posts[1][1]["access_token"] == "aad-token", posts)
        check("wi/the config carries the refresh token, mode 0600",
              json.load(open(cfg)) == {"auths": {"cr.example.com": {"identitytoken": "acr-refresh"}}}
              and (os.stat(cfg).st_mode & 0o777) == 0o600, cfg)
        check("wi/no projected token ⇒ anonymous", acr_registry_config("cr.example.com", {}, fake_post, tmp) is None)
        try:
            acr_registry_config("cr.example.com", wi, lambda u, f: {}, os.path.join(tmp, "auth2"))
            check("wi/a refused exchange is a Refusal", False, "accepted")
        except Refusal:
            pass
        check("wi/the federated token never reaches a sandbox",
              "AZURE_FEDERATED_TOKEN_FILE" not in sandbox_env(dict(wi, PATH="/bin"), {}, "/cache")
              and "AZURE_CLIENT_ID" not in sandbox_env(dict(wi, PATH="/bin"), {}, "/cache"))
        seen_cfg: list[str] = []

        def pull_with_cfg(cmd):
            if "--registry-config" in cmd:
                p_ = cmd[cmd.index("--registry-config") + 1]
                seen_cfg.append(p_)
                check("wi/the config exists during the pull", os.path.isfile(p_), p_)
            os.makedirs(cmd[4], exist_ok=True)
            return 0
        stage_plugins({"digest": "sha256:" + "9" * 64}, os.path.join(tmp, "wicache"),
                      dict(wi, MW_PLUGINS_REGISTRY="cr.example.com"), pull_with_cfg, fake_post)
        check("wi/the pull carries the config, and it is deleted after", seen_cfg and not os.path.exists(seen_cfg[0]), seen_cfg)

        # ── 5d. cache scoping: a stage is bound its own mirror + what it declares, never a root ──
        sc_env = {"MW_BUILD_CHANNEL": "latest", "MW_BUILD_PLATFORM_VERSION": "3.1.10412", "PATH": "/usr/bin:/bin",
                  "MW_BUILD_FRAMEWORK_IDENTITY": "c3e1", "MW_BUILD_CACHE": os.path.join(tmp, "sc-cache"), "MW_BUILD_WORK": tmp}
        hs_ = Host(sc_env, _FakeApi({}), log=lambda m: None, sandbox=Sandbox("/usr/bin/bwrap", ["/portal"]),
                   leftovers=lambda: [], sleep=lambda x: None)
        hs_.plugins_dir = os.path.join(tmp, "sc-cache", "plugin-sets", "k")
        ro = hs_.stage_readonly("MeshWeaver.Plugins", {"needs": []})
        check("scope/no needs: only the repository's own mirror", ro == [os.path.join(tmp, "sc-cache", "git", "MeshWeaver.Plugins.git")], ro)
        ro = hs_.stage_readonly("MeshWeaver.Plugins", {"needs": ["plugins", "outputs", "nuget"]})
        check("scope/declared needs are bound, repository-scoped",
              os.path.join(tmp, "sc-cache", "outputs", "MeshWeaver.Plugins") in ro and hs_.plugins_dir in ro
              and os.path.join(tmp, "sc-cache", "nuget") in ro, ro)
        check("scope/never the cache root, never another repository",
              os.path.join(tmp, "sc-cache") not in ro and not any("MeshWeaver.Education" in p for p in ro), ro)
        av = hs_.sandbox.argv("x", "/w/s", "/w/o", {"PATH": "/bin"}, readonly=ro, writable=["/w/a"])
        check("scope/the binds are read-only, the artifacts dir writable",
              all(f"--ro-bind-try {p} {p}" in " ".join(av) for p in ro) and "--bind /w/a /w/a" in " ".join(av)
              and f"--bind {os.path.join(tmp, 'sc-cache')}" not in " ".join(av), av)
        try:
            validate_stages({"stages": [{"name": "a", "kind": "build", "command": "t", "needs": ["everything"]}]})
            check("scope/an undeclarable need is refused", False, "accepted")
        except Refusal:
            pass
        scripts_seen: list[str] = []

        def sc_run(rc_, out_):
            def run(argv):
                scripts_seen.append(argv[-1])
                return rc_, out_
            return run
        scoping_check(hs_, sc_run(0, "scoped"))
        check("scope/the self-check runs in a sandbox bound like a public stage, reading a private entry",
              scripts_seen and ".boundary-private" in scripts_seen[0] and ".boundary-root-canary" in scripts_seen[0], scripts_seen[:1])
        for rc_, why in ((4, "PRIVATE repository's mirror"), (5, "compile outputs"), (6, "cache ROOT"), (3, "own mirror"), (1, "")):
            try:
                scoping_check(hs_, sc_run(rc_, "nope"))
                check(f"scope/self-check exit {rc_} refuses to start", False, "started")
            except Fatal as e:
                check(f"scope/the refusal names it ({rc_})", why in str(e), e)
        # the reference set is keyed by BOTH digests
        rs_src = os.path.join(tmp, "composed")
        os.makedirs(rs_src)
        open(os.path.join(rs_src, "mw-plugin-test.dll"), "w").close()
        rs_env = dict(sc_env, MW_BUILD_GATE_HOST=rs_src, MW_BUILD_TESTER_DIGEST="sha256:" + "1" * 64,
                      MW_BUILD_PORTAL_DIGEST="sha256:" + "2" * 64)
        hr = Host(rs_env, _FakeApi({}), log=lambda m: None, sandbox=Sandbox("bwrap", [rs_src]), leftovers=lambda: [])
        staged = hr.stage_refset()
        check("refset/keyed by both digests", refset_key("sha256:" + "1" * 64, "sha256:" + "2" * 64) in staged
              and refset_key("sha256:" + "1" * 64, "sha256:" + "2" * 64) != refset_key("sha256:" + "1" * 64, "sha256:" + "3" * 64)
              and refset_key("sha256:" + "1" * 64, "sha256:" + "2" * 64) != refset_key("sha256:" + "4" * 64, "sha256:" + "2" * 64), staged)
        check("refset/the gate host and the bind move to the staged copy",
              hr.env["MW_BUILD_GATE_HOST"] == staged and staged in hr.sandbox.readonly and rs_src not in hr.sandbox.readonly
              and os.path.isfile(os.path.join(staged, "mw-plugin-test.dll")), (hr.env["MW_BUILD_GATE_HOST"], hr.sandbox.readonly))
        try:
            Host(dict(rs_env, MW_BUILD_PORTAL_DIGEST=""), _FakeApi({}), log=lambda m: None, sandbox=Sandbox("bwrap", []),
                 leftovers=lambda: []).stage_refset()
            check("refset/one digest alone cannot key it", False, "keyed")
        except Fatal:
            pass

        # ── 6. the plugins layer is refreshed BEFORE the claim ─────────────────────────────
        order: list[str] = []
        d1 = "sha256:" + "1" * 64
        d2 = "sha256:" + "2" * 64
        inputs = [(200, {"plugins": {"digest": d1, "identity": "c3e1", "version": "1.0.1", "sha": "a" * 40}}),
                  (200, {"plugins": {"digest": d1, "identity": "c3e1", "version": "1.0.1", "sha": "a" * 40}}),
                  (200, {"plugins": {"digest": d2, "identity": "c3e1", "version": "1.0.2", "sha": "b" * 40}})]

        def on_inputs(path, body):
            order.append("inputs")
            return inputs.pop(0)

        def on_claim(path, body):
            order.append(f"claim:{body['pluginsDigest']}")
            return 204, None

        def pull(cmd):
            order.append(f"pull:{cmd[2].rsplit('@', 1)[1]}")
            os.makedirs(cmd[4], exist_ok=True)
            with open(os.path.join(cmd[4], "MeshWeaver.AI.zip"), "w") as fh:
                fh.write("x")
            return 0

        env = {"MW_BUILD_CHANNEL": "latest", "MW_BUILD_PLATFORM_VERSION": "3.1.10412", "PATH": "/usr/bin:/bin",
               "MW_BUILD_FRAMEWORK_IDENTITY": "c3e1", "MW_BUILD_CACHE": cache, "MW_BUILD_WORK": tmp,
               "MW_BUILD_READY_FILE": os.path.join(tmp, "ready"), "MW_PLUGINS_REGISTRY": "cr.example.com",
               "MW_BUILD_HOST": "build-latest-0"}

        def host(api, **kw):
            kw.setdefault("pull", pull)
            # a pull-request run merges onto a base: a fake git that resolves and merges cleanly
            kw.setdefault("git_runner", lambda args, env_: (0, "e" * 40 + "\n" if "rev-parse" in args else ""))
            return Host(env if "env" not in kw else kw.pop("env"), api, log=lambda s: None, sandbox=_EnvSandbox(),
                        leftovers=lambda: [], sleep=lambda s: None, **kw)

        h = host(_FakeApi({("GET", "/hosts/inputs"): on_inputs, ("POST", "/claim"): on_claim}))
        h.preflight()
        for i in range(3):
            check(f"order/cycle {i} idle", h.cycle() == "idle")
        check("order/refresh before claim, pull only on a new digest", order == [
            "inputs", f"pull:{d1}", f"claim:{d1}",
            "inputs", f"claim:{d1}",
            "inputs", f"pull:{d2}", f"claim:{d2}"], order)
        check("order/content-addressed", os.path.isfile(os.path.join(cache, "plugins", "2" * 64, ".complete")))
        check("order/readiness written once staged", os.path.isfile(os.path.join(tmp, "ready")))
        api2 = _FakeApi({("GET", "/hosts/inputs"): [(503, None)], ("POST", "/claim"): on_claim})
        check("order/unreadable inputs → no claim", host(api2).cycle() == "no-inputs"
              and not any(c[1] == "/claim" for c in api2.calls), api2.calls)
        api3 = _FakeApi({("GET", "/hosts/inputs"): [(200, {"plugins": {"digest": "sha256:" + "3" * 64}})],
                         ("POST", "/claim"): on_claim})
        check("order/failed pull → no claim", host(api3, pull=lambda cmd: 1).cycle() == "no-inputs"
              and not any(c[1] == "/claim" for c in api3.calls), api3.calls)
        api4 = _FakeApi({("GET", "/hosts/inputs"): [(200, {"plugins": None})], ("POST", "/claim"): on_claim})
        check("order/no publication registered → claim with null", host(api4).cycle() == "idle"
              and api4.calls[-1][2]["pluginsDigest"] is None, api4.calls)

        # ── 6b. H8: the plugins layer from the registry's bundle index ─────────────────────
        kf = os.path.join(tmp, "mwi")
        with open(kf, "w") as fh:
            fh.write("mwi_secret\n")
        idx = {"frameworkMvid": "c3e1", "bundles": [
            {"plugin": "Store", "version": "1.4.2", "bundleHash": "aa"}, {"plugin": "AI", "version": "1.36.5"}]}
        fetched: list[list[str]] = []

        def fetch_runner(argv, env_):
            fetched.append(argv)
            check("h8/the instance key reaches module-fetch through the ENVIRONMENT only",
                  "mwi_secret" not in " ".join(argv) and env_.get("MW_REGISTRY_INSTANCE_KEY") == "mwi_secret"
                  and argv[argv.index("--key-env") + 1] == "MW_REGISTRY_INSTANCE_KEY", argv)
            out = argv[argv.index("--out") + 1]
            os.makedirs(os.path.join(out, argv[argv.index("module-fetch") + 1]))
            return 0
        claims_seen: list[dict] = []
        h8env = dict(env, MW_REGISTRY_URL="https://portal.example.com", MW_REGISTRY_KEY_FILE=kf,
                     MW_BUILD_CACHE=os.path.join(tmp, "h8cache"))
        h8 = host(_FakeApi({("POST", "/claim"): lambda p_, b: (claims_seen.append(b), (204, None))[1]}), env=h8env,
                  registry_get=lambda u, k: (200, idx) if u.endswith("/api/plugins/bundles/index.json") and k == "mwi_secret" else (401, None),
                  fetch_runner=fetch_runner)
        check("h8/a cycle stages the layer and claims", h8.cycle() == "idle")
        want_key = plugins_key_of(["AI@1.36.5", "Store@1.4.2@aa"])
        check("h8/the key is the digest of the sorted package@version[@hash] list, presented as pluginsKey",
              claims_seen and claims_seen[-1]["pluginsKey"] == want_key, claims_seen[-1:])
        check("h8/every bundle fetched once", sorted(a[a.index("module-fetch") + 1] for a in fetched) == ["AI", "Store"], fetched)
        check("h8/the set links every package", sorted(os.listdir(h8.plugins_dir)) == [".complete", "AI", "Store"], h8.plugins_dir)
        fetched.clear()
        h8.cycle()
        check("h8/an unchanged index fetches nothing", fetched == [], fetched)
        idx["bundles"][1] = {"plugin": "AI", "version": "1.36.6"}
        h8.cycle()
        check("h8/a changed bundle is the only fetch", [a[a.index("module-fetch") + 1] for a in fetched] == ["AI"], fetched)
        check("h8/the new key is presented", claims_seen[-1]["pluginsKey"] == plugins_key_of(["AI@1.36.6", "Store@1.4.2@aa"]))
        n_claims = len(claims_seen)
        idx["bundles"].append({"plugin": "Edu", "version": "2.0.0"})
        h8.fetch_runner = lambda argv, env_: 3
        check("h8/a failed module-fetch means no claim", h8.cycle() == "no-inputs" and len(claims_seen) == n_claims)
        h8w = host(_FakeApi({("POST", "/claim"): lambda p_, b: (claims_seen.append(b), (204, None))[1]}), env=h8env,
                   registry_get=lambda u, k: (200, dict(idx, frameworkMvid="c9e9")), fetch_runner=fetch_runner)
        check("h8/an index for another framework identity means no claim", h8w.cycle() == "no-inputs")
        h8m = host(_FakeApi({("POST", "/claim"): lambda p_, b: (204, None)}), env=h8env,
                   registry_get=lambda u, k: (200, {"bundles": idx["bundles"]}), fetch_runner=fetch_runner)
        check("h8/an index naming NO framework means no claim", h8m.cycle() == "no-inputs")
        h8k = host(_FakeApi({}), env=dict(h8env, MW_REGISTRY_KEY_FILE=os.path.join(tmp, "absent")), fetch_runner=fetch_runner)
        check("h8/no instance key means no claim", h8k.cycle() == "no-inputs")
        for bad in ({}, {"bundles": [{"plugin": "AI"}]}, {"bundles": [{"plugin": "../x", "version": "1"}]}, {"bundles": ["AI"]}):
            try:
                bundle_lines(bad)
                check(f"h8/refuses the index {bad}", False, "keyed")
            except Refusal:
                pass
        check("h8/the instance key never reaches a sandbox",
              not any("REGISTRY" in k for k in sandbox_env(dict(h8env, MW_REGISTRY_INSTANCE_KEY="x"), {}, "/cache")))

        # ── 7. whole runs ─────────────────────────────────────────────────────────────────
        posted: list[tuple] = []

        def on_events(path, body):
            posted.append((path, body["events"]))
            return 202, None

        def checkout_with(stages_doc):
            def co(repo, sha, refs, dest, token, env_, log, **_):
                os.makedirs(os.path.join(dest, ".github"))
                with open(os.path.join(dest, ".github", "build-stages.json"), "w") as fh:
                    json.dump(stages_doc, fh)
            return co

        claim = {"path": "Hosting/Builds/MeshWeaver.Plugins/" + "c" * 40 + "-stable",
                 "repo": "MeshWeaver.Plugins", "sha": "c" * 40, "refs": [], "isTrunk": False,
                 "publish": False, "stages": []}
        env_s = dict(env, MW_BUILD_CHANNEL="stable", BUILD_TOKEN_FILE=tf, AZURE_CLIENT_SECRET="leak")

        def api_ev(*extra):
            return _FakeApi({("POST", "/token"): [(200, {"token": "run-token"})],
                             ("POST", "/MeshWeaver.Plugins/"): on_events, **dict(extra)})

        # fail-fast + diagnostics + channel on every POST
        a5 = api_ev()
        h5 = host(a5, env=env_s, checkout=checkout_with({"stages": [
            {"name": "compile-check", "kind": "build",
             "command": "echo \"$PWD/Hosting/A.cs(3,1): error CS0246: missing X\"; exit 1"},
            {"name": "gate", "kind": "test", "command": f"echo ran > {tmp}/ran2"}]}))
        check("run/ran", h5.run(claim) == "ran")
        evpaths = [c for c in a5.calls if c[1].endswith("/events")]
        check("run/channel on every event POST", evpaths and all(c[3].get("channel") == "stable" for c in evpaths), evpaths)
        check("run/host named", all(c[2]["host"] == "build-latest-0" for c in evpaths), evpaths)
        allev = [(pth, e) for pth, evs in posted for e in evs]
        diag = [e for _, e in allev if e["kind"] == "diagnostic"]
        check("run/diagnostic relative to the checkout", diag and diag[0]["file"] == "Hosting/A.cs"
              and diag[0]["code"] == "CS0246", diag)
        fin = [(pth, e) for pth, e in allev if e["kind"] == "stage-finished"]
        check("run/compile-check failed", any("/compile-check/" in pth and e["state"] == "failed" for pth, e in fin), fin)
        check("run/gate not run and said so", any("/gate/" in pth and e["state"] == "failed"
                                                  and "not run" in e["summary"] for pth, e in fin), fin)
        check("run/fail-fast: the second stage never started", not os.path.exists(os.path.join(tmp, "ran2")))
        rf_ = [e for _, e in allev if e["kind"] == "run-finished"]
        check("run/run-finished false, once", len(rf_) == 1 and rf_[0]["succeeded"] is False, rf_)

        # what a stage SEES: fresh process per stage, publish from the claim, nothing credential-shaped
        seen = os.path.join(tmp, "seen")
        dump = f"echo $$ ${{MW_BUILD_PUBLISH}} ${{AZURE_CLIENT_SECRET:-none}} ${{BUILD_TOKEN_FILE:-none}} ${{GITHUB_TOKEN:-none}} >> {seen}"
        posted.clear()
        key2 = "ef" * 16
        h6 = host(api_ev(), env=env_s, checkout=checkout_with({"stages": [
            {"name": "a", "kind": "build", "command": dump + f"; mkdir -p $MW_BUILD_OUTPUTS/{key2} && echo y > $MW_BUILD_OUTPUTS/{key2}/Y.dll"},
            {"name": "b", "kind": "test", "command": dump}]}))
        h6.run(dict(claim, publish=True))
        rows = [r.split() for r in open(seen).read().splitlines()]
        check("run/a fresh process per stage", len(rows) == 2 and rows[0][0] != rows[1][0], rows)
        check("run/publish from the claim", all(r[1] == "true" for r in rows), rows)
        check("run/no credential reaches a stage", all(r[2:] == ["none", "none", "none"] for r in rows), rows)
        rf_ = [e for _, evs in posted for e in evs if e["kind"] == "run-finished"]
        check("run/green run-finished", rf_ == [{"kind": "run-finished", "succeeded": True, "error": None}], rf_)
        check("run/a PR run's outputs never enter the shared cache",
              not os.path.exists(os.path.join(cache, "outputs", "MeshWeaver.Plugins", key2)))
        keyed_env = dict(env_s, MW_BUILD_TESTER_DIGEST="sha256:" + "7" * 64, MW_BUILD_PORTAL_DIGEST="sha256:" + "8" * 64)
        me = os.path.abspath(__file__)
        keyed = ("echo 'public class A {}' > A.cs; "
                 "printf '{\"kind\":\"nodetype\",\"sources\":[\"A.cs\"],\"options\":\"--warnings-as-errors\"}' > spec.json; "
                 f"k=$(python3 {me} --cache-key spec.json) && mkdir -p $MW_BUILD_OUTPUTS/$k && cp spec.json $MW_BUILD_OUTPUTS/$k/.cache-key.json "
                 "&& echo y > $MW_BUILD_OUTPUTS/$k/A.dll && echo $k > ../KEY")
        h6t = host(api_ev(), env=keyed_env, checkout=checkout_with({"stages": [
            {"name": "a", "kind": "build", "command": keyed + f" && cp ../KEY {tmp}/KEY"},
            {"name": "t", "kind": "test", "command": f"mkdir -p $MW_BUILD_OUTPUTS/{'ab' * 20} && touch $MW_BUILD_OUTPUTS/{'ab' * 20}/verdict"}]}))
        h6t.run(dict(claim, isTrunk=True))
        k1 = open(os.path.join(tmp, "KEY")).read().strip() if os.path.exists(os.path.join(tmp, "KEY")) else ""
        check("run/a trunk BUILD stage's output is promoted under the key the host recomputes",
              k1 and os.path.isfile(os.path.join(cache, "outputs", "MeshWeaver.Plugins", k1, "A.dll")), k1)
        check("run/a TEST stage's output is never promoted (no test verdict is reused)",
              not os.path.exists(os.path.join(cache, "outputs", "MeshWeaver.Plugins", "ab" * 20)))
        # a stage asserting a key the inputs do not produce is refused
        lie = "f" * 64
        h6l = host(api_ev(), env=keyed_env, checkout=checkout_with({"stages": [
            {"name": "a", "kind": "build", "command": "echo x > A.cs; "
             "printf '{\"kind\":\"nodetype\",\"sources\":[\"A.cs\"]}' > s.json; "
             f"mkdir -p $MW_BUILD_OUTPUTS/{lie} && cp s.json $MW_BUILD_OUTPUTS/{lie}/.cache-key.json"}]}))
        h6l.run(dict(claim, isTrunk=True))
        check("run/an output whose name is not its recomputed key is refused", not os.path.exists(os.path.join(cache, "outputs", "MeshWeaver.Plugins", lie)))
        # the key covers sources, reference set, options and compiler
        kd = os.path.join(tmp, "keydir")
        os.makedirs(kd)
        with open(os.path.join(kd, "A.cs"), "w") as fh:
            fh.write("a")
        kenv = {"MW_BUILD_FRAMEWORK_IDENTITY": "c3e1", "MW_BUILD_PORTAL_DIGEST": "sha256:" + "8" * 64,
                "MW_BUILD_TESTER_DIGEST": "sha256:" + "7" * 64, "MW_PLUGINS_KEY": "p1"}
        spec = {"kind": "nodetype", "sources": ["A.cs"], "options": "--warnings-as-errors"}
        base_key = nodetype_cache_key(kd, spec, kenv)
        for what, sp, en in (("options", dict(spec, options=""), kenv), ("plugins key", spec, dict(kenv, MW_PLUGINS_KEY="p2")),
                             ("compiler", spec, dict(kenv, MW_BUILD_TESTER_DIGEST="sha256:" + "6" * 64)),
                             ("identity", spec, dict(kenv, MW_BUILD_FRAMEWORK_IDENTITY="c3e2"))):
            check(f"key/changes with the {what}", nodetype_cache_key(kd, sp, en) != base_key)
        with open(os.path.join(kd, "A.cs"), "w") as fh:
            fh.write("b")
        check("key/changes with a source", nodetype_cache_key(kd, spec, kenv) != base_key)
        for bad in ({"kind": "nodetype", "sources": ["../x"]}, {"kind": "nodetype", "sources": ["/etc/passwd"]},
                    {"kind": "nodetype", "sources": []}, {"kind": "elsewhere"}):
            try:
                cache_key_of(kd, bad, kenv)
                check(f"key/refuses {bad}", False, "keyed")
            except Refusal:
                pass
        check("key/a module key is core's module-build-key.py, run with the HOST's digests",
              module_cache_key(kd, {"kind": "module", "entry": {"package": "AI"}}, kenv,
                               run=lambda a: (0, "c" * 64 + "\n") if a[a.index("--tester-digest") + 1] == kenv["MW_BUILD_TESTER_DIGEST"]
                               and "module-build-key.py" in a[1] else (1, "wrong")) == "c" * 64)

        # 409 abandons; no stages declared is red; a stage past its timeout is killed and red
        a7 = _FakeApi({("POST", "/token"): [(404, None)], ("POST", "/MeshWeaver.Plugins/"): [(409, None)]})
        check("run/409 abandons the run", host(a7, env=env_s, checkout=checkout_with(
            {"stages": [{"name": "a", "kind": "build", "command": "true"}]})).run(claim) == "lost")
        check("run/409 posts nothing after it", sum(1 for c in a7.calls if c[1].endswith("/events")) == 1, a7.calls)
        posted.clear()

        def empty_checkout(repo, sha, refs, dest, token, env_, log, **_):
            os.makedirs(dest)
        host(api_ev(), env=env_s, checkout=empty_checkout).run(dict(claim, stages=["build/modules"]))
        rf_ = [e for _, evs in posted for e in evs if e["kind"] == "run-finished"]
        check("run/undeclared stages are red", rf_ and rf_[0]["succeeded"] is False
              and "declares no build stages" in rf_[0]["error"], rf_)
        check("run/refusal posted to the claimed stage", posted and "/stages/modules/events" in posted[0][0], posted)
        posted.clear()
        t0 = time.monotonic()
        host(api_ev(), env=env_s, checkout=checkout_with({"stages": [
            {"name": "slow", "kind": "test", "command": "sleep 30", "timeoutMinutes": 0.02}]})).run(claim)
        fin = [e for _, evs in posted for e in evs if e["kind"] == "stage-finished"]
        check("run/timeout kills and reds", fin and fin[0]["state"] == "failed" and "timed out" in fin[0]["summary"]
              and time.monotonic() - t0 < 20, (fin, time.monotonic() - t0))
        posted.clear()
        host(api_ev(), env=dict(env_s, MW_BUILD_RUN_DEADLINE_SECONDS="0"), checkout=checkout_with({"stages": [
            {"name": "a", "kind": "build", "command": "true"}]})).run(claim)
        rf_ = [e for _, evs in posted for e in evs if e["kind"] == "run-finished"]
        check("run/past the run deadline is red", rf_ and rf_[0]["succeeded"] is False and "deadline" in rf_[0]["error"], rf_)
        # a stage that CLOSES its output and keeps running is still bound by its timeout
        posted.clear()
        t0 = time.monotonic()
        host(api_ev(), env=env_s, checkout=checkout_with({"stages": [
            {"name": "quiet", "kind": "test", "command": "exec >/dev/null 2>&1; sleep 30", "timeoutMinutes": 0.02}]})).run(claim)
        fin = [e for _, evs in posted for e in evs if e["kind"] == "stage-finished"]
        check("run/EOF is not exit: a silent stage past its timeout is killed and red",
              fin and fin[0]["state"] == "failed" and "timed out" in fin[0]["summary"] and time.monotonic() - t0 < 20,
              (fin, time.monotonic() - t0))

        # an API outage across stage-finished: held, retried, delivered BEFORE run-finished
        order_ev: list[str] = []
        outage = {"left": 9}

        def flaky(path, body):
            kinds = [e["kind"] for e in body["events"]]
            if "stage-finished" in kinds and outage["left"] > 0:
                outage["left"] -= 1
                return 503, None
            order_ev.extend(k for k in kinds if k in ("stage-finished", "run-finished"))
            return 202, None
        hf = host(_FakeApi({("POST", "/token"): [(404, None)], ("POST", "/MeshWeaver.Plugins/"): flaky}), env=env_s,
                  checkout=checkout_with({"stages": [{"name": "a", "kind": "build", "command": "true"}]}))
        check("events/an outage over stage-finished is ridden out", hf.run(claim) == "ran", order_ev)
        check("events/stage-finished is delivered before run-finished", order_ev == ["stage-finished", "run-finished"], order_ev)
        down = host(_FakeApi({("POST", "/token"): [(404, None)], ("POST", "/MeshWeaver.Plugins/"): [(503, None)]}), env=env_s,
                    checkout=checkout_with({"stages": [{"name": "a", "kind": "build", "command": "true"}]}))
        outcome = down.run(claim)
        sent_kinds = {e.get("kind") for c in down.api.calls if c[1].endswith("/events") for e in c[2]["events"]}
        check("events/a lasting outage posts NO run-finished over a missing stage verdict",
              outcome == "undelivered" and "run-finished" not in sent_kinds,
              (outcome, sent_kinds))
        # successful batches never consume the failure budget: 1,000 events drain through 5 batches
        big = EventSink(_FakeApi({("POST", "/r/"): [(202, None)]}), "h", "r", "a" * 40, "s", "latest", lambda m: None,
                        sleep=lambda x: None)
        big.buf = [{"kind": "line", "text": str(i)} for i in range(1000)]
        check("events/many successful batches drain with a budget of one", big.flush(attempts=1) and not big.buf)

        # the checkout is bounded by the run's deadline
        try:
            default_checkout("R", "e" * 40, [], os.path.join(tmp, "late", "src"), None,
                             {"MW_BUILD_CACHE": os.path.join(tmp, "late-cache"), "PATH": "/usr/bin:/bin"},
                             lambda m: None, deadline=time.monotonic() - 1)
            check("checkout/a passed deadline is a Refusal", False, "checked out")
        except Refusal as e:
            check("checkout/names the deadline", "deadline" in str(e), e)
        # H9: artifacts go to the store, read back, and only then are their receipts posted
        store_dir = os.path.join(tmp, "store")
        os.makedirs(store_dir)
        store_calls: list[list[str]] = []
        store_timeouts: list = []

        def fake_store(argv, timeout=None):
            store_calls.append(argv)
            store_timeouts.append(timeout)
            verb = argv[2]
            if verb == "put":
                src = argv[argv.index("--file") + 1]
                h = _sha256_file(src)
                shutil.copy(src, os.path.join(store_dir, h))
                return 0, f"locator={argv[argv.index('--store') + 1]}#sha256={h}\n"
            if verb == "get":
                loc = argv[argv.index("--locator") + 1]
                shutil.copy(os.path.join(store_dir, loc.rsplit("=", 1)[1]), argv[argv.index("--out") + 1])
                return 0, ""
            return 1, "?"
        art_cmd = "mkdir -p $MW_BUILD_ARTIFACTS/Store && echo bundle > $MW_BUILD_ARTIFACTS/Store/Store-1.4.2.zip && echo '{}' > $MW_BUILD_ARTIFACTS/Store/Store.publication.json"
        posted.clear()
        ha = host(api_ev(), env=dict(env_s, MW_BUILD_ARTIFACT_STORE="file:" + store_dir), store_runner=fake_store,
                  checkout=checkout_with({"stages": [{"name": "modules", "kind": "build", "command": art_cmd}]}))
        check("h9/a run with artifacts", ha.run(claim) == "ran")
        evs = [e for _, es in posted for e in es]
        arts = [e for e in evs if e["kind"] == "artifact"]
        check("h9/one receipt per artifact, with unit, key, sha256 and bytes",
              sorted(a["unit"] for a in arts) == ["Store/Store-1.4.2.zip", "Store/Store.publication.json"]
              and all(a["key"].endswith("#sha256=" + a["sha256"]) and a["bytes"] > 0 for a in arts), arts)
        kinds_order = [e["kind"] for e in evs if e["kind"] in ("artifact", "stage-finished")]
        check("h9/receipts precede the stage's verdict", kinds_order[-1] == "stage-finished" and kinds_order.count("artifact") == 2, kinds_order)
        check("h9/every upload was read back", [c[2] for c in store_calls] == ["put", "get", "put", "get"], [c[2] for c in store_calls])
        fin = [e for e in evs if e["kind"] == "stage-finished"]
        check("h9/the stage succeeded", fin and fin[-1]["state"] == "succeeded", fin)
        check("h9/every store call is bounded by the run budget left",
              store_timeouts and all(t is not None and 0 < t <= min(1800, ha.run_deadline) for t in store_timeouts), store_timeouts)
        # the hand-over is INSIDE the run budget: a store that outlasts it, or a budget already spent, fails the stage
        def slow_store(argv, timeout=None):
            raise subprocess.TimeoutExpired(argv, timeout)
        posted.clear()
        host(api_ev(), env=dict(env_s, MW_BUILD_ARTIFACT_STORE="file:" + store_dir), store_runner=slow_store,
             checkout=checkout_with({"stages": [{"name": "modules", "kind": "build", "command": art_cmd}]})).run(claim)
        fin = [e for _, es in posted for e in es if e["kind"] == "stage-finished"]
        check("h9/a store call cut by the run deadline fails the stage, naming it", fin and fin[-1]["state"] == "failed"
              and "run deadline passed during the hand-over" in fin[-1]["summary"], fin)
        store_calls.clear()
        hd = host(api_ev(), env=dict(env_s, MW_BUILD_ARTIFACT_STORE="file:" + store_dir), store_runner=fake_store)
        spent_dir = os.path.join(tmp, "spent-artifacts", "Store")
        os.makedirs(spent_dir)
        open(os.path.join(spent_dir, "x.zip"), "w").write("x")
        _r, problem = hd.hand_over_artifacts("R", "f" * 40, "modules", os.path.dirname(spent_dir), deadline=hd.clock() - 1)
        check("h9/a spent budget makes no store call and is a problem", not store_calls and problem and "run deadline" in problem, (store_calls, problem))
        # a store that reads back other bytes, and a missing store, fail the stage
        def lying_store(argv, timeout=None):
            if argv[2] == "get":
                open(argv[argv.index("--out") + 1], "w").write("other")
                return 0, ""
            return fake_store(argv, timeout)
        posted.clear()
        host(api_ev(), env=dict(env_s, MW_BUILD_ARTIFACT_STORE="file:" + store_dir), store_runner=lying_store,
             checkout=checkout_with({"stages": [{"name": "modules", "kind": "build", "command": art_cmd}]})).run(claim)
        fin = [e for _, es in posted for e in es if e["kind"] == "stage-finished"]
        check("h9/a read-back that does not match fails the stage", fin and fin[-1]["state"] == "failed"
              and "did not read back" in fin[-1]["summary"], fin)
        posted.clear()
        host(api_ev(), env=env_s, store_runner=fake_store,
             checkout=checkout_with({"stages": [{"name": "modules", "kind": "build", "command": art_cmd}]})).run(claim)
        fin = [e for _, es in posted for e in es if e["kind"] == "stage-finished"]
        check("h9/artifacts with no store fail the stage", fin and fin[-1]["state"] == "failed" and "no artifact store" in fin[-1]["summary"], fin)
        # the workspace stays until every receipt was acknowledged
        hk = host(_FakeApi({("POST", "/token"): [(404, None)], ("POST", "/MeshWeaver.Plugins/"): [(503, None)]}),
                  env=dict(env_s, MW_BUILD_ARTIFACT_STORE="file:" + store_dir), store_runner=fake_store,
                  checkout=checkout_with({"stages": [{"name": "modules", "kind": "build", "command": art_cmd}]}))
        before = set(os.listdir(tmp))
        check("h9/unacknowledged receipts", hk.run(claim) == "undelivered")
        kept = [n for n in set(os.listdir(tmp)) - before if n.startswith("MeshWeaver.Plugins-")]
        check("h9/the workspace is kept while receipts are unacknowledged", len(kept) == 1, kept)
        old = time.time() + 3 * hk.run_deadline
        check("h9/a kept workspace is swept after two deadlines", kept and kept[0] in hk.sweep_workspaces(now=old), kept)

        # the PLAN: host-emitted first, naming every stage it will run; seq numbers per stage
        posted.clear()
        seen_seq: list[tuple] = []

        def rec(path, body):
            seen_seq.append((path.split("/stages/")[1].split("/")[0], body["seq"], [e["kind"] for e in body["events"]]))
            posted.append((path, body["events"]))
            return 202, None
        hp = host(_FakeApi({("POST", "/token"): [(404, None)], ("POST", "/MeshWeaver.Plugins/"): rec}), env=env_s,
                  checkout=checkout_with({"stages": [{"name": "a", "kind": "build", "command": "true"},
                                                     {"name": "b", "kind": "test", "command": "true"}]}))
        hp.run(claim)
        kinds = [e for _, evs in posted for e in evs if e["kind"] == "plan"]
        check("plan/one plan, naming every stage the host will run (and, for a PR run, its merge base)",
              [{k: v for k, v in p_.items() if k != "budgetMinutes"} for p_ in kinds]
              == [{"kind": "plan", "stages": ["a", "b"], "mergeBase": "e" * 40}], kinds)
        # the declared budget: the stages' caps + margin, within the deadline, in whole minutes 10..720
        check("budget/the plan declares budgetMinutes", kinds and kinds[0].get("budgetMinutes") == budget_minutes(
            run_budget_seconds([{"timeoutMinutes": 45}, {"timeoutMinutes": 45}], hp.run_deadline)), kinds)
        check("budget/two 40+45-minute stages fit the default deadline",
              run_budget_seconds([{"timeoutMinutes": 40}, {"timeoutMinutes": 45}], DEFAULT_RUN_DEADLINE) == 85 * 60 + RUN_MARGIN_SECONDS)
        check("budget/capped by the deadline", run_budget_seconds([{"timeoutMinutes": 45}] * 4, 2100) == 2100)
        check("budget/2100 s declares 35 minutes", budget_minutes(2100) == 35)
        check("budget/minutes round up and stay in 10..720",
              budget_minutes(61) == 10 and budget_minutes(661) == 12 and budget_minutes(10 ** 6) == 720)
        firsts = [k for _, _, ks in seen_seq for k in ks if k != "line"]
        check("plan/the plan precedes every stage verdict", firsts and firsts[0] == "plan", firsts)
        per_stage: dict = {}
        for st_, sq, _ in seen_seq:
            per_stage.setdefault(st_, []).append(sq)
        check("seq/each stage's batches are numbered 1, 2, …", all(v == list(range(1, len(v) + 1)) for v in per_stage.values()), per_stage)
        # a retried batch keeps its number
        tries: list[int] = []
        answers = [503, 202]
        sq = EventSink(_FakeApi({("POST", "/r/"): lambda p, b: (tries.append(b["seq"]) or answers.pop(0), None)}),
                       "h", "r", "a" * 40, "s", "latest", lambda m: None, sleep=lambda x: None)
        sq.add([{"kind": "line", "text": "x"}])
        sq.flush(attempts=3)
        check("seq/a retried batch keeps its number", tries == [1, 1] and sq.seq == 1, tries)
        # a LOST acknowledgement: the retry re-sends the SAME payload under the SAME seq; events added
        # meanwhile go under the NEXT seq
        sent: list[tuple] = []
        replies = [503, 202, 202]
        lk = EventSink(_FakeApi({("POST", "/r/"): lambda p_, b: (sent.append((b["seq"], json.dumps(b["events"]))) or replies.pop(0), None)}),
                       "h", "r", "a" * 40, "s", "latest", lambda m: None, sleep=lambda x: None)
        lk.buf = [{"kind": "line", "text": "first"}]
        lk.flush(attempts=1)                       # the server committed it; the answer was lost (503)
        lk.add([{"kind": "stage-finished", "state": "succeeded", "summary": "s"}])
        lk.flush(attempts=3)
        check("seq/a retried batch is byte-identical under its seq", sent[0] == sent[1] and sent[0][0] == 1, sent)
        check("seq/events added meanwhile go under the next seq", len(sent) == 3 and sent[2][0] == 2
              and "stage-finished" in sent[2][1] and "stage-finished" not in sent[1][1], sent)
        # the heartbeat: an empty batch once HEARTBEAT_SECONDS passed with nothing posted
        now = {"t": 0.0}
        hbp: list = []
        hs = EventSink(_FakeApi({("POST", "/r/"): lambda p, b: (hbp.append(b), (202, None))[1]}),
                       "h", "r", "a" * 40, "s", "latest", lambda m: None, sleep=lambda x: None, clock=lambda: now["t"])
        hs.beat()
        check("lease/no heartbeat before the period", hbp == [], hbp)
        now["t"] = HEARTBEAT_SECONDS + 1
        hs.add([])
        check("lease/an empty batch is the heartbeat", hbp == [{"host": "h", "claimId": "", "seq": 1, "events": []}], hbp)
        check("lease/the heartbeat period is under a minute", HEARTBEAT_SECONDS < 60)
        hbt = Heartbeat(period=0.05)
        hbt.target = hs
        now["t"] = 2 * HEARTBEAT_SECONDS + 2
        with hbt:
            time.sleep(0.3)
        check("lease/the run's heartbeat thread beats the current sink", len(hbp) >= 2, hbp)
        # claimId: fresh per claim attempt, the SAME one on a network-error retry
        ids: list[str] = []
        ans = [(0, "reset"), (503, None), (204, None), (204, None)]

        def on_claim_id(path, body):
            ids.append(body["claimId"])
            return ans.pop(0)
        hc = host(_FakeApi({("POST", "/claim"): on_claim_id}))
        hc.claim()
        check("claim/a network error retries the SAME claimId", len(ids) == 3 and len(set(ids)) == 1, ids)
        hc.claim()
        check("claim/a new claim attempt carries a fresh claimId", len(ids) == 4 and ids[3] != ids[0], ids)
        check("claim/the claimId is a uuid", all(len(i) == 36 and i.count("-") == 4 for i in ids), ids)
        # ONE merged tree per attempt: null → main's tip resolved ONCE and reused; non-null → verbatim;
        # trunk → no merge
        def merge_host(gitlog, tip="e" * 40, merge_rc=0):
            def git(args, env_):
                gitlog.append(args)
                if "rev-parse" in args:
                    return 0, tip + "\n"
                if "merge" in args and "--abort" not in args:
                    return merge_rc, "CONFLICT" if merge_rc else ""
                return 0, ""
            posted.clear()
            return host(api_ev(), env=env_s, git_runner=git, checkout=checkout_with({"stages": [
                {"name": "a", "kind": "build", "command": "true"}, {"name": "b", "kind": "test", "command": "true"}]}))
        plans = lambda: [e for _, es in posted for e in es if e["kind"] == "plan"]
        gl: list = []
        merge_host(gl).run(dict(claim, isTrunk=False, mergeBaseSha=None))
        check("merge/null: main's tip resolved ONCE", sum(1 for a in gl if "rev-parse" in a) == 1, gl)
        check("merge/null: merged ONCE for every stage", sum(1 for a in gl if "merge" in a) == 1, gl)
        check("merge/null: the plan names the resolved base", plans() and plans()[0].get("mergeBase") == "e" * 40, plans())
        gl = []
        merge_host(gl).run(dict(claim, isTrunk=False, mergeBaseSha="F" * 40))
        check("merge/non-null: used VERBATIM, never re-resolved", not any("rev-parse" in a for a in gl)
              and any(a[-1] == "f" * 40 and "fetch" in a for a in gl) and plans()[0].get("mergeBase") == "f" * 40, (gl, plans()))
        gl = []
        merge_host(gl).run(dict(claim, isTrunk=True, mergeBaseSha=None))
        check("merge/trunk: no merge, no mergeBase", gl == [] and "mergeBase" not in plans()[0], (gl, plans()))
        gl = []
        merge_host(gl).run(dict(claim, isTrunk=False, kind="probe", mergeBaseSha=None))
        check("merge/probe: no merge, no mergeBase", gl == [] and "mergeBase" not in plans()[0], (gl, plans()))
        gl = []
        merge_host(gl).run(dict(claim, isTrunk=False, isProbe=True, mergeBaseSha=None))
        check("merge/isProbe: no merge, no mergeBase", gl == [] and "mergeBase" not in plans()[0], (gl, plans()))
        # a probe run's id rides on every events POST (and the token request); a normal run's never
        pq: list = []
        hpq = host(_FakeApi({("POST", "/token"): lambda p_, b: (pq.append(("token", None)), (404, None))[1],
                             ("POST", "/MeshWeaver.Plugins/"): lambda p_, b: (202, None)}), env=env_s,
                   checkout=checkout_with({"stages": [{"name": "a", "kind": "build", "command": "true"}]}))
        hpq.run(dict(claim, isTrunk=False, isProbe=True, probe="p-123"))
        evq = [c[3] for c in hpq.api.calls if c[1].endswith("/events")]
        check("probe/every events POST carries &probe=<id>", evq and all(q.get("probe") == "p-123" and q.get("channel") == "stable" for q in evq), evq)
        check("probe/the token request carries it too", any(c[1] == "/token" and c[3].get("probe") == "p-123" for c in hpq.api.calls), hpq.api.calls[:2])
        hnq = host(_FakeApi({("POST", "/token"): [(404, None)], ("POST", "/MeshWeaver.Plugins/"): [(202, None)]}), env=env_s,
                   checkout=checkout_with({"stages": [{"name": "a", "kind": "build", "command": "true"}]}))
        hnq.run(dict(claim, probe=None))
        check("probe/a normal run carries no probe", all("probe" not in c[3] for c in hnq.api.calls if c[1].endswith("/events")), hnq.api.calls[:3])
        gl = []
        merge_host(gl, merge_rc=1).run(dict(claim, isTrunk=False, mergeBaseSha=None))
        rf = [e for _, es in posted for e in es if e["kind"] == "run-finished"]
        check("merge/a conflict is red, never a test of the unmerged head",
              rf and rf[0]["succeeded"] is False and "does not merge" in rf[0]["error"] and not plans(), (rf, plans()))
        gl = []
        merge_host(gl).run(dict(claim, isTrunk=False, mergeBaseSha="not-a-sha"))
        rf = [e for _, es in posted for e in es if e["kind"] == "run-finished"]
        check("merge/a malformed mergeBaseSha is refused", rf and "not a full commit" in rf[0]["error"], rf)

        # every batch carries the assignment's claimId; a stale-claim 409 ends the run; 503 is retried
        bodies: list[dict] = []
        hci = host(_FakeApi({("POST", "/claim"): [(200, dict(claim, stages=[]))], ("POST", "/token"): [(404, None)],
                             ("POST", "/MeshWeaver.Plugins/"): lambda p_, b: (bodies.append(b), (202, None))[1]}), env=env_s,
                   checkout=checkout_with({"stages": [{"name": "a", "kind": "build", "command": "true"}]}))
        got = hci.claim()
        hci.run(got)
        check("claimId/every batch carries the assignment's claimId",
              bodies and all(b.get("claimId") == hci.claim_id and len(hci.claim_id) == 36 for b in bodies), bodies[:2])
        stale = host(_FakeApi({("POST", "/token"): [(404, None)],
                               ("POST", "/MeshWeaver.Plugins/"): [(409, {"error": "stale-claim"})]}), env=env_s,
                     checkout=checkout_with({"stages": [{"name": "a", "kind": "build", "command": f"sleep 30; echo ran > {tmp}/stale"}]}))
        t1 = time.monotonic()
        check("claimId/a stale-claim 409 abandons the run and kills the child",
              stale.run(dict(claim, claimId="old")) == "lost" and time.monotonic() - t1 < 20
              and not os.path.exists(os.path.join(tmp, "stale")), time.monotonic() - t1)
        tries503: list[int] = []
        ans503 = [503, 503, 202]
        s503 = EventSink(_FakeApi({("POST", "/r/"): lambda p_, b: (tries503.append(b["seq"]) or ans503.pop(0), None)}),
                         "h", "r", "a" * 40, "s", "latest", lambda m: None, sleep=lambda x: None, claim_id="c")
        s503.add([{"kind": "line", "text": "x"}])
        check("503/retried with the same batch and seq, never a failure", s503.flush(attempts=6) and tries503 == [1, 1, 1], tries503)
        check("sandbox/TMPDIR is the stage's tmpfs scratch", sandbox_env({}, {}, "/cache")["TMPDIR"] == f"{SCRATCH}/tmp"
              and f"--tmpfs {SCRATCH}" in " ".join(Sandbox("bwrap", []).argv("x", "/w/s", "/w/o", {"PATH": "/bin"})))
        # 409 stale-claim-id: a FRESH id, once; 409 stale-set: drain, no new claim
        sid: list[str] = []
        sans = [(409, {"error": "stale-claim-id"}), (204, None)]
        hsc = host(_FakeApi({("POST", "/claim"): lambda p_, b: (sid.append(b["claimId"]), sans.pop(0))[1]}))
        hsc.claim()
        check("claim/stale-claim-id → the next request carries a DIFFERENT claimId", len(sid) == 2 and sid[0] != sid[1], sid)
        sid.clear()
        sans2 = [(409, {"error": "stale-claim-id"}), (409, {"error": "stale-claim-id"}), (204, None)]
        hsc2 = host(_FakeApi({("POST", "/claim"): lambda p_, b: (sid.append(b["claimId"]), sans2.pop(0))[1]}))
        check("claim/stale-claim-id twice → once more only, never a loop", hsc2.claim() is None and len(sid) == 2 and not hsc2.stopping, sid)
        sid.clear()
        hss = host(_FakeApi({("POST", "/claim"): lambda p_, b: (sid.append(b["claimId"]), (409, {"error": "stale-set"}))[1]}))
        check("claim/stale-set → drain, no new claim", hss.claim() is None and len(sid) == 1 and hss.stopping, sid)
        check("claim/a draining host claims nothing more", hss.cycle() == "stopping" and len(sid) == 1, sid)
        # the plugins layer is in the cache key in EITHER mode
        hk1 = host(_FakeApi({}))
        hk1.plugins_digest, hk1.plugins_key = "sha256:" + "a" * 64, None
        check("key/the seal's digest keys the plugins layer when there is no index key", hk1.effective_plugins_key() == "sha256:" + "a" * 64)
        hk1.plugins_key = "sha256:" + "b" * 64
        check("key/the index key wins when present", hk1.effective_plugins_key() == "sha256:" + "b" * 64)
        # a refusal's JSON body reaches claim() through the real transport
        import http.server
        class _H(http.server.BaseHTTPRequestHandler):
            def do_POST(self):
                self.rfile.read(int(self.headers.get("Content-Length") or 0))
                body = json.dumps({"error": "stale-set"}).encode()
                self.send_response(409); self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(body))); self.end_headers(); self.wfile.write(body)
            def log_message(self, *a):
                pass
        srv = http.server.HTTPServer(("127.0.0.1", 0), _H)
        threading.Thread(target=srv.handle_request, daemon=True).start()
        st_, body_ = Api(f"http://localhost:{srv.server_address[1]}/api/builds", "t").call("POST", "/claim", {})
        srv.server_close()
        check("api/a 409's JSON body is kept beside its status", st_ == 409 and body_ == {"error": "stale-set"}, (st_, body_))
        # the stage declaration is read from the MERGED tree
        def merging_git(args, env_):
            if "rev-parse" in args:
                return 0, "e" * 40 + "\n"
            if "merge" in args and "--abort" not in args:
                gs = os.path.join(args[1], ".github", "build-stages.json")
                json.dump({"stages": [{"name": "merged", "kind": "build", "command": "true"}]}, open(gs, "w"))
            return 0, ""
        posted.clear()
        hmt = host(api_ev(), env=env_s, git_runner=merging_git, checkout=checkout_with({"stages": [
            {"name": "head", "kind": "build", "command": "true"}]}))
        hmt.run(dict(claim, isTrunk=False, mergeBaseSha=None))
        pl = [e for _, es in posted for e in es if e["kind"] == "plan"]
        check("merge/the stages run are the MERGED tree's declaration", pl and pl[0]["stages"] == ["merged"], pl)
        # the run budget
        check("budget/the default deadline covers the declared stages and stays in range",
              MIN_RUN_DEADLINE <= DEFAULT_RUN_DEADLINE <= MAX_RUN_DEADLINE)
        for bad_deadline in ("60", str(721 * 60)):
            try:
                host(_FakeApi({}), env=dict(env, MW_BUILD_RUN_DEADLINE_SECONDS=bad_deadline)).preflight()
                check(f"budget/a run deadline of {bad_deadline} s is refused", False, "started")
            except Refusal as e:
                check("budget/the refusal names the RUN budget", "RUN budget" in str(e), e)
        # a probe runs EXACTLY its requestedStages and plans only those
        posted.clear()
        hrq = host(api_ev(), env=env_s, checkout=checkout_with({"stages": [
            {"name": "a", "kind": "build", "command": f"echo a >> {tmp}/rq"}, {"name": "b", "kind": "test", "command": f"echo b >> {tmp}/rq"}]}))
        hrq.run(dict(claim, isProbe=True, probe="p-9", requestedStages=["b"]))
        pl = [e for _, es in posted for e in es if e["kind"] == "plan"]
        check("probe/requestedStages: only those stages are planned", pl and pl[0]["stages"] == ["b"], pl)
        check("probe/requestedStages: only those stages run", open(os.path.join(tmp, "rq")).read().split() == ["b"])
        posted.clear()
        hrq.run(dict(claim, isProbe=True, probe="p-9", requestedStages=[]))
        pl = [e for _, es in posted for e in es if e["kind"] == "plan"]
        check("probe/empty requestedStages = every declared stage", pl and pl[0]["stages"] == ["a", "b"], pl)
        posted.clear()
        hrq.run(dict(claim, isProbe=True, probe="p-9", requestedStages=["nope"]))
        rf = [e for _, es in posted for e in es if e["kind"] == "run-finished"]
        check("probe/a requested stage the repository does not declare is red", rf and rf[0]["succeeded"] is False, rf)
        plan_from_child = OutputParser("/w", "x").feed('::mw-event::{"kind":"plan","stages":["x"]}')
        check("plan/a child cannot emit the plan", plan_from_child and "refused" in plan_from_child[0]["text"], plan_from_child)
    finally:
        shutil.rmtree(tmp, ignore_errors=True)

    if failures:
        for f in failures:
            print(f"SELF-TEST FAILED: {f}")
        return 1
    print("build-run self-test: all cases passed (diagnostics, dedup, vstest + xunit results, structured events, "
          "stage declaration, the bwrap argv, the env allow-list, the token file, the startup sandbox probe, "
          "pid-namespace leftovers, one merged tree per PR attempt, the network-boundary self-check, trunk-only BUILD-stage cache promotion under recomputed keys, the plan, batch seq, the lease heartbeat, the claimId, the git mirror, cache scoping per stage, the refset key, the H9 artifact hand-over, the workload-identity pull, the registry bundle index (H8), plugins refresh BEFORE claim, fresh process per "
          "stage, fail-fast, channel + publish, 409, undeclared stages, stage timeout, run deadline)")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
