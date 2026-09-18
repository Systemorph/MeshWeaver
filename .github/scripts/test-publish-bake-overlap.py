#!/usr/bin/env python3
"""EXECUTE publish-bake-bundles.sh with TWO publishers on one prefix, and prove what gets sealed.

WHY
---
`prebuilt-bundles/<framework-identity>/plugins/` has TWO writers: core CD's `plugins-bake` job
(publishing MeshWeaver.Plugins content at `gate.outputs.plugins_sha`) and the MeshWeaver.Plugins
satellite's own `publish-bake` (publishing its own head). Both call this one script, and they
resolve the SAME framework identity whenever the core surface has not changed between core's tip
and the satellite's pin — the ordinary case, because the identity is breaking-change-keyed.

Interleave them and `publish_one_target` seals a sentinel over a directory holding some of each
one's bytes: one seal, one generation, self-consistent to every consumer, and WRONG. #3460's
read-side generation cannot see it (there is nothing stale to refuse) and the boot seeder cannot
see it (the sentinel is present and every listed bundle exists). The first symptom is
`dependency record mismatch — built against mvid:…, live is mvid:…` on a portal that renders
nothing. MeshWeaver#3461.

Nothing executed this script's publish path before. It talks to Azure Files, it runs only on main
in five repositories, and the first execution of any edit to it is a production publish.

HOW IT STAYS HONEST
-------------------
* The REAL script is run, both times — a copy passes while the real thing rots. The `--script`
  option exists so the same cases can be run against the PRE-FIX script to prove they fail there;
  CI always runs them against the script beside this file.
* The verdict is read off THE BYTES on the fake share, never off the script's own log: each fixture
  bundle's content names the bake that produced it, so "the sealed directory holds bytes from two
  bakes" is a fact about the shelf, not a claim the script makes about itself.
* Three CONTROL cases must PASS (a settled publish, a republish of new content, an already-
  published skip). A harness whose only cases are failures cannot tell "the script refuses
  correctly" from "the script always refuses".
* Every case prints its denominator — files expected, files present, files per bake — and a case
  that finds ZERO files FAILS. A check that looked in the wrong place must go red, not green.
* The stub `az` refuses an argument shape or a `--query` it does not implement, so the script and
  the stub cannot drift apart silently: change the script's query and this harness goes red until
  the stub is taught the new one.

TWO I/O EDGES, TWO STUBS
------------------------
Since 2026-09-08 the script publishes and reads back through `publish-bake-files.py` — ONE process
per phase per target on the Azure SDK, replacing 46 + 46 CLI launches per target — and keeps `az`
only for the per-target decisions (does the sentinel exist, what does a marker say) and the rare
paths (unseal, release marker, architecture backfill). So the harness substitutes BOTH edges over
ONE filesystem share: the stub `az` on PATH, and a fake share backend the helper loads from
`PUBLISH_BAKE_FAKE_SHARE_BACKEND`. They read and write the same files and the same per-file
metadata, so a file the helper uploads is the file the stub's `exists`/`download` answers about.
The second-publisher hooks moved to the fake backend's `upload`, and the harness runs the upload
pool ONE wide so the plan order is the upload order and "hook onto architecture.txt" means what it
always meant; one control case runs the default pool width so the parallel path is exercised too.

WHAT THIS HARNESS CANNOT PROVE
------------------------------
The fake is not Azure Files. It reproduces the parts the script depends on — per-file metadata,
directories, the sentinel written last — and nothing else. Two things are asserted against the REAL
pieces instead: `sdk-check` imports the PINNED SDK the script installs and constructs the clients
the helper uses (no network), so an SDK surface move is red here and not in a production publish;
and the helper's TSV rendering — five fields, `-` for an absent stamp, `absent`/`error` never
disguised as "no digest" — is pinned by running the helper itself against the fake, because that
rendering is what the bash parse asserts a field count on. The lesson behind that case is older
than the helper: the first draft of the CLI-based read used a `--query` shape that knack rendered as
two LINES instead of two columns, so `$2` would have read empty for every file and the
postcondition would have refused every publication in the fleet — and the stub of the day said
nothing, because it was written to match the draft.

Beyond that the script fails CLOSED on an unreadable, empty or unexpectedly-shaped answer, so a
rendering change is a RED publish rather than a silent one.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
DEFAULT_SCRIPT = HERE / "publish-bake-bundles.sh"
HELPER = HERE / "publish-bake-files.py"

IDENTITY = "s0123456789abcdef0123456789abcdef"
SOURCE = "plugins"
ACCOUNT = "portalstore"
SHARE = "memex-data"
TARGET = f"{ACCOUNT}/{SHARE}"
DEST = f"prebuilt-bundles/{IDENTITY}/{SOURCE}"
SENTINEL = "_complete"
# Must match ShippedPrebuiltBundles.PublicationPointerFileName and the POINTER in the script.
POINTER = "_current"

BUNDLES = ["Chess.zip", "Edu.zip", "Store.zip"]
MODULES = ["MeshWeaver.AI.module.nupkg", "MeshWeaver.Maps.module.nupkg"]
# The platform surface the bake writes beside framework-mvid.txt (MeshWeaver#3651) — published
# beside _complete like the other markers, and OPTIONAL: a bake taken with an image that predates
# it publishes none, with a warning, and the gate reports the link check as undetermined.
SURFACE = "platform-surface.json"
# bundles + modules + modules/_index + source-commit.txt + platform-surface.json + architecture.txt
# + repository.txt
EXPECTED_FILES = len(BUNDLES) + len(MODULES) + 5


# ────────────────────────────── the stub `az` ──────────────────────────────
# Written to disk and put first on PATH. It models exactly the surface publish-bake-bundles.sh
# uses, and it REFUSES anything else rather than shrugging: an unimplemented flag or query would
# otherwise let the script drift away from what this harness exercises.
STUB_AZ = r'''#!/usr/bin/env python3
import base64, hashlib, json, os, subprocess, sys
from pathlib import Path

ROOT = Path(os.environ["MOCK_AZ_ROOT"])

def die(msg):
    sys.stderr.write("stub-az: " + msg + "\n")
    sys.exit(64)

argv = sys.argv[1:]
if argv[:2] != ["storage"]:
    if len(argv) < 2 or argv[0] != "storage":
        die("only `az storage …` is modelled; got: " + " ".join(argv))
group, verb = argv[1], argv[2] if len(argv) > 2 else ""
opts, i = {}, 3
while i < len(argv):
    a = argv[i]
    if not a.startswith("--"):
        die("unexpected positional argument %r in: %s" % (a, " ".join(argv)))
    vals = []
    i += 1
    while i < len(argv) and not argv[i].startswith("--"):
        vals.append(argv[i]); i += 1
    # -o tsv arrives as `-o` `tsv` only when the caller writes it that way; the script uses `-o`.
    opts.setdefault(a, []).extend(vals)
# `-o tsv` is written as a short flag; normalise it.
raw = " ".join(argv)
out_tsv = " -o tsv" in raw

def flag(name, required=True):
    v = opts.get(name)
    if not v:
        if required:
            die("missing %s in: %s" % (name, raw))
        return None
    return v[0]

def share_root():
    return ROOT / flag("--account-name") / flag("--share-name")

def meta_path(p):
    return ROOT / ".meta" / flag("--account-name") / flag("--share-name") / (str(p) + ".json")

def emit(v):
    sys.stdout.write(v + "\n")

if group == "storage" and verb == "":
    die("no verb: " + raw)

sub = argv[1]          # directory | file
action = argv[2]       # exists | create | upload | download | delete | show
if sub == "directory":
    name = flag("--name")
    d = share_root() / name
    if action == "exists":
        if opts.get("--query", [""])[0] != "exists" or not out_tsv:
            die("only `--query exists -o tsv` is modelled for directory exists: " + raw)
        emit("true" if d.is_dir() else "false"); sys.exit(0)
    if action == "create":
        d.mkdir(parents=True, exist_ok=True); sys.exit(0)
    die("unmodelled directory action %r" % action)

if sub != "file":
    die("unmodelled storage group %r" % sub)

path = flag("--path")

if action == "exists":
    if opts.get("--query", [""])[0] != "exists" or not out_tsv:
        die("only `--query exists -o tsv` is modelled for file exists: " + raw)
    emit("true" if (share_root() / path).is_file() else "false"); sys.exit(0)

if action == "upload":
    # Only the architecture BACKFILL still uploads through the CLI (unstamped, onto a sealed
    # incumbent); the publication itself goes through the helper and the fake backend below.
    src = Path(flag("--source"))
    dest_dir = share_root() / path
    # 🚨 The real CLI silently treats an EXTENSIONLESS --path as a DIRECTORY and appends the
    # source basename; the script relies on that for the markers it still uploads this way.
    # Model it by the same rule the script documents: an existing directory wins.
    if dest_dir.is_dir():
        target = dest_dir / src.name
    else:
        target = share_root() / path
    rel = str(target.relative_to(share_root()))
    if opts.get("--metadata"):
        die("a stamped upload through the CLI — the publication's files go through "
            "publish-bake-files.py now; this stub models only the unstamped backfill: " + raw)
    if not target.parent.is_dir():
        sys.stderr.write("stub-az: ParentNotFound for %s\n" % target); sys.exit(1)
    # Two failure shapes for a CLI upload, both measured rather than imagined: a plain failure, and
    # the one this share produced on 2026-09-08 — SUCCESS reported for a file that was never stored.
    if os.environ.get("MOCK_AZ_FAIL_UPLOAD_OF") == target.name:
        sys.stderr.write("stub-az: simulated upload failure for %s\n" % target); sys.exit(1)
    if os.environ.get("MOCK_AZ_DROP_UPLOAD_OF") == target.name:
        sys.exit(0)
    target.write_bytes(src.read_bytes())
    # A hook AFTER a CLI upload landed — the pointer is the only file that still goes this way, so
    # this is how a sibling publication is made to move `_current` in the gap between this run's
    # pointer write and its read-back of it.
    if os.environ.get("MOCK_AZ_AFTER_UPLOAD_OF") == target.name:
        cmd = os.environ["MOCK_AZ_AFTER_UPLOAD_CMD"]
        env = dict(os.environ)
        for k in ("MOCK_AZ_AFTER_UPLOAD_OF", "MOCK_AZ_AFTER_UPLOAD_CMD"):
            env.pop(k, None)
        subprocess.run(["bash", "-c", cmd], env=env, check=False)
    mp = meta_path(rel)
    if mp.exists():
        mp.unlink()
    sys.exit(0)

if action == "download":
    src = share_root() / path
    dest = Path(flag("--dest"))
    if not src.is_file():
        sys.stderr.write("stub-az: ResourceNotFound %s\n" % path); sys.exit(1)
    dest.parent.mkdir(parents=True, exist_ok=True)
    dest.write_bytes(src.read_bytes()); sys.exit(0)

if action == "delete":
    f = share_root() / path
    if not f.is_file():
        sys.stderr.write("stub-az: ResourceNotFound %s\n" % path); sys.exit(1)
    f.unlink()
    mp = meta_path(path)
    if mp.exists():
        mp.unlink()
    sys.exit(0)

if action == "list":
    # The ONE listing the script takes through the CLI: the diagnostic on a read-back refusal
    # (MeshWeaver#3461, #3731) — "what the directory holds now", from inside the job's credential.
    q = opts.get("--query", [None])[0]
    if q != "[].{name:name, bytes:properties.contentLength}" or not out_tsv:
        die("only the refusal diagnostic's listing query is modelled for file list: " + raw)
    d = share_root() / path
    if not d.is_dir():
        sys.stderr.write("stub-az: ResourceNotFound %s\n" % path); sys.exit(1)
    for p in sorted(d.iterdir()):
        if p.is_file():
            emit("%s\t%d" % (p.name, p.stat().st_size))
    sys.exit(0)

# `show` is deliberately NOT modelled: the per-file read-back is the launch storm this harness's
# subject retired (46 processes per target). A script that reaches for it again dies here.
die("unmodelled file action %r" % action)
'''


# ────────────────────────────── the stub `gh` (the compare API only) ──────────────────────────────
# The publisher orders two source commits through `gh api repos/<repo>/compare/<base>...<head>` in
# TWO places: the decision-time never-seal-backwards guard, and the pointer-move guard that repeats
# it ~90 seconds later (MeshWeaver#3461 phase 4). Without a stub, `gh` is either absent or the real
# CLI against a real repository, so both guards took their "could not order" arm and the cases below
# would have measured the fallback rather than the guard.
#
# MOCK_GH_AHEAD holds the HEAD-side shas that are to be reported as NEWER than (and containing) the
# base — i.e. `.status == "ahead"`, the real API's answer. MOCK_GH_FAIL makes the call fail the way
# a token without access to a private repository does, which is the arm that measured 5 of 25 core
# re-seals on 2026-09-13.
STUB_GH = r'''#!/usr/bin/env python3
import os, sys

argv = sys.argv[1:]
if not argv or argv[0] != "api":
    sys.stderr.write("stub gh: only `gh api` is implemented\n")
    sys.exit(2)
if os.environ.get("MOCK_GH_FAIL"):
    sys.stderr.write("gh: Not Found (HTTP 404)\n")
    sys.exit(1)
path = argv[1] if len(argv) > 1 else ""
if "/compare/" not in path:
    sys.stderr.write(f"stub gh: unsupported api path {path!r}\n")
    sys.exit(2)
pair = path.split("/compare/", 1)[1]
base, _, head = pair.partition("...")
ahead = os.environ.get("MOCK_GH_AHEAD", "").split()
if head in ahead and head != base:
    print("ahead")
elif base in ahead and head != base:
    print("behind")
elif head == base:
    print("identical")
else:
    print("diverged")
'''


# ────────────────────────────── the fake share backend ──────────────────────────────
# Loaded by publish-bake-files.py from PUBLISH_BAKE_FAKE_SHARE_BACKEND, over the SAME filesystem
# share and the SAME `.meta` sidecars the stub `az` reads and writes. Every second-publisher hook
# lives here now, on `upload`, because that is where the publication's bytes land.
FAKE_BACKEND = r'''
import json, os, subprocess, sys, threading
from pathlib import Path

ROOT = Path(os.environ["MOCK_AZ_ROOT"])
_LOCK = threading.Lock()


def _run_hook(when, rel):
    on = os.environ.get("MOCK_AZ_HOOK_ON")
    if not on or on != rel:
        return
    if os.environ.get("MOCK_AZ_HOOK_WHEN", "before") != when:
        return
    once = os.environ.get("MOCK_AZ_HOOK_ONCE")
    if once:
        marker = Path(once)
        if marker.exists():
            return
        marker.parent.mkdir(parents=True, exist_ok=True)
        marker.write_text("fired")
    cmd = os.environ["MOCK_AZ_HOOK_CMD"]
    env = dict(os.environ)
    for k in ("MOCK_AZ_HOOK_ON", "MOCK_AZ_HOOK_CMD", "MOCK_AZ_HOOK_ONCE", "MOCK_AZ_HOOK_WHEN"):
        env.pop(k, None)
    subprocess.run(["bash", "-c", cmd], env=env, check=False)


class FakeShare:
    def __init__(self, account, share):
        self.root = ROOT / account / share
        self.meta = ROOT / ".meta" / account / share

    def _meta(self, path):
        return self.meta / (path + ".json")

    def ensure_directory(self, path):
        (self.root / path).mkdir(parents=True, exist_ok=True)

    def upload(self, path, local, metadata):
        _run_hook("before", path)
        refuse = os.environ.get("MOCK_AZ_FAIL_UPLOAD_PATH")
        if refuse and path.endswith(refuse):
            raise RuntimeError("simulated upload failure for %s" % path)
        counter = os.environ.get("MOCK_AZ_UPLOAD_COUNTER")
        if counter:
            with _LOCK:
                c = Path(counter)
                n = (int(c.read_text()) if c.exists() else 0) + 1
                c.write_text(str(n))
            cap = os.environ.get("MOCK_AZ_FAIL_UPLOADS_AFTER")
            if cap and n > int(cap):
                raise RuntimeError("simulated upload failure (#%d)" % n)
        target = self.root / path
        if not target.parent.is_dir():
            raise FileNotFoundError("ParentNotFound for %s" % target)
        target.write_bytes(Path(local).read_bytes())
        mp = self._meta(path)
        mp.parent.mkdir(parents=True, exist_ok=True)
        mp.write_text(json.dumps(dict(metadata)))
        _run_hook("after", path)

    def list_files(self, directory):
        d = self.root / directory
        if not d.is_dir():
            return {}
        return {p.name: p.stat().st_size for p in d.iterdir() if p.is_file()}

    def delete_file(self, path):
        # 🚨 THE READER'S VIEW, recorded at the instant BEFORE each delete (#3461 phase 5): what
        # `_current` names, whether that generation is sealed, and whether the flat copy at the
        # prefix is still sealed. "A reader is never left with neither" is then a fact about every
        # line of this log rather than a claim about the order the script's lines are written in.
        log = os.environ.get("MOCK_AZ_DELETE_LOG")
        prefix = os.environ.get("MOCK_AZ_DISPOSAL_PREFIX")
        if log and prefix:
            src = self.root / prefix
            pointer = src / "_current"
            named = pointer.read_text().strip() if pointer.is_file() else ""
            gen_sealed = bool(named) and (src / named / "_complete").is_file()
            flat_sealed = (src / "_complete").is_file()
            with _LOCK, open(log, "a") as fh:
                fh.write("%s\t%s\t%d\t%d\n" % (path, named or "-", gen_sealed, flat_sealed))
        _run_hook("before", path)
        fails = os.environ.get("MOCK_AZ_DELETE_FAILS")
        if fails and path.endswith(fails):
            raise RuntimeError("simulated delete failure for %s" % path)
        f = self.root / path
        if not f.is_file():
            return False
        f.unlink()
        mp = self._meta(path)
        if mp.exists():
            mp.unlink()
        # A hook AFTER a delete landed: this is how a LEGACY writer is made to seal the flat copy
        # inside the disposal, which is the one interleaving the deletion order cannot prevent.
        _run_hook("after", path)
        return True

    def delete_directory(self, path):
        d = self.root / path
        if not d.is_dir():
            return False
        d.rmdir()   # raises when not empty, exactly as the share refuses a non-empty directory
        return True

    def get_properties(self, path):
        # Reproduces the ONE way a read-back loop could end early: a command inside a `while read`
        # loop that consumes the loop's stdin. The helper runs OUTSIDE that loop now, and this
        # keeps the case honest if anyone ever moves it back in.
        if os.environ.get("MOCK_AZ_EAT_STDIN") == "1":
            try:
                sys.stdin.read()
            except Exception:
                pass
        fails = os.environ.get("MOCK_AZ_SHOW_FAILS")
        if fails and path.endswith(fails):
            raise RuntimeError("simulated read failure for %s" % path)
        f = self.root / path
        if not f.is_file():
            raise FileNotFoundError("ResourceNotFound %s" % path)
        mp = self._meta(path)
        md = json.loads(mp.read_text()) if mp.exists() else {}
        return md, f.stat().st_size


def make_backend(account, share):
    return FakeShare(account, share)
'''


# ────────────────────────────── fixtures ──────────────────────────────
class Bake:
    """One producer's bake directory — every byte in it names the bake that made it."""

    def __init__(self, root: Path, name: str, source_sha: str, surface: bool = True,
                 surface_shared: bool = False, extra_bundles: tuple[str, ...] = ()):
        self.name = name
        self.source_sha = source_sha
        self.bundles = list(BUNDLES) + list(extra_bundles)
        self.dir = root / f"bake-{name}"
        (self.dir / "modules").mkdir(parents=True, exist_ok=True)
        (self.dir / "framework-mvid.txt").write_text(IDENTITY + "\n")
        for b in self.bundles:
            (self.dir / b).write_text(f"{b} produced by bake {name} at {source_sha}\n")
        for m in MODULES:
            (self.dir / "modules" / m).write_text(f"{m} produced by bake {name} at {source_sha}\n")
        if surface:
            if surface_shared:
                # 🚨 THE REAL DOCUMENT IS A PROPERTY OF THE PLATFORM IMAGE, NOT OF THE BAKE RUN, so
                # two bakes taken with one image write it byte-identically — measured on the
                # 2026-09-08 incident, where source-commit.txt, repository.txt, architecture.txt,
                # modules/_index AND platform-surface.json were all byte-identical between the two
                # racing publications and only the compiled zips differed. A fixture that made it
                # differ per bake could never reproduce a convergence, and the case below would
                # pass having tested the wrong thing. It carries no "produced by bake" phrase, so
                # the shelf reader counts it as a marker rather than as a second bake's bytes.
                (self.dir / SURFACE).write_text(
                    f"{SURFACE} for the platform image behind {IDENTITY}\n")
            else:
                # The harness reads bytes, not shape, so the fixture line names its producer like
                # every other file — a mix is then a fact about the shelf.
                (self.dir / SURFACE).write_text(
                    f"{SURFACE} produced by bake {name} at {source_sha}\n")


