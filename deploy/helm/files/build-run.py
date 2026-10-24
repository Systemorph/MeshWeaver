#!/usr/bin/env python3
"""build-run.py — the standing build host's runner: claim a run, build and test it in fresh processes.

The HOST half of the standing build hosts (MeshWeaver.Plugins `Hosting/StandingBuildHosts` §2 and
§4.3; platform reference Doc/Architecture/BuildHosts). One warm Deployment per release channel
(`build-latest`, `build-stable`, chart `deploy/helm/templates/build-host/`) runs this as its main
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
  2. `POST {api}/claim` with the host's channel, platform set, framework identity and plugins
     digest. 204 = nothing claimable → sleep. 200 = a run: `{path, repo, sha, refs, isTrunk,
     publish, stages}` — `path` is authoritative (a non-latest run's node id is `<sha>-<channel>`).
  3. The run is checked out at `sha` into a FRESH work directory, and every stage the repository
     declares in `.github/build-stages.json` runs in its own FRESH child process — never a process
     reused across stages or runs, so one PR's test cannot poison the next PR's verdict. The child
     gets the checkout, the read-only caches, the run's short-lived installation token and
     `MW_BUILD_PUBLISH=true|false` (from the claim — a `stable` run never hands anything to the
     registry), and NOTHING else: the worker token and every *TOKEN/*SECRET/*PASSWORD variable of
     the host are scrubbed from its environment.
  4. The child's output is translated into PROGRESS EVENTS (§7a) and POSTed in batches to
     `{api}/{repo}/{sha}/stages/{stage}/events?channel=<c>`: compiler `file(line,col): error CODE:
     message` → `diagnostic`; test results (vstest `Passed/Failed/Skipped` and xunit `[FAIL]`) →
     `test-finished`; a structured `::mw-event::{json}` line → that event; plus the host's own
     `line`, `stage-finished` and `run-finished`. Everything else the child prints stays in a local
     tail and is forwarded ONLY when the stage failed with nothing else to explain it ("CI logs
     warn/error + verdicts only").
  5. 409 on an event POST = the run is claimed by another host: the child is killed, nothing more
     is posted for that run.

`.github/build-stages.json` (declared by each repository; validated here):
    {"stages": [{"name": "compile-check", "kind": "build", "command": "python3 …",
                 "timeoutMinutes": 40}, …]}
  * `name` is ONE path segment ([a-z0-9][a-z0-9._-]*); its Check Run is `build/<name>`.
  * `kind` is `build` or `test`; `timeoutMinutes` defaults to 45.
  * stages run in declaration order, fail-fast: after a failed stage the rest are finished
    `failed` with "not run" — never left open, never silently skipped. When the claim names
    `stages`, only those run, and a claimed stage the repository does not declare is RED.

Environment (rendered by the chart from `buildHosts.*`):
  BUILD_API                    https://<operations instance>/api/builds              required
  BUILD_TOKEN                  the worker token (Hosting:Builds:WorkerToken), bearer  required
  MW_BUILD_CHANNEL             latest | stable                                        required
  MW_BUILD_PLATFORM_VERSION    the set this host runs (3.x.y-ci.N / 3.x.N)            required
  MW_BUILD_FRAMEWORK_IDENTITY  or MW_BUILD_IDENTITY_FILE (first token of the file)    required
  MW_BUILD_HOST                the claiming host's name (default: hostname)
  MW_BUILD_CACHE               the shared cache volume                    default /cache
  MW_BUILD_WORK                the per-run work root                       default /work
  MW_BUILD_READY_FILE          written once the host is staged (readiness) default /tmp/build-host-ready
  MW_BUILD_GIT_BASE            clone base                                  default https://github.com/Systemorph
  MW_BUILD_IDLE_SECONDS        sleep after a 204                           default 15
  MW_PLUGINS_REGISTRY          registry host the plugins publication is pulled from
  MW_PLUGINS_SOURCE            the publication's source name               default plugins
  MW_PLUGINS_REGISTRY_CONFIG   a docker config.json with the pull credential (optional)
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
MW_EVENT = re.compile(r"^::mw-event::(?P<json>\{.*\})\s*$")
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

    # -- the feed --------------------------------------------------------------------------
    def feed(self, raw: str) -> list[dict]:
        line = raw.rstrip("\r\n")
        self.tail.append(line[:MAX_LINE])
        if len(self.tail) > TAIL_LINES:
            del self.tail[0]

        m = MW_EVENT.match(line)
        if m:
            try:
                ev = json.loads(m.group("json"))
            except ValueError:
                return []
            if isinstance(ev, dict) and ev.get("kind") in CHILD_KINDS:
                if ev["kind"] == "diagnostic":
                    return self._diag(str(ev.get("severity") or "error"), self.rel(str(ev.get("file") or "")),
                                      int(ev.get("line") or 0), str(ev.get("code") or ""),
                                      str(ev.get("message") or ""))
                if ev["kind"] == "test-finished" and ev.get("outcome") in self.tests:
                    self.tests[ev["outcome"]] += 1
                return [ev]
            return []

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
                          f"(its Check Run is build/<name>)")
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
        out.append({"name": name, "kind": kind, "command": cmd, "timeoutMinutes": float(tmo)})
    return out


def select_stages(declared: list[dict], claimed: object) -> list[dict]:
    """The stages this run executes: all declared, or — when the claim names stages — those, in
    declaration order. A claimed stage the repository does not declare is a Refusal."""
    names = []
    for c in claimed or []:
        n = c.get("name") if isinstance(c, dict) else c
        if isinstance(n, str) and n:
            names.append(n[len("build/"):] if n.startswith("build/") else n)
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
            return e.code, None
        except (urllib.error.URLError, OSError, ValueError) as e:
            return 0, str(e)


def events_path(repo: str, sha: str, stage: str) -> str:
    return f"/{urllib.parse.quote(repo, safe='')}/{sha}/stages/{urllib.parse.quote(stage, safe='')}/events"


class EventSink:
    """Batches one stage's events and POSTs them. 409 raises Lost; other failures are retried."""

    def __init__(self, api: Api, host: str, repo: str, sha: str, stage: str, channel: str,
                 log: Callable[[str], None], sleep: Callable[[float], None] = time.sleep,
                 clock: Callable[[], float] = time.monotonic) -> None:
        self.api, self.host, self.log, self.sleep, self.clock = api, host, log, sleep, clock
        self.path = events_path(repo, sha, stage)
        self.query = {"channel": channel}
        self.buf: list[dict] = []
        self.last = clock()
        self.dropped = 0

    def add(self, events: Iterable[dict]) -> None:
        for e in events:
            if e.get("kind") == "line":
                e = dict(e, text=str(e.get("text", ""))[:MAX_LINE])
            self.buf.append(e)
        if len(self.buf) > 20 * BATCH_EVENTS:      # the instance is unreachable for a long time
            over = len(self.buf) - 20 * BATCH_EVENTS
            del self.buf[:over]
            self.dropped += over
        if len(self.buf) >= BATCH_EVENTS or self.clock() - self.last >= BATCH_SECONDS:
            self.flush(attempts=1)

    def flush(self, attempts: int = 4) -> bool:
        if not self.buf:
            self.last = self.clock()
            return True
        if self.dropped:
            self.buf.insert(0, {"kind": "line", "text": f"{self.dropped} progress event(s) were dropped "
                                                        f"while the build API was unreachable"})
            self.dropped = 0
        delay = 2.0
        for i in range(attempts):
            batch = self.buf[:BATCH_EVENTS]
            status, _ = self.api.call("POST", self.path, {"host": self.host, "events": batch}, self.query)
            if status == 409:
                raise Lost(f"{self.path}: claimed by another host (409)")
            if 200 <= status < 300:
                del self.buf[:len(batch)]
                self.last = self.clock()
                if not self.buf:
                    return True
                continue
            self.log(f"events POST {self.path} → {status}; {len(self.buf)} event(s) held (attempt {i + 1}/{attempts})")
            if i + 1 < attempts:
                self.sleep(delay)
                delay *= 2
        self.last = self.clock()
        return not self.buf


