#!/usr/bin/env python3
"""core-tests.py — pack core's built test assemblies into the `mw-core-tests` image, and run one shard of them.

The FIFTH digest of the platform image set (Doc/Architecture/DeployTestSwitchLatest, decision D3):
core's test projects, built in main-cd from the set's commit with the set's SDK, carried as an image
so a build host runs core's suites ON the deployed build server without building anything from
source. This one file is both halves, and it ships INSIDE the image beside the bytes it packed, so
the runner that reads a manifest is always the one that wrote it.

    core-tests.py pack --repo <core checkout> --out <dir> --sha <40 hex> --version <set version>
        Reads CI_SHARD_TOTAL from .github/workflows/dotnet-test.yml, asks .github/scripts/shard-assign.sh
        — the SAME script CI's build and test jobs use — for every shard's units, and stores each
        assigned project's bin/Release/net10.0 CONTENT-ADDRESSED under <out>/blobs/<sha256>, with
        <out>/manifest.json naming every shard's units and every project's files. Self-contained
        test bins carry near-identical copies of the framework and MeshWeaver assemblies; storing
        each distinct file once is what keeps the image a fraction of the bins' sum (CI's per-shard
        tarballs get the same effect from zstd's long window). It also packs the plugin tester
        (tools/MeshWeaver.PluginTester), which the doc-gate stage runs, exactly as CI's shard-0
        artifact carries it.
        REFUSES: a shard with no units (unless a narrowed TEST_SELECTION was asked for), an assigned
        project with no built bin, a symlink in a bin, a tester that was not built.

    core-tests.py run --shard <i> --total <n> [--root <dir> ...] [--require-sha <40 hex>] [--work <dir>]
        One shard, as one build-host stage (`.github/build-stages.json`, stage `test-shard-<i>`).
        Materialises the shard's projects from the blobs into a WRITABLE work directory (the image
        tree is bound read-only and a test host writes its TestResults beside its assembly),
        verifying every blob's hash on the way, then runs each unit's native xUnit v3 host exactly
        as dotnet-test.yml's shard loop does: CWD = the bin, `-trx`, `-showLiveOutput`,
        `-longRunning 60`, an 8-minute per-unit wall clock, and a split project's every-Nth class
        of the SORTED `-list classes` output. RED on: a --total the image was not packed for (the
        stage declaration and the image disagree), a shard index outside it, a shard with nothing
        to run, a tampered blob, a unit with no classes, and any unit whose host exits non-zero
        or writes no trx (a trx left by an earlier run is deleted first). Each unit leads its own
        process group, and whatever remains in it when the host ends is terminated (TERM, then KILL).

    core-tests.py doc-gate [--root <dir> ...] [--require-sha <40 hex>] [--work <dir>] [--repo .]
        The doc and samples gates (dotnet-test.yml's doc-gate job), against the checkout at the
        set's commit, with the plugin tester from this image: stage-doc-gate.sh /
        stage-samples-gate.sh, then bake-then-gate.sh with each tree's allow file, warning
        baseline and dynamic-edge baseline, then check-tests-area-verdicts.py over each tree and its gate log (`--require-tests`
        for Doc), as dotnet-test.yml's doc-gate job does. RED when either gate or verifier is.

    WHICH PACK (`--root`, repeatable, with `--require-sha`). The same stages serve two runs: a
        candidate DEPLOYMENT (the set's image at /core-tests, built by main-cd from the set's commit)
        and a pull request or trunk run on a build host, whose `build` stage packed THIS run's tree
        into $MW_BUILD_OUTPUTS/core-tests. With `--require-sha` the runner takes the FIRST root whose
        manifest's `coreSha` is that commit, and is RED when none is — so the tests that run are always
        the run's own commit's, never the image of whatever set the host happens to have installed.
        Without it exactly one `--root` (default: beside this file) is read, as before.

    core-tests.py --self-test
        Proves each refusal fires and the green path is green, against a fake repository and a fake
        `dotnet` — no SDK, no network.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import signal
import stat
import subprocess
import sys
import tempfile
import time
from typing import Optional

SCHEMA = 1
WORKFLOW = ".github/workflows/dotnet-test.yml"
SHARD_ASSIGN = ".github/scripts/shard-assign.sh"
TESTER_PROJECT = "tools/MeshWeaver.PluginTester/MeshWeaver.PluginTester.csproj"
TFM_DIR = os.path.join("bin", "Release", "net10.0")
# dotnet-test.yml's per-unit cap (`timeout … 8m`): a unit past it is a wedge, never a slow pass.
UNIT_TIMEOUT_SECONDS = 480
# How long a timed-out unit's process group gets between SIGTERM and SIGKILL.
KILL_GRACE_SECONDS = 10
SHARD_TOTAL_RE = re.compile(r"^\s*CI_SHARD_TOTAL:\s*(\d+)\s*(?:#.*)?$", re.M)
CLASS_RE = re.compile(r"^[A-Za-z_][A-Za-z0-9_.]*$")
SHA_RE = re.compile(r"^[0-9a-f]{40}$")


class Refusal(Exception):
    """A pack or a run that cannot be trusted, with the reason it is red."""


def read_shard_total(workflow_text: str) -> int:
    """CI_SHARD_TOTAL from dotnet-test.yml — the ONE place the shard count is declared."""
    found = SHARD_TOTAL_RE.findall(workflow_text)
    if len(found) != 1:
        raise Refusal(f"{WORKFLOW} must declare CI_SHARD_TOTAL exactly once (found {len(found)})")
    n = int(found[0])
    if n < 1:
        raise Refusal(f"CI_SHARD_TOTAL is {n}; a suite needs at least one shard")
    return n


def split_unit(entry: str) -> tuple[str, int, int]:
    """`<csproj>` or `<csproj>#<part>/<parts>` — shard-assign.sh's unit shape."""
    if "#" not in entry:
        return entry, 1, 1
    csproj, spec = entry.split("#", 1)
    part, parts = spec.split("/", 1)
    p, n = int(part), int(parts)
    if not (1 <= p <= n):
        raise Refusal(f"unit {entry!r}: part {p} of {n} is not a part")
    return csproj, p, n


