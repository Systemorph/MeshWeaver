#!/usr/bin/env python3
"""EXECUTE the lanes' one-build-per-assembly-name assertion (MeshWeaver#3732).

WHY
---
A module-owned `MeshWeaver.*` sibling RIDES every bundle that references it, and 19 of
MeshWeaver.Plugins' 37 bundles ride an assembly some OTHER package declares as its module
(`Doc/Architecture/ModuleOwnedSiblingsRide`). That decision stands. It rests on ONE invariant:

    one assembly name, one framework identity, ONE BUILD — across every copy in the set,
    declared or riding.

`MeshWeaver.*` assemblies bind by a strictly synchronised `AssemblyVersion`, so two copies under one
simple name are ONE assembly identity: whichever loads first wins the process and the loser's bytes
are never in memory. When the copies differ, the bake stamps every NodeType's dependency record
against whichever copy IT loaded, and every portal that loaded another one declines all of them
("dependency record mismatch — built against mvid:A, live is mvid:B"), falls back to a local
compile, and — with two replicas holding two builds — recompiles in a ping-pong that never
converges while the partition serves the default config.

The design page believed re-keying preserved the invariant (`bake-scope.sh` and
`module-build-key.py` fold a sibling's SOURCES into every bundle that carries it). Re-keying makes
the bundles REBUILD; it says nothing about the bytes, and one publication is composed from SEVERAL
independent compilations. Measured on memex 2026-09-10: ONE pod held
`MeshWeaver.Markdown.Collaboration` in 15 copies and THREE builds, grouped exactly by which
compilation produced them. So the invariant was asserted nowhere at the producer — only at the
consumer, by `PublishedBundleCatalogue`, which turns it into `SealedSetInconsistent` and HOLDS the
roll for the whole fleet, days later, on a portal.

The lanes now assert it where every copy is first in one hand — the `ext-modules` composition, which
is also where the damage is done. This harness EXECUTES that assertion.

HOW IT STAYS HONEST
-------------------
* The step is EXTRACTED from the workflow by its `id:`, never copied here — a copy passes while the
  real thing rots. BOTH lanes are extracted (they carry the same block on purpose, so the gate and
  the bake cannot disagree) and finding fewer than both is RED.
* The verdict is read off the step's EXIT STATUS and the bytes it composed, not off prose.
* Every case prints the step's own DENOMINATOR line, and one case exists purely to check it: a
  check aimed at an empty directory refuses nothing while ticking exactly like a clean measurement.
* Both directions, on the same fixtures: divergent copies must go RED and name both producers;
  IDENTICAL copies of the same shared sibling must go GREEN — a checker that flagged every duplicate
  would fail there, and that is the whole decision the design page took.
* TWO falsification arms that MUTATE THE REAL STEP:
  - `strip`  — the assertion removed. The divergent case must then PASS. An assertion nobody has
               watched fail is not an assertion.
  - `blind`  — the assertion kept but pointed at an EMPTY directory. The divergent case must then
               pass AND report `0 MeshWeaver.* assembly file(s)`, which is the shape a misdirected
               check takes and the reason the denominator is printed at all.
"""
from __future__ import annotations

import importlib.util
import json
import sys
import tempfile
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]

# (workflow, step id) — the same block lives in both lanes and both are executed.
LANES = [
    (".github/workflows/node-repo-gate.yml", "ext-modules"),
    (".github/workflows/node-repo-publish-bake.yml", "ext-modules"),
]

# The extraction and execution helpers are SHARED with the sibling harness rather than copied:
# both run the same step, and a second copy of "how to extract it" is a second thing to rot.
_spec = importlib.util.spec_from_file_location(
    "module_asset_landing_harness", Path(__file__).with_name("test-module-asset-landing.py"))
_harness = importlib.util.module_from_spec(_spec)
assert _spec.loader is not None
_spec.loader.exec_module(_harness)

extract_step = _harness.extract_step
run_step = _harness.run_step
ext_dir = _harness.ext_dir
module_folder = _harness.module_folder

FAILURES: list[str] = []


def fail(message: str) -> None:
    FAILURES.append(message)
    print(f"  FAIL  {message}")


def ok(message: str) -> None:
    print(f"  ok    {message}")


def die(message: str) -> None:
    print(f"error: {message}", file=sys.stderr)
    sys.exit(1)


