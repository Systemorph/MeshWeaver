#!/usr/bin/env python3
"""EXECUTE the publication-wide one-build-per-assembly-name reading (MeshWeaver#3732).

WHY
---
The lanes already assert "one assembly name, one BUILD" over the set a bake COMPOSES, and
`test-module-set-consistency.py` executes that assertion. Measured, that set is **4** bundles on
Reinsurance's gate and **5** on Plugins' publish-bake. The defect the issue was filed on lived in a
different population: `MeshWeaver.Markdown.Collaboration` in **15 copies and THREE builds** across
the ~40 *module bundles* a publication carries. So every green reading of the existing assertion has
been green about something else, which is what a denominator of 4 against a population of 40 means.

`module-set-census.py` takes the census where the bytes are (one bundle, in a pack leg) and the
verdict where the whole wave is in one hand (`node-repo-module-pack.yml`'s `verify` job). This
harness executes BOTH halves against real zip fixtures.

HOW IT STAYS HONEST
-------------------
* Real archives, real sha256 — the census reads bytes, so the fixtures are bytes.
* BOTH directions on the same fixtures: divergent copies must be NAMED with both builds and both
  carriers; IDENTICAL copies of a shared sibling must stay clean. A check that flagged every
  duplicate would fail the second, and riding duplicates are the whole point of
  `Doc/Architecture/ModuleOwnedSiblingsRide`.
* The DENOMINATOR is asserted, not just the verdict — including the two shapes that print a zero
  while looking like a clean measurement (no receipts directory; receipts carrying no census).
* `--enforce` is asserted in both positions: it must exit 1 on divergence and 0 without it. The
  report posture is the CURRENT one and is deliberate (see the script's docstring), so it is pinned
  as behaviour rather than left to drift.
* A FALSIFICATION arm that mutates the real subject: with the digest comparison removed, the
  divergent fixture must go quiet. An assertion nobody has watched fail is not an assertion.
* The workflow wiring is asserted against the REAL workflow file, by `id:`/argument, so a step
  renamed or an argument dropped is RED here rather than silently unmeasured in every satellite.
"""
from __future__ import annotations

import hashlib
import importlib.util
import io
import json
import re
import sys
import tempfile
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / ".github" / "scripts" / "module-set-census.py"
PACK_WORKFLOW = ROOT / ".github" / "workflows" / "node-repo-module-pack.yml"

FAILURES: list[str] = []


def check(condition: bool, label: str, detail: str = "") -> None:
    if condition:
        print(f"  ok    {label}")
    else:
        print(f"  FAIL  {label}{(' — ' + detail) if detail else ''}")
        FAILURES.append(label)