# ───────────────────────────────── the plugins layer ──────────────────────────────────────

def stage_plugins(plugins: dict, cache: str, env: dict,
                  pull: Callable[[list[str]], int]) -> str:
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
    if env.get("MW_PLUGINS_REGISTRY_CONFIG"):
        cmd += ["--registry-config", env["MW_PLUGINS_REGISTRY_CONFIG"]]
    rc = pull(cmd)
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


# ───────────────────────────────── the host ───────────────────────────────────────────────

def api_url_acceptable(url: str) -> bool:
    """https anywhere; plain http only to a cluster-local host (no dot, *.svc, *.cluster.local,
    localhost) — the same rule the chart's `memex.buildHosts` gate applies."""
    p = urllib.parse.urlparse(url or "")
    host = (p.hostname or "").lower()
    if p.scheme == "https" and host:
        return True
    return p.scheme == "http" and bool(host) and (
        "." not in host or host.endswith(".svc") or host.endswith(".cluster.local"))


def child_env(base: dict, extra: dict) -> dict:
    """The environment a stage process gets: the host's, minus every credential, plus the run's."""
    env = {k: v for k, v in base.items() if not SECRET_ENV.search(k) and k != "BUILD_API"}
    env.update(extra)
    return env


def git_auth_env(token: Optional[str]) -> dict:
    """Git credentials through the ENVIRONMENT (never argv, never a URL, never the checkout)."""
    env = {"GIT_TERMINAL_PROMPT": "0"}
    if token:
        basic = base64.b64encode(f"x-access-token:{token}".encode()).decode()
        env.update({"GIT_CONFIG_COUNT": "1", "GIT_CONFIG_KEY_0": "http.https://github.com/.extraheader",
                    "GIT_CONFIG_VALUE_0": f"AUTHORIZATION: basic {basic}"})
    return env


