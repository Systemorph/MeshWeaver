#!/usr/bin/env python3
"""test-module-pack-extension-sdk.py: `select` keys the SDK the CALLER's checkout resolves.

`node-repo-module-pack.yml` keys a build extension's publishing SDK in `select` and publishes with
exactly that version in `build-workspace`. The two jobs check the caller out at DIFFERENT places:
`select` at `repo/`, `build-workspace` at the workspace root. `dotnet` picks its SDK from the first
`global.json` walking up from its working directory, so a `select` that resolves at the workspace
root never sees the caller's pin. It keys a version the publish job then refuses as a mismatch, or
names an SDK the caller pinned away from.

This harness reads the workflow (never a copy) and checks the structure: `select` installs from
`repo/global.json` when the caller has one, falls back to the floating `10.0.x` only when it has
none, and runs the resolve step with `working-directory: repo`. `build-workspace` then installs the
version `select` keyed. It also RUNS the resolve step's real bash, from the working directory the
workflow declares, against a stub `dotnet` that answers the way SDK resolution does: the pinned
version where a `global.json` is in scope, the runner's newest otherwise. The negative control runs
the same bash from the workspace root, and it must NOT yield the caller's pin. That proves the
verdict rests on the working directory, not on a stub that always agrees.
"""
from __future__ import annotations

import copy
import json
import os
import subprocess
import sys
import tempfile
from pathlib import Path

import yaml

ROOT = Path(__file__).resolve().parents[2]
PACK = ROOT / ".github" / "workflows" / "node-repo-module-pack.yml"
RESOLVE = "Resolve the SDK the build extension is published with"
PIN, NEWEST = "10.0.105", "10.0.300"

failures: list[str] = []


def check(label: str, ok: bool, detail: str = "") -> None:
    print(("  ok   " if ok else "  FAIL ") + label + (f" — {detail}" if detail and not ok else ""))
    if not ok:
        failures.append(label)


def load() -> dict:
    doc = yaml.safe_load(PACK.read_text(encoding="utf-8"))
    if True in doc and "on" not in doc:
        doc["on"] = doc.pop(True)
    return doc


def structure_problems(doc: dict) -> list[str]:
    problems: list[str] = []
    sel = doc["jobs"]["select"]["steps"]
    names = [s.get("name") for s in sel]
    if RESOLVE not in names:
        return [f"select has no step {RESOLVE!r}"]
    i = names.index(RESOLVE)
    resolve = sel[i]
    if (resolve.get("working-directory") or "").rstrip("/") != "repo":
        problems.append("the resolve step does not run in the caller's checkout (working-directory: repo)")
    setups = [s for s in sel[:i] if str(s.get("uses", "")).startswith("actions/setup-dotnet@")
              and "build-extensions-project" in str(s.get("if", ""))]
    pinned = [s for s in setups if (s.get("with") or {}).get("global-json-file") == "repo/global.json"
              and "hashFiles('repo/global.json') != ''" in str(s.get("if", ""))]
    floating = [s for s in setups if "dotnet-version" in (s.get("with") or {})]
    if not pinned:
        problems.append("select does not install the SDK the caller's repo/global.json names")
    if any("hashFiles('repo/global.json') == ''" not in str(s.get("if", "")) for s in floating):
        problems.append("select installs a floating SDK even when the caller pins one in repo/global.json")
    bw = doc["jobs"]["build-workspace"]["steps"]
    if not any(str(s.get("uses", "")).startswith("actions/setup-dotnet@")
               and "needs.select.outputs.build-extensions-sdk" in str((s.get("with") or {}).get("dotnet-version", ""))
               for s in bw):
        problems.append("build-workspace does not install the exact SDK select keyed")
    return problems


def mutations(doc: dict) -> list[tuple[str, dict]]:
    out: list[tuple[str, dict]] = []
    m = copy.deepcopy(doc)
    for s in m["jobs"]["select"]["steps"]:
        if s.get("name") == RESOLVE:
            s.pop("working-directory", None)
    out.append(("the resolve step runs at the workspace root", m))
    m = copy.deepcopy(doc)
    m["jobs"]["select"]["steps"] = [s for s in m["jobs"]["select"]["steps"]
                                    if (s.get("with") or {}).get("global-json-file") != "repo/global.json"]
    out.append(("the caller's global.json is not installed from", m))
    m = copy.deepcopy(doc)
    for s in m["jobs"]["select"]["steps"]:
        if "dotnet-version" in (s.get("with") or {}) and "build-extensions-project" in str(s.get("if", "")):
            s["if"] = "inputs.build-extensions-project != ''"
    out.append(("the floating SDK is installed regardless of the caller's pin", m))
    return out