def load(path: Path = SCRIPT):
    spec = importlib.util.spec_from_file_location(f"mod_{path.stem}_{id(path)}", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def make_bundle(path: Path, declared: str, assemblies: dict[str, bytes], extra: dict | None = None) -> None:
    """A module bundle: `meshweaver/manifest.json` + `meshweaver/modules/*.dll`."""
    with zipfile.ZipFile(path, "w") as archive:
        archive.writestr("meshweaver/manifest.json", json.dumps({
            "plugin": declared.lower(),
            "frameworkMvid": "s" + "0" * 31,
            "module": {"assemblyName": declared},
        }))
        for name, payload in assemblies.items():
            archive.writestr(f"meshweaver/modules/{name}.dll", payload)
        for name, payload in (extra or {}).items():
            archive.writestr(name, payload)


def capture(module, receipts, enforce=False) -> tuple[int, str]:
    buffer = io.StringIO()
    code = module.verdict(receipts, enforce, out=buffer)
    return code, buffer.getvalue()


def receipt(name: str, assemblies, lane: str = "lane-1") -> dict:
    r = {"lane": lane, "module": name, "package": name.lower(), "version": "1.0.0"}
    if assemblies is not None:
        r["assemblies"] = assemblies
    return r


# ══════════════════════════════════════════════════════════════════════════════
#  1. The census — over real bytes
# ══════════════════════════════════════════════════════════════════════════════
def test_census(module, tmp: Path) -> None:
    print("\ncensus — one bundle's MeshWeaver.* assemblies, digested from its own bytes")
    bundle = tmp / "ai.module.nupkg"
    make_bundle(bundle, "MeshWeaver.AI", {
        "MeshWeaver.AI": b"AI-BUILD-1",
        "MeshWeaver.Markdown.Collaboration": b"COLLAB-BUILD-1",
        "MeshWeaver": b"CORE-1",
    }, extra={
        "meshweaver/modules/deps/MeshWeaver.Nested.dll": b"NESTED",     # not maxdepth 1
        "meshweaver/modules/Newtonsoft.Json.dll": b"THIRD-PARTY",       # not MeshWeaver.*
        "meshweaver/moduleassets/wwwroot/x.js": b"ASSET",               # not an assembly
    })
    rows = module.census(bundle)
    names = sorted(r["name"] for r in rows)
    check(names == ["MeshWeaver", "MeshWeaver.AI", "MeshWeaver.Markdown.Collaboration"],
          "selects MeshWeaver.dll and MeshWeaver.*.dll at the top of meshweaver/modules/ only",
          f"got {names}")
    roles = {r["name"]: r["role"] for r in rows}
    check(roles["MeshWeaver.AI"] == "declared",
          "the manifest's module.assemblyName is the DECLARED copy")
    check(roles["MeshWeaver.Markdown.Collaboration"] == "riding",
          "everything else in the bundle is RIDING")
    expected = hashlib.sha256(b"COLLAB-BUILD-1").hexdigest()
    check(next(r for r in rows if r["name"] == "MeshWeaver.Markdown.Collaboration")["sha256"] == expected,
          "the digest is of the bundle's OWN bytes")

    # 🚨 Read out of the ARCHIVE, never from an unpacked folder: two bundles that declare the same
    # entry assembly unpack into one directory and the second overwrites the first — the accident of
    # glob order that made this defect invisible. Same-named bundles must still be measured apart.
    twin = tmp / "essentials.module.nupkg"
    make_bundle(twin, "MeshWeaver.Essentials", {
        "MeshWeaver.Essentials": b"ESS-1",
        "MeshWeaver.Markdown.Collaboration": b"COLLAB-BUILD-2",
    })
    other = module.census(twin)
    check(next(r for r in other if r["name"] == "MeshWeaver.Markdown.Collaboration")["sha256"]
          != expected,
          "two bundles carrying one sibling at two builds are digested apart")

    bare = tmp / "bare.bundle.zip"
    with zipfile.ZipFile(bare, "w") as archive:
        archive.writestr("meshweaver/modules/MeshWeaver.Solo.dll", b"SOLO")
    rows = module.census(bare)
    check([r["role"] for r in rows] == ["riding"],
          "a bundle with no manifest still censuses — nothing is declared, so nothing claims to be")


# ══════════════════════════════════════════════════════════════════════════════
#  2. The verdict — both directions, over the whole wave
# ══════════════════════════════════════════════════════════════════════════════
def test_verdict(module) -> None:
    print("\nverdict — the reading across a lane's receipts")

    a = hashlib.sha256(b"COLLAB-1").hexdigest()
    b = hashlib.sha256(b"COLLAB-2").hexdigest()

    identical = [
        receipt("MeshWeaver.AI", [
            {"name": "MeshWeaver.AI", "sha256": "a" * 64, "role": "declared"},
            {"name": "MeshWeaver.Markdown.Collaboration", "sha256": a, "role": "riding"}]),
        receipt("MeshWeaver.Essentials", [
            {"name": "MeshWeaver.Essentials", "sha256": "b" * 64, "role": "declared"},
            {"name": "MeshWeaver.Markdown.Collaboration", "sha256": a, "role": "riding"}]),
    ]
    code, text = capture(module, identical)
    check(code == 0, "identical copies of a shared sibling are CLEAN")
    check("0 carried at more than one BUILD" in text,
          "…and the verdict says so in the denominator line", text.strip())
    check("1 carried by more than one bundle" in text,
          "…while still counting it as SHARED — riding is the design, not the defect", text.strip())
    check("::warning::" not in text, "a clean set raises nothing")

    diverged = [
        identical[0],
        receipt("MeshWeaver.Essentials", [
            {"name": "MeshWeaver.Essentials", "sha256": "b" * 64, "role": "declared"},
            {"name": "MeshWeaver.Markdown.Collaboration", "sha256": b, "role": "riding"}]),
    ]
    code, text = capture(module, diverged)
    check("1 carried at more than one BUILD" in text, "divergent copies are COUNTED", text.strip())
    check("MeshWeaver.Markdown.Collaboration reaches this publication as 2 different builds" in text,
          "…named, with how many builds", text.strip())
    check(a[:16] in text and b[:16] in text, "…and BOTH builds are printed", text.strip())
    check("MeshWeaver.AI (a sibling riding it)" in text
          and "MeshWeaver.Essentials (a sibling riding it)" in text,
          "…each with the bundle that carries it and the role it plays there", text.strip())
    check(code == 0, "a REPORT does not fail the job — no lane passes --enforce yet")
    check("::warning::" in text and "--enforce" in text,
          "…and says loudly that it is a report, and why", text.strip())

    code, text = capture(module, diverged, enforce=True)
    check(code == 1, "--enforce REFUSES the same set")
    check("::error::" in text, "…as an error annotation", text.strip())

    # 🚨 The denominator's two zero-shapes. A reading over nothing must never be spelled like a
    # clean reading over something.
    code, text = capture(module, [])
    check("across 0 module bundle(s) of 0 read" in text,
          "no receipts at all prints a ZERO denominator", text.strip())

    silent = [receipt("MeshWeaver.AI", None), identical[0]]
    code, text = capture(module, silent)
    check("across 1 module bundle(s) of 2 read" in text,
          "a receipt with no census is NOT counted as measured", text.strip())
    check("not measured: 1 bundle(s)" in text and "MeshWeaver.AI" in text,
          "…and is named, with the reason", text.strip())

    # 🚨 The reading is only publication-wide if it saw every call, so it NAMES the lanes it folded.
    two_calls = [
        receipt("MeshWeaver.AI", [{"name": "S", "sha256": a, "role": "riding"}], lane="modules-floor"),
        receipt("MeshWeaver.Mcp", [{"name": "S", "sha256": b, "role": "riding"}], lane="modules-rest"),
    ]
    code, text = capture(module, two_calls)
    check("folded 2 lane(s): modules-floor, modules-rest" in text,
          "the verdict names which calls it folded", text.strip())
    check("1 carried at more than one BUILD" in text,
          "…and a divergence ACROSS two calls is exactly what a lane-filtered reading would miss",
          text.strip())


# ══════════════════════════════════════════════════════════════════════════════
#  3. Attribution — a lane's own receipts, never a sibling call's
# ══════════════════════════════════════════════════════════════════════════════
def test_attribution(module, tmp: Path) -> None:
    print("\nattribution — this call's receipts only (artifacts are RUN-wide)")
    directory = tmp / "receipts"
    directory.mkdir()
    mine = receipt("MeshWeaver.AI", [{"name": "X", "sha256": "1" * 64, "role": "riding"}], lane="lane-1")
    theirs = receipt("MeshWeaver.AI", [{"name": "X", "sha256": "2" * 64, "role": "riding"}], lane="lane-2")
    (directory / "mine.json").write_text(json.dumps(mine))
    (directory / "theirs.json").write_text(json.dumps(theirs))
    (directory / "broken.json").write_text("{not json")

    picked = module._receipts(directory, "lane-1", ["MeshWeaver.AI"])
    check([p["lane"] for p in picked] == ["lane-1"],
          "a sibling call's receipt is not read — the LANE stamp separates them")

    picked = module._receipts(directory, "lane-1", ["MeshWeaver.Other"])
    check(picked == [], "a module outside THIS call's matrix is not read either")

    code = module.main(["verdict", "--receipts", str(tmp / "absent"), "--lane", "lane-1"])
    check(code == 0, "an absent receipts directory does not crash — verify judges the absence")


# ══════════════════════════════════════════════════════════════════════════════
#  4. FALSIFICATION — mutate the real subject and watch the finding disappear
# ══════════════════════════════════════════════════════════════════════════════
def test_falsification(tmp: Path) -> None:
    print("\nfalsification — the divergence finding must DEPEND on comparing digests")
    source = SCRIPT.read_text()
    mutated = source.replace(
        "diverged = sorted({n for n in names if len({c[1] for c in copies if c[0] == n}) > 1})",
        "diverged = []   # MUTATION: the digest comparison removed",
    )
    if mutated == source:
        check(False, "the mutation anchor still exists in module-set-census.py",
              "the verdict's divergence line was renamed — update this arm")
        return
    path = tmp / "mutated-census.py"
    path.write_text(mutated)
    module = load(path)
    a = hashlib.sha256(b"COLLAB-1").hexdigest()
    b = hashlib.sha256(b"COLLAB-2").hexdigest()
    diverged = [
        receipt("MeshWeaver.AI", [{"name": "C", "sha256": a, "role": "riding"}]),
        receipt("MeshWeaver.Essentials", [{"name": "C", "sha256": b, "role": "riding"}]),
    ]
    code, text = capture(module, diverged, enforce=True)
    check(code == 0 and "::error::" not in text,
          "with the comparison removed the divergent set passes — so the real one is doing the work",
          text.strip())


# ══════════════════════════════════════════════════════════════════════════════
#  5. The wiring — asserted against the REAL workflow, not described
# ══════════════════════════════════════════════════════════════════════════════
def test_wiring() -> None:
    print("\nwiring — node-repo-module-pack.yml actually calls both halves")
    text = PACK_WORKFLOW.read_text()
    # The pack leg reaches the helper through MODULE_SET_CENSUS, the same convention
    # MODULE_PACK_BATCH uses — so BOTH halves are asserted: the variable resolves to this script,
    # and the leg invokes `census` through it. Either alone passes while the other rots.
    check(re.search(r"MODULE_SET_CENSUS:\s*\$\{\{\s*github\.workspace\s*\}\}"
                    r"/build-logic/\.github/scripts/module-set-census\.py", text) is not None,
          "MODULE_SET_CENSUS resolves to this script at the build-logic ref")
    check('"$MODULE_SET_CENSUS" census --bundle' in text,
          "the pack leg takes the census of the bundle it just produced")
    check('"$MODULE_SET_CENSUS" --self-test' in text,
          "the select job's preflight PROVES the fetched helper works, not just that it exists")
    check("module-set-census.py" in text and "verdict --receipts" in text,
          "the verify job reads the verdict")
    # 🚨 The reading must be RUN-wide, not call-wide. A publication spans several calls of this
    # lane; narrowing by lane would make each verifier drop the other call's receipts and print a
    # confident zero — the same denominator error this whole reading exists to fix, one level up.
    tail = text.split("verdict --receipts")[-1].split("\n\n")[0]
    check("--lane" not in tail,
          "the verdict passes NO --lane — a publication spans calls, so folding them is the point")
    check("--declared" not in tail,
          "…and no --declared, for the same reason")
    check("pattern: module-pack-receipt-*" in text,
          "…fed by a download of EVERY receipt in the run, not the lane-scoped one")
    check("--enforce" not in tail,
          "the verdict is NOT enforced yet — deliberate, and pinned so a change is visible")
    # 🚨 The report must still be able to go RED when the reading DID NOT HAPPEN. `continue-on-error`
    # there would leave a required job green while promising a reading nobody took.
    step = text.split("The publication's module set, read at one build per assembly name")[-1]
    step = step.split("      - name:")[0]
    # Match the KEY, not the phrase: the step's own comment explains why it has none, and a bare
    # substring check hits that comment and reports a pass as a failure.
    check(not re.search(r"^\s*continue-on-error\s*:", step, re.M),
          "the reader step carries NO continue-on-error — a reading that cannot fail is not a reading")
    # The census must be embedded in the receipt, or `verify` has nothing to read.
    check('"assemblies"' in text or "assemblies:" in text or "--argjson asm" in text,
          "the census lands ON the receipt, which is the only thing verify collects")


def main() -> int:
    print(f"Executing {SCRIPT.relative_to(ROOT)} against real bundle fixtures")
    module = load()
    with tempfile.TemporaryDirectory() as raw:
        tmp = Path(raw)
        test_census(module, tmp)
        test_verdict(module)
        test_attribution(module, tmp)
        test_falsification(tmp)
    test_wiring()

    print()
    if FAILURES:
        print(f"FAILED — {len(FAILURES)} check(s): " + "; ".join(FAILURES))
        return 1
    print("All checks passed.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