# ── fixtures ────────────────────────────────────────────────────────────────────────────────
def make_bundle(path: Path, module: str, rides: dict[str, bytes],
                entry: bytes | None = None) -> None:
    """One module bundle in the shape `ModulePackCommand` writes: a manifest naming the entry
    assembly, the entry DLL, and a FLAT closure beside it — which is where a riding sibling lives."""
    folder = module_folder()
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as archive:
        archive.writestr("meshweaver/manifest.json", json.dumps({
            "plugin": module.replace("MeshWeaver.", ""),
            "version": "1.2.3",
            "module": {"assemblyName": module},
        }))
        archive.writestr(f"{folder}/{module}.dll", entry or b"MZ-entry-" + module.encode())
        for name, content in rides.items():
            archive.writestr(f"{folder}/{name}.dll", content)


BUILD_A = b"MZ-MeshWeaver.Shared-compiled-by-workspace-floor"
BUILD_B = b"MZ-MeshWeaver.Shared-compiled-by-workspace-rest\x00"


def bundles(tmp: Path, case: str) -> list[Path]:
    """The four fixtures, all built from ONE shape so the only variable is the sibling's BYTES."""
    out = tmp / f"bundles-{case}"
    out.mkdir()
    if case == "divergent":
        make_bundle(out / "alpha.module.nupkg", "MeshWeaver.Alpha", {"MeshWeaver.Shared": BUILD_A})
        make_bundle(out / "beta.module.nupkg", "MeshWeaver.Beta", {"MeshWeaver.Shared": BUILD_B})
    elif case == "declared-vs-riding":
        make_bundle(out / "shared.module.nupkg", "MeshWeaver.Shared", {})
        make_bundle(out / "beta.module.nupkg", "MeshWeaver.Beta", {"MeshWeaver.Shared": BUILD_B})
    elif case == "agreeing":
        make_bundle(out / "alpha.module.nupkg", "MeshWeaver.Alpha", {"MeshWeaver.Shared": BUILD_A})
        make_bundle(out / "beta.module.nupkg", "MeshWeaver.Beta", {"MeshWeaver.Shared": BUILD_A})
    elif case == "same-entry-two-bundles":
        # 🚨 The collision a scan of /ext CANNOT see: both bundles declare MeshWeaver.Alpha, so the
        # landing loop merges them into ONE /ext/MeshWeaver.Alpha/ and the second `cp -R` overwrites
        # the first. Only a reading taken per bundle, before the merge, has both copies.
        make_bundle(out / "alpha-artifact.module.nupkg", "MeshWeaver.Alpha", {},
                    entry=b"MZ-MeshWeaver.Alpha-from-this-run's-artifact")
        make_bundle(out / "alpha-registry.bundle.zip", "MeshWeaver.Alpha", {},
                    entry=b"MZ-MeshWeaver.Alpha-from-the-registry\x00")
    elif case == "disjoint":
        make_bundle(out / "alpha.module.nupkg", "MeshWeaver.Alpha", {})
        make_bundle(out / "beta.module.nupkg", "MeshWeaver.Beta", {})
    else:  # pragma: no cover - a typo in a case name must not silently test nothing
        die(f"unknown fixture case '{case}'")
    return sorted(out.iterdir())


# ── mutating the real step ──────────────────────────────────────────────────────────────────
MARKER = "THE VERDICT ON THE SET"
OPENER = 'if [ "$DIVERGED" -ne 0 ]; then'


def block_bounds(body: str, lane: str) -> tuple[int, int]:
    lines = body.splitlines()
    try:
        start = next(i for i, line in enumerate(lines) if MARKER in line)
        opener = next(i for i, line in enumerate(lines) if OPENER in line)
    except StopIteration:
        die(f"{lane}: the module-set assertion is not in the step — every arm below would mutate "
            "nothing and pass having tested nothing")
    indent = len(lines[opener]) - len(lines[opener].lstrip())
    closer = next((i for i in range(opener + 1, len(lines))
                   if lines[i].strip() == "fi" and len(lines[i]) - len(lines[i].lstrip()) == indent),
                  None)
    if closer is None:
        die(f"{lane}: cannot find the `fi` closing the module-set assertion")
    return start, closer


def strip_assertion(body: str, lane: str) -> str:
    """The verdict block removed: the reading still runs, nothing decides on it."""
    start, closer = block_bounds(body, lane)
    lines = body.splitlines()
    out = "\n".join(lines[:start] + lines[closer + 1:])
    if "DIVERGED" in out:
        die(f"{lane}: the strip arm still mentions DIVERGED — it removed the wrong region")
    return out


