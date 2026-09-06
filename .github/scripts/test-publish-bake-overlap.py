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

WHAT THIS HARNESS CANNOT PROVE
------------------------------
The stub is not Azure Files. It reproduces the parts the script depends on — an extensionless
`--path` naming a directory, per-file metadata, the sentinel written last — and nothing else. In
particular the exact JSON shape of the real `az storage file show` is taken from the CLI's own
transformer (`transform_file_show_result` puts the file's metadata at the TOP level), and asserted
only there. What the harness CAN prove about the real CLI it does: the fidelity case runs the read-
back query through the real `jmespath` engine azure-cli evaluates `--query` with, and asserts the
shape knack then renders as ONE tab-separated row. That case exists because the first draft of this
change used a flat `[a, b]` — which knack prints as TWO LINES, so `$2` would have read empty for
every file and the postcondition would have refused every publication in the fleet. The stub said
nothing, because the stub was written to match the draft.

Beyond that the script fails CLOSED on an unreadable, empty or unexpectedly-shaped answer, so a CLI
change is a RED publish rather than a silent one — the only protection available against a shape
this harness cannot run.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

try:
    import jmespath
except ImportError:  # pragma: no cover — the CI step installs it; locally `pip install jmespath`
    print("::error::test-publish-bake-overlap.py needs jmespath (the engine azure-cli evaluates "
          "--query with) to assert how the publish script's read-back query renders. Refusing "
          "rather than skipping that case: it is the one thing this harness's stub cannot prove "
          "about the real CLI.")
    raise SystemExit(1) from None

HERE = Path(__file__).resolve().parent
DEFAULT_SCRIPT = HERE / "publish-bake-bundles.sh"

IDENTITY = "s0123456789abcdef0123456789abcdef"
SOURCE = "plugins"
ACCOUNT = "portalstore"
SHARE = "memex-data"
TARGET = f"{ACCOUNT}/{SHARE}"
DEST = f"prebuilt-bundles/{IDENTITY}/{SOURCE}"
SENTINEL = "_complete"

# 🚨 THE ONE `file show` QUERY, and its exact shape is load-bearing. knack's `format_tsv` renders a
# TOP-LEVEL list as ROWS, so `--query "[a, b]" -o tsv` prints TWO LINES rather than two columns; a
# script parsing `$2` would then read EMPTY for every file, count `ours` as zero on every publish,
# and refuse every publication in the fleet as "superseded". A list-of-ONE-ROW renders one
# tab-separated line. And a null field renders as the LITERAL 'None', which is why both fields
# carry a `|| '-'` guard. Measured against azure-cli 2.90.0's own jmespath + knack formatter; the
# fidelity case below re-asserts it with the real jmespath engine on every run.
SHOW_QUERY = "[[metadata.digest || '-', metadata.publication || '-']]"

BUNDLES = ["Chess.zip", "Edu.zip", "Store.zip"]
MODULES = ["MeshWeaver.AI.module.nupkg", "MeshWeaver.Maps.module.nupkg"]
# bundles + modules + modules/_index + source-commit.txt + architecture.txt
EXPECTED_FILES = len(BUNDLES) + len(MODULES) + 3


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

def run_hook(when, rel):
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
    src = Path(flag("--source"))
    dest_dir = share_root() / path
    # 🚨 The real CLI silently treats an EXTENSIONLESS --path as a DIRECTORY and appends the
    # source basename; the script relies on that for _complete, modules/_index and the two
    # markers. Model it by the same rule the script documents: an existing directory wins.
    if dest_dir.is_dir():
        target = dest_dir / src.name
    else:
        target = share_root() / path
    rel = str(target.relative_to(share_root()))
    run_hook("before", rel)
    n = int(os.environ.get("MOCK_AZ_UPLOADS_SO_FAR_FILE_COUNT", "0"))
    counter = os.environ.get("MOCK_AZ_UPLOAD_COUNTER")
    if counter:
        c = Path(counter)
        n = (int(c.read_text()) if c.exists() else 0) + 1
        c.write_text(str(n))
        cap = os.environ.get("MOCK_AZ_FAIL_UPLOADS_AFTER")
        if cap and n > int(cap):
            sys.stderr.write("stub-az: simulated upload failure (#%d)\n" % n)
            sys.exit(1)
    if not target.parent.is_dir():
        sys.stderr.write("stub-az: ParentNotFound for %s\n" % target); sys.exit(1)
    target.write_bytes(src.read_bytes())
    md = {}
    for kv in opts.get("--metadata", []):
        k, _, v = kv.partition("=")
        md[k] = v
    mp = meta_path(rel)
    mp.parent.mkdir(parents=True, exist_ok=True)
    mp.write_text(json.dumps(md))
    run_hook("after", rel)
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

if action == "show":
    SHOW_QUERY = os.environ["MOCK_AZ_SHOW_QUERY"]
    q = opts.get("--query", [None])[0]
    if q != SHOW_QUERY or not out_tsv:
        die("this stub models exactly one `file show` query, %r with -o tsv — the script asked "
            "for %r. Teach the stub the new query (and re-measure how knack renders it) rather "
            "than loosening it." % (SHOW_QUERY, q))
    fails = os.environ.get("MOCK_AZ_SHOW_FAILS")
    if fails and path.endswith(fails):
        sys.stderr.write("stub-az: simulated read failure for %s\n" % path); sys.exit(1)
    f = share_root() / path
    if not f.is_file():
        sys.stderr.write("stub-az: ResourceNotFound %s\n" % path); sys.exit(1)
    mp = meta_path(path)
    md = json.loads(mp.read_text()) if mp.exists() else {}
    # 🚨 The rendering the REAL cli produces, measured against azure-cli 2.90.0's own jmespath +
    # knack.output.format_tsv: `[[a || '-', b || '-']]` is ONE tab-separated row, and an absent
    # value is '-'. A flat `[a, b]` would print two LINES instead, which is the shape the script
    # must never go back to — see the fidelity assertion in run_cases.
    emit("%s\t%s" % (md.get("digest") or "-", md.get("publication") or "-"))
    sys.exit(0)

die("unmodelled file action %r" % action)
'''


# ────────────────────────────── fixtures ──────────────────────────────
class Bake:
    """One producer's bake directory — every byte in it names the bake that made it."""

    def __init__(self, root: Path, name: str, source_sha: str):
        self.name = name
        self.source_sha = source_sha
        self.dir = root / f"bake-{name}"
        (self.dir / "modules").mkdir(parents=True, exist_ok=True)
        (self.dir / "framework-mvid.txt").write_text(IDENTITY + "\n")
        for b in BUNDLES:
            (self.dir / b).write_text(f"{b} produced by bake {name} at {source_sha}\n")
        for m in MODULES:
            (self.dir / "modules" / m).write_text(f"{m} produced by bake {name} at {source_sha}\n")


class Shelf:
    """The fake Azure Files share, read as a consumer would: by the bytes, not by the log."""

    def __init__(self, root: Path):
        self.root = root
        self.dest = root / ACCOUNT / SHARE / DEST

    def sealed(self) -> bool:
        return (self.dest / SENTINEL).is_file()

    def files(self) -> dict[str, str]:
        """path-under-dest → content, for every published file (the sentinel excluded)."""
        out: dict[str, str] = {}
        if not self.dest.is_dir():
            return out
        for p in sorted(self.dest.rglob("*")):
            if p.is_file() and p.name != SENTINEL:
                out[str(p.relative_to(self.dest))] = p.read_text()
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

    def publish(self, bake: Bake, publisher: str, run_id: str, env_extra: dict | None = None):
        env = dict(os.environ)
        env["PATH"] = f"{self.bin}{os.pathsep}{env['PATH']}"
        env["MOCK_AZ_ROOT"] = str(self.shelf_root)
        env["MOCK_AZ_SHOW_QUERY"] = SHOW_QUERY
        env["BAKE_PUBLISH_TARGETS"] = TARGET
        env["GITHUB_REPOSITORY"] = publisher
        env["GITHUB_RUN_ID"] = run_id
        env["GITHUB_RUN_ATTEMPT"] = "1"
        env.pop("EXT_MODULES_DIR", None)
        env.pop("GITHUB_STEP_SUMMARY", None)
        for k in ("MOCK_AZ_HOOK_ON", "MOCK_AZ_HOOK_CMD", "MOCK_AZ_HOOK_ONCE", "MOCK_AZ_HOOK_WHEN",
                  "MOCK_AZ_FAIL_UPLOADS_AFTER", "MOCK_AZ_UPLOAD_COUNTER", "MOCK_AZ_SHOW_FAILS"):
            env.pop(k, None)
        env.update({k: str(v) for k, v in (env_extra or {}).items()})
        return subprocess.run(
            ["bash", str(self.script), str(bake.dir), SOURCE, bake.source_sha],
            capture_output=True, text=True, env=env,
        )

    def publish_command(self, bake: Bake, publisher: str, run_id: str,
                        env_extra: dict | None = None) -> str:
        """The same publication, as a shell command a stub hook can run mid-upload."""
        assigns = {
            "PATH": f"{self.bin}:$PATH",
            "MOCK_AZ_ROOT": str(self.shelf_root),
            "MOCK_AZ_SHOW_QUERY": SHOW_QUERY,
            "BAKE_PUBLISH_TARGETS": TARGET,
            "GITHUB_REPOSITORY": publisher,
            "GITHUB_RUN_ID": run_id,
            "GITHUB_RUN_ATTEMPT": "1",
        }
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


def denominator(shelf: Shelf) -> str:
    files = shelf.files()
    bakes = shelf.bakes_present()
    return (f"{len(files)}/{EXPECTED_FILES} file(s) present, sealed={shelf.sealed()}, "
            f"by bake: {bakes or '{}'}")


# ────────────────────────────── the cases ──────────────────────────────
def check_query_fidelity(script: Path) -> None:
    """The one thing the stub cannot prove: how the REAL cli renders this script's read-back query.

    knack.output.format_tsv does `result if isinstance(result, list) else [result]` and then dumps
    each element as a ROW. So a flat two-element list is two LINES, and only a list-of-one-list is
    one row of two tab-separated columns. Asserted here with the engine azure-cli itself uses.
    """
    print("\nquery fidelity (the stub cannot prove this; the real jmespath engine can):")
    text = script.read_text()
    check("the script asks for exactly the query this harness models",
          SHOW_QUERY in text, f"{SHOW_QUERY!r}")

    rows = jmespath.compile(SHOW_QUERY).search(
        {"name": "a.zip", "metadata": {"digest": "abc", "publication": "Systemorph-MW-1-1"}})
    check("the query yields ONE row of TWO columns, not two rows",
          isinstance(rows, list) and len(rows) == 1
          and isinstance(rows[0], list) and len(rows[0]) == 2,
          f"jmespath -> {rows!r}; knack would render {chr(9).join(rows[0])!r} on one line"
          if isinstance(rows, list) and rows and isinstance(rows[0], list) else f"jmespath -> {rows!r}")

    # A null field renders as the literal string 'None' unless it is guarded, and 'None' is not
    # empty — so the script's fail-closed "no digest recorded" branch would never fire.
    for label, doc in (("no metadata key", {"name": "a.zip"}),
                       ("empty metadata", {"name": "a.zip", "metadata": {}}),
                       ("null metadata", {"name": "a.zip", "metadata": None})):
        got = jmespath.compile(SHOW_QUERY).search(doc)
        check(f"an absent value is rendered '-' rather than a null ({label})",
              got == [["-", "-"]], f"jmespath -> {got!r}")


def run_cases(script: Path, work: Path, expect_defect: bool) -> None:
    # The pre-fix script reads nothing back, so it has no query to be faithful about. Every OTHER
    # run asserts it — including `--script` pointed at a copy of the current one.
    if not expect_defect:
        check_query_fidelity(script)
    h = Harness(script, work)
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

    # ── CONTROL 2: a second publication of DIFFERENT content reseals cleanly. ──────────────────
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
          f"({len(BUNDLES)} bundle(s), {len(MODULES)} module(s), 3 marker/index file(s))")
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