class Shelf:
    """The fake Azure Files share, read as a consumer would: by the bytes, not by the log."""

    def __init__(self, root: Path):
        self.root = root
        self.dest = root / ACCOUNT / SHARE / DEST

    def sealed(self) -> bool:
        return (self.dest / SENTINEL).is_file()

    def under(self, sub: str) -> "Shelf":
        """The same share, read at a SUBDIRECTORY of the source dir — i.e. one generation."""
        s = Shelf(self.root)
        s.dest = self.dest / sub
        return s

    def generations(self) -> list[str]:
        """Every publication directory under the source dir (the pointer and markers excluded)."""
        if not self.dest.is_dir():
            return []
        return sorted(d.name for d in self.dest.iterdir()
                      if d.is_dir() and d.name != "modules")

    def pointer(self) -> str:
        f = self.dest / POINTER
        return f.read_text().strip() if f.is_file() else ""

    def stamp(self, rel: str) -> dict:
        """The metadata the publisher wrote on one file — `digest` and `publication`."""
        mp = self.root / ".meta" / ACCOUNT / SHARE / DEST / (rel + ".json")
        return json.loads(mp.read_text()) if mp.is_file() else {}

    def files(self) -> dict[str, str]:
        """path-under-dest -> content, for every published file of THIS publication.

        The sentinel and the pointer are excluded (neither is published content), and so is
        anything under a GENERATION directory: a generation is its own publication, read through
        `under()`. Without that exclusion a flat-copy assertion would silently count the
        generations' files too and pass on a number that means nothing.
        """
        out: dict[str, str] = {}
        if not self.dest.is_dir():
            return out
        skip = set(self.generations())
        for p in sorted(self.dest.rglob("*")):
            if not p.is_file() or p.name in (SENTINEL, POINTER):
                continue
            rel = p.relative_to(self.dest)
            if rel.parts[0] in skip:
                continue
            out[str(rel)] = p.read_text()
        return out

    def bakes_present(self) -> dict[str, int]:
        """bake name → how many published files came from it. THE denominator of every verdict."""
        counts: dict[str, int] = {}
        for content in self.files().values():
            for token in content.split():
                pass
            # every fixture file says "… produced by bake <name> at <sha>"
            parts = content.split(" produced by bake ", 1)
            who = parts[1].split(" at ", 1)[0] if len(parts) == 2 else "<marker>"
            counts[who] = counts.get(who, 0) + 1
        return counts

    def sealed_mix(self) -> bool:
        """A sentinel over bytes from more than one bake — the defect, stated as a fact."""
        if not self.sealed():
            return False
        return len([b for b in self.bakes_present() if b != "<marker>"]) > 1


# ────────────────────────────── the runner ──────────────────────────────
class Harness:
    def __init__(self, script: Path, workdir: Path):
        self.script = script
        self.work = workdir
        self.shelf_root = workdir / "share"
        self.bin = workdir / "bin"
        self.bin.mkdir(parents=True, exist_ok=True)
        az = self.bin / "az"
        az.write_text(STUB_AZ)
        az.chmod(0o755)
        gh = self.bin / "gh"
        gh.write_text(STUB_GH)
        gh.chmod(0o755)
        self.backend = workdir / "fake_share_backend.py"
        self.backend.write_text(FAKE_BACKEND)

    def base_env(self) -> dict[str, str]:
        return {
            "MOCK_AZ_ROOT": str(self.shelf_root),
            "PUBLISH_BAKE_FAKE_SHARE_BACKEND": str(self.backend),
            # The harness's own interpreter carries the pinned SDK (CI installs it into the same
            # venv this file runs from), so the script does not build a venv per publication —
            # and sdk-check still holds it to the pins on every run.
            "PUBLISH_BAKE_PYTHON": sys.executable,
            # ONE upload at a time, so the plan order is the upload order and a hook "before
            # Store.zip" / "after architecture.txt" means what it always meant. The parallel
            # control case below unsets this.
            "PUBLISH_BAKE_UPLOAD_WORKERS": "1",
            "BAKE_PUBLISH_TARGETS": TARGET,
            "GITHUB_RUN_ATTEMPT": "1",
        }

    def publish(self, bake: Bake, publisher: str, run_id: str, env_extra: dict | None = None):
        env = dict(os.environ)
        env["PATH"] = f"{self.bin}{os.pathsep}{env['PATH']}"
        env.update(self.base_env())
        env["GITHUB_REPOSITORY"] = publisher
        env["GITHUB_RUN_ID"] = run_id
        env.pop("EXT_MODULES_DIR", None)
        env.pop("GITHUB_STEP_SUMMARY", None)
        env.pop("RUNNER_TEMP", None)
        for k in ("MOCK_AZ_HOOK_ON", "MOCK_AZ_HOOK_CMD", "MOCK_AZ_HOOK_ONCE", "MOCK_AZ_HOOK_WHEN",
                  "MOCK_AZ_FAIL_UPLOADS_AFTER", "MOCK_AZ_UPLOAD_COUNTER", "MOCK_AZ_SHOW_FAILS",
                  "MOCK_AZ_EAT_STDIN", "MOCK_AZ_DELETE_LOG", "MOCK_AZ_DISPOSAL_PREFIX",
                  "MOCK_AZ_DELETE_FAILS", "MOCK_AZ_FAIL_UPLOAD_PATH", "MOCK_AZ_FAIL_UPLOAD_OF",
                  "MOCK_AZ_DROP_UPLOAD_OF", "MOCK_AZ_AFTER_UPLOAD_OF", "MOCK_AZ_AFTER_UPLOAD_CMD"):
            env.pop(k, None)
        for k, v in (env_extra or {}).items():
            if v is None:
                env.pop(k, None)
            else:
                env[k] = str(v)
        return subprocess.run(
            ["bash", str(self.script), str(bake.dir), SOURCE, bake.source_sha],
            capture_output=True, text=True, env=env,
        )

    def publish_command(self, bake: Bake, publisher: str, run_id: str,
                        env_extra: dict | None = None) -> str:
        """The same publication, as a shell command a backend hook can run mid-upload."""
        assigns = {"PATH": f"{self.bin}:$PATH"}
        assigns.update(self.base_env())
        assigns.update({
            "GITHUB_REPOSITORY": publisher,
            "GITHUB_RUN_ID": run_id,
        })
        assigns.update({k: str(v) for k, v in (env_extra or {}).items()})
        prefix = " ".join(f'{k}="{v}"' for k, v in assigns.items())
        return (f'{prefix} bash "{self.script}" "{bake.dir}" {SOURCE} {bake.source_sha}'
                f' > "{self.work}/inner-{run_id}.log" 2>&1')

    def reset(self):
        if self.shelf_root.exists():
            shutil.rmtree(self.shelf_root)
        self.shelf_root.mkdir(parents=True, exist_ok=True)

    def shelf(self) -> Shelf:
        return Shelf(self.shelf_root)