def blind_assertion(body: str, lane: str) -> str:
    """The assertion kept, pointed at an empty directory — what a misdirected check looks like."""
    needle = 'done < <(find "$u/meshweaver/modules" -maxdepth 1 -type f'
    if needle not in body:
        die(f"{lane}: the reading's `find` is not in the step — the blind arm has nothing to aim "
            "elsewhere")
    return body.replace(
        needle,
        'mkdir -p "${RUNNER_TEMP:-/tmp}/nowhere"\n'
        '              done < <(find "${RUNNER_TEMP:-/tmp}/nowhere" -maxdepth 1 -type f')


# ── cases ───────────────────────────────────────────────────────────────────────────────────
def summary_line(stdout: str) -> str:
    for line in stdout.splitlines():
        if line.startswith("module set: "):
            return line
    return ""


def case_divergent_is_refused(lane: str, body: str, tmp: Path) -> None:
    work = tmp / "divergent"
    work.mkdir()
    result = run_step(body, work, bundles(tmp, "divergent"))
    line = summary_line(result.stdout)
    print(f"        {line or '(no denominator line)'}")
    if result.returncode == 0:
        fail(f"{lane}: two DIFFERENT builds of MeshWeaver.Shared composed into one mesh and the "
             f"step exited 0 — this is the publication that holds the fleet's rolls")
        return
    combined = result.stdout + result.stderr
    missing = [needle for needle in
               ("MeshWeaver.Shared reaches this mesh as more than one build",
                "as a sibling riding",
                "1 carried at more than one BUILD")
               if needle not in combined]
    if missing:
        fail(f"{lane}: the refusal does not say what is wrong — missing {missing}")
        return
    digests = {chunk.split()[1] for chunk in combined.splitlines() if chunk.strip().startswith("sealed ")}
    if len(digests) != 2:
        fail(f"{lane}: the refusal named {len(digests)} build(s); an operator needs BOTH to tell "
             "which producer to change")
        return
    ok(f"{lane}: divergent copies are refused, naming both builds and both producers")


def case_declared_vs_riding_is_labelled(lane: str, body: str, tmp: Path) -> None:
    work = tmp / "declared"
    work.mkdir()
    result = run_step(body, work, bundles(tmp, "declared-vs-riding"))
    combined = result.stdout + result.stderr
    if result.returncode == 0:
        fail(f"{lane}: a bundle riding a DIFFERENT build of another package's DECLARED module "
             "passed — that is the exact shape measured on memex")
        return
    if ("as the declared module of" not in combined
            or "as a sibling riding" not in combined):
        fail(f"{lane}: the refusal does not distinguish the declared copy from the riding one, "
             "which is the half that tells an operator which producer to change")
        return
    ok(f"{lane}: declared-vs-riding divergence is refused and each copy's ROLE is named")


def case_same_entry_from_two_bundles_is_refused(lane: str, body: str, tmp: Path) -> None:
    """🚨 The collision a scan of the COMPOSED directory cannot see.

    Two bundles declaring the same `module.assemblyName` land in ONE `/ext/<name>/` and the second
    `cp -R` overwrites the first — the lane's own notice calls the precedence between an artifact
    bundle and a registry bundle an accident of glob order. So the reading has to be taken per
    bundle, before the merge; a version of this check that scanned `/ext` afterwards passed here.
    """
    work = tmp / "same-entry"
    work.mkdir()
    result = run_step(body, work, bundles(tmp, "same-entry-two-bundles"))
    line = summary_line(result.stdout)
    print(f"        {line or '(no denominator line)'}")
    combined = result.stdout + result.stderr
    if result.returncode == 0:
        fail(f"{lane}: two bundles declared MeshWeaver.Alpha at DIFFERENT builds and the step "
             "exited 0 — the merge hid one of them, so the reading is being taken after the "
             "collision instead of before it")
        return
    if "MeshWeaver.Alpha reaches this mesh as more than one build" not in combined:
        fail(f"{lane}: the refusal does not name MeshWeaver.Alpha as the colliding entry")
        return
    named = {chunk.split()[-1] for chunk in combined.splitlines()
             if chunk.strip().startswith("sealed ")}
    if named != {"alpha-artifact.module.nupkg", "alpha-registry.bundle.zip"}:
        fail(f"{lane}: the refusal names {sorted(named)} rather than both source bundles — an "
             "operator cannot tell which producer to change")
        return
    ok(f"{lane}: two bundles declaring one module at two builds are refused, both bundles named")