STUB = f'''#!/usr/bin/env python3
import json, os, sys
d = os.getcwd()
while True:
    g = os.path.join(d, "global.json")
    if os.path.isfile(g):
        print(json.load(open(g))["sdk"]["version"]); sys.exit(0)
    up = os.path.dirname(d)
    if up == d:
        break
    d = up
print("{NEWEST}")
'''


def run_resolve(body: str, cwd: Path, tmp: Path) -> tuple[int, str, dict]:
    assert "${{" not in body, "the resolve step must read its inputs from env, never an inline expression"
    bindir = tmp / "bin"
    bindir.mkdir(exist_ok=True)
    stub = bindir / "dotnet"
    stub.write_text(STUB, encoding="utf-8")
    stub.chmod(0o755)
    out = tmp / "out.txt"
    out.write_text("", encoding="utf-8")
    script = tmp / "step.sh"
    script.write_text(body, encoding="utf-8")
    env = {"PATH": f"{bindir}{os.pathsep}{os.environ['PATH']}", "GITHUB_OUTPUT": str(out)}
    r = subprocess.run(["bash", "-e", str(script)], env=env, capture_output=True, text=True, cwd=cwd)
    outputs = dict(line.split("=", 1) for line in out.read_text(encoding="utf-8").splitlines() if "=" in line)
    return r.returncode, r.stdout + r.stderr, outputs


def behaviour(doc: dict) -> None:
    resolve = next(s for s in doc["jobs"]["select"]["steps"] if s.get("name") == RESOLVE)
    with tempfile.TemporaryDirectory() as t:
        ws = Path(t) / "workspace"
        (ws / "repo").mkdir(parents=True)
        (ws / "repo" / "global.json").write_text(json.dumps({"sdk": {"version": PIN}}), encoding="utf-8")
        cwd = ws / (resolve.get("working-directory") or ".")
        code, log, out = run_resolve(resolve["run"], cwd, Path(t))
        check(f"select keys the caller's pinned SDK ({PIN}) from the declared working directory",
              code == 0 and out.get("version") == PIN, f"exit={code} version={out.get('version')} {log[-200:]}")
        code, log, out = run_resolve(resolve["run"], ws, Path(t))
        check("NEGATIVE CONTROL — the same bash at the workspace root does NOT yield the caller's pin",
              code == 0 and out.get("version") == NEWEST, f"exit={code} version={out.get('version')}")
        (ws / "repo" / "global.json").unlink()
        code, log, out = run_resolve(resolve["run"], cwd, Path(t))
        check("a caller without global.json keys what the runner resolves",
              code == 0 and out.get("version") == NEWEST, f"exit={code} version={out.get('version')}")
        (ws / "repo" / "global.json").write_text(json.dumps({"sdk": {"version": "9.0.100"}}), encoding="utf-8")
        code, log, out = run_resolve(resolve["run"], cwd, Path(t))
        check("a non-10.0 SDK is refused, not keyed", code != 0 and "version" not in out, f"exit={code}")


def main() -> int:
    doc = load()
    print("== structure")
    problems = structure_problems(doc)
    check("select resolves the extension SDK from the caller's checkout", not problems, "; ".join(problems))
    print("== structure: every rule can say no (mutations)")
    for label, mutated in mutations(doc):
        check(f"MUTATION caught: {label}", bool(structure_problems(mutated)), "the mutated workflow passed")
    print("== behaviour")
    behaviour(doc)
    print()
    if failures:
        print(f"::error::{len(failures)} case(s) FAILED: " + "; ".join(failures))
        return 1
    print("test-module-pack-extension-sdk: select keys the SDK the caller's global.json resolves, and every rule can say no")
    return 0


if __name__ == "__main__":
    sys.exit(main())