def default_checkout(repo: str, sha: str, refs: list, dest: str, token: Optional[str],
                     env: dict, log: Callable[[str], None]) -> None:
    base = env.get("MW_BUILD_GIT_BASE", "https://github.com/Systemorph").rstrip("/")
    mirror = os.path.join(env.get("MW_BUILD_CACHE", "/cache"), "git", f"{repo}.git")
    genv = {**{k: v for k, v in env.items() if not SECRET_ENV.search(k)}, **git_auth_env(token)}

    def git(*args: str) -> None:
        r = subprocess.run(["git", *args], env=genv, capture_output=True, text=True)
        if r.returncode != 0:
            raise Refusal(f"git {args[0]} failed (exit {r.returncode}): {(r.stderr or '').strip()[-400:]}")

    clone = ["clone", "--no-checkout", "--quiet"]
    if os.path.isdir(mirror):
        clone += ["--reference-if-able", mirror]
    git(*clone, f"{base}/{repo}.git", dest)
    wanted = [sha] + [r for r in (refs or []) if isinstance(r, str) and r]
    git("-C", dest, "fetch", "--quiet", "origin", *wanted)
    git("-C", dest, "checkout", "--quiet", "--detach", sha)


class Host:
    def __init__(self, env: dict, api: Api, *, log: Callable[[str], None] = None,
                 pull: Callable[[list[str]], int] = None, checkout: Callable = None,
                 sleep: Callable[[float], None] = time.sleep,
                 clock: Callable[[], float] = time.monotonic) -> None:
        self.env = env
        self.api = api
        self.log = log or (lambda s: print(f"build-run: {s}", flush=True))
        self.pull = pull or (lambda cmd: subprocess.run(cmd).returncode)
        self.checkout = checkout or default_checkout
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
        self.cache = env.get("MW_BUILD_CACHE", "/cache")
        self.work = env.get("MW_BUILD_WORK", "/work")
        self.ready_file = env.get("MW_BUILD_READY_FILE", "/tmp/build-host-ready")
        self.plugins_digest: Optional[str] = None
        self.plugins_dir: Optional[str] = None
        self.plugins_known = False          # True once an `inputs` read succeeded and was staged

    def preflight(self) -> None:
        missing = []
        if not api_url_acceptable(self.api.base):
            missing.append("BUILD_API (the operations instance's /api/builds: https, or http to a "
                           "cluster-local host — the worker token rides on it)")
        if not self.api.token:
            missing.append("BUILD_TOKEN (the worker token, Hosting:Builds:WorkerToken)")
        if not CHANNEL.match(self.channel):
            missing.append("MW_BUILD_CHANNEL (latest | stable)")
        if not self.platform_version:
            missing.append("MW_BUILD_PLATFORM_VERSION (the set this host runs)")
        if not self.identity:
            missing.append("MW_BUILD_FRAMEWORK_IDENTITY or a readable MW_BUILD_IDENTITY_FILE")
        if missing:
            raise Refusal("the host cannot start — provide: " + "; ".join(missing))

    # -- step 1: the plugins layer, refreshed BEFORE any claim -------------------------------
    def refresh_inputs(self) -> bool:
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
        status, body = self.api.call("POST", "/claim", {
            "host": self.host, "channel": self.channel, "platformVersion": self.platform_version,
            "frameworkIdentity": self.identity, "pluginsDigest": self.plugins_digest})
        if status == 204:
            return None
        if status == 200 and isinstance(body, dict):
            return body
        self.log(f"claim answered HTTP {status} — retrying after a pause")
        return None

    def cycle(self) -> str:
        """One loop turn. Returns what happened: 'stopping', 'no-inputs', 'idle', 'ran', 'lost'."""
        if self.stopping:
            return "stopping"
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
        if not REPO.match(repo) or not SHA.match(sha):
            self.log(f"claim {path!r} carries an unusable repo/sha ({repo!r}, {sha!r}) — not running it")
            return "idle"
        started = self.clock()
        workdir = tempfile.mkdtemp(prefix=f"{repo}-{sha[:12]}-", dir=self.work)
        checkout_dir = os.path.join(workdir, "src")
        last_stage = None
        stages: list[dict] = []
        try:
            fallback = [c.get("name") if isinstance(c, dict) else c for c in (claim.get("stages") or [])]
            last_stage = next((s[len("build/"):] if s.startswith("build/") else s
                               for s in fallback if isinstance(s, str) and s), "verdict")
            self.log(f"claimed {path} ({repo}@{sha[:12]}, channel {self.channel}, publish={publish})")
            token = self.installation_token(claim)
            try:
                self.checkout(repo, sha, claim.get("refs") or [], checkout_dir, token, self.env, self.log)
                stages = select_stages(load_stages(os.path.join(checkout_dir, ".github", "build-stages.json")),
                                       claim.get("stages"))
            except Refusal as r:
                sink = self.sink(repo, sha, last_stage)
                sink.add([{"kind": "line", "text": f"run refused before any stage: {r}"},
                          {"kind": "stage-finished", "state": "failed", "summary": str(r)[:MAX_LINE]},
                          {"kind": "run-finished", "succeeded": False, "error": str(r)[:MAX_LINE]}])
                sink.flush(attempts=6)
                return "ran"
            extra = {
                "MW_BUILD_PUBLISH": "true" if publish else "false",
                "MW_BUILD_CHANNEL": self.channel, "MW_BUILD_REPO": repo, "MW_BUILD_SHA": sha,
                "MW_BUILD_RUN_PATH": path, "MW_BUILD_IS_TRUNK": "true" if claim.get("isTrunk") is True else "false",
                "MW_BUILD_PLATFORM_VERSION": self.platform_version,
                "MW_BUILD_FRAMEWORK_IDENTITY": self.identity,
                "MW_PLUGINS_DIR": self.plugins_dir or "", "MW_PLUGINS_DIGEST": self.plugins_digest or "",
                "MW_BUILD_CACHE": self.cache, "HOME": os.path.join(workdir, "home"),
            }
            if token:
                extra["GITHUB_TOKEN"] = token
            os.makedirs(extra["HOME"], exist_ok=True)
            failed_at = None
            for st in stages:
                last_stage = st["name"]
                sink = self.sink(repo, sha, st["name"])
                if failed_at is not None:
                    sink.add([{"kind": "stage-finished", "state": "failed",
                               "summary": f"not run — stage {failed_at} failed first"}])
                    sink.flush(attempts=6)
                    continue
                ok = self.run_stage(st, checkout_dir, sink, {**extra, "MW_BUILD_STAGE": st["name"]})
                if not ok:
                    failed_at = st["name"]
            sink = self.sink(repo, sha, last_stage)
            sink.add([{"kind": "run-finished", "succeeded": failed_at is None,
                       "error": None if failed_at is None else f"stage {failed_at} failed"}])
            sink.flush(attempts=6)
            self.log(f"{path}: {'succeeded' if failed_at is None else 'failed at ' + failed_at} in "
                     f"{_hms(self.clock() - started)}")
            return "ran"
        except Lost as e:
            self.log(f"{path}: {e} — abandoning the run")
            return "lost"
        finally:
            shutil.rmtree(workdir, ignore_errors=True)

    def sink(self, repo: str, sha: str, stage: str) -> EventSink:
        return EventSink(self.api, self.host, repo, sha, stage, self.channel, self.log,
                         sleep=self.sleep, clock=self.clock)

    def installation_token(self, claim: dict) -> Optional[str]:
        """A one-hour installation token scoped to the run's repository (`POST /token`, Build
        service §1.3). None when the instance hands none out — a public repository still clones."""
        status, body = self.api.call("POST", "/token", {"host": self.host, "path": claim.get("path"),
                                                        "repo": claim.get("repo"), "sha": claim.get("sha")},
                                     {"channel": self.channel})
        if status == 200 and isinstance(body, dict) and body.get("token"):
            return str(body["token"])
        self.log(f"no installation token for {claim.get('repo')} (HTTP {status}) — cloning anonymously")
        return None

    def run_stage(self, st: dict, cwd: str, sink: EventSink, extra: dict) -> bool:
        """Run ONE stage in a fresh child process; post its events; return whether it succeeded."""
        name, started = st["name"], self.clock()
        parser = OutputParser(cwd, name)
        sink.add([{"kind": "line", "text": f"[00:00:00] {st['kind']}/{name} on build-{self.channel} "
                                           f"({self.platform_version}, plugins {(self.plugins_digest or '-')[:19]})"}])
        try:
            proc = subprocess.Popen(["/bin/bash", "-c", st["command"]], cwd=cwd,
                                    env=child_env(self.env, extra), stdout=subprocess.PIPE,
                                    stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL,
                                    start_new_session=True, text=True, errors="replace", bufsize=1)
        except OSError as e:
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
                    ln = ""
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
            rc = proc.wait()
        except Lost:
            _kill(proc)
            raise
        events = parser.finish()
        ok = rc == 0 and not timed_out
        if not ok and parser.errors == 0 and parser.tests["failed"] == 0:
            # nothing structured explains the failure — the tail is the evidence
            events += [{"kind": "line", "text": "│ " + t} for t in parser.tail if t.strip()]
        note = f"timed out after {st['timeoutMinutes']:g} min" if timed_out else ""
        events.append({"kind": "stage-finished", "state": "succeeded" if ok else "failed",
                       "summary": parser.summary(rc, self.clock() - started, note)})
        sink.add(events)
        sink.flush(attempts=6)
        return ok

    def loop(self) -> int:
        idle = float(self.env.get("MW_BUILD_IDLE_SECONDS", "15") or 15)
        while not self.stopping:
            what = self.cycle()
            if what in ("idle", "no-inputs"):
                self.sleep(idle if what == "idle" else max(idle, 30))
        self.log("stopping: no further claims (the run in hand, if any, has finished)")
        return 0


