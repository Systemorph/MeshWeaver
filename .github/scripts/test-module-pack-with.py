#!/usr/bin/env python3
"""test-module-pack-with.py — EXECUTE the SDK path's per-module pack additions (#4367).

`module-pack --deps-closure` REFUSES a bundle (exit 2) for a RID-specific MANAGED asset or a
native at a layout the module loader does not probe, unless something the module NAMES carries it
(`--with <file>` / `--with-native runtimes/<rid>/native/<file>`). `node-repo-module-pack.yml`
composes the pack arguments itself, so a CI-built module states its carrier in its OWN csproj —
`<MeshWeaverPackWith Include="…" />` / `<MeshWeaverPackWithNative Include="…" />` — and the SDK
path reads them with `dotnet msbuild -getItem` and appends one flag per item. Without that read the
refusal would prescribe a step no CI-built module could take.

This harness EXTRACTS the block between its markers from the workflow — never a copy — asserts it
sits in the `sdk)` case, and runs it under bash against temp csprojs, with `dotnet` stubbed to
answer in the JSON shape MSBuild's `-getItem` was measured to print (SDK 10.0.400: `{"Items":
{"<type>": [{"Identity": …}, …]}}`, both keys present and empty when nothing is declared) and the
REAL jq. It fails red when it cannot find the block, and it carries a falsification arm: with the
append deleted from the extracted block, the declaring csproj must yield nothing — proof that the
positive case's verdict rests on the append and not on something that always passes.

Stdlib only (plus jq, which every runner image carries; its absence is RED, never a skip).
"""
from __future__ import annotations

import os
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

WORKFLOW = Path(__file__).resolve().parents[1] / "workflows" / "node-repo-module-pack.yml"
START = "# >>> module pack additions (#4367)"
END = "# <<< module pack additions"
APPEND = '>> "$packargs"'

# `dotnet` as the block calls it: records every argument, then answers like `dotnet msbuild
# <csproj> -getItem:A -getItem:B` — the requested item types, each an Identity list, read from the
# csproj's own XML. STUB_MODE switches it to the two failure shapes the block must turn RED.
STUB = r'''
import json, os, sys
import xml.etree.ElementTree as ET
args = sys.argv[1:]
with open(os.environ["DOTNET_ARGS_LOG"], "a") as log:
    log.write("\n".join(args) + "\n")
mode = os.environ.get("STUB_MODE", "msbuild")
if mode == "fail":
    sys.stderr.write("MSB1009: Project file does not exist.\n")
    sys.exit(1)
if mode == "garbage":
    print("MSBuild version 17.14 for .NET\nthis is not JSON")
    sys.exit(0)
if mode == "no-items":
    print("{}")
    sys.exit(0)
assert args[0] == "msbuild", args
root = ET.parse(args[1]).getroot()
wanted = [a.split(":", 1)[1] for a in args if a.startswith("-getItem:")]
print(json.dumps({"Items": {w: [{"Identity": e.get("Include")} for e in root.iter(w)] for w in wanted}},
                 indent=2))
'''