def _sha256(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


# ───────────────────────────────── pack ─────────────────────────────────────────────

def shard_units(repo: str, index: int, total: int) -> list[str]:
    env = dict(os.environ, SHARD_INDEX=str(index), SHARD_TOTAL=str(total))
    p = subprocess.run(["bash", SHARD_ASSIGN], cwd=repo, env=env, capture_output=True, text=True)
    if p.returncode != 0:
        raise Refusal(f"shard-assign.sh failed for shard {index}/{total} (exit {p.returncode}): {p.stderr.strip()[-400:]}")
    return [line.strip() for line in p.stdout.splitlines() if line.strip()]


def _store_tree(src: str, rel_root: str, blobs: str) -> dict[str, list]:
    """Every file under src → blobs/<sha256>; returns {relpath: [sha256, mode]}."""
    files: dict[str, list] = {}
    for dirpath, dirnames, filenames in os.walk(src):
        dirnames.sort()
        for name in sorted(filenames) + [d for d in dirnames if os.path.islink(os.path.join(dirpath, d))]:
            full = os.path.join(dirpath, name)
            rel = os.path.relpath(full, src)
            if os.path.islink(full):
                raise Refusal(f"{rel_root}/{rel} is a symlink — a test bin is plain files, and a link would "
                              f"resolve to something this image does not carry")
            digest = _sha256(full)
            dest = os.path.join(blobs, digest)
            if not os.path.exists(dest):
                shutil.copyfile(full, dest)
            mode = stat.S_IMODE(os.stat(full).st_mode) & 0o755 | 0o444
            files[rel.replace(os.sep, "/")] = [digest, mode]
    return files


def pack(repo: str, out: str, sha: str, version: str) -> dict:
    if not SHA_RE.match(sha or ""):
        raise Refusal(f"--sha {sha!r} is not a 40-hex commit — a test image must name the commit it was built from")
    if not version:
        raise Refusal("--version is empty — the image carries the set's version, the tag every image of the set shares")
    with open(os.path.join(repo, WORKFLOW)) as fh:
        total = read_shard_total(fh.read())
    selection = os.environ.get("TEST_SELECTION", "all") or "all"
    shards: dict[str, list[str]] = {}
    for i in range(total):
        units = shard_units(repo, i, total)
        if not units and selection == "all":
            raise Refusal(f"shard {i}/{total} was assigned no unit under TEST_SELECTION=all — an image whose "
                          f"shard runs nothing would report a green stage over zero tests")
        shards[str(i)] = units
    if os.path.exists(out):
        shutil.rmtree(out)
    blobs = os.path.join(out, "blobs")
    os.makedirs(blobs)
    projects: dict[str, dict] = {}
    for csproj in sorted({split_unit(u)[0] for units in shards.values() for u in units} | {TESTER_PROJECT}):
        bin_dir = os.path.join(os.path.dirname(csproj), TFM_DIR)
        src = os.path.join(repo, bin_dir)
        if not os.path.isdir(src):
            what = "the plugin tester the doc-gate stage runs" if csproj == TESTER_PROJECT else "an assigned test project"
            raise Refusal(f"{bin_dir} does not exist — {what} was never built (is it in MeshWeaver.slnx, and did "
                          f"the build run with -c Release?). CI calls this MISSING_BIN.")
        projects[csproj] = {"binDir": bin_dir.replace(os.sep, "/"), "files": _store_tree(src, bin_dir, blobs)}
    manifest = {"schema": SCHEMA, "coreSha": sha, "version": version, "shardTotal": total,
                "selection": selection, "tester": TESTER_PROJECT, "shards": shards, "projects": projects}
    with open(os.path.join(out, "manifest.json"), "w") as fh:
        json.dump(manifest, fh, indent=1, sort_keys=True)
    shutil.copyfile(os.path.abspath(__file__), os.path.join(out, "core-tests.py"))
    stored = len(os.listdir(blobs))
    referenced = sum(len(p["files"]) for p in projects.values())
    print(f"packed {sum(len(u) for u in shards.values())} units over {total} shards, {len(projects)} bins: "
          f"{referenced} files stored as {stored} blobs")
    return manifest


# ───────────────────────────────── run ──────────────────────────────────────────────

def load_manifest(root: str) -> dict:
    path = os.path.join(root, "manifest.json")
    try:
        with open(path) as fh:
            m = json.load(fh)
    except (OSError, ValueError) as e:
        raise Refusal(f"{path} is unreadable ({e}) — is the mw-core-tests image staged at {root}?")
    if m.get("schema") != SCHEMA:
        raise Refusal(f"{path} is schema {m.get('schema')!r}; this runner reads schema {SCHEMA}")
    return m


def select_root(roots: list[str], require_sha: Optional[str]) -> str:
    """The pack this run reads. Without `require_sha` exactly one root, as before. With it, the FIRST
    root whose manifest names that commit — RED when none does, naming what each root holds."""
    if not roots:
        raise Refusal("no --root was given — there is no test pack to read")
    if require_sha is None:
        if len(roots) != 1:
            raise Refusal(f"{len(roots)} roots were given without --require-sha — nothing says which pack is this run's")
        return roots[0]
    if not SHA_RE.match(require_sha):
        raise Refusal(f"--require-sha {require_sha!r} is not a 40-hex commit")
    seen: list[str] = []
    for root in roots:
        try:
            m = load_manifest(root)
        except Refusal as e:
            seen.append(f"{root}: {str(e)[:120]}")
            continue
        if m.get("coreSha") == require_sha:
            return root
        seen.append(f"{root}: packed from {str(m.get('coreSha'))[:12]}")
    raise Refusal(f"no test pack was built from {require_sha[:12]} — " + "; ".join(seen)
                  + " (a host run's `build` stage packs its own tree; a deployment reads the set's image)")


def materialise(root: str, manifest: dict, csproj: str, work: str) -> str:
    """The project's bin, copied from the blobs into the writable work tree, every hash checked."""
    proj = manifest["projects"].get(csproj)
    if proj is None:
        raise Refusal(f"{csproj} is assigned but the image carries no bin for it")
    dest_root = os.path.join(work, proj["binDir"])
    for rel, (digest, mode) in sorted(proj["files"].items()):
        dest = os.path.join(dest_root, rel)
        os.makedirs(os.path.dirname(dest), exist_ok=True)
        shutil.copyfile(os.path.join(root, "blobs", digest), dest)
        if _sha256(dest) != digest:
            raise Refusal(f"{proj['binDir']}/{rel}: blob {digest[:12]} does not hash to its name — the image is damaged")
        os.chmod(dest, mode | 0o200)
    return dest_root


def _end_group(pgid: int) -> None:
    """TERM, then (after a bounded grace) KILL, every process left in the unit's process group."""
    for sig, grace in ((signal.SIGTERM, KILL_GRACE_SECONDS), (signal.SIGKILL, 0)):
        try:
            os.killpg(pgid, sig)
        except (ProcessLookupError, PermissionError):
            return
        deadline = time.monotonic() + grace
        while time.monotonic() < deadline:
            try:
                os.killpg(pgid, 0)
            except (ProcessLookupError, PermissionError):
                return
            time.sleep(0.1)


def _run_unit(argv: list[str], cwd: str, timeout: int, env: Optional[dict] = None,
              capture: bool = False) -> tuple[int, str]:
    """One test-host invocation in its OWN session, so the wall clock ends the whole tree.

    `subprocess.run(timeout=…)` kills only the immediate child: a host that launched processes of
    its own (a server under test, a nested `dotnet`) leaves them running into the next unit, holding
    ports and writing into the work tree. The host leads a new process group here, and whatever is
    still in that group when the host ends — on its own or at the wall clock — is terminated before
    the next unit starts. Returns (exit code, stdout when captured); 124 is the wall clock.
    """
    proc = subprocess.Popen(argv, cwd=cwd, env=env, start_new_session=True,
                            stdout=subprocess.PIPE if capture else None, text=True)
    try:
        out, _ = proc.communicate(timeout=timeout)
        rc = proc.returncode
    except subprocess.TimeoutExpired:
        out, rc = "", 124
    finally:
        _end_group(proc.pid)
    if rc == 124 and proc.returncode is None:
        proc.wait()
    return rc, out or ""


def _list_classes(dotnet: str, name: str, cwd: str) -> list[str]:
    _, out = _run_unit([dotnet, f"{name}.dll", "-list", "classes"], cwd, UNIT_TIMEOUT_SECONDS, capture=True)
    return sorted(line.strip() for line in out.splitlines() if CLASS_RE.match(line.strip()))


# What a crash looks like in the marker CI's shard loop writes (dotnet-test.yml, `[CI] … exit=N`), by
# the exit code a shell reports for a signal death (128 + signal).
SIGNAL_NAMES = {132: "SIGILL", 134: "SIGABRT", 135: "SIGBUS", 139: "SIGSEGV"}
# The temp-dir traces CI's "Collect logs" step copies into collected-logs/ — written by the test
# hosts to Path.GetTempPath(), which on Unix is $TMPDIR when set (a host stage's scratch).
TEMP_TRACES = ("meshweaver-test-trace.log", "meshweaver-memory-delta.log", "meshweaver-msg-trace.log")


def classify(label: str, name: str, rc: int, trx_path: str, unit_timeout: int) -> tuple[str, str, int]:
    """(marker, crash, rc) for one unit — the case statement of dotnet-test.yml's shard loop.

    `crash` is non-empty for every outcome in which the host did NOT complete; it is written INTO
    the trx (record-host-crash.py) so no trx reader can report a pass over a dead host (#2495).
    """
    if rc < 0:
        rc = 128 - rc  # Popen reports a signal death as -signum; a shell reports 128 + signum
    if rc == 0:
        if os.path.isfile(trx_path):
            return f"[CI] {name} exit=0", "", 0
        return (f"[CI] {label} exit=98 MASKED (exit=0 but no trx written — the host recorded no result)",
                "MASKED: exit=0 with no trx — the host recorded no result.", 98)
    if rc in (124, 137):
        return (f"[CI] {label} exit={rc} TIMEOUT ({unit_timeout}s wall-clock cap hit — likely fixture/init hang)",
                f"TIMEOUT: killed at the {unit_timeout}s wall-clock cap — a fixture/init hang, not a test failure. "
                "The last TEST_START without a matching TEST_END in collected-logs/_meshweaver-test-trace.log "
                "names the test it was stuck in.", rc)
    if rc in SIGNAL_NAMES:
        sig = SIGNAL_NAMES[rc]
        return (f"[CI] {label} exit={rc} SIGNAL {sig} (host died on a signal, not an assertion)",
                f"SIGNAL {sig}: the host died on a signal, not an assertion.", rc)
    fails = 0
    if os.path.isfile(trx_path):
        with open(trx_path, errors="replace") as fh:
            fails = len(re.findall(r'<UnitTestResult[^>]*outcome="Failed"', fh.read()))
    if fails > 0 and rc < 128:
        return (f"[CI] {name} exit={rc} TESTFAIL ({fails} failing test(s) recorded in trx; xUnit v3 exits 1 for "
                "any failure; the host completed normally)", "", rc)
    if not os.path.isfile(trx_path):
        return (f"[CI] {label} exit={rc} MASKED (no trx written — the host died before recording any result)",
                f"MASKED: exit={rc} with no trx — the host died before recording any result.", rc)
    return (f"[CI] {label} exit={rc} MASKED (trx records {fails} failing test(s), which does not explain exit={rc} "
            "— host crashed after streaming results)",
            f"MASKED: exit={rc}, but the trx records only {fails} failing test(s) — that does not explain the exit "
            "code, so the host crashed AFTER streaming results.", rc)


def _script(repo: str, name: str) -> str:
    path = os.path.join(repo, ".github", "scripts", name)
    if not os.path.isfile(path):
        raise Refusal(f"{path} is absent — the shard cannot record/check its results as CI does (is --repo the "
                      f"checkout of the run's commit?)")
    return path


def collect_logs(work: str, bins: list[str], dest: str, tmp: str) -> None:
    """dotnet-test.yml's "Collect logs" step: every bin's test-logs/*.log, flat, plus the temp-dir traces —
    read from `tmp`, the temp directory THIS invocation gave its test hosts (never the process-wide one,
    whose append-only traces another shard on the same host may have left behind)."""
    os.makedirs(dest, exist_ok=True)
    for d in bins:
        logs = os.path.join(d, "test-logs")
        if os.path.isdir(logs):
            for n in sorted(os.listdir(logs)):
                full = os.path.join(logs, n)
                if n.endswith(".log") and os.path.isfile(full) and not os.path.islink(full):
                    shutil.copyfile(full, os.path.join(dest, n))
    for n in TEMP_TRACES:
        full = os.path.join(tmp, n)
        if os.path.isfile(full) and not os.path.islink(full):
            shutil.copyfile(full, os.path.join(dest, f"_{n}"))


def hand_back(work: str, shard: int, artifacts: str, trx_files: list[str]) -> int:
    """Leave the shard's results under $MW_BUILD_ARTIFACTS/testResults-shard<i>/ in the SAME tree the
    `test` job uploads as its testResults-shard<i>-attempt<a> artifact (test/**/*.trx,
    test/test-results.log, collected-logs/), so collect-results consolidates a host shard exactly as an
    Actions one. The host uploads every file to the CI artifact store, reads it back and posts a receipt
    (Doc/Architecture/BuildHosts §2 step 7). Returns how many files were handed back.

    Only `trx_files` — the trx THIS invocation's units wrote or had synthesized — are handed back: a
    reused work directory (or a bin packed with a TestResults of its own) can hold another
    invocation's trx, which consolidation would otherwise count as this shard's."""
    unit = os.path.join(artifacts, f"testResults-shard{shard}")
    if os.path.exists(unit):
        shutil.rmtree(unit)
    produced = {os.path.abspath(f) for f in trx_files}
    count = 0
    for top in ("test", "collected-logs"):
        base = os.path.join(work, top)
        for dirpath, dirnames, filenames in os.walk(base):
            dirnames.sort()
            for n in sorted(filenames):
                full = os.path.join(dirpath, n)
                rel = os.path.relpath(full, work)
                keep = (top == "collected-logs" or full in produced or rel == os.path.join("test", "test-results.log"))
                if not keep or os.path.islink(full) or not os.path.isfile(full):
                    continue
                out = os.path.join(unit, rel)
                os.makedirs(os.path.dirname(out), exist_ok=True)
                shutil.copyfile(full, out)
                count += 1
    return count


def run_shard(root: str, shard: int, total: int, work: str, dotnet: str = "dotnet",
              unit_timeout: int = UNIT_TIMEOUT_SECONDS, repo: str = ".", artifacts: Optional[str] = None) -> int:
    """One shard, as dotnet-test.yml's `test` job runs it: the shard loop (markers in
    test/test-results.log, a crash written INTO the trx), the log collection, the hand-back of the
    results tree, and then the untyped-content gate (check-untyped-content.sh) over collected-logs —
    AFTER the hand-back, as CI gates after its upload, so the logs a red is decided on are evidence.

    `repo` is the checkout of the run's commit (the scripts the shard records and gates with);
    `artifacts` is $MW_BUILD_ARTIFACTS on a host — unset elsewhere, and then nothing is handed back."""
    m = load_manifest(root)
    if total != m["shardTotal"]:
        raise Refusal(f"the stage runs shard {shard} of {total}, but this image was packed for {m['shardTotal']} "
                      f"shards (CI_SHARD_TOTAL at {m['coreSha'][:12]}) — .github/build-stages.json and the image disagree")
    if not (0 <= shard < total):
        raise Refusal(f"shard {shard} is outside 0..{total - 1}")
    units = m["shards"].get(str(shard)) or []
    if not units:
        raise Refusal(f"shard {shard}/{total} carries no unit — a stage that runs nothing is not a green one")
    repo = os.path.abspath(repo)
    # Absolute: the untyped-content gate runs with cwd=repo, where a relative work path would name
    # another directory — absent (a false red) or holding stale logs (a false green).
    work = os.path.abspath(work)
    recorder = _script(repo, "record-host-crash.py")
    untyped_gate = _script(repo, "check-untyped-content.sh")
    os.makedirs(work, exist_ok=True)
    results_log = os.path.join(work, "test", "test-results.log")
    os.makedirs(os.path.dirname(results_log), exist_ok=True)
    with open(results_log, "w"):
        pass  # this invocation's markers only — a reused work directory starts empty, as CI's `: >` does
    collected = os.path.join(work, "collected-logs")
    if os.path.exists(collected):
        shutil.rmtree(collected)
    # The test hosts' temp-dir traces are APPEND-only (TestTraceLog.Append) and land in
    # Path.GetTempPath() — $TMPDIR on Unix. A temp directory shared with an earlier shard on this host
    # would hand THAT run's trace (an unresolved-content line, a dangling TEST_START) to this run's
    # gate: a red this run did not earn. Each invocation gets its own, empty, and collects only it.
    host_tmp = shard_temp_dir(repo, shard)
    try:
        return _run_units(m, root, shard, total, units, work, repo, dotnet, unit_timeout, artifacts,
                          recorder, untyped_gate, results_log, collected, host_tmp)
    finally:
        shutil.rmtree(host_tmp, ignore_errors=True)


def shard_temp_dir(repo: str, shard: int) -> str:
    """A fresh, empty temp directory for one shard's test hosts — OUTSIDE the checkout.

    It is created under the temp directory this runner inherited (a host stage's `$TMPDIR`, the
    sandbox's scratch; the runner's `/tmp` on Actions), never under `--work`, which defaults INTO
    the checkout. A test host's temp must not sit inside a source tree: tests build throw-away
    projects under `Path.GetTempPath()` and the csproj evaluator (like MSBuild) walks UP from them
    to the nearest `Directory.Build.props` / `Directory.Packages.props` — inside a checkout that is
    the REPOSITORY's, and the tests then evaluate core's build instead of their own project.
    Measured on build-latest: MeshWeaver.PluginTester.Test, 82 of 474 red on
    `<checkout>/src/Directory.Build.props: <Target …> is not executed by this builder`, all green on
    Actions, whose temp is outside the checkout. REFUSES when the inherited temp itself lies inside
    the checkout — the same defect one level up, named rather than run."""
    base = os.path.realpath(tempfile.gettempdir())
    checkout = os.path.realpath(repo)
    if os.path.commonpath([base, checkout]) == checkout:
        raise Refusal(f"the temp directory {base} lies inside the checkout {checkout} — a test host's "
                      f"Path.GetTempPath() must be outside any source tree (its throw-away projects would "
                      f"import the checkout's Directory.Build.props); give the stage a $TMPDIR outside it")
    return tempfile.mkdtemp(prefix=f"mw-core-tests-shard{shard}-", dir=base)


def _run_units(m: dict, root: str, shard: int, total: int, units: list, work: str, repo: str, dotnet: str,
               unit_timeout: int, artifacts: Optional[str], recorder: str, untyped_gate: str,
               results_log: str, collected: str, host_tmp: str) -> int:

    def record(line: str) -> None:
        print(line, flush=True)
        with open(results_log, "a") as fh:
            fh.write(line + "\n")

    def record_crash(trx_path: str, name: str, rc: int, crash: str, label: str) -> None:
        os.makedirs(os.path.dirname(trx_path), exist_ok=True)
        if subprocess.run([sys.executable, recorder, trx_path, name, str(rc), crash]).returncode != 0:
            print(f"::error::{label}: could not record the host crash into {trx_path}", flush=True)
            record(f"[CI] {name} exit=95 CRASH_RECORD_FAILED (the crash could not be written into the trx — "
                   f"read the marker above, not the trx)")

    print(f"mw-core-tests {m['version']} ({m['coreSha'][:12]}): shard {shard}/{total}, {len(units)} unit(s)", flush=True)
    env = dict(os.environ, DOTNET_ENVIRONMENT="Development", Logging__LogLevel__Default="Warning",
               TESTCONTAINERS_RYUK_DISABLED="true", TMPDIR=host_tmp, TMP=host_tmp, TEMP=host_tmp)
    bins: dict[str, str] = {}
    failed: list[str] = []
    trx_files: list[str] = []
    for entry in units:
        csproj, part, parts = split_unit(entry)
        name = os.path.basename(csproj)[: -len(".csproj")]
        label = name if parts == 1 else f"{name} (part {part}/{parts})"
        if csproj not in bins:
            bins[csproj] = materialise(root, m, csproj, work)
            # This invocation's logs only: a reused work directory keeps an earlier run's per-bin
            # test-logs/ (materialise overwrites packed files, never removes), which the gate would
            # read again. Cleared once per bin, so a later split part's logs in this run survive.
            shutil.rmtree(os.path.join(bins[csproj], "test-logs"), ignore_errors=True)
        d = bins[csproj]
        if not os.path.isfile(os.path.join(d, f"{name}.dll")) or not os.path.isfile(os.path.join(d, "xunit.v3.core.dll")):
            record(f"[CI] {name} exit=0 (helper project — no xUnit v3 test host)")
            continue
        args = []
        trx = name
        if parts > 1:
            classes = _list_classes(dotnet, name, d)
            if not classes:
                record(f"[CI] {name} exit=96 NO_CLASSES (class enumeration failed for a split project)")
                no_classes_trx = os.path.join(d, "TestResults", f"{name}.part{part}.trx")
                if os.path.lexists(no_classes_trx):
                    os.remove(no_classes_trx)
                record_crash(no_classes_trx, label, 96,
                             f"NO_CLASSES: class enumeration failed for a split project — part {part}/{parts} ran nothing.",
                             label)
                trx_files.append(no_classes_trx)
                failed.append(label)
                continue
            for c in classes[part - 1::parts]:
                args += ["-class", c]
            trx = f"{name}.part{part}"
        os.makedirs(os.path.join(d, "TestResults"), exist_ok=True)
        trx_path = os.path.join(d, "TestResults", f"{trx}.trx")
        # The trx must be THIS invocation's: a reused work directory (or a bin packed with a
        # TestResults of its own) would otherwise satisfy the no-result check below with an old file.
        if os.path.lexists(trx_path):
            os.remove(trx_path)
        print(f"::group::{label}", flush=True)
        started = time.monotonic()
        rc, _ = _run_unit([dotnet, f"{name}.dll", "-trx", f"TestResults/{trx}.trx", "-showLiveOutput",
                           "-longRunning", "60", *args], d, unit_timeout, env=env)
        print("::endgroup::", flush=True)
        marker, crash, rc = classify(label, name, rc, trx_path, unit_timeout)
        if crash:
            record_crash(trx_path, name, rc, crash, label)
        record(f"{marker} elapsed={int(time.monotonic() - started)}s")
        trx_files.append(trx_path)
        if rc != 0:
            failed.append(label)
    collect_logs(work, sorted(set(bins.values())), collected, host_tmp)
    if artifacts:
        n = hand_back(work, shard, artifacts, trx_files)
        print(f"handed back {n} result file(s) as testResults-shard{shard}/ (trx, test-results.log, collected-logs)", flush=True)
    gate = subprocess.run(["bash", untyped_gate, collected], cwd=repo).returncode
    print(f"[CI] untyped-content gate exit={gate}", flush=True)
    if failed or gate != 0:
        why = [f"{len(failed)} unit(s) did not pass: {', '.join(failed)}"] if failed else []
        if gate != 0:
            why.append("check-untyped-content.sh found content degraded to an untyped JsonElement (or could not scan)")
        print(f"shard {shard}/{total}: RED — {'; '.join(why)}", flush=True)
        return 1
    print(f"shard {shard}/{total}: green — every unit passed, no content degraded", flush=True)
    return 0


# bake-then-gate.sh's positional signature: tester, stage, bake dir, allow file, source sha, log
# prefix, warning baseline, dynamic baseline. Its own `${N:?…}` lines are the source of truth; the
# self-test parses them (when the script is beside this file in a checkout) and holds this number to
# them, and the self-test's stub refuses any other count — so a caller left one argument short is
# RED here instead of aborting the real gate before either tree is baked.
BAKE_THEN_GATE_ARITY = 8


def bake_then_gate_args(tester: str, stage: str, work: str, tree: str, sha: str, gate_log: str) -> list[str]:
    """The exact argument list doc_gate hands bake-then-gate.sh for one tree."""
    return [tester, stage, os.path.join(work, "bake", tree), f".github/{tree}-gate.allow", sha,
            gate_log, f".github/{tree}-gate-warnings.allow", f".github/{tree}-gate-dynamic.allow"]


def script_arity(text: str) -> int:
    """The highest positional parameter a script REQUIRES (`${N:?…}`)."""
    required = [int(n) for n in re.findall(r"\$\{(\d+):\?", text)]
    if not required:
        raise Refusal("bake-then-gate.sh declares no required positional parameter — cannot read its signature")
    return max(required)


def doc_gate(root: str, repo: str, work: str) -> int:
    m = load_manifest(root)
    tester_dir = materialise(root, m, m["tester"], work)
    tester = os.path.join(tester_dir, "mw-plugin-test.dll")
    if not os.path.isfile(tester):
        raise Refusal(f"{tester} is not in the image — the doc gate cannot run")
    sha = os.environ.get("MW_BUILD_SHA") or m["coreSha"]
    status = 0
    for tree, stager, src in (("doc", "stage-doc-gate.sh", "src/MeshWeaver.Documentation/Data"),
                              ("samples", "stage-samples-gate.sh", "samples/Graph/Data")):
        stage = os.path.join(work, tree)
        subprocess.run(["bash", f".github/scripts/{stager}", src, stage], cwd=repo, check=True)
        gate_log = os.path.join(work, f"{tree}-gate")
        rc = subprocess.run(["bash", ".github/scripts/bake-then-gate.sh",
                             *bake_then_gate_args(tester, stage, work, tree, sha, gate_log)],
                            cwd=repo).returncode
        print(f"[CI] {tree}-gate exit={rc}", flush=True)
        # bake-then-gate.sh can be green over a `Test/` suite that reported `tests=skipped` or no
        # positive count; dotnet-test.yml's doc-gate job closes that with this verifier, and so does
        # this stage — the Doc tree must carry at least one suite (`--require-tests`), exactly as CI.
        verify = ["python3", ".github/scripts/check-tests-area-verdicts.py",
                  "--gate-log", f"{gate_log}.log", "--tree", stage]
        if tree == "doc":
            verify.append("--require-tests")
        vrc = subprocess.run(verify, cwd=repo).returncode
        print(f"[CI] {tree}-tests-area exit={vrc}", flush=True)
        status = status or rc or vrc
    return status


# ───────────────────────────────── self-test ────────────────────────────────────────

FAKE_DOTNET = r'''#!/usr/bin/env python3
import os, sys, time
dll, rest = sys.argv[1], sys.argv[2:]
behaviour = open(dll).read().strip()
if rest[:2] == ["-list", "classes"]:
    print("xUnit.net v3 header line"); print("Ns.C"); print("Ns.A"); print("Ns.B"); sys.exit(0)
if behaviour == "hang":
    time.sleep(30)
if behaviour == "segv":
    # A REAL signal death only on Linux (CI, the hosts). On a developer's Mac a real SIGSEGV pops a
    # crash-report dialog per run, so there the host reports the death the way a shell would (128 + 11);
    # the classifier reads both the same way (Popen's -11 and a shell's 139).
    if sys.platform.startswith("linux"):
        import signal
        os.kill(os.getpid(), signal.SIGSEGV)
    sys.exit(139)
if behaviour == "degrade":
    os.makedirs("test-logs", exist_ok=True)
    open(os.path.join("test-logs", "B.log"), "w").write(
        "warn: MeshWeaverX[0] MeshNodeContentUnresolvedException: content for nodeType 'Demo' was NEVER resolvable on this replica\n")
if behaviour == "trace":
    import tempfile
    open(os.path.join(tempfile.gettempdir(), "meshweaver-test-trace.log"), "w").write("TEST_START B.T1\n")
if behaviour == "tmpdir":
    open(os.path.join(os.getcwd(), "tmpdir.seen"), "w").write(os.environ.get("TMPDIR", ""))
if behaviour == "spawn":
    import subprocess
    child = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(60)"])
    open(os.path.join(os.getcwd(), "child.pid"), "w").write(str(child.pid))
    time.sleep(30)
classes = [rest[i + 1] for i, a in enumerate(rest) if a == "-class"]
with open(os.path.join(os.getcwd(), "classes.log"), "a") as fh:
    fh.write(",".join(classes) + "\n")
if behaviour != "notrx":
    trx = rest[rest.index("-trx") + 1]
    os.makedirs(os.path.dirname(trx), exist_ok=True)
    open(trx, "w").write("<TestRun/>")
sys.exit(1 if behaviour == "fail" else 0)
'''


def _self_test() -> int:
    failures: list[str] = []

    def expect(cond: bool, what: str) -> None:
        print(("  ok   " if cond else "  FAIL ") + what)
        if not cond:
            failures.append(what)

    def refused(fn, needle: str, what: str) -> None:
        try:
            fn()
        except Refusal as e:
            expect(needle in str(e), f"{what} (refused: {str(e)[:90]}…)")
            return
        expect(False, f"{what} — NOT refused")

    with tempfile.TemporaryDirectory() as t:
        # The shard collects the test hosts' temp-dir traces; the self-test's must be its OWN, never a
        # trace another run on this machine left in the real temp dir (measured: one carried a real
        # degradation and the gate, correctly, read it).
        own_tmp = os.path.join(t, "tmp")
        os.makedirs(own_tmp)
        os.environ["TMPDIR"] = own_tmp
        tempfile.tempdir = own_tmp
        repo = os.path.join(t, "repo")
        os.makedirs(os.path.join(repo, ".github", "workflows"))
        os.makedirs(os.path.join(repo, ".github", "scripts"))
        with open(os.path.join(repo, WORKFLOW), "w") as fh:
            fh.write("env:\n  CI_SHARD_TOTAL: 2   # a comment\n")
        # shard 0: A whole + Split part 1/2; shard 1: Split part 2/2 + B (fails on demand)
        with open(os.path.join(repo, SHARD_ASSIGN), "w") as fh:
            fh.write('case "$SHARD_INDEX" in\n'
                     '0) echo test/A/A.csproj; echo "test/Split/Split.csproj#1/2";;\n'
                     '1) echo "test/Split/Split.csproj#2/2"; echo test/B/B.csproj; echo test/Helper/Helper.csproj;;\n'
                     'esac\n')

        # The scripts a shard records and gates with: the REAL ones when this runs in a checkout (CI's
        # self-test step), small stand-ins with the same contract inside the packed image.
        here = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", ".github", "scripts")
        real_scripts = all(os.path.isfile(os.path.join(here, n)) for n in ("record-host-crash.py", "check-untyped-content.sh"))
        for n, stub_text in (("record-host-crash.py",
                              "import sys\nopen(sys.argv[1], 'w').write('<TestRun><UnitTestResult testName=\"%s\" "
                              "outcome=\"Failed\"/></TestRun>' % sys.argv[2])\n"),
                             ("check-untyped-content.sh",
                              'd="${1:?}"; [ -d "$d" ] || exit 1\n'
                              '! grep -rlq -e MeshNodeContentUnresolvedException "$d"\n')):
            src = os.path.join(here, n)
            with open(os.path.join(repo, ".github", "scripts", n), "w") as fh:
                fh.write(open(src).read() if real_scripts else stub_text)
        print(f"  (recording and gating with the {'REAL' if real_scripts else 'stand-in'} record-host-crash.py and check-untyped-content.sh)")

        def bin_of(proj: str, behaviour: Optional[str], extra: str = "shared") -> None:
            d = os.path.join(repo, os.path.dirname(TESTER_PROJECT) if proj == "tester" else os.path.join("test", proj), TFM_DIR)
            os.makedirs(d, exist_ok=True)
            open(os.path.join(d, "Shared.dll"), "w").write(extra)  # identical across bins → one blob
            if behaviour is not None:
                open(os.path.join(d, f"{proj}.dll"), "w").write(behaviour)
                open(os.path.join(d, "xunit.v3.core.dll"), "w").write("xunit")

        bin_of("A", "pass"); bin_of("Split", "pass"); bin_of("B", "pass"); bin_of("Helper", None)
        bin_of("tester", None)
        open(os.path.join(repo, os.path.dirname(TESTER_PROJECT), TFM_DIR, "mw-plugin-test.dll"), "w").write("tester")
        dotnet = os.path.join(t, "dotnet")
        with open(dotnet, "w") as fh:
            fh.write(FAKE_DOTNET)
        os.chmod(dotnet, 0o755)
        sha = "a" * 40
        img = os.path.join(t, "image")

        print("positive control — pack and run green:")
        m = pack(repo, img, sha, "3.1.1")
        expect(m["shardTotal"] == 2, "the shard total is read from dotnet-test.yml (2)")
        expect(m["shards"]["0"] == ["test/A/A.csproj", "test/Split/Split.csproj#1/2"], "the units are shard-assign.sh's")
        blobs = os.listdir(os.path.join(img, "blobs"))
        shared_sha = hashlib.sha256(b"shared").hexdigest()
        expect(blobs.count(shared_sha) == 1 and len(m["projects"]) == 5,
               "an identical file in five bins is stored ONCE (content-addressed)")
        expect(os.path.isfile(os.path.join(img, "core-tests.py")), "the runner ships beside the bytes it packed")
        expect(run_shard(img, 0, 2, os.path.join(t, "w0"), dotnet, repo=repo) == 0, "shard 0/2 runs green")
        log = open(os.path.join(t, "w0", "test", "Split", TFM_DIR, "classes.log")).read().splitlines()
        expect(log == ["Ns.A,Ns.C"], f"part 1/2 runs every 2nd class of the SORTED list from the first ({log})")
        expect(run_shard(img, 1, 2, os.path.join(t, "w1"), dotnet, repo=repo) == 0, "shard 1/2 runs green (a helper bin is not a failure)")

        print("the test hosts' temp — outside the checkout, this invocation's own, gone afterwards:")
        open(os.path.join(repo, "test", "B", TFM_DIR, "B.dll"), "w").write("tmpdir")
        pack(repo, img, sha, "3.1.1")
        inside = os.path.join(repo, ".core-tests", "shard-1")
        expect(run_shard(img, 1, 2, inside, dotnet, repo=repo) == 0, "a shard whose --work lies INSIDE the checkout runs green")
        seen = open(os.path.join(inside, "test", "B", TFM_DIR, "tmpdir.seen")).read()
        real_repo, real_seen = os.path.realpath(repo), os.path.realpath(seen) if seen else ""
        expect(bool(seen) and os.path.commonpath([real_seen, real_repo]) != real_repo,
               f"…and its test host's $TMPDIR is OUTSIDE the checkout ({seen}), so a throw-away project there "
               f"never imports the checkout's Directory.Build.props")
        expect(bool(seen) and os.path.dirname(real_seen) == os.path.realpath(tempfile.gettempdir())
               and os.path.basename(seen).startswith("mw-core-tests-shard1-"),
               "…a fresh directory under the temp the runner inherited (the host stage's $TMPDIR)")
        expect(bool(seen) and not os.path.exists(seen), "…removed once the shard's traces are collected")
        os.environ["TMPDIR"] = tempfile.tempdir = os.path.join(repo, ".tmp")
        os.makedirs(tempfile.tempdir)
        try:
            refused(lambda: run_shard(img, 1, 2, os.path.join(t, "wt"), dotnet, repo=repo), "lies inside the checkout",
                    "an inherited $TMPDIR INSIDE the checkout (the defect one level up)")
        finally:
            os.environ["TMPDIR"] = tempfile.tempdir = own_tmp
        open(os.path.join(repo, "test", "B", TFM_DIR, "B.dll"), "w").write("pass")
        pack(repo, img, sha, "3.1.1")

        print("which pack — by commit identity:")
        other = os.path.join(t, "other")
        pack(repo, other, "b" * 40, "run-b")
        missing = os.path.join(t, "absent")
        expect(select_root([missing, img], sha) == img, "the first root packed from the required commit is taken (an absent run pack is passed over)")
        expect(select_root([other, img], sha) == img, "a pack of ANOTHER commit is passed over, never read")
        expect(select_root([img], None) == img, "one root without --require-sha reads it, as before")
        refused(lambda: select_root([other, missing], sha), "no test pack was built from",
                "no root packed from the run's commit (a host's installed image of another set)")
        refused(lambda: select_root([img, other], None), "without --require-sha", "two roots and nothing to choose between them")
        refused(lambda: select_root([img], "main"), "not a 40-hex", "a --require-sha that is not a commit")
        refused(lambda: select_root([], sha), "no --root", "no root at all")

        print("negative controls — each must be RED:")
        refused(lambda: run_shard(img, 0, 3, os.path.join(t, "wx"), dotnet, repo=repo), "packed for 2",
                "a stage total the image was not packed for")
        refused(lambda: run_shard(img, 2, 2, os.path.join(t, "wx"), dotnet, repo=repo), "outside", "a shard index outside the total")
        open(os.path.join(repo, "test", "B", TFM_DIR, "B.dll"), "w").write("fail")
        pack(repo, img, sha, "3.1.1")
        expect(run_shard(img, 1, 2, os.path.join(t, "wf"), dotnet, repo=repo) == 1, "a unit whose host exits non-zero reds the shard")
        open(os.path.join(repo, "test", "B", TFM_DIR, "B.dll"), "w").write("notrx")
        pack(repo, img, sha, "3.1.1")
        expect(run_shard(img, 1, 2, os.path.join(t, "wn"), dotnet, repo=repo) == 1, "a unit that exits 0 with NO trx reds the shard")
        open(os.path.join(repo, "test", "B", TFM_DIR, "B.dll"), "w").write("hang")
        pack(repo, img, sha, "3.1.1")
        expect(run_shard(img, 1, 2, os.path.join(t, "wh"), dotnet, unit_timeout=2, repo=repo) == 1,
               "a unit past its wall clock is killed and reds the shard")
        open(os.path.join(repo, "test", "B", TFM_DIR, "B.dll"), "w").write("spawn")
        pack(repo, img, sha, "3.1.1")
        expect(run_shard(img, 1, 2, os.path.join(t, "wg"), dotnet, unit_timeout=2, repo=repo) == 1,
               "a unit that spawned a child and hung reds the shard")
        pid = int(open(os.path.join(t, "wg", "test", "B", TFM_DIR, "child.pid")).read())
        alive = True
        for _ in range(50):
            try:
                os.kill(pid, 0)
            except ProcessLookupError:
                alive = False
                break
            time.sleep(0.1)
        if alive:
            os.kill(pid, signal.SIGKILL)
        expect(not alive, "the wall clock ends the unit's whole process group, not just the host "
                          "(its child does not outlive it into the next unit)")
        # A stale trx from an earlier run of the same work directory must not stand in for this one.
        open(os.path.join(repo, "test", "B", TFM_DIR, "B.dll"), "w").write("pass")
        pack(repo, img, sha, "3.1.1")
        expect(run_shard(img, 1, 2, os.path.join(t, "ws"), dotnet, repo=repo) == 0, "a reused work directory: first run green")
        expect(os.path.isfile(os.path.join(t, "ws", "test", "B", TFM_DIR, "TestResults", "B.trx")),
               "…and it left B.trx behind")
        open(os.path.join(repo, "test", "B", TFM_DIR, "B.dll"), "w").write("notrx")
        pack(repo, img, sha, "3.1.1")
        expect(run_shard(img, 1, 2, os.path.join(t, "ws"), dotnet, repo=repo) == 1,
               "a host that writes NO trx into a reused work directory reds the shard (the old trx is not this run's)")

        print("CI parity — the results tree, the hand-back and the untyped-content gate:")
        art = os.path.join(t, "artifacts")
        open(os.path.join(repo, "test", "B", TFM_DIR, "B.dll"), "w").write("trace")
        pack(repo, img, sha, "3.1.1")
        # The work directory first ran SHARD 0 (A.trx, Split.part1.trx): those are not shard 1's.
        expect(run_shard(img, 0, 2, os.path.join(t, "wr"), dotnet, repo=repo) == 0, "shard 0/2 into a work directory shard 1 then reuses")
        expect(run_shard(img, 1, 2, os.path.join(t, "wr"), dotnet, repo=repo, artifacts=art) == 0, "a green shard with a host to hand back to")
        markers = open(os.path.join(t, "wr", "test", "test-results.log")).read()
        expect("[CI] B exit=0 elapsed=" in markers and "[CI] Helper exit=0 (helper project" in markers,
               f"test/test-results.log carries CI's `[CI] <name> exit=N` markers ({markers.strip()[:120]})")
        unit = os.path.join(art, "testResults-shard1")
        handed = sorted(os.path.relpath(os.path.join(dp, n), unit) for dp, _, ns in os.walk(unit) for n in ns)
        expect(handed == ["collected-logs/_meshweaver-test-trace.log", "test/B/bin/Release/net10.0/TestResults/B.trx",
                          "test/Split/bin/Release/net10.0/TestResults/Split.part2.trx", "test/test-results.log"],
               f"the hand-back is the `test` job's artifact tree — THIS run's trx (not the reused directory's "
               f"A.trx / Split.part1.trx), markers, collected-logs, and no bins ({handed})")
        expect(not os.path.exists(os.path.join(tempfile.gettempdir(), "meshweaver-test-trace.log")),
               "…the host's trace landed in the invocation's OWN temp directory, not the shared one")
        # Successive shards on one host: an earlier run's append-only trace — in the work directory's
        # temp from a previous invocation, or in the shared $TMPDIR — is never this run's evidence.
        unresolved = "MeshNodeContentUnresolvedException: content for nodeType 'Demo' was NEVER resolvable on this replica\n"
        open(os.path.join(tempfile.gettempdir(), "meshweaver-test-trace.log"), "w").write(unresolved)
        stale_tmp = os.path.join(tempfile.gettempdir(), "mw-core-tests-shard1-stale")
        os.makedirs(stale_tmp, exist_ok=True)
        open(os.path.join(stale_tmp, "meshweaver-test-trace.log"), "a").write(unresolved)
        open(os.path.join(repo, "test", "B", TFM_DIR, "B.dll"), "w").write("pass")
        pack(repo, img, sha, "3.1.1")
        expect(run_shard(img, 1, 2, os.path.join(t, "wr"), dotnet, repo=repo) == 0,
               "a passing shard after an earlier one left an unresolved-content trace (shared $TMPDIR and the reused "
               "work directory's temp) is green — another run's trace is not this run's red")
        expect(not os.path.exists(os.path.join(t, "wr", "collected-logs", "_meshweaver-test-trace.log")),
               "…and no earlier run's trace reaches this run's collected-logs")
        os.remove(os.path.join(tempfile.gettempdir(), "meshweaver-test-trace.log"))
        open(os.path.join(repo, "test", "B", TFM_DIR, "B.dll"), "w").write("degrade")
        pack(repo, img, sha, "3.1.1")
        expect(run_shard(img, 1, 2, os.path.join(t, "wu"), dotnet, repo=repo, artifacts=art) == 1,
               "RED: every unit exits 0 but a log shows content NEVER resolvable — the untyped-content gate decides")
        expect(os.path.isfile(os.path.join(unit, "collected-logs", "B.log")),
               "…and the log it was red on was handed back first (the evidence survives the red)")
        open(os.path.join(repo, "test", "B", TFM_DIR, "B.dll"), "w").write("pass")
        pack(repo, img, sha, "3.1.1")
        expect(run_shard(img, 1, 2, os.path.join(t, "wu"), dotnet, repo=repo, artifacts=art) == 0,
               "a passing run in the SAME work directory is green (the earlier run's test-logs/B.log is not re-read)")
        cwd = os.getcwd()
        os.chdir(t)
        try:
            expect(run_shard(img, 1, 2, "wrel", dotnet, repo=repo) == 0,
                   "a RELATIVE --work is gated where its logs were collected (not resolved against --repo)")
        finally:
            os.chdir(cwd)
        open(os.path.join(repo, "test", "B", TFM_DIR, "B.dll"), "w").write("segv")
        pack(repo, img, sha, "3.1.1")
        expect(run_shard(img, 1, 2, os.path.join(t, "wc"), dotnet, repo=repo) == 1, "RED: a host that died on SIGSEGV")
        markers = open(os.path.join(t, "wc", "test", "test-results.log")).read()
        crashed_trx = os.path.join(t, "wc", "test", "B", TFM_DIR, "TestResults", "B.trx")
        crashed = open(crashed_trx).read() if os.path.isfile(crashed_trx) else ""
        expect("exit=139 SIGNAL SIGSEGV" in markers and 'outcome="Failed"' in crashed,
               "the signal death is named in the marker AND written into the trx as a failed result (#2495)")
        expect(classify("X", "X", -11, os.path.join(t, "absent.trx"), 480)[0].startswith("[CI] X exit=139 SIGNAL SIGSEGV")
               and classify("X", "X", 139, os.path.join(t, "absent.trx"), 480)[2] == 139,
               "a signal death reads the same as Popen's -11 and as a shell's 139")
        expect(not classify("X", "X", 0, os.path.join(t, "absent.trx"), 480)[1] == "",
               "negative control: exit 0 with no trx is NOT a pass (a crash is recorded)")
        open(os.path.join(repo, "test", "B", TFM_DIR, "B.dll"), "w").write("notrx")
        pack(repo, img, sha, "3.1.1")
        run_shard(img, 1, 2, os.path.join(t, "wm"), dotnet, repo=repo)
        masked_trx = os.path.join(t, "wm", "test", "B", TFM_DIR, "TestResults", "B.trx")
        expect(os.path.isfile(masked_trx) and 'outcome="Failed"' in open(masked_trx).read(),
               "a host that wrote no trx gets one recording the failure — no trx reader can miss it")
        expect(classify("X", "X", 1, os.path.join(t, "absent.trx"), 480)[0].startswith("[CI] X exit=1 MASKED (no trx"),
               "exit 1 with no trx is MASKED, never TESTFAIL")
        bare = os.path.join(t, "bare")
        os.makedirs(bare)
        refused(lambda: run_shard(img, 1, 2, os.path.join(t, "wb"), dotnet, repo=bare), "is absent",
                "RED: a checkout without the recorder and the gate (the shard could not check what CI checks)")
        open(os.path.join(repo, "test", "B", TFM_DIR, "B.dll"), "w").write("pass")
        pack(repo, img, sha, "3.1.1")

        print("doc-gate — the Tests-area verifier decides alongside the gate:")
        scripts = os.path.join(repo, ".github", "scripts")
        verifier_log = os.path.join(t, "verifier.log")
        for stager in ("stage-doc-gate.sh", "stage-samples-gate.sh"):
            with open(os.path.join(scripts, stager), "w") as fh:
                fh.write('mkdir -p "$2"\n')
        # The stub keeps the REAL script's signature: any other argument count is refused, exactly
        # as the real `${N:?…}` lines refuse it. And the arity it enforces is held to the real
        # script's own declaration whenever that script is present (a checkout — CI's self-test
        # step); in the packed image the script is not shipped, and the stub's count still guards.
        real_script = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..",
                                   ".github", "scripts", "bake-then-gate.sh")
        if os.path.isfile(real_script):
            arity = script_arity(open(real_script).read())
            expect(arity == BAKE_THEN_GATE_ARITY,
                   f"bake-then-gate.sh requires {arity} arguments and BAKE_THEN_GATE_ARITY says {BAKE_THEN_GATE_ARITY}")
        expect(len(bake_then_gate_args("t", "s", "w", "doc", "sha", "log")) == BAKE_THEN_GATE_ARITY,
               "doc_gate hands bake-then-gate.sh exactly the arguments its signature requires")
        stub = os.path.join(scripts, "bake-then-gate.sh")
        with open(stub, "w") as fh:
            fh.write(f'[ "$#" -eq {BAKE_THEN_GATE_ARITY} ] || {{ echo "bake-then-gate.sh: expected '
                     f'{BAKE_THEN_GATE_ARITY} arguments, got $#" >&2; exit 2; }}\n'
                     'echo "GATE OK" > "$6.log"\nexit 0\n')
        # Negative control: the stub really refuses a caller one argument short.
        expect(subprocess.run(["bash", stub, *["x"] * (BAKE_THEN_GATE_ARITY - 1)],
                              capture_output=True).returncode != 0,
               "the stub refuses a short argument list, as the real script does")
        with open(os.path.join(scripts, "check-tests-area-verdicts.py"), "w") as fh:
            fh.write("import os, sys\n"
                     f"open({verifier_log!r}, 'a').write(' '.join(sys.argv[1:]) + '\\n')\n"
                     "sys.exit(1 if os.environ.get('FAKE_VERIFIER_RED') and '--require-tests' in sys.argv else 0)\n")
        gw = os.path.join(t, "wd")
        expect(doc_gate(img, repo, gw) == 0, "both gates green and every Test/ suite counted ⇒ green")
        calls = open(verifier_log).read().splitlines()
        expect(calls == [f"--gate-log {gw}/doc-gate.log --tree {gw}/doc --require-tests",
                         f"--gate-log {gw}/samples-gate.log --tree {gw}/samples"],
               f"the verifier reads each tree's gate log and staged tree, --require-tests for Doc only ({calls})")
        os.environ["FAKE_VERIFIER_RED"] = "1"
        try:
            expect(doc_gate(img, repo, os.path.join(t, "wd2")) != 0,
                   "a green bake whose Doc Tests area did not run its cases reds the stage")
        finally:
            del os.environ["FAKE_VERIFIER_RED"]

        b_sha = hashlib.sha256(b"pass").hexdigest()
        os.chmod(os.path.join(img, "blobs", b_sha), 0o644)
        open(os.path.join(img, "blobs", b_sha), "w").write("tampered")
        refused(lambda: run_shard(img, 0, 2, os.path.join(t, "wt"), dotnet, repo=repo), "does not hash", "a tampered blob")
        refused(lambda: pack(repo, img, "nope", "3.1.1"), "40-hex", "a pack that names no commit")
        shutil.rmtree(os.path.join(repo, "test", "B", "bin"))
        refused(lambda: pack(repo, img, sha, "3.1.1"), "never built", "an assigned project with no built bin")
        bin_of("B", "pass")
        shutil.rmtree(os.path.join(repo, "tools"))
        refused(lambda: pack(repo, img, sha, "3.1.1"), "plugin tester", "a pack with no plugin tester")
        bin_of("tester", None)
        with open(os.path.join(repo, SHARD_ASSIGN), "w") as fh:
            fh.write('case "$SHARD_INDEX" in 0) echo test/A/A.csproj;; esac\n')
        refused(lambda: pack(repo, img, sha, "3.1.1"), "no unit", "a shard assigned nothing under TEST_SELECTION=all")
        refused(lambda: read_shard_total("env:\n  CI_SHARD_TOTAL: 8\n  CI_SHARD_TOTAL: 9\n"), "exactly once",
                "a workflow that declares the shard total twice")

    print(f"\n{'SELF-TEST FAILED' if failures else 'self-test: every refusal fires, the green path is green'}")
    return 1 if failures else 0


