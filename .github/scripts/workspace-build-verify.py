#!/usr/bin/env python3
"""workspace-build-verify.py — the ONE global workspace build asserts its OWN postcondition.

    "the workspace build is allowed to report success while not having built something a
     downstream job will require. A green there is not evidence that the set is complete."
    — Plugins#1051

WHAT THIS IS FOR
----------------
`build-workspace` compiles every SELECTED container entry once, centrally, and hands the result
to the pack matrix as one artifact. The matrix then demands, per module:

    <workspace>/<Module>/<Module>.dll        the emitted assembly
    <workspace>/<Module>.closure.txt         which in-tree siblings ride THIS bundle
    <workspace>/workspace-build.log          carrying `platform AssemblyVersion <v>`

Until this script, NOTHING asserted that the build had produced them. The build's exit code says
"the compiler did not fail"; it says nothing about the SET. So two enumerators — the projects the
build was handed, and the modules the matrix demands — were free to disagree, and the
disagreement surfaced N jobs later as seven red bundle jobs saying `the global workspace build
produced no MeshWeaver.AI.OpenAI.dll`, on a PR whose whole diff was one XML doc comment
(Plugins#1051, run 33480755857). The error was accurate and in the wrong place: the job that
could have named the missing module was green.

🚨 IT IS THE SAME ENUMERATOR ON BOTH SIDES, ON PURPOSE. This script is fed
`needs.select.outputs.modules` — the exact JSON the pack matrix expands. A postcondition derived
from anything else (a glob of the output, the caller's declared list, a re-derivation from the
projects) would be a second enumerator, which is the defect one level up.

🚨 A GLOB WOULD PASS THE CASE THIS EXISTS TO CATCH. The workspace holds every entry's assemblies
side by side plus their in-tree dependencies, so "the output directory has .dlls in it" is true
in exactly the failure being diagnosed. Only a per-selected-module check can see it — which is
also why the self-test's headline mutation is a NON-EMPTY workspace missing one selected module.

WHAT IT DELIBERATELY DOES NOT DO
--------------------------------
It never asks the reverse question. The workspace legitimately carries assemblies nobody
selected — an in-tree `ProjectReference` of a selected module is emitted beside it and rides its
bundle by way of the closure manifest — so "emitted but not selected" is normal and is not a
finding.

    workspace-build-verify.py --modules @matrix.json --output "$RUNNER_TEMP/module-build"
    workspace-build-verify.py --self-test

EXIT 0 only when every selected `build: container` entry has a non-empty assembly, a closure
manifest, and a build log the binding-identity check downstream can actually read.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import sys
from pathlib import Path

# The same value the pack job extracts with
# `sed -n 's/.*platform AssemblyVersion \([0-9][0-9.]*\).*/\1/p'` — the expected side of the
# MeshWeaver#143 binding-identity check. When it is unreadable EVERY pack job dies on
# "a check that quietly does not run reads exactly like one that passed"; naming it here costs
# one job instead of N.
PLATFORM_VERSION = re.compile(r"platform AssemblyVersion ([0-9][0-9.]*)")


def container_entries(modules: object) -> list[dict]:
    """The selection's `build: container` entries — the ones this job actually compiles.

    `sdk` entries are built by the pack job on the runner and are none of this job's business;
    an entry with no `build` key defaults to `sdk`, exactly as the workflow reads it.
    """
    if not isinstance(modules, list):
        return []
    return [e for e in modules
            if isinstance(e, dict) and (e.get("build") or "sdk") == "container"]


def verify(entries: list[dict], out: Path) -> tuple[int, list[str], list[str]]:
    """(exit code, errors, notes) for one workspace output directory."""
    errors: list[str] = []
    notes: list[str] = []

    if not entries:
        notes.append("the selection carries no `build: container` entry — the global build had "
                     "nothing to emit, and there is no postcondition to assert.")
        return 0, errors, notes

    if not out.is_dir():
        errors.append(
            f"the global build reported success but wrote no output directory at {out}. Every "
            f"pack job in this call downloads that directory as its ONLY input; without it all "
            f"{len(entries)} of them would fail one job later, each blaming its own module.")
        return 1, errors, notes

    for entry in entries:
        module = entry.get("module")
        project = entry.get("project", "<no project>")
        if not isinstance(module, str) or not module:
            errors.append(f"a container entry building {project} declares no `module` name — the "
                          "pack matrix keys every artifact on it, so it cannot be blank.")
            continue
        assembly = out / module / f"{module}.dll"
        closure = out / f"{module}.closure.txt"
        if not assembly.is_file():
            errors.append(
                f"{module}: the global build emitted no {module}/{module}.dll. It was handed "
                f"{project} and returned success, so the ASSEMBLY NAME the matrix demands and the "
                f"one the project emits disagree, or the entry was never compiled. Fix the "
                f"selection or the entry — there is deliberately no local rebuild downstream to "
                f"slide into.")
            continue
        if assembly.stat().st_size == 0:
            errors.append(f"{module}: {module}/{module}.dll is ZERO BYTES — an emit that was "
                          "truncated reads to the pack job as a file that is present.")
            continue
        if not closure.is_file():
            errors.append(
                f"{module}: the global build wrote no {module}.closure.txt. A shared workspace "
                f"holds every entry's assemblies side by side, so without it the pack job cannot "
                f"know which in-tree siblings ride THIS bundle and globbing would ride every "
                f"other module's (Plugins#1077).")
            continue

    log = out / "workspace-build.log"
    if not log.is_file():
        errors.append(f"the global build left no workspace-build.log in {out}. The pack job reads "
                      "the platform AssemblyVersion out of it for the MeshWeaver#143 "
                      "binding-identity check, and a check that cannot run reads exactly like one "
                      "that passed.")
    else:
        text = log.read_text(encoding="utf-8", errors="replace")
        found = PLATFORM_VERSION.search(text)
        if not found:
            errors.append("workspace-build.log carries no `platform AssemblyVersion <v>` line. "
                          "That value is the EXPECTED side of the binding-identity check every "
                          "pack job runs (MeshWeaver#143); unreadable here, it reds all of them.")
        else:
            notes.append(f"the build log carries the platform AssemblyVersion "
                         f"({found.group(1)}) the binding-identity check compares against")

    if not errors:
        named = ", ".join(sorted(e["module"] for e in entries if e.get("module")))
        notes.insert(0, f"all {len(entries)} selected container entr(y|ies) emitted an assembly "
                        f"AND a closure manifest: {named}")
    return (1 if errors else 0), errors, notes


# ─────────────────────────────── THE GATE'S OWN PROOF ───────────────────────────────
# A postcondition that cannot fail is the very defect this script exists to remove, one level up.
# Every case below starts from the GREEN fixture and mutates ONE thing, so a case that stops
# firing is visible as a case that stopped firing rather than as a quiet pass.

def _fixture(root: Path, modules: list[str], *, log: bool = True,
             platform_version: str = "3.0.0.0") -> None:
    root.mkdir(parents=True, exist_ok=True)
    for module in modules:
        (root / module).mkdir(exist_ok=True)
        (root / module / f"{module}.dll").write_bytes(b"MZ\x90\x00")
        (root / f"{module}.closure.txt").write_text(f"{module}\n", encoding="utf-8")
    if log:
        (root / "workspace-build.log").write_text(
            "[MeshWeaver.AI] start — 168 source file(s)\n"
            f"platform AssemblyVersion {platform_version}\n"
            "ok\n", encoding="utf-8")


def _matrix(modules: list[str], sdk: list[str] | None = None) -> list[dict]:
    entries = [{"package": m.split(".")[-1], "module": m,
                "project": f"src/{m}/{m}.csproj", "build": "container"} for m in modules]
    entries += [{"package": m.split(".")[-1], "module": m,
                 "project": f"src/{m}/{m}.csproj"} for m in (sdk or [])]
    return entries


def self_test() -> int:
    import tempfile
    failures: list[str] = []

    def check(name: str, ok: bool, detail: str = "") -> None:
        print(f"  {'✓' if ok else '✗'} {name}{'' if ok else ': ' + detail}")
        if not ok:
            failures.append(name)

    three = ["MeshWeaver.AI", "MeshWeaver.AI.OpenAI", "MeshWeaver.Mcp"]

    with tempfile.TemporaryDirectory() as tmp:
        out = Path(tmp) / "module-build"
        _fixture(out, three)

        print("the green run every mutation below is a mutation OF:")
        code, errors, notes = verify(container_entries(_matrix(three)), out)
        check("a workspace holding every selected entry ⇒ pass", code == 0, f"{errors}")
        check("and it SAYS what it verified", any("closure manifest" in n for n in notes),
              f"{notes}")

        print("mutations — each must turn RED and NAME the module:")

        # 🚨 THE HEADLINE CASE, and Plugins#1051 verbatim: the workspace is NOT empty — it holds
        # MeshWeaver.AI and MeshWeaver.Mcp and their closure manifests — and the one module the
        # matrix will demand is absent. Any glob-shaped or count-shaped postcondition passes here.
        (out / "MeshWeaver.AI.OpenAI" / "MeshWeaver.AI.OpenAI.dll").unlink()
        code, errors, _ = verify(container_entries(_matrix(three)), out)
        check("a NON-EMPTY workspace missing ONE selected module's dll fails, naming it",
              code == 1 and any("MeshWeaver.AI.OpenAI" in e and "emitted no" in e for e in errors),
              f"{errors}")
        check("…and the other two are not blamed for it",
              code == 1 and len(errors) == 1, f"{errors}")
        _fixture(out, three)

        (out / "MeshWeaver.Mcp" / "MeshWeaver.Mcp.dll").write_bytes(b"")
        code, errors, _ = verify(container_entries(_matrix(three)), out)
        check("a ZERO-BYTE assembly fails (present is not the same as emitted)",
              code == 1 and any("MeshWeaver.Mcp" in e and "ZERO BYTES" in e for e in errors),
              f"{errors}")
        _fixture(out, three)

        (out / "MeshWeaver.AI.closure.txt").unlink()
        code, errors, _ = verify(container_entries(_matrix(three)), out)
        check("a missing closure manifest fails HERE instead of in the pack job",
              code == 1 and any("MeshWeaver.AI" in e and "closure.txt" in e for e in errors),
              f"{errors}")
        _fixture(out, three)

        (out / "workspace-build.log").unlink()
        code, errors, _ = verify(container_entries(_matrix(three)), out)
        check("no build log ⇒ the binding-identity check downstream cannot run ⇒ red",
              code == 1 and any("workspace-build.log" in e for e in errors), f"{errors}")

        _fixture(out, three, log=False)
        (out / "workspace-build.log").write_text("[MeshWeaver.AI] start\nok\n", encoding="utf-8")
        code, errors, _ = verify(container_entries(_matrix(three)), out)
        check("a build log with no `platform AssemblyVersion` line ⇒ red",
              code == 1 and any("binding-identity" in e for e in errors), f"{errors}")
        _fixture(out, three)

        code, errors, _ = verify(container_entries(_matrix(three)), Path(tmp) / "nowhere")
        check("no output directory at all ⇒ red, naming every pack job that would have died",
              code == 1 and any("no output directory" in e for e in errors), f"{errors}")

        code, errors, _ = verify(
            container_entries([{"package": "X", "project": "src/X/X.csproj",
                                "build": "container"}]), out)
        check("a container entry with no `module` name ⇒ red (the matrix keys artifacts on it)",
              code == 1 and any("declares no `module`" in e for e in errors), f"{errors}")

        print("cases that must NOT fire — the bias that keeps the assertion honest:")

        # An `sdk` entry is built by the pack job on the runner; the workspace holds nothing for
        # it and never should. Asserting one here would red every mixed selection.
        code, errors, _ = verify(container_entries(_matrix(three, sdk=["MeshWeaver.Testing"])), out)
        check("an `sdk` entry is not this job's to emit ⇒ pass", code == 0, f"{errors}")

        # A `build` key that is absent means `sdk` — the workflow's own default. Reading it as
        # container would red exactly the entries this job must ignore.
        code, errors, notes = verify(container_entries(_matrix([], sdk=three)), out)
        check("an entry with NO `build` key defaults to sdk ⇒ nothing asserted",
              code == 0 and any("no `build: container` entry" in n for n in notes),
              f"{errors} {notes}")

        # The workspace carries in-tree dependencies of selected modules beside them. They ride
        # their dependents' bundles by way of the closure manifest and are NOT selections.
        (out / "MeshWeaver.Graph").mkdir(exist_ok=True)
        (out / "MeshWeaver.Graph" / "MeshWeaver.Graph.dll").write_bytes(b"MZ")
        code, errors, _ = verify(container_entries(_matrix(three)), out)
        check("an emitted assembly nobody selected is a dependency, not a finding ⇒ pass",
              code == 0, f"{errors}")

        # An empty selection reaches this script only if the workflow's own short-circuit changed;
        # it must not invent a failure out of nothing to assert.
        code, errors, notes = verify(container_entries([]), out)
        check("an empty selection asserts nothing and says so",
              code == 0 and any("nothing to emit" in n for n in notes), f"{errors} {notes}")

    if failures:
        print(f"\n::error title=workspace-build-verify self-test failed::{len(failures)} case(s). "
              "This is the only thing that makes the ONE global build's green mean 'the selected "
              "set was emitted' rather than 'the compiler did not throw'.")
        return 1
    print("\n✓ workspace-build-verify self-test: 2 green-run assertions, 8 mutations that must go "
          "red (headline: a NON-EMPTY workspace missing one selected module — the case a glob "
          "passes), and 4 cases that must NOT fire (sdk entries, a defaulted `build` key, an "
          "unselected in-tree dependency, an empty selection) — all green.")
    return 0


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0])
    p.add_argument("--modules", default="",
                   help="the SELECTION the pack matrix expands (select.outputs.modules), as JSON "
                        "or @path-to-a-file. The same enumerator on both sides, deliberately.")
    p.add_argument("--output", default="",
                   help="the global build's output directory (what gets uploaded as workspace-build-<lane>)")
    p.add_argument("--self-test", action="store_true", dest="self_test")
    args = p.parse_args()
    if args.self_test:
        return self_test()
    if not args.modules or not args.output:
        p.error("--modules and --output are required")

    raw = args.modules
    if raw.startswith("@"):
        raw = Path(raw[1:]).read_text(encoding="utf-8")
    try:
        modules = json.loads(raw)
    except json.JSONDecodeError as ex:
        print(f"::error title=Workspace build incomplete::the selection could not be parsed as "
              f"JSON ({ex}) — the postcondition cannot be asserted, and an assertion that cannot "
              f"run reads exactly like one that passed.")
        return 1

    entries = container_entries(modules)
    code, errors, notes = verify(entries, Path(args.output))
    for note in notes:
        print(f"✓ {note}")
    for error in errors:
        print(f"::error title=Workspace build incomplete::{error}")
    if os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as fh:
            fh.write(f"### {'✅' if code == 0 else '❌'} The global build emitted what was selected\n\n")
            fh.write(f"{len(entries)} container entr(y|ies) selected\n\n")
            for line in notes + errors:
                fh.write(f"- {line}\n")
    return code


if __name__ == "__main__":
    sys.exit(main())
