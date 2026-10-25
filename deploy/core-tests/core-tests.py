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

    core-tests.py run --shard <i> --total <n> [--root /core-tests] [--work <dir>]
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

    core-tests.py doc-gate [--root /core-tests] [--work <dir>] [--repo .]
        The doc and samples gates (dotnet-test.yml's doc-gate job), against the checkout at the
        set's commit, with the plugin tester from this image: stage-doc-gate.sh /
        stage-samples-gate.sh, then bake-then-gate.sh with each tree's allow file and warning
        baseline, then check-tests-area-verdicts.py over each tree and its gate log (`--require-tests`
        for Doc), as dotnet-test.yml's doc-gate job does. RED when either gate or verifier is.

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


def run_shard(root: str, shard: int, total: int, work: str, dotnet: str = "dotnet",
              unit_timeout: int = UNIT_TIMEOUT_SECONDS) -> int:
    m = load_manifest(root)
    if total != m["shardTotal"]:
        raise Refusal(f"the stage runs shard {shard} of {total}, but this image was packed for {m['shardTotal']} "
                      f"shards (CI_SHARD_TOTAL at {m['coreSha'][:12]}) — .github/build-stages.json and the image disagree")
    if not (0 <= shard < total):
        raise Refusal(f"shard {shard} is outside 0..{total - 1}")
    units = m["shards"].get(str(shard)) or []
    if not units:
        raise Refusal(f"shard {shard}/{total} carries no unit — a stage that runs nothing is not a green one")
    os.makedirs(work, exist_ok=True)
    print(f"mw-core-tests {m['version']} ({m['coreSha'][:12]}): shard {shard}/{total}, {len(units)} unit(s)", flush=True)
    env = dict(os.environ, DOTNET_ENVIRONMENT="Development", Logging__LogLevel__Default="Warning",
               TESTCONTAINERS_RYUK_DISABLED="true")
    bins: dict[str, str] = {}
    failed: list[str] = []
    for entry in units:
        csproj, part, parts = split_unit(entry)
        name = os.path.basename(csproj)[: -len(".csproj")]
        label = name if parts == 1 else f"{name} (part {part}/{parts})"
        if csproj not in bins:
            bins[csproj] = materialise(root, m, csproj, work)
        d = bins[csproj]
        if not os.path.isfile(os.path.join(d, f"{name}.dll")) or not os.path.isfile(os.path.join(d, "xunit.v3.core.dll")):
            print(f"[CI] {name} exit=0 (helper project — no xUnit v3 test host)", flush=True)
            continue
        args = []
        trx = name
        if parts > 1:
            classes = _list_classes(dotnet, name, d)
            if not classes:
                print(f"[CI] {name} exit=96 NO_CLASSES (class enumeration failed for a split project)", flush=True)
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
        rc, _ = _run_unit([dotnet, f"{name}.dll", "-trx", f"TestResults/{trx}.trx", "-showLiveOutput",
                           "-longRunning", "60", *args], d, unit_timeout, env=env)
        print("::endgroup::", flush=True)
        if rc == 124:
            print(f"[CI] {label} exit=124 TIMEOUT ({unit_timeout}s wall-clock cap hit — a wedge, not a slow pass)", flush=True)
        elif rc == 0 and not os.path.isfile(trx_path):
            print(f"[CI] {label} exit=0 MASKED (no trx written — the host recorded no result)", flush=True)
            rc = 98
        else:
            print(f"[CI] {label} exit={rc}", flush=True)
        if rc != 0:
            failed.append(label)
    if failed:
        print(f"shard {shard}/{total}: RED — {len(failed)} unit(s) did not pass: {', '.join(failed)}", flush=True)
        return 1
    print(f"shard {shard}/{total}: green — every unit passed", flush=True)
    return 0


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
        rc = subprocess.run(["bash", ".github/scripts/bake-then-gate.sh", tester, stage,
                             os.path.join(work, "bake", tree), f".github/{tree}-gate.allow", sha,
                             gate_log, f".github/{tree}-gate-warnings.allow"],
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
        expect(run_shard(img, 0, 2, os.path.join(t, "w0"), dotnet) == 0, "shard 0/2 runs green")
        log = open(os.path.join(t, "w0", "test", "Split", TFM_DIR, "classes.log")).read().splitlines()
        expect(log == ["Ns.A,Ns.C"], f"part 1/2 runs every 2nd class of the SORTED list from the first ({log})")
        expect(run_shard(img, 1, 2, os.path.join(t, "w1"), dotnet) == 0, "shard 1/2 runs green (a helper bin is not a failure)")

        print("negative controls — each must be RED:")
        refused(lambda: run_shard(img, 0, 3, os.path.join(t, "wx"), dotnet), "packed for 2",
                "a stage total the image was not packed for")
        refused(lambda: run_shard(img, 2, 2, os.path.join(t, "wx"), dotnet), "outside", "a shard index outside the total")
        open(os.path.join(repo, "test", "B", TFM_DIR, "B.dll"), "w").write("fail")
        pack(repo, img, sha, "3.1.1")
        expect(run_shard(img, 1, 2, os.path.join(t, "wf"), dotnet) == 1, "a unit whose host exits non-zero reds the shard")
        open(os.path.join(repo, "test", "B", TFM_DIR, "B.dll"), "w").write("notrx")
        pack(repo, img, sha, "3.1.1")
        expect(run_shard(img, 1, 2, os.path.join(t, "wn"), dotnet) == 1, "a unit that exits 0 with NO trx reds the shard")
        open(os.path.join(repo, "test", "B", TFM_DIR, "B.dll"), "w").write("hang")
        pack(repo, img, sha, "3.1.1")
        expect(run_shard(img, 1, 2, os.path.join(t, "wh"), dotnet, unit_timeout=2) == 1,
               "a unit past its wall clock is killed and reds the shard")
        open(os.path.join(repo, "test", "B", TFM_DIR, "B.dll"), "w").write("spawn")
        pack(repo, img, sha, "3.1.1")
        expect(run_shard(img, 1, 2, os.path.join(t, "wg"), dotnet, unit_timeout=2) == 1,
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
        expect(run_shard(img, 1, 2, os.path.join(t, "ws"), dotnet) == 0, "a reused work directory: first run green")
        expect(os.path.isfile(os.path.join(t, "ws", "test", "B", TFM_DIR, "TestResults", "B.trx")),
               "…and it left B.trx behind")
        open(os.path.join(repo, "test", "B", TFM_DIR, "B.dll"), "w").write("notrx")
        pack(repo, img, sha, "3.1.1")
        expect(run_shard(img, 1, 2, os.path.join(t, "ws"), dotnet) == 1,
               "a host that writes NO trx into a reused work directory reds the shard (the old trx is not this run's)")

        print("doc-gate — the Tests-area verifier decides alongside the gate:")
        scripts = os.path.join(repo, ".github", "scripts")
        verifier_log = os.path.join(t, "verifier.log")
        for stager in ("stage-doc-gate.sh", "stage-samples-gate.sh"):
            with open(os.path.join(scripts, stager), "w") as fh:
                fh.write('mkdir -p "$2"\n')
        with open(os.path.join(scripts, "bake-then-gate.sh"), "w") as fh:
            fh.write('echo "GATE OK" > "$6.log"\nexit 0\n')
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
        refused(lambda: run_shard(img, 0, 2, os.path.join(t, "wt"), dotnet), "does not hash", "a tampered blob")
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
    r.add_argument("--root", default=os.path.dirname(os.path.abspath(__file__)))
    r.add_argument("--work", default=None)
    g = sub.add_parser("doc-gate")
    g.add_argument("--root", default=os.path.dirname(os.path.abspath(__file__)))
    g.add_argument("--repo", default=".")
    g.add_argument("--work", default=None)
    a = ap.parse_args(argv)
    try:
        if a.cmd == "pack":
            pack(a.repo, a.out, a.sha, a.version)
            return 0
        # The image tree is read-only on a host; the run's checkout (the CWD) is writable disk.
        if a.cmd == "run":
            return run_shard(a.root, a.shard, a.total, a.work or os.path.abspath(f".core-tests/shard-{a.shard}"))
        return doc_gate(a.root, os.path.abspath(a.repo), a.work or os.path.abspath(".core-tests/doc-gate"))
    except Refusal as e:
        print(f"::error::{e}", flush=True)
        return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