def main(argv: list[str]) -> int:
    if argv == ["--self-test"]:
        return _self_test()
    ap = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0])
    sub = ap.add_subparsers(dest="cmd", required=True)
    p = sub.add_parser("pack")
    p.add_argument("--repo", default=".")
    p.add_argument("--out", required=True)
    p.add_argument("--sha", required=True)
    p.add_argument("--version", required=True)
    r = sub.add_parser("run")
    r.add_argument("--shard", type=int, required=True)
    r.add_argument("--total", type=int, required=True)
    r.add_argument("--root", action="append", default=None)
    r.add_argument("--require-sha", default=None)
    r.add_argument("--work", default=None)
    r.add_argument("--repo", default=".")
    g = sub.add_parser("doc-gate")
    g.add_argument("--root", action="append", default=None)
    g.add_argument("--require-sha", default=None)
    g.add_argument("--repo", default=".")
    g.add_argument("--work", default=None)
    a = ap.parse_args(argv)
    try:
        if a.cmd == "pack":
            pack(a.repo, a.out, a.sha, a.version)
            return 0
        root = select_root(a.root or [os.path.dirname(os.path.abspath(__file__))], a.require_sha)
        # The image tree is read-only on a host; the run's checkout (the CWD) is writable disk.
        if a.cmd == "run":
            return run_shard(root, a.shard, a.total, a.work or os.path.abspath(f".core-tests/shard-{a.shard}"),
                             repo=a.repo, artifacts=os.environ.get("MW_BUILD_ARTIFACTS") or None)
        return doc_gate(root, os.path.abspath(a.repo), a.work or os.path.abspath(".core-tests/doc-gate"))
    except Refusal as e:
        print(f"::error::{e}", flush=True)
        return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