def _kill(proc: subprocess.Popen) -> None:
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
    a = ap.parse_args(argv)
    if a.self_test:
        return self_test()
    env = dict(os.environ)
    host = Host(env, Api(env.get("BUILD_API", ""), env.get("BUILD_TOKEN", "")))
    try:
        host.preflight()
    except Refusal as r:
        print(f"build-run: {r}", file=sys.stderr, flush=True)
        return 1

    def stop(_sig, _frm) -> None:
        # The old replica of a roll: stop claiming, finish the run in hand, exit.
        host.stopping = True
    signal.signal(signal.SIGTERM, stop)
    signal.signal(signal.SIGINT, stop)
    if a.once:
        print(f"build-run: {host.cycle()}", flush=True)
        return 0
    return host.loop()


# ───────────────────────────────── self-test ──────────────────────────────────────────────

class _FakeApi(Api):
    def __init__(self, script: dict) -> None:
        super().__init__("https://control.example.com/api/builds", "worker")
        self.script = script          # (method, path-prefix) → list of (status, body) or callable
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

    # ── 2. test results (vstest shape, with message and source line) ────────────────────────
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

    # xunit console shape
    x = OutputParser("/w", "tests")
    xo = x.feed("    MeshWeaver.Store.Test.GateTest.Refuses [FAIL]") + x.feed("      Expected refusal, got grant") + x.finish()
    check("xunit/fail with message", xo and xo[0]["outcome"] == "failed"
          and xo[0]["message"] == "Expected refusal, got grant" and xo[0]["suite"] == "MeshWeaver.Store.Test.GateTest", xo)

    # structured events: passed through for unit/count, REFUSED for a child's own verdict
    s = OutputParser("/w", "modules")
    check("mw-event/unit", s.feed('::mw-event::{"kind":"unit-finished","unit":"Store","outcome":"cached","durationMs":3}')
          == [{"kind": "unit-finished", "unit": "Store", "outcome": "cached", "durationMs": 3}])
    check("mw-event/a child cannot declare its stage green",
          s.feed('::mw-event::{"kind":"stage-finished","state":"succeeded"}') == [])
    check("mw-event/garbage ignored", s.feed("::mw-event::{not json}") == [])

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
    try:
        select_stages(d, ["portal-hosts"])
        check("stages/undeclared claim is red", False, "accepted")
    except Refusal:
        pass

    check("url/in-cluster http", api_url_acceptable("http://memex-portal-service:8080/api/builds"))
    check("url/svc http", api_url_acceptable("http://memex-portal-service.memex-ops.svc:8080/api/builds"))
    check("url/https", api_url_acceptable("https://control.example.com/api/builds"))
    check("url/public http refused", not api_url_acceptable("http://control.example.com/api/builds"))
    check("url/empty refused", not api_url_acceptable(""))

    # ── 4. env: credentials scrubbed, publish passed through ───────────────────────────────
    ce = child_env({"PATH": "/bin", "BUILD_TOKEN": "w", "MW_REGISTRY_PASSWORD": "p", "AZURE_CLIENT_SECRET": "s",
                    "BUILD_API": "https://x"}, {"MW_BUILD_PUBLISH": "false", "GITHUB_TOKEN": "run-token"})
    check("env/scrubbed", "BUILD_TOKEN" not in ce and "MW_REGISTRY_PASSWORD" not in ce
          and "AZURE_CLIENT_SECRET" not in ce and "BUILD_API" not in ce, ce)
    check("env/run token and publish", ce.get("GITHUB_TOKEN") == "run-token" and ce.get("MW_BUILD_PUBLISH") == "false", ce)
    check("env/git credential never in argv", "x-access-token" not in json.dumps(sorted(git_auth_env("t"))), git_auth_env("t"))

    # ── 5. the plugins layer is refreshed BEFORE the claim ─────────────────────────────────
    tmp = tempfile.mkdtemp(prefix="build-run-selftest-")
    try:
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

        env = {"MW_BUILD_CHANNEL": "latest", "MW_BUILD_PLATFORM_VERSION": "3.1.10412",
               "MW_BUILD_FRAMEWORK_IDENTITY": "c3e1", "MW_BUILD_CACHE": tmp, "MW_BUILD_WORK": tmp,
               "MW_BUILD_READY_FILE": os.path.join(tmp, "ready"), "MW_PLUGINS_REGISTRY": "cr.example.com",
               "MW_BUILD_HOST": "build-latest-0"}
        api = _FakeApi({("GET", "/hosts/inputs"): on_inputs, ("POST", "/claim"): on_claim})
        h = Host(env, api, log=lambda s: None, pull=pull, sleep=lambda s: None)
        h.preflight()
        check("order/first cycle idle", h.cycle() == "idle")
        check("order/second cycle idle", h.cycle() == "idle")
        check("order/third cycle idle", h.cycle() == "idle")
        check("order/refresh before claim, pull only on a new digest", order == [
            "inputs", f"pull:{d1}", f"claim:{d1}",
            "inputs", f"claim:{d1}",
            "inputs", f"pull:{d2}", f"claim:{d2}"], order)
        check("order/content-addressed", os.path.isfile(os.path.join(tmp, "plugins", "2" * 64, ".complete")))
        check("order/readiness written once staged", os.path.isfile(os.path.join(tmp, "ready")))

        # an unreadable inputs, or a failed pull, means NO claim
        order.clear()
        api2 = _FakeApi({("GET", "/hosts/inputs"): [(503, None)], ("POST", "/claim"): on_claim})
        h2 = Host(env, api2, log=lambda s: None, pull=pull, sleep=lambda s: None)
        check("order/unreadable inputs → no claim", h2.cycle() == "no-inputs" and not any(
            c[1] == "/claim" for c in api2.calls), api2.calls)
        d3 = "sha256:" + "3" * 64
        api3 = _FakeApi({("GET", "/hosts/inputs"): [(200, {"plugins": {"digest": d3}})], ("POST", "/claim"): on_claim})
        h3 = Host(env, api3, log=lambda s: None, pull=lambda cmd: 1, sleep=lambda s: None)
        check("order/failed pull → no claim", h3.cycle() == "no-inputs" and not any(
            c[1] == "/claim" for c in api3.calls), api3.calls)
        api4 = _FakeApi({("GET", "/hosts/inputs"): [(200, {"plugins": None})], ("POST", "/claim"): on_claim})
        h4 = Host(env, api4, log=lambda s: None, pull=pull, sleep=lambda s: None)
        check("order/no publication registered → claim with null", h4.cycle() == "idle"
              and api4.calls[-1][2]["pluginsDigest"] is None, api4.calls)

        # ── 6. a whole run: fresh processes, fail-fast, channel on every POST, publish env ──
        posted: list[tuple] = []

        def on_events(path, body):
            posted.append((path, body["events"]))
            return 202, None

        def fake_checkout(repo, sha, refs, dest, token, env_, log):
            os.makedirs(os.path.join(dest, ".github"))
            with open(os.path.join(dest, ".github", "build-stages.json"), "w") as fh:
                json.dump({"stages": [
                    {"name": "compile-check", "kind": "build",
                     "command": "echo pid=$$ publish=$MW_BUILD_PUBLISH token=${BUILD_TOKEN:-none} > ../pid1; "
                                "echo \"$PWD/Hosting/A.cs(3,1): error CS0246: missing X\"; exit 1"},
                    {"name": "gate", "kind": "test", "command": "echo should-not-run > ../ran2"}]}, fh)

        claim = {"path": "Hosting/Builds/MeshWeaver.Plugins/" + "c" * 40 + "-stable",
                 "repo": "MeshWeaver.Plugins", "sha": "c" * 40, "refs": [], "isTrunk": True,
                 "publish": False, "stages": []}
        env_s = dict(env, MW_BUILD_CHANNEL="stable", BUILD_TOKEN="worker-secret")
        api5 = _FakeApi({("POST", "/token"): [(200, {"token": "run-token"})],
                         ("POST", "/MeshWeaver.Plugins/"): on_events})
        h5 = Host(env_s, api5, log=lambda s: None, pull=pull, checkout=fake_checkout, sleep=lambda s: None)
        check("run/ran", h5.run(claim) == "ran")
        evpaths = [c for c in api5.calls if c[1].endswith("/events")]
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
        check("run/fail-fast: the second process never started", not os.path.exists(os.path.join(tmp, "ran2")))
        rf = [e for _, e in allev if e["kind"] == "run-finished"]
        check("run/run-finished false, once", len(rf) == 1 and rf[0]["succeeded"] is False, rf)
        # the child's own record: publish=false from the claim, and the worker token NOT visible
        pid_files = [os.path.join(r, f) for r, _, fs in os.walk(tmp) for f in fs if f == "pid1"]
        check("run/work dir removed", not pid_files, pid_files)

        # same run, but the child writes OUTSIDE the work dir so we can read what it saw
        seen = os.path.join(tmp, "seen")

        def fake_checkout2(repo, sha, refs, dest, token, env_, log):
            os.makedirs(os.path.join(dest, ".github"))
            with open(os.path.join(dest, ".github", "build-stages.json"), "w") as fh:
                json.dump({"stages": [
                    {"name": "a", "kind": "build", "command": f"echo $$ ${{MW_BUILD_PUBLISH}} ${{BUILD_TOKEN:-none}} >> {seen}"},
                    {"name": "b", "kind": "test", "command": f"echo $$ ${{MW_BUILD_PUBLISH}} ${{BUILD_TOKEN:-none}} >> {seen}"}]}, fh)

        posted.clear()
        api6 = _FakeApi({("POST", "/token"): [(200, {"token": "run-token"})],
                         ("POST", "/MeshWeaver.Plugins/"): on_events})
        h6 = Host(env_s, api6, log=lambda s: None, pull=pull, checkout=fake_checkout2, sleep=lambda s: None)
        h6.run(dict(claim, publish=True))
        rows = [r.split() for r in open(seen).read().splitlines()]
        check("run/a fresh process per stage", len(rows) == 2 and rows[0][0] != rows[1][0], rows)
        check("run/publish from the claim", all(r[1] == "true" for r in rows), rows)
        check("run/worker token scrubbed", all(r[2] == "none" for r in rows), rows)
        rf = [e for _, evs in posted for e in evs if e["kind"] == "run-finished"]
        check("run/green run-finished", rf == [{"kind": "run-finished", "succeeded": True, "error": None}], rf)

        # 409: the run is someone else's — stop posting
        api7 = _FakeApi({("POST", "/token"): [(404, None)], ("POST", "/MeshWeaver.Plugins/"): [(409, None)]})
        h7 = Host(env_s, api7, log=lambda s: None, pull=pull, checkout=fake_checkout2, sleep=lambda s: None)
        check("run/409 abandons the run", h7.run(claim) == "lost")
        check("run/409 posts nothing after it", sum(1 for c in api7.calls if c[1].endswith("/events")) == 1, api7.calls)

        # no stages declared → a named red, never a green with nothing run
        posted.clear()

        def empty_checkout(repo, sha, refs, dest, token, env_, log):
            os.makedirs(dest)

        api8 = _FakeApi({("POST", "/token"): [(404, None)], ("POST", "/MeshWeaver.Plugins/"): on_events})
        Host(env_s, api8, log=lambda s: None, pull=pull, checkout=empty_checkout, sleep=lambda s: None).run(
            dict(claim, stages=["build/modules"]))
        rf = [e for _, evs in posted for e in evs if e["kind"] == "run-finished"]
        check("run/undeclared stages are red", rf and rf[0]["succeeded"] is False and "declares no build stages" in rf[0]["error"], rf)
        check("run/refusal posted to the claimed stage", posted and "/stages/modules/events" in posted[0][0], posted)

        # a stage that outlives its timeout is killed and red
        def slow_checkout(repo, sha, refs, dest, token, env_, log):
            os.makedirs(os.path.join(dest, ".github"))
            with open(os.path.join(dest, ".github", "build-stages.json"), "w") as fh:
                json.dump({"stages": [{"name": "slow", "kind": "test", "command": "sleep 30", "timeoutMinutes": 0.02}]}, fh)

        posted.clear()
        api9 = _FakeApi({("POST", "/token"): [(404, None)], ("POST", "/MeshWeaver.Plugins/"): on_events})
        t0 = time.monotonic()
        Host(env_s, api9, log=lambda s: None, pull=pull, checkout=slow_checkout, sleep=lambda s: None).run(claim)
        fin = [e for _, evs in posted for e in evs if e["kind"] == "stage-finished"]
        check("run/timeout kills and reds", fin and fin[0]["state"] == "failed" and "timed out" in fin[0]["summary"]
              and time.monotonic() - t0 < 20, (fin, time.monotonic() - t0))
    finally:
        shutil.rmtree(tmp, ignore_errors=True)

    if failures:
        for f in failures:
            print(f"SELF-TEST FAILED: {f}")
        return 1
    print("build-run self-test: all cases passed (compiler + workflow-command diagnostics, dedup, vstest + "
          "xunit results with message and source line, structured events, stage declaration, credential "
          "scrubbing, plugins refresh BEFORE claim, fresh process per stage, fail-fast, channel + publish, "
          "409, undeclared stages, stage timeout)")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