def case_agreeing_copies_pass(lane: str, body: str, tmp: Path) -> None:
    work = tmp / "agreeing"
    work.mkdir()
    result = run_step(body, work, bundles(tmp, "agreeing"))
    line = summary_line(result.stdout)
    print(f"        {line or '(no denominator line)'}")
    if result.returncode != 0:
        fail(f"{lane}: two IDENTICAL copies of one riding sibling were refused. Riding is the "
             "decision (ModuleOwnedSiblingsRide) and byte equality is the invariant — a check that "
             f"refuses agreement refuses 19 of 37 bundles. stderr: {result.stderr.strip()[-400:]}")
        return
    if "1 carried by more than one bundle" not in line:
        fail(f"{lane}: the step passed but its denominator does not show it SAW the shared copies "
             f"({line!r}) — a check that looked at nothing passes the same way")
        return
    if "0 carried at more than one BUILD" not in line:
        fail(f"{lane}: the denominator does not state the divergence count on the green path ({line!r})")
        return
    ok(f"{lane}: identical copies pass, and the denominator proves both were measured")


def case_disjoint_reports_a_denominator(lane: str, body: str, tmp: Path) -> None:
    work = tmp / "disjoint"
    work.mkdir()
    result = run_step(body, work, bundles(tmp, "disjoint"))
    line = summary_line(result.stdout)
    print(f"        {line or '(no denominator line)'}")
    if result.returncode != 0:
        fail(f"{lane}: a set with no shared sibling was refused: {result.stderr.strip()[-400:]}")
        return
    if not line.startswith("module set: 2 MeshWeaver.* assembly file(s) across 2 bundle(s)"):
        fail(f"{lane}: the two composed entry assemblies are not in the denominator ({line!r}) — "
             "the assertion is not reading the entries, so a module composed twice at two builds "
             "would be invisible to it")
        return
    ok(f"{lane}: entry assemblies count too — the denominator is 2 with no riding sibling at all")


def case_strip_arm_goes_green(lane: str, body: str, tmp: Path) -> None:
    work = tmp / "strip"
    work.mkdir()
    result = run_step(strip_assertion(body, lane), work, bundles(tmp, "divergent"))
    if result.returncode != 0:
        fail(f"{lane}: with the assertion REMOVED the divergent set still failed "
             f"({result.stderr.strip()[-300:]}) — something else is refusing it, so the positive "
             "case above proves nothing about this block")
        return
    ok(f"{lane}: falsification arm `strip` — without the assertion the divergent set sails through")


def case_blind_arm_shows_its_zero(lane: str, body: str, tmp: Path) -> None:
    work = tmp / "blind"
    work.mkdir()
    result = run_step(blind_assertion(body, lane), work, bundles(tmp, "divergent"))
    line = summary_line(result.stdout)
    if result.returncode != 0:
        fail(f"{lane}: the blind arm was expected to measure nothing and pass; it failed instead "
             f"({result.stderr.strip()[-300:]})")
        return
    if not line.startswith("module set: 0 MeshWeaver.* assembly file(s)"):
        fail(f"{lane}: a check aimed at an empty directory did not report a ZERO denominator "
             f"({line!r}) — then nothing on the log distinguishes it from a clean measurement")
        return
    ok(f"{lane}: falsification arm `blind` — a misdirected check passes, and its ZERO is on the log")


CASES = (
    case_divergent_is_refused,
    case_declared_vs_riding_is_labelled,
    case_same_entry_from_two_bundles_is_refused,
    case_agreeing_copies_pass,
    case_disjoint_reports_a_denominator,
    case_strip_arm_goes_green,
    case_blind_arm_shows_its_zero,
)


def main() -> int:
    lanes = [(workflow, extract_step(workflow, step_id)) for workflow, step_id in LANES]
    if len(lanes) != len(LANES):
        die("not every lane was extracted")
    for workflow, body in lanes:
        lane = Path(workflow).name
        print(f"{lane}:")
        for case in CASES:
            with tempfile.TemporaryDirectory() as raw:
                case(lane, body, Path(raw))
    if FAILURES:
        print(f"\nFAILED — {len(FAILURES)} finding(s):")
        for message in FAILURES:
            print(f"  - {message}")
        return 1
    print(f"\nPASS — {len(lanes)} lane(s), {len(CASES)} case(s) each: divergent copies are refused "
          "and named, identical copies pass and are counted, and BOTH falsification arms behave.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