DECLARING = """<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <MeshWeaverPackWith Include="System.IO.Ports.dll" />
    <MeshWeaverPackWith Include="libflat.so" />
    <MeshWeaverPackWithNative Include="runtimes/linux-x64/native/libodd.so" />
  </ItemGroup>
</Project>
"""
DECLARING_NONE = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
</Project>
"""


def block_lines() -> tuple[list[str], int]:
    lines = WORKFLOW.read_text().splitlines()
    starts = [i for i, l in enumerate(lines) if l.strip().startswith(START)]
    if len(starts) != 1:
        raise AssertionError(f"expected exactly ONE '{START}' block in {WORKFLOW.name}, found "
                             f"{len(starts)} — the SDK path's csproj read is gone or duplicated")
    a = starts[0]
    b = next((k for k in range(a, len(lines)) if lines[k].strip() == END), None)
    if b is None:
        raise AssertionError(f"'{START}' has no closing '{END}'")
    return lines[a:b + 1], a


def dedent(lines: list[str]) -> str:
    width = min(len(l) - len(l.lstrip()) for l in lines if l.strip())
    return "\n".join(l[width:] for l in lines)


def run(block: str, csproj: str | None, mode: str = "msbuild", refs: str | None = None) -> dict:
    root = Path(tempfile.mkdtemp())
    workspace = root / "caller"
    (workspace / "src" / "Mod").mkdir(parents=True)
    if csproj is not None:
        (workspace / "src" / "Mod" / "Mod.csproj").write_text(csproj)
    stub = root / "dotnet_stub.py"
    stub.write_text(STUB)
    packargs = root / "pack-args"
    packargs.write_text("--deps-closure\n")
    script = "\n".join([
        "set -euo pipefail",
        f'dotnet() {{ python3 "{stub}" "$@"; }}',
        f'packargs="{packargs}"',
        block,
    ])
    env = dict(os.environ, GITHUB_WORKSPACE=str(workspace), PROJECT="src/Mod/Mod.csproj",
               VERSION="1.2.3", DOTNET_ARGS_LOG=str(root / "dotnet-args"), STUB_MODE=mode)
    env.pop("REFS", None)
    if refs is not None:
        env["REFS"] = refs
    proc = subprocess.run(["bash", "-c", script], cwd=workspace, env=env,
                          capture_output=True, text=True)
    log = root / "dotnet-args"
    return {
        "rc": proc.returncode,
        "stdout": proc.stdout,
        "stderr": proc.stderr,
        "packargs": packargs.read_text().splitlines(),
        "dotnet": log.read_text().splitlines() if log.exists() else [],
        "workspace": str(workspace),
    }


class ModulePackWithTest(unittest.TestCase):
    def setUp(self):
        if shutil.which("jq") is None:
            self.fail("jq is not on PATH — the block under test pipes MSBuild's answer through jq, "
                      "so without it this harness would test nothing. RED, never a skip.")
        lines, self.at = block_lines()
        self.block = dedent(lines)

    def test_the_block_sits_in_the_sdk_case(self):
        lines = WORKFLOW.read_text().splitlines()
        last_sdk = max(i for i in range(self.at) if lines[i].strip() == "sdk)")
        containers = [i for i in range(self.at) if lines[i].strip() == "container)"]
        self.assertTrue(not containers or max(containers) < last_sdk,
                        "the csproj read must run on the SDK path — the only path that packs with "
                        "--deps-closure, and so the only one the refusal fires on")
        self.assertIn("--deps-closure", lines[self.at - 1],
                      "the read belongs right after the --deps-closure flag it exists to satisfy")

    def test_a_csproj_declaring_items_yields_one_flag_per_item(self):
        r = run(self.block, DECLARING)
        self.assertEqual(0, r["rc"], r["stderr"])
        self.assertEqual(["--deps-closure",
                          "--with", "System.IO.Ports.dll",
                          "--with", "libflat.so",
                          "--with-native", "runtimes/linux-x64/native/libodd.so"], r["packargs"])
        self.assertIn("3 pack addition(s) declared", r["stdout"])

    def test_a_csproj_declaring_none_yields_none(self):
        r = run(self.block, DECLARING_NONE)
        self.assertEqual(0, r["rc"], r["stderr"])
        self.assertEqual(["--deps-closure"], r["packargs"])
        self.assertIn("0 pack addition(s) declared", r["stdout"])

    def test_the_read_is_msbuilds_own_evaluation_under_the_builds_properties(self):
        r = run(self.block, DECLARING, refs="/refs/platform")
        self.assertEqual(0, r["rc"], r["stderr"])
        args = r["dotnet"]
        self.assertEqual(["msbuild", "src/Mod/Mod.csproj"], args[:2])
        for expected in ("-getItem:MeshWeaverPackWith", "-getItem:MeshWeaverPackWithNative",
                         "-p:Configuration=Release", f"-p:MeshWeaverRoot={r['workspace']}/meshweaver",
                         "-p:Version=1.2.3", "-p:MeshWeaverRefs=/refs/platform"):
            self.assertIn(expected, args)

    def test_no_refs_passes_no_refs_property(self):
        r = run(self.block, DECLARING)
        self.assertEqual(0, r["rc"], r["stderr"])
        self.assertFalse(any(a.startswith("-p:MeshWeaverRefs") for a in r["dotnet"]))

    def test_a_failed_evaluation_is_red_never_declares_none(self):
        r = run(self.block, DECLARING, mode="fail")
        self.assertNotEqual(0, r["rc"])
        self.assertEqual(["--deps-closure"], r["packargs"])

    def test_an_answer_that_is_not_the_json_shape_is_red(self):
        for mode in ("garbage", "no-items"):
            r = run(self.block, DECLARING, mode=mode)
            self.assertNotEqual(0, r["rc"], f"{mode}: {r['stdout']}")
            self.assertEqual(["--deps-closure"], r["packargs"], mode)

    def test_falsification_without_the_append_the_declaring_csproj_yields_nothing(self):
        mutated = "\n".join(l for l in self.block.splitlines() if APPEND not in l)
        self.assertNotEqual(self.block, mutated, f"the block carries no '{APPEND}' to delete")
        r = run(mutated, DECLARING)
        self.assertNotIn("--with", r["packargs"],
                         "the positive case must rest on the append — if this still yields flags, "
                         "its assertion would pass whatever the block did")


if __name__ == "__main__":
    unittest.main()
