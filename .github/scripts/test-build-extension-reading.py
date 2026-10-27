#!/usr/bin/env python3
"""test-build-extension-reading.py: the REAL Razor extension parses and keys cleanly.

`node-repo-scope.py` reads a build extension's MSBuild graph, and `module-build-key.py` keys it,
refusing anything they cannot model exactly. A refusal rule written against fixtures alone once
refused the one real extension, `tools/MeshWeaver.PluginTester.Razor`: it stages its source
generator from `$(MeshWeaverRazorGeneratorSource)/*.dll`, a property it defines from the SDK's own
`$(NetCoreRoot)`. This positive control runs both scripts over THIS repository's tree, so that
regression cannot ship again. It lives here and not in either script's `--self-test`, because
those also run where no core tree exists (the build-host image, satellite lanes).

It asserts:
  - scope: no refusal, and the reach covers the extension's referenced project and its manifest;
  - key: keyed with a publishing SDK, it records that SDK (proof the SDK path was really read);
  - NEGATIVE: keyed WITHOUT an SDK, it is refused by name (SdkNotKeyed), naming the file and the
    expression, so the positive above cannot be passing by reading nothing.
"""
from __future__ import annotations

import importlib.util
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPTS = ROOT / ".github" / "scripts"
EXTENSION = "tools/MeshWeaver.PluginTester.Razor/MeshWeaver.PluginTester.Razor.csproj"

failures: list[str] = []


def check(label: str, ok: bool, detail: str = "") -> None:
    print(("  ok   " if ok else "  FAIL ") + label + (f" — {detail}" if detail and not ok else ""))
    if not ok:
        failures.append(label)


def load(name: str, module: str):
    spec = importlib.util.spec_from_file_location(module, SCRIPTS / name)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def main() -> int:
    if not (ROOT / EXTENSION).is_file():
        print(f"::error::{EXTENSION} is not in this tree; this control has nothing to check. If the extension "
              "moved, point EXTENSION at its new home rather than deleting the control.")
        return 1
    scope = load("node-repo-scope.py", "node_repo_scope")
    key = load("module-build-key.py", "module_build_key")

    print("== scope reads the real extension")
    try:
        reach = scope.shelf_dirs(ROOT, EXTENSION)
        check("no refusal", reach is not None)
        hit = lambda f: any(f.startswith(d + "/") or scope.msbuild_glob_matches(f, d) for d in reach)
        check("the reach covers the referenced Extensibility project",
              hit("tools/MeshWeaver.PluginTester.Extensibility/X.cs"), str(sorted(reach)))
        check("the reach covers the extension's manifest", hit("tools/MeshWeaver.PluginTester.Razor/build-extension.json"))
    except scope.UnmodeledMSBuild as exc:
        check("no refusal", False, str(exc))

    print("== the key reads the real extension")
    try:
        got = key.shelf_inputs(ROOT, EXTENSION, "build-extensions-project", sdk="10.0.200")
        check("keyed cleanly with a publishing SDK", True)
        check("the SDK it stages the generator from is recorded (the SDK path was really read)",
              got.get("sdk") == "10.0.200", str(got.get("sdk")))
        check("the closure includes the Extensibility project",
              "tools/MeshWeaver.PluginTester.Extensibility" in got["closure"], str(sorted(got["closure"])))
    except ValueError as exc:
        check("keyed cleanly with a publishing SDK", False, str(exc))

    print("== negative control: without a publishing SDK it is refused, by name")
    try:
        key.shelf_inputs(ROOT, EXTENSION, "build-extensions-project", sdk="")
        check("refused without an SDK", False, "no error raised")
    except ValueError as exc:
        # module-build-key.py loads its OWN copy of node-repo-scope.py, so the class is matched by name.
        check("refused without an SDK (SdkNotKeyed), naming the file and the expression",
              type(exc).__name__ == "SdkNotKeyed" and EXTENSION in str(exc)
              and "$(MeshWeaverRazorGeneratorSource)" in str(exc), f"{type(exc).__name__}: {exc}")

    print()
    if failures:
        print(f"::error::{len(failures)} case(s) FAILED: " + "; ".join(failures))
        return 1
    print("test-build-extension-reading: the real Razor extension parses and keys cleanly, and refuses without its SDK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