FAILURES: list[str] = []
PASSES: list[str] = []


def check(name: str, condition: bool, detail: str = ""):
    if condition:
        PASSES.append(name)
        print(f"  OK   {name}" + (f" — {detail}" if detail else ""))
    else:
        FAILURES.append(name)
        print(f"  FAIL {name}" + (f" — {detail}" if detail else ""))


def inner_receipt(work: Path, run_id: str) -> str:
    """The final receipt of a publication a backend hook ran — `<no receipt>` when it never got there."""
    log = work / f"inner-{run_id}.log"
    text = log.read_text() if log.is_file() else ""
    return next((l for l in text.splitlines() if l.startswith("bake published:")), "<no receipt>")


def inner_run_succeeded(work: Path, run_id: str) -> bool:
    """A hooked publication ran to its receipt and failed no target. Its exit code is not captured
    (the hook runs it inside the outer run's upload), so success is read off what it printed."""
    log = work / f"inner-{run_id}.log"
    text = log.read_text() if log.is_file() else ""
    return "bake published:" in text and "FAILED" not in text


def denominator(shelf: Shelf) -> str:
    files = shelf.files()
    bakes = shelf.bakes_present()
    return (f"{len(files)}/{EXPECTED_FILES} file(s) present, sealed={shelf.sealed()}, "
            f"by bake: {bakes or '{}'}")


# ────────────────────────────── the cases ──────────────────────────────
def script_code_lines(script: Path) -> str:
    """The script minus its comments — the launch storm is named in a comment as history."""
    return "\n".join(l for l in script.read_text().splitlines() if not l.lstrip().startswith("#"))


def check_helper_shape(script: Path, h: "Harness") -> None:
    """Pin the two things the fake cannot stand in for: the helper's rendering, and the real SDK.

    The bash parse asserts a FIELD COUNT on every row and reads `-` as "no stamp"; a rendering
    change on the helper's side is therefore a fleet-wide false verdict unless it is pinned here.
    And the fake backend never imports the SDK, so `sdk-check` — the same call the script makes
    after its pinned install — is run against the real one: a moved client surface goes red in
    this harness instead of in a production publish.
    """
    print("\nthe bulk helper (the fake cannot prove these; the helper and the real SDK can):")
    code = script_code_lines(script)
    check("the script publishes and reads back through publish-bake-files.py",
          "publish-bake-files.py" in code and code.count('"$PYTHON" "$HELPER"') >= 4,
          "upload, verify, stamp and sdk-check are all routed through the helper")
    check("the script no longer launches a CLI process per file",
          "az storage file show" not in code and "--metadata" not in code,
          "no `az storage file show`, no stamped `az storage file upload` outside comments")
    pins = re.search(r'SDK_PINS=\((.*?)\)', code)
    check("the script pins the SDK it installs", bool(pins) and "==" in (pins.group(1) if pins else ""),
          pins.group(1) if pins else "no SDK_PINS=( … ) in the script")
    if pins:
        pin_list = " ".join(p.strip('"') for p in pins.group(1).split())
        r = subprocess.run([sys.executable, str(HELPER), "sdk-check", "--pins", pin_list],
                           capture_output=True, text=True)
        check("sdk-check passes against the REAL, pinned SDK (clients construct, no network)",
              r.returncode == 0 and "token_intent=backup" in r.stderr,
              f"rc={r.returncode}: {(r.stderr or r.stdout).strip().splitlines()[-1:] }")

    # The rendering, pinned by running the helper against the fake: four states of one shelf.
    h.reset()
    shelf = h.shelf_root / ACCOUNT / SHARE / DEST
    meta = h.shelf_root / ".meta" / ACCOUNT / SHARE / DEST
    (shelf / "modules").mkdir(parents=True)
    meta.mkdir(parents=True)
    (shelf / "stamped.zip").write_bytes(b"12345")
    (meta / "stamped.zip.json").write_text(json.dumps({"digest": "d1", "publication": "P-1-1"}))
    (shelf / "unstamped.zip").write_bytes(b"")
    (meta / "unstamped.zip.json").write_text(json.dumps({}))
    (shelf / "modules" / "half.nupkg").write_bytes(b"xy")
    (meta / "modules").mkdir()
    (meta / "modules" / "half.nupkg.json").write_text(json.dumps({"digest": "", "publication": "P-2-1"}))
    (shelf / "stray.zip").write_bytes(b"not in the manifest")
    manifest = h.work / "shape-manifest"
    manifest.write_text("stamped.zip\td1\nunstamped.zip\td2\nmodules/half.nupkg\td3\n"
                        "missing.zip\td4\nbroken.zip\td5\n")
    (shelf / "broken.zip").write_bytes(b"?")
    env = dict(os.environ, **h.base_env(), MOCK_AZ_SHOW_FAILS="broken.zip")
    r = subprocess.run([sys.executable, str(HELPER), "verify", "--account", ACCOUNT, "--share", SHARE,
                        "--dest", DEST, "--manifest", str(manifest)],
                       capture_output=True, text=True, env=env)
    rows = [l.split("\t") for l in r.stdout.splitlines() if l]
    check("verify answers ONE row per manifest line, in manifest order, and exits 0",
          r.returncode == 0 and [row[0] for row in rows] == [
              f"{DEST}/stamped.zip", f"{DEST}/unstamped.zip", f"{DEST}/modules/half.nupkg",
              f"{DEST}/missing.zip", f"{DEST}/broken.zip"],
          f"rc={r.returncode}, rows={[row[0] for row in rows]}")
    check("every row has exactly FIVE tab-separated fields",
          bool(rows) and all(len(row) == 5 for row in rows),
          f"field counts {[len(row) for row in rows]}")
    by_name = {row[0].rsplit("/", 1)[-1]: row for row in rows}
    check("a stamped file reads ok with both stamps and its length",
          by_name.get("stamped.zip") == [f"{DEST}/stamped.zip", "ok", "d1", "P-1-1", "5"],
          repr(by_name.get("stamped.zip")))
    check("an absent stamp renders '-' — never empty, never 'None'",
          by_name.get("unstamped.zip", [None] * 5)[1:4] == ["ok", "-", "-"]
          and by_name.get("half.nupkg", [None] * 5)[1:4] == ["ok", "-", "P-2-1"],
          f"{by_name.get('unstamped.zip')!r} / {by_name.get('half.nupkg')!r}")
    check("a file the listing does not contain is 'absent', not 'no digest'",
          by_name.get("missing.zip") == [f"{DEST}/missing.zip", "absent", "-", "-", "-"],
          repr(by_name.get("missing.zip")))
    check("a file whose read FAILS is 'error', named on stderr, and the sweep still completes",
          by_name.get("broken.zip") == [f"{DEST}/broken.zip", "error", "-", "-", "-"]
          and "::error::" in r.stderr and "broken.zip" in r.stderr,
          repr(by_name.get("broken.zip")))
    check("the sweep names its cost and its denominator",
          re.search(r"verified 5 file\(s\) under .* in [0-9.]+s: 3 read, 1 absent, 1 unreadable "
                    r"\(5 listed in 2 listing call\(s\)", r.stderr) is not None,
          next((l for l in r.stderr.splitlines() if l.startswith("verified ")), "<no 'verified' line>"))
    check("a file on the shelf that is not in the publication is noted, not read",
          "stray.zip" in r.stderr and not any(row[0].endswith("stray.zip") for row in rows))
    r = subprocess.run([sys.executable, str(HELPER), "stamp", "--account", ACCOUNT, "--share", SHARE,
                        "--path", f"{DEST}/stamped.zip"], capture_output=True, text=True, env=env)
    check("stamp answers the same row shape for one file",
          r.returncode == 0 and r.stdout.strip().split("\t") == [f"{DEST}/stamped.zip", "ok", "d1", "P-1-1", "5"],
          repr(r.stdout.strip()))
    # An EMPTY manifest must not verify nothing and exit 0.
    empty = h.work / "empty-manifest"
    empty.write_text("\n")
    r = subprocess.run([sys.executable, str(HELPER), "verify", "--account", ACCOUNT, "--share", SHARE,
                        "--dest", DEST, "--manifest", str(empty)], capture_output=True, text=True, env=env)
    check("an empty manifest is refused, never verified into a green",
          r.returncode != 0 and "EMPTY" in r.stderr, f"rc={r.returncode}")

    # ── `dispose` (#3461 phase 5): the only helper phase that DELETES from a production share, so
    # ── its reach is pinned directly, not only through the script. `fnmatch`'s `*` crosses `/`, so a
    # ── pattern matched against a full relative path would claim a LIVE generation's bytes.
    h.reset()
    gen_dir = shelf / "Systemorph-MeshWeaver-1-1"
    (gen_dir / "modules").mkdir(parents=True)
    (shelf / "modules").mkdir(parents=True)
    for p in (shelf / "_complete", shelf / "_current", shelf / "Store.zip", shelf / "modules" / "A.module.nupkg",
              gen_dir / "_complete", gen_dir / "Store.zip", gen_dir / "modules" / "A.module.nupkg"):
        p.write_text("x")
    r = subprocess.run([sys.executable, str(HELPER), "dispose", "--account", ACCOUNT, "--share", SHARE,
                        "--dest", DEST, "--keep", "_current", "--match", "*.zip",
                        "--match", "modules/*.module.nupkg", "--match", "_current"],
                       capture_output=True, text=True, env=env)
    check("dispose removes the prefix's seal and its recognised files, and exits 0",
          r.returncode == 0 and not (shelf / "_complete").exists() and not (shelf / "Store.zip").exists()
          and not (shelf / "modules").exists(),
          f"rc={r.returncode}: {r.stderr.strip().splitlines()[-1:]}")
    check("…a pattern never reaches INTO a generation directory, and --keep wins over a pattern",
          (gen_dir / "_complete").is_file() and (gen_dir / "Store.zip").is_file()
          and (gen_dir / "modules" / "A.module.nupkg").is_file() and (shelf / "_current").is_file(),
          f"generation now holds {sorted(str(p.relative_to(gen_dir)) for p in gen_dir.rglob('*'))}")
    r = subprocess.run([sys.executable, str(HELPER), "dispose", "--account", ACCOUNT, "--share", SHARE,
                        "--dest", DEST], capture_output=True, text=True, env=env)
    check("a dispose that recognises NOTHING is refused before it unseals anything",
          r.returncode != 0 and "at least one --match" in r.stderr, f"rc={r.returncode}")
    r = subprocess.run([sys.executable, str(HELPER), "dispose", "--account", ACCOUNT, "--share", SHARE,
                        "--dest", DEST, "--match", "a/b/*.zip"], capture_output=True, text=True, env=env)
    check("a pattern two directories deep is refused",
          r.returncode != 0 and "is not '<glob>' or '<dir>/<glob>'" in r.stderr, f"rc={r.returncode}")


def run_cases(script: Path, work: Path, expect_defect: bool) -> None:
    h = Harness(script, work)
    # The pre-fix script has no helper to be faithful about. Every OTHER run asserts it —
    # including `--script` pointed at a copy of the current one.
    if not expect_defect:
        check_helper_shape(script, h)
    core = Bake(work, "core-cd", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")
    sat = Bake(work, "satellite", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")

    # ── CONTROL 1: one publisher, empty shelf. Must seal. ──────────────────────────────────────
    print("\ncontrols (a harness whose only cases are failures cannot tell refusing from broken):")
    h.reset()
    r = h.publish(core, "Systemorph/MeshWeaver", "1001")
    s = h.shelf()
    check("a settled publication seals",
          r.returncode == 0 and s.sealed() and len(s.files()) == EXPECTED_FILES,
          f"rc={r.returncode}, {denominator(s)}")
    check("a settled publication holds exactly ONE bake's bytes",
          len(s.files()) > 0 and set(s.bakes_present()) - {"<marker>"} == {"core-cd"},
          denominator(s))

    if not expect_defect:
        check("…and it was one upload process and one read-back process, not one per file",
              r.stderr.count("uploaded ") == 2 and r.stderr.count("verified ") == 1
              and f"uploaded {EXPECTED_FILES} file(s)" in r.stderr and "one process" in r.stderr,
              "one `uploaded N file(s)` line for the publication, one for the sentinel, one sweep")

    # ── CONTROL 1b: the same publication with the DEFAULT pool width (the production shape). ───
    # Every other case runs the pool one wide so the hooks are deterministic; this one exercises
    # the parallel path the fleet actually runs, on a fresh shelf with nothing to interleave.
    h.reset()
    started = time.monotonic()
    r = h.publish(core, "Systemorph/MeshWeaver", "1002", {"PUBLISH_BAKE_UPLOAD_WORKERS": None})
    s = h.shelf()
    if not expect_defect:
        check("a settled publication with the default (parallel) pool seals and verifies every file",
              r.returncode == 0 and s.sealed() and len(s.files()) == EXPECTED_FILES
              and f"{EXPECTED_FILES}/{EXPECTED_FILES} file(s) hold this run's bytes" in r.stdout
              and "12 worker(s)" in r.stderr,
              f"rc={r.returncode}, {denominator(s)}, {time.monotonic() - started:.1f}s")

    # ── CONTROL 2: a second publication of DIFFERENT content reseals cleanly. ──────────────────
    h.reset()
    r = h.publish(core, "Systemorph/MeshWeaver", "1001")
    r = h.publish(sat, "Systemorph/MeshWeaver.Plugins", "2001")
    s = h.shelf()
    check("a republication of new content reseals",
          r.returncode == 0 and s.sealed() and set(s.bakes_present()) - {"<marker>"} == {"satellite"},
          f"rc={r.returncode}, {denominator(s)}")

    # ── CONTROL 3: the same content again is SKIPPED, and writes nothing. ─────────────────────
    r = h.publish(sat, "Systemorph/MeshWeaver.Plugins", "2002")
    s = h.shelf()
    check("the same content is skipped, not republished",
          r.returncode == 0 and s.sealed() and "already published; skipping" in r.stdout,
          f"rc={r.returncode}, {denominator(s)}")
    # 🚨 …AND THE RECEIPT SAYS SO (#4247). The skip above used to record NO outcome, so the run's
    # final receipt read `targets-published=0 targets-converged=0` — byte-identical to a
    # publication that reached NOTHING. Both readers got it wrong on main-cd #8457/#8459: a human
    # filed two release markers written for "a publication that reached ZERO targets" (the log
    # says twice "holds a COMPLETE publication of THIS content … already published; skipping"),
    # and `resolve-platform.py --verify-source`, which refuses a receipt whose
    # published+converged is zero, passed both sealed sets over as unattributable. `already` is
    # the word that separates "the set is live at this target" from "this reached no target".
    check("…and the receipt COUNTS it as already-published, not as a publication that reached nothing",
          "targets-published=0 targets-converged=0 targets-already=1" in r.stdout,
          "receipt: " + (next((line for line in r.stdout.splitlines() if line.startswith("bake published:")),
                              "<no final receipt>")))

    # ── THE SECOND `already` BRANCH: a producer that gives NO content sha. ─────────────────────
    # 🚨 Copilot on #4335, and it was right: every case above publishes WITH a source sha
    # (`h.publish` passes `bake.source_sha`), so the framework-producer branch — where the
    # framework identity IS the content key, so any sealed directory is already this publication —
    # writes the new outcome with nothing exercising it. Two production paths, two cases, or the
    # uncovered one regresses to the old uncounted skip unnoticed.
    h.reset()
    sourceless = Bake(work, "framework-identity", "")
    r = h.publish(sourceless, "Systemorph/MeshWeaver", "4001")
    s = h.shelf()
    check("a source-less (framework-identity) publication seals",
          r.returncode == 0 and s.sealed(), f"rc={r.returncode}, {denominator(s)}")
    r = h.publish(sourceless, "Systemorph/MeshWeaver", "4002")
    check("…and the same one again is skipped as already published",
          r.returncode == 0 and "surface unchanged, bake already published; skipping" in r.stdout,
          f"rc={r.returncode}")
    check("…and THAT branch counts it too — the receipt is not silent about the source-less skip",
          "targets-already=1" in r.stdout,
          "receipt: " + (next((line for line in r.stdout.splitlines() if line.startswith("bake published:")),
                              "<no final receipt>")))

    # ── AND THE THIRD SKIP, which is NOT `already`: the decision-time never-seal-backwards. ────
    # The target holds a SEALED publication from a commit that is NEWER and contains this one, so
    # this content is not what it serves — `superseded`, never `already`. (The pointer-time repeat
    # of the same question is covered further down, under the generation layout; this is the one
    # taken before a single byte is uploaded.)
    h.reset()
    older_seal = Bake(work, "older-seal", "a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1")
    newer_seal = Bake(work, "newer-seal", "b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2")
    r = h.publish(newer_seal, "Systemorph/MeshWeaver", "4101")
    check("the newer content sealed first (the case is not vacuous)",
          r.returncode == 0 and h.shelf().sealed(), f"rc={r.returncode}, {denominator(h.shelf())}")
    r = h.publish(older_seal, "Systemorph/MeshWeaver", "4102", {
        "BAKE_CONTENT_REPOSITORY": "Systemorph/MeshWeaver.Plugins",
        "GH_TOKEN": "stub", "MOCK_GH_AHEAD": newer_seal.source_sha,
    })
    s = h.shelf()
    check("a target sealed on NEWER content is left alone, and the run still succeeds",
          r.returncode == 0 and "Not sealing backwards; skipping" in r.stdout
          and set(s.bakes_present()) - {"<marker>"} == {"newer-seal"},
          f"rc={r.returncode}, {denominator(s)}")
    check("…and it is counted as SUPERSEDED, never as already-published — the set is NOT live there",
          "targets-superseded=1" in r.stdout and "targets-already=0" in r.stdout,
          "receipt: " + (next((line for line in r.stdout.splitlines() if line.startswith("bake published:")),
                              "<no final receipt>")))

    # ── AN OWN-EMPTY MODULE SET is sealed when the workflow SAYS so, and refused when it does not. ──
    # MeshWeaver#3732: a downstream composes its upstream's modules for the compile surface and
    # seals none of them, so its bake stages ZERO modules while EXT_MODULES_DIR is set. The
    # workflow that knows the split exports SEAL_MODULES_OWN=0 and the script seals an EMPTY
    # modules/_index; the same shape WITHOUT that export is the pre-#2707 workflow skew and stays
    # refused — both directions, on the same bake.
    h.reset()
    downstream = Bake(work, "downstream", "d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0")
    for m in list((downstream.dir / "modules").iterdir()):
        m.unlink()
    ext = work / "ext-modules-composed"
    (ext / "MeshWeaver.AI").mkdir(parents=True, exist_ok=True)
    r = h.publish(downstream, "Systemorph/MeshWeaver.Crm", "3001",
                  {"EXT_MODULES_DIR": str(ext), "SEAL_MODULES_OWN": "0", "EXT_MODULES_UPSTREAM": "1"})
    s = h.shelf()
    index = s.dest / "modules" / "_index"
    check("a bake that composed only UPSTREAM modules (SEAL_MODULES_OWN=0) seals an EMPTY modules/_index",
          r.returncode == 0 and s.sealed() and index.is_file() and index.read_text() == ""
          and "sealing an EMPTY modules/_index by design (MeshWeaver#3732)" in r.stdout,
          f"rc={r.returncode}, sealed={s.sealed()}, index={(index.read_text() if index.is_file() else 'absent')!r}")
    h.reset()
    r = h.publish(downstream, "Systemorph/MeshWeaver.Crm", "3002",
                  {"EXT_MODULES_DIR": str(ext), "SEAL_MODULES_OWN": None, "EXT_MODULES_UPSTREAM": None})
    s = h.shelf()
    check("…and the same bake from a workflow that does NOT export the own count is still refused (skew)",
          r.returncode != 0 and not s.sealed()
          and "Refusing to seal an empty module set that would claim completeness" in r.stdout + r.stderr,
          f"rc={r.returncode}, sealed={s.sealed()}")

    # ── THE PLATFORM SURFACE rides the publication, and its absence is loud, not fatal. #3651 ──
    #
    # The release gate links a landed module against platform-surface.json to answer "would it load
    # on the target"; a bake from an image that predates #3651 writes none. The script must publish
    # the file when the bake carries it (verified like every other file), and must still SEAL when
    # it does not — a satellite on an older tester pin must not lose its publication over a check
    # its consumer already reports as undetermined. Both arms are asserted so neither can rot into
    # "always skipped" or "always required".
    print("\nthe platform surface is published when the bake carries it, and its absence warns:")
    h.reset()
    r = h.publish(core, "Systemorph/MeshWeaver", "1801")
    s = h.shelf()
    if expect_defect:
        check("PRE-FIX: the surface is not published at all",
              SURFACE not in s.files(), denominator(s))
    else:
        check("a bake carrying the surface publishes it beside _complete",
              r.returncode == 0 and s.sealed() and SURFACE in s.files()
              and s.files()[SURFACE].startswith(f"{SURFACE} produced by bake core-cd"),
              denominator(s))
        check("the surface is verified with the rest of the publication",
              f"{EXPECTED_FILES}/{EXPECTED_FILES} file(s) hold this run's bytes" in r.stdout,
              "it is in the manifest, so it is read back before the seal")
    h.reset()
    legacy = Bake(work, "legacy-image", "dddddddddddddddddddddddddddddddddddddddd", surface=False)
    r = h.publish(legacy, "Systemorph/MeshWeaver.Plugins", "1802")
    s = h.shelf()
    if expect_defect:
        check("PRE-FIX: a bake without a surface seals identically",
              r.returncode == 0 and s.sealed(), denominator(s))
    else:
        check("a bake WITHOUT a surface still seals — one file fewer, never a refusal",
              r.returncode == 0 and s.sealed() and SURFACE not in s.files()
              and len(s.files()) == EXPECTED_FILES - 1,
              denominator(s))
        check("…and says so, naming what the gate will not be able to measure",
              "::warning::" in r.stdout and SURFACE in r.stdout and "MeshWeaver#3651" in r.stdout,
              "the absence is loud, not silent")
        check("the verification denominator shrinks with it",
              f"{EXPECTED_FILES - 1}/{EXPECTED_FILES - 1} file(s) hold this run's bytes" in r.stdout,
              "expected/verified both printed for the smaller publication")

    # ── ATTRIBUTION: repository.txt records the CONTENT repository, not the LANE. #3583 ────────
    #
    # This is the marker SealedSyncGate.BelongsTo reads, and it takes the marker branch whenever the
    # marker is non-empty — it never falls back to commit attribution. So a `plugins` seal stamped
    # with core CD's own repository name is not attributable to MeshWeaver.Plugins at all: the
    # gate's `mine` list comes back empty and it returns Go, inert for the one repository it exists
    # to hold. The two cases are one variable apart: same lane, same bake; only whose CONTENT it is
    # differs.
    print("\nattribution — the repository marker names whose CONTENT was baked (#3583):")
    h.reset()
    r = h.publish(core, "Systemorph/MeshWeaver", "1301",
                  {"BAKE_CONTENT_REPOSITORY": "Systemorph/MeshWeaver.Plugins"})
    marker = h.shelf().files().get("repository.txt", "").strip()
    check("the case is not vacuous — a marker was written at all",
          r.returncode == 0 and marker != "", f"rc={r.returncode}, repository.txt={marker!r}")
    if expect_defect:
        check("PRE-FIX: a bake of another repository's content is stamped with the LANE",
              marker == "Systemorph/MeshWeaver", f"repository.txt={marker!r}")
    else:
        check("a bake of another repository's content records THAT repository",
              marker == "Systemorph/MeshWeaver.Plugins", f"repository.txt={marker!r}")

    # The control for it: a lane baking its OWN content still records itself, so the fix cannot be
    # "always write something else".
    h.reset()
    r = h.publish(core, "Systemorph/MeshWeaver", "1302")
    marker = h.shelf().files().get("repository.txt", "").strip()
    check("a bake of the lane's own content records the lane",
          r.returncode == 0 and marker == "Systemorph/MeshWeaver",
          f"rc={r.returncode}, repository.txt={marker!r}")

    # ── OVERLAP A: the other lane dies mid-publication, having overwritten part of ours. ───────
    print("\noverlap — the other lane is STILL IN FLIGHT when we seal:")
    h.reset()
    counter = work / "counter-inflight"
    inner = h.publish_command(
        sat, "Systemorph/MeshWeaver.Plugins", "2101",
        {"MOCK_AZ_UPLOAD_COUNTER": counter, "MOCK_AZ_FAIL_UPLOADS_AFTER": 2},
    )
    r = h.publish(core, "Systemorph/MeshWeaver", "1101", {
        "MOCK_AZ_HOOK_ON": f"{DEST}/{BUNDLES[-1]}",
        "MOCK_AZ_HOOK_WHEN": "before",
        "MOCK_AZ_HOOK_ONCE": work / "fired-inflight",
        "MOCK_AZ_HOOK_CMD": inner,
    })
    s = h.shelf()
    mixed = len(set(s.bakes_present()) - {"<marker>"}) > 1
    check("the fixture really did produce a two-bake directory (the case is not vacuous)",
          mixed, denominator(s))
    if expect_defect:
        check("PRE-FIX: a mix is SEALED", s.sealed_mix(), denominator(s))
    else:
        check("a mix is never sealed", not s.sealed_mix(), denominator(s))
        check("the publisher refuses, naming the mix",
              r.returncode != 0 and "holds a MIX of two publications" in r.stdout,
              f"rc={r.returncode}")
        check("the refusal names the overlapping publication",
              "Systemorph-MeshWeaver.Plugins-2101" in r.stdout,
              "the other lane's run is identified by name")

    # ── OVERLAP B: the other lane finishes and SEALS, then we keep overwriting. ────────────────
    print("\noverlap — the other lane COMPLETES inside a gap in our uploads:")
    h.reset()
    inner = h.publish_command(sat, "Systemorph/MeshWeaver.Plugins", "2201")
    r = h.publish(core, "Systemorph/MeshWeaver", "1201", {
        "MOCK_AZ_HOOK_ON": f"{DEST}/{BUNDLES[1]}",
        "MOCK_AZ_HOOK_WHEN": "before",
        "MOCK_AZ_HOOK_ONCE": work / "fired-complete",
        "MOCK_AZ_HOOK_CMD": inner,
    })
    s = h.shelf()
    check("the fixture really did produce a two-bake directory (the case is not vacuous)",
          len(set(s.bakes_present()) - {"<marker>"}) > 1, denominator(s))
    if expect_defect:
        check("PRE-FIX: a mix is SEALED", s.sealed_mix(), denominator(s))
    else:
        check("a seal written by the other lane over a directory we then overwrote is REMOVED",
              not s.sealed_mix() and not s.sealed(), denominator(s))
        check("the publisher refuses and says the sentinel was removed",
              r.returncode != 0 and f"removed {DEST}/{SENTINEL}" in r.stdout,
              f"rc={r.returncode}")

    # ── OVERLAP C: we are ENTIRELY superseded. The other publication must be left alone. ───────
    print("\noverlap — we are entirely superseded (their publication is whole; leave it):")
    h.reset()
    inner = h.publish_command(sat, "Systemorph/MeshWeaver.Plugins", "2301")
    r = h.publish(core, "Systemorph/MeshWeaver", "1301", {
        "MOCK_AZ_HOOK_ON": f"{DEST}/architecture.txt",
        "MOCK_AZ_HOOK_WHEN": "after",
        "MOCK_AZ_HOOK_ONCE": work / "fired-superseded",
        "MOCK_AZ_HOOK_CMD": inner,
    })
    s = h.shelf()
    only_theirs = set(s.bakes_present()) - {"<marker>"} == {"satellite"}
    check("the fixture really did leave the other lane's publication whole (not vacuous)",
          only_theirs and len(s.files()) == EXPECTED_FILES, denominator(s))
    if expect_defect:
        check("PRE-FIX: the superseded run seals its own sentinel over their bytes",
              s.sealed() and r.returncode == 0,
              f"rc={r.returncode}, {denominator(s)}")
    else:
        check("a whole foreign publication keeps its seal",
              s.sealed() and only_theirs, denominator(s))
        check("the superseded publisher goes RED rather than claiming it published",
              r.returncode != 0 and "entirely superseded" in r.stdout,
              f"rc={r.returncode}")
        check("…and says the shelf describes DIFFERENT content, so it did not converge",
              "describes DIFFERENT content" in r.stdout,
              "a different source sha is what separates this from the convergence case below")

    # ── CONVERGENCE: superseded by a publication of THE SAME CONTENT. #3461, 2026-09-08 ───────
    #
    # The shape that actually bit, and the one the postcondition used to call a failure. Core CD
    # runs 34205409381 and 34206854855 both published `plugins` at source cfac152ef… for identity
    # s057b1e77…; each won one of the two storage targets and each went RED on the other, with the
    # same verdict — "40 of 45 file(s) were overwritten … the remaining 5 are byte-identical".
    # The 40 are bundle zips and module packages (the compile is not reproducible byte-for-byte);
    # the 5 are the markers, the module index and the platform surface, byte-identical BECAUSE it
    # is the same content. Both targets ended sealed with exactly the right bytes, and both CD runs
    # failed — a false red that produced no sealed set and blocked the platform pin behind it.
    #
    # The publication key the sealed-skip uses is CONTENT × FRAMEWORK, so a sibling that published
    # this content and sealed it has made the publication this run was asked for. Reporting that is
    # the same statement the skip makes a minute earlier; refusing was the two ends of one script
    # disagreeing.
    print("\nconvergence — a sibling publishes THE SAME CONTENT and seals it while we are in flight:")
    same_sha = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"
    mine = Bake(work, "run-a", same_sha, surface_shared=True)
    sibling = Bake(work, "run-b", same_sha, surface_shared=True)
    h.reset()
    inner = h.publish_command(sibling, "Systemorph/MeshWeaver", "1902")
    r = h.publish(mine, "Systemorph/MeshWeaver", "1901", {
        "MOCK_AZ_HOOK_ON": f"{DEST}/architecture.txt",
        "MOCK_AZ_HOOK_WHEN": "after",
        "MOCK_AZ_HOOK_ONCE": work / "fired-converged",
        "MOCK_AZ_HOOK_CMD": inner,
    })
    s = h.shelf()
    theirs_only = set(s.bakes_present()) - {"<marker>"} == {"run-b"}
    check("the fixture really did leave the sibling's publication whole (not vacuous)",
          theirs_only and len(s.files()) == EXPECTED_FILES and s.sealed(), denominator(s))
    check("…and this run really did publish first, so it had bytes to lose (not vacuous)",
          f"publication Systemorph-MeshWeaver-1901-1: {EXPECTED_FILES} file(s)" in r.stdout
          and f"→ {TARGET}: {DEST}" in r.stdout,
          "it uploaded a full publication before the sibling overwrote it")
    if expect_defect:
        check("PRE-FIX: the superseded run seals its own sentinel over their bytes",
              s.sealed() and r.returncode == 0, f"rc={r.returncode}, {denominator(s)}")
    else:
        check("a run superseded by an equivalent publication SUCCEEDS",
              r.returncode == 0 and "Superseded by an equivalent publication" in r.stdout,
              f"rc={r.returncode}")
        check("…naming the publication that made it",
              "Systemorph-MeshWeaver-1902-1" in r.stdout)
        check("…and counting it apart from a seal this run did not write",
              "targets-published=0 targets-converged=1" in r.stdout,
              "the summary never claims a publication it did not make")
        check("the sibling's seal is left exactly as it is",
              s.stamp(SENTINEL).get("publication") == "Systemorph-MeshWeaver-1902-1",
              f"_complete is stamped {s.stamp(SENTINEL).get('publication')!r}")
        check("nothing of this run's is on the shelf under their seal",
              theirs_only, denominator(s))

    # The SAME fixture, one variable apart: the sibling never seals. Convergence is a positive
    # proof that the publication EXISTS, so an unsealed directory must stay RED — a run reporting
    # a publication that is not there is the silent-nothing outcome, whoever would have made it.
    print("\n…but an unsealed sibling is not a publication:")
    h.reset()
    counter = work / "counter-unsealed"
    inner = h.publish_command(sibling, "Systemorph/MeshWeaver", "1912",
                              {"MOCK_AZ_UPLOAD_COUNTER": counter,
                               "MOCK_AZ_FAIL_UPLOADS_AFTER": EXPECTED_FILES})
    r = h.publish(mine, "Systemorph/MeshWeaver", "1911", {
        "MOCK_AZ_HOOK_ON": f"{DEST}/architecture.txt",
        "MOCK_AZ_HOOK_WHEN": "after",
        "MOCK_AZ_HOOK_ONCE": work / "fired-unsealed",
        "MOCK_AZ_HOOK_CMD": inner,
    })
    s = h.shelf()
    check("the fixture really did overwrite everything without sealing (not vacuous)",
          set(s.bakes_present()) - {"<marker>"} == {"run-b"} and not s.sealed(), denominator(s))
    if not expect_defect:
        check("an unsealed sibling does NOT converge — the run stays red",
              r.returncode != 0 and "entirely superseded" in r.stdout
              and "has not sealed" in r.stdout, f"rc={r.returncode}")

    # And one variable the other way: the sibling seals the same CONTENT but a different bundle
    # SET. `source-commit.txt` cannot see that, so the sealed listing's digest is what does — the
    # reason the check reads `_complete` rather than trusting the content marker alone.
    print("\n…and an equivalent-looking sibling with a DIFFERENT bundle set is refused:")
    h.reset()
    bigger = Bake(work, "run-c", same_sha, surface_shared=True, extra_bundles=("Extra.zip",))
    inner = h.publish_command(bigger, "Systemorph/MeshWeaver", "1922")
    r = h.publish(mine, "Systemorph/MeshWeaver", "1921", {
        "MOCK_AZ_HOOK_ON": f"{DEST}/architecture.txt",
        "MOCK_AZ_HOOK_WHEN": "after",
        "MOCK_AZ_HOOK_ONCE": work / "fired-widerset",
        "MOCK_AZ_HOOK_CMD": inner,
    })
    s = h.shelf()
    check("the fixture really did seal a WIDER set (not vacuous)",
          s.sealed() and len(s.files()) == EXPECTED_FILES + 1, denominator(s))
    if not expect_defect:
        check("a sibling sealing a different bundle set does NOT converge",
              r.returncode != 0 and "entirely superseded" in r.stdout
              and "lists a different bundle set" in r.stdout, f"rc={r.returncode}")

    # ── THE GENERATION LAYOUT: two interleaved publishers cannot write one directory. #3461 ───
    #
    # Phase 2 of Doc/Architecture/SealedPublicationGenerations. The postcondition above turns an
    # overlap from a silent mix into a loud refusal; it is a postcondition, not mutual exclusion,
    # and it leaves the interval between its last read and the seal. This layout removes the shared
    # directory instead: each publication is written under its own run-unique token, so there is
    # nothing to interleave, and a one-line `_current` pointer says which one applies.
    #
    # 🚨 The selector defaults to `flat`, and the three CONTROL cases at the top of this file are
    # the regression suite for that: they run the same script with no BAKE_PUBLICATION_LAYOUT set
    # and must keep passing byte for byte. These cases opt in explicitly.
    gen = {"BAKE_PUBLICATION_LAYOUT": "generation"}

    print("\ngeneration layout — one publisher, on an empty prefix:")
    h.reset()
    r = h.publish(core, "Systemorph/MeshWeaver", "3001", gen)
    s = h.shelf()
    tok_a = "Systemorph-MeshWeaver-3001-1"
    ga = s.under(tok_a)
    if expect_defect:
        check("PRE-FIX: the layout selector does not exist, so nothing is written under a token",
              s.generations() == [], f"generations={s.generations()}")
    else:
        check("the publication is written into its own generation directory, and sealed there",
              r.returncode == 0 and ga.sealed() and len(ga.files()) == EXPECTED_FILES,
              f"rc={r.returncode}, generation {tok_a}: {denominator(ga)}")
        check("the pointer names it",
              s.pointer() == tok_a, f"_current={s.pointer()!r}")
        # 🚨 PHASE 5 (#3461): no flat compatibility copy is written any more. Until phase 5 this
        # case asserted the opposite — the copy for pre-pointer readers, refreshed in place.
        check("NO flat compatibility copy is written — the prefix holds the pointer and the generation, nothing else",
              not s.sealed() and s.files() == {} and not (s.dest / "modules").exists(),
              f"flat: {denominator(s)}")
        check("the pointer is NOT stamped — it is written after the postcondition, by construction",
              s.stamp(POINTER) == {}, f"stamp={s.stamp(POINTER)!r}")

    # ── 🚨 PHASE 5 (#3461): THE FLAT COMPATIBILITY COPY IS DISPOSED OF — AFTER THE POINTER, SEAL FIRST.
    # ──
    # ── The prefix below starts as every prefix on the share looked before its first phase-5
    # ── publication: a SEALED flat copy (here published by the flat arm, with no pointer at all — the
    # ── migration case, the hardest one, because until the pointer lands the flat copy is the ONLY
    # ── sealed publication a reader of this prefix has). A generation run then publishes.
    # ──
    # ── Merely ceasing to refresh the copy would leave it sealed and complete while `_current` moves
    # ── on, so every torn pointer read would be served a publication frozen at that moment. What is
    # ── asserted is the approved disposal: the pointer lands, THEN `_complete` goes, THEN the files —
    # ── and the fake backend records the reader's view at the instant before EVERY delete, so "a
    # ── reader is never left with neither" is read off the log, not off the script's own lines.
    print("\nphase 5 — a generation publication disposes of the flat copy it makes obsolete:")
    h.reset()
    r = h.publish(core, "Systemorph/MeshWeaver", "3500")              # flat — no pointer yet
    s = h.shelf()
    check("the fixture really did start from a SEALED flat copy with no pointer (not vacuous)",
          r.returncode == 0 and s.sealed() and len(s.files()) == EXPECTED_FILES and s.pointer() == "",
          f"rc={r.returncode}, {denominator(s)}, _current={s.pointer()!r}")
    (s.dest / "operator-notes.txt").write_text("not part of any publication\n")
    deletes = work / "deletes-migration.log"
    tok_p5 = "Systemorph-MeshWeaver.Plugins-3501-1"
    r = h.publish(sat, "Systemorph/MeshWeaver.Plugins", "3501", {
        "MOCK_AZ_DELETE_LOG": deletes, "MOCK_AZ_DISPOSAL_PREFIX": DEST, **gen,
    })
    s = h.shelf()
    rows = [l.split("\t") for l in deletes.read_text().splitlines()] if deletes.is_file() else []
    if not expect_defect:
        check("the generation publication succeeds, sealed and pointed at",
              r.returncode == 0 and s.pointer() == tok_p5 and s.under(tok_p5).sealed()
              and len(s.under(tok_p5).files()) == EXPECTED_FILES
              and set(s.under(tok_p5).bakes_present()) - {"<marker>"} == {"satellite"},
              f"rc={r.returncode}, _current={s.pointer()!r}: {denominator(s.under(tok_p5))}")
        check("the flat copy is GONE: no seal, no bundle, no module, no marker, no modules/ directory",
              not s.sealed() and set(s.files()) == {"operator-notes.txt"}
              and not (s.dest / "modules").exists(),
              f"flat: {denominator(s)}, files={sorted(s.files())}")
        check("…a file that is not part of the flat publication is LEFT, and named",
              (s.dest / "operator-notes.txt").is_file()
              and "1 unrecognised file(s) left in place: operator-notes.txt" in r.stderr,
              "positive identification: the worst case of a pattern that is too narrow is bytes that stay")
        check("…and the pointer itself is never deleted",
              (s.dest / POINTER).is_file() and not any(row[0].endswith("/" + POINTER) for row in rows),
              f"deleted: {[row[0] for row in rows]}")
        check("the fixture really did record every delete (not vacuous)",
              len(rows) == EXPECTED_FILES + 2,
              f"{len(rows)} delete(s) logged, wanted the seal + {EXPECTED_FILES} file(s) + the "
              f"post-disposal re-read of the seal (the legacy-writer postcondition)")
        check("_complete is the FIRST thing deleted, and ALONE — no other file goes while the flat copy is sealed",
              bool(rows) and rows[0][0] == f"{DEST}/{SENTINEL}"
              and all(row[3] == "0" for row in rows[1:]),
              f"delete order: {[row[0].rsplit('/', 2)[-1] + ('(sealed)' if row[3] == '1' else '') for row in rows]}")
        check("the pointer had already LANDED on a sealed generation before the first delete",
              bool(rows) and rows[0][1] == tok_p5 and rows[0][2] == "1" and rows[0][3] == "1",
              f"at the seal's delete: _current={rows[0][1] if rows else '?'}, generation sealed="
              f"{rows[0][2] if rows else '?'}, flat sealed={rows[0][3] if rows else '?'}")
        check("a reader is NEVER left with neither — at every delete a sealed generation is named",
              bool(rows) and all(row[2] == "1" or row[3] == "1" for row in rows),
              f"{sum(1 for row in rows if row[2] != '1' and row[3] != '1')} of {len(rows)} delete(s) left a reader with neither")
        check("…and the log says what it disposed of, with its denominator",
              f"{EXPECTED_FILES} of {EXPECTED_FILES} recognised file(s) deleted" in r.stderr
              and "seal removed" in r.stderr,
              next((l for l in r.stderr.splitlines() if l.startswith("disposed of the flat")), "<no summary line>"))
        check("…and the announcement names the order the code runs",
              f"then {POINTER}, then disposing of the flat compatibility copy ({SENTINEL} first)" in r.stdout,
              "the generation-layout line must match what the log below it shows")

    # The CONTROL for the migration case: a prefix that is NOT published again keeps its flat copy.
    # The disposal is incremental and per prefix — no sweep of the share — so an identity nothing
    # publishes any more keeps the copy an old image's reader may still be resolving.
    print("\nphase 5 — a prefix that is not published again keeps its flat copy (no sweep):")
    h.reset()
    h.publish(core, "Systemorph/MeshWeaver", "3510")
    r = h.publish(core, "Systemorph/MeshWeaver", "3511", gen)         # the SAME content ⇒ a skip
    s = h.shelf()
    check("the fixture really did skip (not vacuous)",
          r.returncode == 0 and "already published; skipping" in r.stdout, f"rc={r.returncode}")
    check("a run that publishes nothing disposes of nothing — the flat copy stays sealed and whole",
          s.sealed() and len(s.files()) == EXPECTED_FILES and s.pointer() == "",
          f"flat: {denominator(s)}")

    # ── The seal cannot be removed ⇒ NOTHING is removed, and the target fails. A sealed flat copy that
    # ── is no longer refreshed is exactly the frozen serve phase 5 exists to end, so it must be red.
    print("\nphase 5 — the flat copy's seal cannot be deleted:")
    h.reset()
    h.publish(core, "Systemorph/MeshWeaver", "3520")
    r = h.publish(sat, "Systemorph/MeshWeaver.Plugins", "3521", {
        "MOCK_AZ_DELETE_FAILS": f"{DEST}/{SENTINEL}", **gen})
    s = h.shelf()
    if not expect_defect:
        check("the target FAILS, naming the seal it could not remove",
              r.returncode != 0 and "STILL SEALED" in r.stderr, f"rc={r.returncode}")
        check("…and nothing else was deleted: the flat copy is still sealed and whole",
              s.sealed() and len(s.files()) == EXPECTED_FILES
              and set(s.bakes_present()) - {"<marker>"} == {"core-cd"},
              f"flat: {denominator(s)}")
        check("…while the new publication stays live for every pointer-following reader",
              s.pointer() == "Systemorph-MeshWeaver.Plugins-3521-1"
              and s.under("Systemorph-MeshWeaver.Plugins-3521-1").sealed(),
              f"_current={s.pointer()!r}")

    # ── Once the seal is gone, a file that cannot be deleted is storage, not a publication: named,
    # ── and not fatal. Going red here would fail a publication that is live and a prefix no reader
    # ── serves, over bytes the next publication removes.
    print("\nphase 5 — a flat file that cannot be deleted AFTER the seal went:")
    h.reset()
    h.publish(core, "Systemorph/MeshWeaver", "3530")
    r = h.publish(sat, "Systemorph/MeshWeaver.Plugins", "3531", {
        "MOCK_AZ_DELETE_FAILS": f"{DEST}/{BUNDLES[1]}", **gen})
    s = h.shelf()
    if not expect_defect:
        check("the run succeeds: the prefix is unsealed, so a left-over file serves nobody",
              r.returncode == 0 and not s.sealed() and set(s.files()) == {BUNDLES[1]},
              f"rc={r.returncode}, flat: {denominator(s)}, files={sorted(s.files())}")
        check("…and the left-over is named as a warning, never silently",
              f"::warning::could not delete {TARGET}/{DEST}/{BUNDLES[1]}" in r.stderr
              and "UNSEALED but not fully removed" in r.stderr,
              "a disposal that says nothing about what it left is indistinguishable from a complete one")

    # ── 🚨 THE POINTER DID NOT LAND. The CLI reported SUCCESS for files it never stored on this
    # ── share (39 of 45, 2026-09-08). On the migration prefix the flat copy is then the ONLY sealed
    # ── publication there is: disposing of it on the upload's word alone would leave every reader of
    # ── this prefix with NEITHER. The pointer is read back first, and a pointer that is not there
    # ── disposes of nothing.
    print("\nphase 5 — the pointer upload reports success and stores nothing:")
    h.reset()
    h.publish(core, "Systemorph/MeshWeaver", "3540")
    r = h.publish(sat, "Systemorph/MeshWeaver.Plugins", "3541", {
        "MOCK_AZ_DROP_UPLOAD_OF": POINTER, **gen})
    s = h.shelf()
    check("the fixture really did lose the pointer (not vacuous)",
          s.pointer() == "" and s.under("Systemorph-MeshWeaver.Plugins-3541-1").sealed(),
          f"_current={s.pointer()!r}, generations={s.generations()}")
    if not expect_defect:
        check("the target FAILS, saying the pointer did not land",
              r.returncode != 0 and "The pointer did not land" in r.stdout, f"rc={r.returncode}")
        check("…and the flat copy — the only sealed publication this prefix has — is untouched",
              s.sealed() and len(s.files()) == EXPECTED_FILES
              and set(s.bakes_present()) - {"<marker>"} == {"core-cd"},
              f"flat: {denominator(s)}")

    # ── 🚨 …AND THE POINTER UPLOAD CAN LEAVE THE PREVIOUS POINTER IN PLACE, which is the shape that
    # ── makes "does `_current` name a sealed generation" too weak a read-back (Copilot's review of
    # ── this PR). The share reports SUCCESS for a file it did not store, so the PREVIOUS pointer
    # ── survives — it resolves to the previous generation, sealed and whole. A check that asked only
    # ── "is a generation live" therefore passed, counted this run as published, and deleted the flat
    # ── copy for a publication nobody points at. The read-back compares against THIS run's token.
    print("\nphase 5 — the pointer upload stores nothing and the PREVIOUS pointer survives:")
    h.reset()
    h.publish(core, "Systemorph/MeshWeaver", "3580", gen)        # generation A, pointed at
    tok_a5 = "Systemorph-MeshWeaver-3580-1"
    prefix_dir = h.shelf_root / ACCOUNT / SHARE / DEST
    # A flat copy at the prefix, as a producer still running a pre-phase-5 publisher leaves one.
    for p in sorted((prefix_dir / tok_a5).iterdir()):
        if p.is_file():
            shutil.copy2(p, prefix_dir / p.name)
    (prefix_dir / "modules").mkdir(exist_ok=True)
    for p in sorted((prefix_dir / tok_a5 / "modules").iterdir()):
        shutil.copy2(p, prefix_dir / "modules" / p.name)
    r = h.publish(sat, "Systemorph/MeshWeaver.Plugins", "3581", {
        "MOCK_AZ_DROP_UPLOAD_OF": POINTER, **gen})
    s = h.shelf()
    check("the fixture really did leave the PREVIOUS pointer live (not vacuous)",
          s.pointer() == tok_a5 and s.under(tok_a5).sealed()
          and "Systemorph-MeshWeaver.Plugins-3581-1" in s.generations(),
          f"_current={s.pointer()!r}, generations={s.generations()}")
    if not expect_defect:
        check("the read-back compares against THIS run's generation, so the target FAILS",
              r.returncode != 0 and "The pointer did not land" in r.stdout
              and "Systemorph-MeshWeaver.Plugins-3581-1" in r.stdout,
              f"rc={r.returncode} — a previous generation being live is not this publication being live")
        check("…and the flat copy is untouched: nothing was disposed of for a publication nobody points at",
              s.sealed() and len(s.files()) == EXPECTED_FILES,
              f"flat: {denominator(s)}")

    # ── 🚨 THE ONE INTERLEAVING THE ORDER CANNOT PREVENT: a producer still running a pre-phase-5
    # ── publisher refreshes the flat copy — unseal, upload, verify, seal LAST — and its seal lands
    # ── AFTER this disposal's deletes. The prefix would then be SEALED over a set this sweep has
    # ── emptied: a complete-looking flat publication with files missing, which a torn pointer read
    # ── would be served. There is no lease on this store, so the answer is a POSTCONDITION: read the
    # ── sentinel again after the deletes and remove it, turning that into "being republished".
    print("\nphase 5 — a legacy writer SEALS the flat copy inside the disposal:")
    h.reset()
    h.publish(core, "Systemorph/MeshWeaver", "3590")             # a flat copy to dispose of
    reseal = (f'printf "Chess.zip\\n" > "{h.shelf_root}/{ACCOUNT}/{SHARE}/{DEST}/{SENTINEL}"')
    r = h.publish(sat, "Systemorph/MeshWeaver.Plugins", "3591", {
        "MOCK_AZ_HOOK_ON": f"{DEST}/architecture.txt",
        "MOCK_AZ_HOOK_WHEN": "after",
        "MOCK_AZ_HOOK_ONCE": work / "fired-reseal",
        "MOCK_AZ_HOOK_CMD": reseal,
        **gen})
    s = h.shelf()
    check("the fixture really did re-seal the flat copy mid-disposal (not vacuous)",
          (work / "fired-reseal").is_file(), "the hook fired on the last flat file's delete")
    if not expect_defect:
        check("the re-appeared seal is removed again, so no reader can be served an incomplete set",
              r.returncode == 0 and not s.sealed(),
              f"rc={r.returncode}, flat sealed={s.sealed()} — 'being republished' is the state readers handle")
        check("…and it says so, naming the writer that is still refreshing the copy",
              "was written again WHILE this disposal ran" in r.stderr,
              "a postcondition that fires silently is indistinguishable from one that never fires")

    # ── …and the SIBLING case, which must NOT be red: a newer publication takes the pointer in the
    # ── gap between this run's pointer write and its read-back. Nothing is wrong — that run owns the
    # ── prefix and its own disposal — so this one is `superseded`, exactly as the pre-pointer check
    # ── reports it, and it disposes of nothing.
    print("\nphase 5 — a NEWER publication takes the pointer between the write and the read-back:")
    h.reset()
    older5 = Bake(work, "older-5", "e5e5e5e5e5e5e5e5e5e5e5e5e5e5e5e5e5e5e5e5")
    newer5 = Bake(work, "newer-5", "f6f6f6f6f6f6f6f6f6f6f6f6f6f6f6f6f6f6f6f6")
    # 🚨 A flat copy of THIRD-PARTY content: publishing `older5` here would make the run under test
    # skip as already-published, and the case would pass having exercised nothing.
    h.publish(core, "Systemorph/MeshWeaver", "3595")             # a flat copy at the prefix
    inner = h.publish_command(newer5, "Systemorph/MeshWeaver", "3597", {
        **gen, "BAKE_CONTENT_REPOSITORY": "Systemorph/MeshWeaver.Plugins",
        "GH_TOKEN": "stub", "MOCK_GH_AHEAD": newer5.source_sha})
    r = h.publish(older5, "Systemorph/MeshWeaver", "3596", {
        "MOCK_AZ_AFTER_UPLOAD_OF": POINTER,
        "MOCK_AZ_AFTER_UPLOAD_CMD": inner,
        "BAKE_CONTENT_REPOSITORY": "Systemorph/MeshWeaver.Plugins",
        "GH_TOKEN": "stub", "MOCK_GH_AHEAD": newer5.source_sha,
        **gen})
    s = h.shelf()
    check("the fixture really did hand the pointer to the newer publication (not vacuous)",
          s.pointer() == "Systemorph-MeshWeaver-3597-1" and inner_run_succeeded(work, "3597"),
          f"_current={s.pointer()!r}, inner receipt: {inner_receipt(work, '3597')!r}")
    if not expect_defect:
        check("the run SUCCEEDS as superseded — a sibling winning the pointer is not this run failing",
              r.returncode == 0 and "A newer publication took the pointer" in r.stdout
              and "targets-superseded=1" in r.stdout,
              f"rc={r.returncode}")
        check("…and it disposes of nothing: the prefix belongs to the run that owns the pointer",
              "disposing of the flat compatibility copy beside it" not in r.stdout,
              "the loser of a pointer race must not delete bytes the winner is responsible for")

    # ── 🚨 A FAILED pointer upload must stop the target where it fails. Until this change the
    # ── per-target subshell ran as an `if` CONDITION, where bash suspends `set -e` for the whole body:
    # ── the failed upload was followed by "this publication is now the live one".
    print("\nphase 5 — the pointer upload FAILS:")
    h.reset()
    h.publish(core, "Systemorph/MeshWeaver", "3550")
    r = h.publish(sat, "Systemorph/MeshWeaver.Plugins", "3551", {
        "MOCK_AZ_FAIL_UPLOAD_OF": POINTER, **gen})
    s = h.shelf()
    check("the fixture really did fail the pointer upload (not vacuous)",
          "simulated upload failure" in r.stderr and s.pointer() == "", f"_current={s.pointer()!r}")
    if not expect_defect:
        check("the target stops AT the failure — it never claims the publication is live",
              r.returncode != 0 and "now the live one" not in r.stdout
              and "disposing of the flat compatibility copy beside it" not in r.stdout,
              f"rc={r.returncode}")
        check("…and the flat copy is untouched",
              s.sealed() and len(s.files()) == EXPECTED_FILES, f"flat: {denominator(s)}")

    # ── The same suspended `set -e`, on the FLAT arm's seal: a failed `_complete` upload printed
    # ── "sealed:" and was counted as a publication. Asserted on the receipt as well as the exit code.
    print("\nthe seal upload FAILS (flat arm):")
    h.reset()
    r = h.publish(core, "Systemorph/MeshWeaver", "3560", {
        "MOCK_AZ_FAIL_UPLOAD_PATH": f"{DEST}/{SENTINEL}"})
    s = h.shelf()
    check("the fixture really did fail the seal upload (not vacuous)",
          "simulated upload failure" in r.stderr and not s.sealed(), f"sealed={s.sealed()}")
    if not expect_defect:
        check("the target FAILS and never prints 'sealed:' for a seal that is not there",
              r.returncode != 0 and f"sealed: {TARGET}/{DEST}/{SENTINEL}" not in r.stdout,
              f"rc={r.returncode}")

    # …and on the generation arm, where the next step would have moved the pointer to it.
    print("\nthe seal upload FAILS (generation arm):")
    h.reset()
    h.publish(core, "Systemorph/MeshWeaver", "3570")
    tok_unsealed = "Systemorph-MeshWeaver.Plugins-3571-1"
    r = h.publish(sat, "Systemorph/MeshWeaver.Plugins", "3571", {
        "MOCK_AZ_FAIL_UPLOAD_PATH": f"{DEST}/{tok_unsealed}/{SENTINEL}", **gen})
    s = h.shelf()
    check("the fixture really did leave the generation unsealed (not vacuous)",
          tok_unsealed in s.generations() and not s.under(tok_unsealed).sealed(),
          f"generations={s.generations()}")
    if not expect_defect:
        check("an unsealed generation is never pointed at, and the flat copy is untouched",
              r.returncode != 0 and s.pointer() == "" and s.sealed()
              and len(s.files()) == EXPECTED_FILES,
              f"rc={r.returncode}, _current={s.pointer()!r}, flat: {denominator(s)}")

    # ── THE HEADLINE: interleave two publishers and neither directory can hold the other's bytes.
    print("\ngeneration layout — two publishers interleaved on one prefix:")
    h.reset()
    inner = h.publish_command(sat, "Systemorph/MeshWeaver.Plugins", "3102", gen)
    r = h.publish(core, "Systemorph/MeshWeaver", "3101", {
        "MOCK_AZ_HOOK_ON": f"{DEST}/Systemorph-MeshWeaver-3101-1/{BUNDLES[1]}",
        "MOCK_AZ_HOOK_WHEN": "before",
        "MOCK_AZ_HOOK_ONCE": work / "fired-gen-overlap",
        "MOCK_AZ_HOOK_CMD": inner,
        **gen,
    })
    s = h.shelf()
    tok_core, tok_sat = "Systemorph-MeshWeaver-3101-1", "Systemorph-MeshWeaver.Plugins-3102-1"
    gc, gs = s.under(tok_core), s.under(tok_sat)
    check("the fixture really did interleave them (both generations exist — not vacuous)",
          sorted(s.generations()) == sorted([tok_core, tok_sat]),
          f"generations={s.generations()}")
    if not expect_defect:
        check("each publisher sealed its OWN generation",
              gc.sealed() and gs.sealed(),
              f"{tok_core}: {denominator(gc)} | {tok_sat}: {denominator(gs)}")
        check("neither generation holds a byte of the other — a mix is UNREPRESENTABLE",
              set(gc.bakes_present()) - {"<marker>"} == {"core-cd"}
              and set(gs.bakes_present()) - {"<marker>"} == {"satellite"},
              f"{tok_core} by bake {gc.bakes_present()}, {tok_sat} by bake {gs.bakes_present()}")
        check("both generations are COMPLETE, so whichever the pointer names is whole",
              len(gc.files()) == EXPECTED_FILES and len(gs.files()) == EXPECTED_FILES,
              f"{len(gc.files())} and {len(gs.files())} of {EXPECTED_FILES}")
        check("the pointer names exactly ONE of them, and it is one that exists",
              s.pointer() in (tok_core, tok_sat), f"_current={s.pointer()!r}")
        # 🚨 Until phase 5 this asserted "the flat copy still races, and is never sealed over a mix":
        # both runs rewrote the flat compatibility copy in place, and the loser went red. Phase 5
        # removed that write, so the two runs share NO directory at all — and both SUCCEED.
        check("nothing is written at the prefix itself any more, so there is nothing left to race",
              not s.sealed() and s.files() == {},
              f"flat: {denominator(s)}")
        check("…and so NEITHER interleaved run goes red — an overlap no longer costs a publication",
              r.returncode == 0 and inner_run_succeeded(work, "3102"),
              f"outer rc={r.returncode}; inner receipt: {inner_receipt(work, '3102')!r}")
        check("…and the generation the pointer names is served whole regardless of what the flat copy holds",
              s.under(s.pointer()).sealed()
              and len(s.under(s.pointer()).files()) == EXPECTED_FILES,
              f"_current={s.pointer()!r}: {denominator(s.under(s.pointer()))}")

    # ── 🚨 AND THE POINTER MUST NOT MOVE BACKWARDS WHEN THE FINISH ORDER INVERTS THE CONTENT ORDER.
    # ── The case above deliberately tolerates either generation winning, because for two runs of
    # ── UNORDERED content either answer is whole and correct. This one is the ordered pair, and it
    # ── is the one thing the generation layout can get silently wrong that the flat layout could
    # ── not: under `flat` an overlap writes ONE directory, so the byte-level postcondition finds
    # ── the other run's bytes and refuses; under `generation` the two write disjoint directories,
    # ── there is no mix to refuse, and whichever run finishes LAST moves `_current` — which, when
    # ── that is the run carrying OLDER content, hands every reader a complete, sealed publication
    # ── of older bytes with nothing red anywhere.
    # ──
    # ── The decision-time never-seal-backwards guard cannot see it: it reads the publication that
    # ── was live ~90 seconds earlier, before the newer run sealed. So the question is asked again
    # ── immediately before the pointer write.
    print("\ngeneration layout — the pointer never moves BACKWARDS (the newer run sealed first):")
    h.reset()
    older = Bake(work, "older-content", "e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1")
    newer = Bake(work, "newer-content", "f2f2f2f2f2f2f2f2f2f2f2f2f2f2f2f2f2f2f2f2")
    tok_old, tok_new = "Systemorph-MeshWeaver-3701-1", "Systemorph-MeshWeaver-3702-1"
    # The NEWER content publishes (and moves the pointer) while the OLDER run is still uploading.
    inner = h.publish_command(newer, "Systemorph/MeshWeaver", "3702", {
        **gen,
        "BAKE_CONTENT_REPOSITORY": "Systemorph/MeshWeaver.Plugins",
        "GH_TOKEN": "stub", "MOCK_GH_AHEAD": newer.source_sha,
    })
    # 🚨 …and then a SEALED FLAT COPY of the newer bytes appears at the prefix — the state a
    # producer still running a pre-phase-5 publisher leaves (a reconcile at an older platform-ref
    # refreshes the copy), i.e. a flat copy ANOTHER run is serving. Without it the newer phase-5
    # run has already disposed of the copy and "the superseded run did not delete it" could not be
    # observed at all — the assertion below would pass on a shelf with nothing to delete.
    prefix_dir = f"{h.shelf_root}/{ACCOUNT}/{SHARE}/{DEST}"
    planted = work / "planted-flat-copy"
    inner = (f'{inner}; cp "{prefix_dir}/{tok_new}"/* "{prefix_dir}"/ 2>/dev/null; '
             f'mkdir -p "{prefix_dir}/modules"; cp "{prefix_dir}/{tok_new}"/modules/* "{prefix_dir}/modules"/; '
             f'if [ -f "{prefix_dir}/{SENTINEL}" ]; then echo planted > "{planted}"; fi')
    r = h.publish(older, "Systemorph/MeshWeaver", "3701", {
        "MOCK_AZ_HOOK_ON": f"{DEST}/{tok_old}/{BUNDLES[1]}",
        "MOCK_AZ_HOOK_WHEN": "before",
        "MOCK_AZ_HOOK_ONCE": work / "fired-backwards",
        "MOCK_AZ_HOOK_CMD": inner,
        "BAKE_CONTENT_REPOSITORY": "Systemorph/MeshWeaver.Plugins",
        "GH_TOKEN": "stub", "MOCK_GH_AHEAD": newer.source_sha,
        **gen,
    })
    s = h.shelf()
    check("the fixture really did invert the order (both generations exist — not vacuous)",
          sorted(s.generations()) == sorted([tok_old, tok_new]),
          f"generations={s.generations()}")
    check("…and the newer run really did seal first, so there was something to take back",
          s.under(tok_new).sealed(), f"{tok_new}: {denominator(s.under(tok_new))}")
    if expect_defect:
        check("PRE-FIX: the older run moved the pointer onto its own publication, so every reader "
              "serves a COMPLETE, SEALED publication of older bytes and nothing is red",
              r.returncode == 0 and s.pointer() == tok_old,
              f"rc={r.returncode}, _current={s.pointer()!r}")
    else:
        check("the pointer is left on the NEWER publication",
              s.pointer() == tok_new and s.under(tok_new).sealed()
              and len(s.under(tok_new).files()) == EXPECTED_FILES,
              f"_current={s.pointer()!r}: {denominator(s.under(tok_new))}")
        check("…and the older run still SUCCEEDS — its content is contained in what is live, so "
              "this is not a failure to report",
              r.returncode == 0, f"rc={r.returncode}")
        check("…and says so, naming both commits rather than leaving a silent no-op",
              "Not moving the pointer backwards" in r.stdout
              and older.source_sha in r.stdout and newer.source_sha in r.stdout,
              "a pointer that was deliberately not moved must be readable as a decision")
        check("…and the summary counts it, on every run, as its own denominator",
              "targets-superseded=1" in r.stdout,
              "a run that published nothing readers see must never be counted as having published")
        check("the older run's generation is sealed and complete, just named by nothing",
              s.under(tok_old).sealed() and len(s.under(tok_old).files()) == EXPECTED_FILES,
              f"{tok_old}: {denominator(s.under(tok_old))} — retention collects it once it is past the window")
        check("the fixture really did plant a sealed flat copy of the NEWER bytes while the older run was in flight (not vacuous)",
              inner_run_succeeded(work, "3702") and planted.is_file(),
              f"inner receipt: {inner_receipt(work, '3702')!r}, planted={planted.is_file()} — observed "
              "at the hook, so this reads what the older run was handed, not what it left")
        check("the SUPERSEDED run disposes of nothing — the flat copy another run is serving is left sealed and whole",
              s.sealed() and len(s.files()) == EXPECTED_FILES
              and set(s.bakes_present()) - {"<marker>"} == {"newer-content"},
              f"flat: {denominator(s)} — a run that lost the pointer race owns nothing at this prefix")

    # ── The writer must decide "already published" from the POINTED-TO directory, not the prefix.
    # Getting this wrong is the silent one: the writer would read the flat compatibility copy while
    # portals served the generation, and republish (or skip) against a publication nobody serves.
    print("\ngeneration layout — 'already published' is read from the generation the pointer names:")
    h.reset()
    r = h.publish(sat, "Systemorph/MeshWeaver.Plugins", "3201", gen)
    r = h.publish(sat, "Systemorph/MeshWeaver.Plugins", "3202", gen)
    s = h.shelf()
    check("the fixture really did publish once (not vacuous)",
          s.pointer() == "Systemorph-MeshWeaver.Plugins-3201-1", f"_current={s.pointer()!r}")
    if not expect_defect:
        check("the same content is skipped — resolved through the pointer, not off the prefix",
              r.returncode == 0 and "already published; skipping" in r.stdout
              and "Systemorph-MeshWeaver.Plugins-3201-1" in r.stdout,
              f"rc={r.returncode}, generations={s.generations()}")
        check("…and nothing new was written",
              s.generations() == ["Systemorph-MeshWeaver.Plugins-3201-1"],
              f"generations={s.generations()}")

    # ── A pointer this writer will not follow must degrade to the flat layout, exactly as the
    # reader does. The escape shapes are the ones that must never be followed at all.
    print("\ngeneration layout — an unusable pointer degrades to the prefix, never to bytes outside it:")
    for label, value in (("an escaping pointer", "../../etc"),
                         ("a rooted pointer", "/etc"),
                         ("a dot pointer", ".."),
                         ("a blank pointer", "   "),
                         ("a dangling pointer", "Systemorph-MeshWeaver-9999-1")):
        h.reset()
        h.publish(core, "Systemorph/MeshWeaver", "3301")          # a flat publication to fall back to
        (h.shelf().dest / POINTER).write_text(value + "\n")
        r = h.publish(core, "Systemorph/MeshWeaver", "3302")
        if not expect_defect:
            check(f"{label} reads the prefix as its own publication",
                  r.returncode == 0 and "already published; skipping" in r.stdout,
                  f"rc={r.returncode}, pointer={value!r}")

    # ── An unknown layout is refused, not silently treated as flat: the value decides where a
    # publication is written and which directory every reader resolves.
    print("\ngeneration layout — an unrecognised selector is refused:")
    h.reset()
    r = h.publish(core, "Systemorph/MeshWeaver", "3401", {"BAKE_PUBLICATION_LAYOUT": "generations"})
    if not expect_defect:
        check("a typo'd layout fails loudly and publishes nothing",
              r.returncode != 0 and "is not a known publication layout" in r.stdout
              and not h.shelf().sealed(), f"rc={r.returncode}")

    # ── MIXED LAYOUT: one producer of a prefix has flipped to `generation` and the other has not.
    # ── This is not a hypothetical: `prebuilt-bundles/<identity>/plugins` has TWO producers living
    # ── in TWO repositories (core CD's `plugins-bake` and the MeshWeaver.Plugins satellite's own
    # ── `publish-bake`), so phase 4 CANNOT be one atomic change set — the fleet must pass through
    # ── this state. This lane's own `publication-layout` description names what happens then: "the
    # ── new one moves the pointer, the old one replaces the flat copy in place and never touches
    # ── it, so a pointer-following reader keeps serving its generation and never sees the old
    # ── writer's NEWER publication — a stale serve with nothing red anywhere". The writer now makes
    # ── that state UNREACHABLE rather than merely forbidden: the layout belongs to the PREFIX, so a
    # ── live pointer means this run publishes a generation whatever its caller asked for.
    print("\nmixed layout — a FLAT caller on a prefix a generation publisher pointed at:")
    h.reset()
    h.publish(core, "Systemorph/MeshWeaver", "3501", gen)
    tok_gen, tok_flat = "Systemorph-MeshWeaver-3501-1", "Systemorph-MeshWeaver.Plugins-3502-1"
    r = h.publish(sat, "Systemorph/MeshWeaver.Plugins", "3502")     # flat — the default, unflipped
    s = h.shelf()
    if expect_defect:
        check("PRE-FIX: the run wrote the FLAT directory and left the pointer on the generation, so "
              "every consumer serves the OLDER publication while this run reports success",
              r.returncode == 0 and s.pointer() == tok_gen and s.generations() == [tok_gen]
              and set(s.bakes_present()) - {"<marker>"} == {"satellite"},
              f"rc={r.returncode}, _current={s.pointer()!r}, generations={s.generations()}")
    else:
        check("the run publishes a GENERATION although its caller asked for flat",
              r.returncode == 0 and sorted(s.generations()) == sorted([tok_gen, tok_flat]),
              f"rc={r.returncode}, generations={s.generations()}")
        check("…and the pointer names THIS run's publication, so consumers resolve what it sealed",
              s.pointer() == tok_flat and s.under(tok_flat).sealed()
              and len(s.under(tok_flat).files()) == EXPECTED_FILES,
              f"_current={s.pointer()!r}: {denominator(s.under(tok_flat))}")
        check("…and it says so out loud, naming the caller that should be flipped",
              "is on the GENERATION layout" in r.stdout and "publication-layout: flat" in r.stdout,
              "a half-migrated prefix must not be repaired silently")
        check("the earlier generation is untouched — nothing went backwards and nothing was deleted",
              s.under(tok_gen).sealed() and len(s.under(tok_gen).files()) == EXPECTED_FILES
              and set(s.under(tok_gen).bakes_present()) - {"<marker>"} == {"core-cd"},
              f"{tok_gen}: {denominator(s.under(tok_gen))}")

    # ── …and the SKIP path. A flat caller whose content is already the live publication must not
    # ── acquire the prefix at all: its decisions are read from the generation the pointer names
    # ── (unchanged), so it skips — and the pointer stays exactly where the other producer put it.
    # ── Retiring or re-pointing here is what would move consumers BACKWARDS, because the pointer is
    # ── moved BEFORE the flat compatibility copy is refreshed.
    print("\nmixed layout — a FLAT caller with the live content changes nothing (never backwards):")
    h.reset()
    h.publish(core, "Systemorph/MeshWeaver", "3601", gen)
    r = h.publish(core, "Systemorph/MeshWeaver", "3602")            # the SAME content, flat
    s = h.shelf()
    check("the fixture really did skip (not vacuous — nothing was republished)",
          r.returncode == 0 and "already published; skipping" in r.stdout, f"rc={r.returncode}")
    check("the pointer is left on the generation the other producer published",
          s.pointer() == "Systemorph-MeshWeaver-3601-1" and s.generations() == ["Systemorph-MeshWeaver-3601-1"],
          f"_current={s.pointer()!r}, generations={s.generations()}")

    # ── FAIL CLOSED: a file that cannot be read back is refused, not assumed unchanged. ────────
    print("\nfail-closed (an unreadable answer is not a permissive one):")
    h.reset()
    r = h.publish(core, "Systemorph/MeshWeaver", "1401", {"MOCK_AZ_SHOW_FAILS": BUNDLES[0]})
    s = h.shelf()
    if expect_defect:
        check("PRE-FIX: nothing reads the publication back at all, so it seals",
              s.sealed() and r.returncode == 0, f"rc={r.returncode}, {denominator(s)}")
    else:
        check("an unreadable file refuses the seal",
              r.returncode != 0 and not s.sealed()
              and "could not be read back" in r.stdout, f"rc={r.returncode}, {denominator(s)}")
        check("an undecidable verdict does NOT remove a sentinel",
              f"removed {DEST}/{SENTINEL}" not in r.stdout)
        # #3731: the refusal lists what the directory actually holds, from inside the job. The
        # listing is a CLI call after the bulk sweep, so it is a second witness with a second
        # client — asserted so the diagnostic cannot rot into its own "the listing itself failed".
        check("the refusal lists what the directory holds (#3731), and the listing succeeded",
              f"what {TARGET}/{DEST} holds now" in r.stdout
              and f"{BUNDLES[0]}\t" in r.stdout and "the listing itself failed" not in r.stdout,
              "the diagnostic group names the refused file with its byte count")

    # ── UNSTAMPED: a publisher that writes no digest is not one of ours. ───────────────────────
    print("\nan incumbent written by a publisher that stamps no digest:")
    h.reset()
    strip = (f'python3 -c "import json,pathlib;'
             f"p=pathlib.Path(r'{h.shelf_root}/{ACCOUNT}/{SHARE}/{DEST}/{BUNDLES[0]}');"
             f"m=pathlib.Path(r'{h.shelf_root}/.meta/{ACCOUNT}/{SHARE}/{DEST}/{BUNDLES[0]}.json');"
             f'm.write_text(json.dumps({{}}))"')
    r = h.publish(core, "Systemorph/MeshWeaver", "1501", {
        "MOCK_AZ_HOOK_ON": f"{DEST}/architecture.txt",
        "MOCK_AZ_HOOK_WHEN": "after",
        "MOCK_AZ_HOOK_ONCE": work / "fired-unstamped",
        "MOCK_AZ_HOOK_CMD": strip,
    })
    s = h.shelf()
    if expect_defect:
        check("PRE-FIX: an unstamped file is invisible and the publication seals",
              s.sealed() and r.returncode == 0, f"rc={r.returncode}, {denominator(s)}")
    else:
        check("a file carrying no digest is treated as foreign, and the seal is refused",
              r.returncode != 0 and not s.sealed()
              and "no digest recorded" in r.stdout, f"rc={r.returncode}, {denominator(s)}")

    # ── DENOMINATOR: the verification covers the whole publication, and says so. ───────────────
    print("\nthe verification prints its denominator:")
    h.reset()
    r = h.publish(core, "Systemorph/MeshWeaver", "1601")
    if expect_defect:
        check("PRE-FIX: no verification runs, so there is no denominator to print",
              "file(s) carry this run's bytes" not in r.stdout)
    else:
        check(f"every one of the {EXPECTED_FILES} published files is verified before the seal",
              f"{EXPECTED_FILES}/{EXPECTED_FILES} file(s) hold this run's bytes" in r.stdout,
              "expected/verified are both printed")

    # ── The read-back loop is fed by a redirect. A command inside it that ate stdin would end it
    # ── early, and a verification covering a PREFIX would report no foreign bytes and SEAL. The
    # ── helper runs OUTSIDE that loop now; this keeps the case honest if it is ever moved back.
    print("\nthe read-back loop cannot be truncated by something eating its stdin:")
    h.reset()
    r = h.publish(core, "Systemorph/MeshWeaver", "1701", {"MOCK_AZ_EAT_STDIN": "1"})
    s = h.shelf()
    if expect_defect:
        check("PRE-FIX: no read-back loop exists to truncate",
              s.sealed() and r.returncode == 0, f"rc={r.returncode}, {denominator(s)}")
    else:
        check("stdin-eating does not truncate the verification",
              r.returncode == 0 and s.sealed()
              and f"{EXPECTED_FILES}/{EXPECTED_FILES} file(s) hold this run's bytes" in r.stdout,
              f"rc={r.returncode}, {denominator(s)} — all {EXPECTED_FILES} still verified")

    # ── SAME LANE: the shape that was actually measured most often. Two pushes to ONE repo bake
    # ── concurrently and land on one identity; a single owner for the prefix does not touch it.
    print("\nsame lane, two concurrent runs (four such overlaps measured in one day; zero cross-lane):")
    h.reset()
    later = Bake(work, "satellite-later", "cccccccccccccccccccccccccccccccccccccccc")
    inner = h.publish_command(later, "Systemorph/MeshWeaver.Plugins", "2402")
    r = h.publish(sat, "Systemorph/MeshWeaver.Plugins", "2401", {
        "MOCK_AZ_HOOK_ON": f"{DEST}/{BUNDLES[1]}",
        "MOCK_AZ_HOOK_WHEN": "before",
        "MOCK_AZ_HOOK_ONCE": work / "fired-samelane",
        "MOCK_AZ_HOOK_CMD": inner,
    })
    s = h.shelf()
    check("the fixture really did produce a two-bake directory (the case is not vacuous)",
          len(set(s.bakes_present()) - {"<marker>"}) > 1, denominator(s))
    if expect_defect:
        check("PRE-FIX: two runs of ONE repo seal a mix just as readily",
              s.sealed_mix(), denominator(s))
    else:
        check("two runs of ONE repo are caught by the same postcondition",
              not s.sealed_mix() and r.returncode != 0
              and "holds a MIX of two publications" in r.stdout,
              f"rc={r.returncode}, {denominator(s)}")
        check("the refusal names same-lane concurrency, not only the other repo",
              "WITHIN one lane" in r.stdout)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--script", type=Path, default=DEFAULT_SCRIPT,
                    help="the publish script to execute (default: the one beside this file)")
    ap.add_argument("--expect-defect", action="store_true",
                    help="assert the PRE-FIX behaviour instead: overlaps seal a mix. Used to "
                         "falsify the guard by running these same cases against the old script.")
    args = ap.parse_args()

    if not args.script.is_file():
        print(f"::error::{args.script} does not exist — nothing to execute. A harness that cannot "
              f"find its subject must go red, never green.")
        return 1

    print(f"publish-bake overlap harness — executing {args.script}")
    print(f"  identity {IDENTITY}, source '{SOURCE}', {EXPECTED_FILES} file(s) per publication "
          f"({len(BUNDLES)} bundle(s), {len(MODULES)} module(s), 5 marker/index/surface file(s))")
    with tempfile.TemporaryDirectory(prefix="publish-bake-overlap-") as tmp:
        run_cases(args.script, Path(tmp), args.expect_defect)

    total = len(PASSES) + len(FAILURES)
    if total == 0:
        print("::error::the harness ran ZERO assertions — a case list that executes nothing "
              "renders the same green tick as one that passes. Refusing.")
        return 1
    print(f"\n{len(PASSES)} passed, {len(FAILURES)} failed, {total} assertion(s) total.")
    if FAILURES:
        print("::error title=publish-bake overlap harness failed::"
              + "; ".join(FAILURES))
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
