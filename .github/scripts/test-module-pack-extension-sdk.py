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
import re
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
    # MSBuild's own evaluation: produced AFTER the SDK is resolved, BEFORE the scope, and read by the
    # scope, the ledger's keys and the pack job's newer-commit check — one file, every consumer.
    ev_names = [s.get("name") for s in sel]
    if "Evaluate the shelf / build extension with MSBuild" not in ev_names:
        problems.append("select does not evaluate the shelf / extension with MSBuild")
    else:
        e = ev_names.index("Evaluate the shelf / build extension with MSBuild")
        if not (i < e < ev_names.index("Decide which module bundles this diff can reach")):
            problems.append("the MSBuild evaluation does not run after the SDK resolve and before the scope")
        if "msbuild-evaluate.py" not in sel[e].get("run", ""):
            problems.append("the evaluation step does not run msbuild-evaluate.py")
        body = sel[e].get("run", "")
        if not ("worktree add" in body and "--skip-missing" in body and "msbuild-evaluation-base.json" in body):
            problems.append("the evaluation step does not also evaluate the diff BASE tree (BASE ∪ HEAD)")
        if not ('--pin-sdk "$HEAD_SDK"' in body and "steps.ext-sdk.outputs.version" in str(sel[e].get("env", {}))):
            problems.append("the BASE tree is not evaluated under the HEAD's resolved SDK")
    scope_body = next((s.get("run", "") for s in sel if s.get("name") == "Decide which module bundles this diff can reach"), "")
    if "msbuild-evaluation-base.json" not in scope_body:
        problems.append("the scope does not union the BASE evaluation with HEAD's")
    for step_name in ("Decide which module bundles this diff can reach", "Consult the module build ledger"):
        body = next((s.get("run", "") for s in sel if s.get("name") == step_name), "")
        if "--msbuild-evaluation" not in body:
            problems.append(f"select's {step_name!r} does not pass --msbuild-evaluation")
    if "msbuild-eval" not in str((doc["jobs"]["select"].get("outputs") or {}).get("msbuild-evaluation", "")):
        problems.append("select does not output its MSBuild evaluation")
    newer = next((s for s in doc["jobs"]["pack"]["steps"] if s.get("id") == "newer"), {})
    if "--msbuild-evaluation" not in newer.get("run", "") or "msbuild-evaluation" not in str(newer.get("env", {})):
        problems.append("the pack job's newer-commit scope check does not read select's MSBuild evaluation")
    nb = newer.get("run", "")
    if not ("worktree add" in nb and "msbuild-evaluate.py" in nb and "msbuild-evaluation-tip.json" in nb
            and '--pin-sdk "$KEYED_SDK"' in nb):
        problems.append("the supersession check does not union this run's evaluation with the newer TIP's "
                        "(evaluated under the keyed SDK)")
    pack_steps = doc["jobs"]["pack"]["steps"]
    n = next((i for i, s in enumerate(pack_steps) if s.get("id") == "newer"), 0)
    if not any(str(s.get("uses", "")).startswith("actions/setup-dotnet@")
               and "needs.select.outputs.build-extensions-sdk" in str((s.get("with") or {}).get("dotnet-version", ""))
               for s in pack_steps[:n]):
        problems.append("the pack job does not install the keyed SDK before the supersession check")
    # EVERY setup-dotnet in the lane needs a writable install dir on the non-root fleet runner:
    # DOTNET_INSTALL_DIR (job or step env) under /home/runner, keyed on THAT job's own runner.
    for job_id, job in doc["jobs"].items():
        runs_on = re.sub(r"^\$\{\{\s*|\s*\}\}$", "", str(job.get("runs-on", ""))).strip()
        for st in job.get("steps") or []:
            if not str(st.get("uses", "")).startswith("actions/setup-dotnet@"):
                continue
            install_dir = str((st.get("env") or {}).get("DOTNET_INSTALL_DIR") or (job.get("env") or {}).get("DOTNET_INSTALL_DIR") or "")
            if "/home/runner/.dotnet" not in install_dir or runs_on not in install_dir:
                problems.append(f"job {job_id!r}: a setup-dotnet step has no writable DOTNET_INSTALL_DIR keyed on its "
                                f"runner ({runs_on!r}) — it fails on the non-root fleet runner")
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
    m = copy.deepcopy(doc)
    m["jobs"]["select"]["steps"] = [s for s in m["jobs"]["select"]["steps"]
                                    if s.get("name") != "Evaluate the shelf / build extension with MSBuild"]
    out.append(("select no longer evaluates with MSBuild", m))
    m = copy.deepcopy(doc)
    for s in m["jobs"]["select"]["steps"]:
        if s.get("name") == "Decide which module bundles this diff can reach":
            s["run"] = s["run"].replace("--msbuild-evaluation", "--no-evaluation")
    out.append(("the scope stops reading the evaluation", m))
    m = copy.deepcopy(doc)
    for s in m["jobs"]["select"]["steps"]:
        if s.get("name") == "Decide which module bundles this diff can reach":
            s["run"] = s["run"].replace("msbuild-evaluation-base.json", "no-base.json")
    out.append(("the scope stops unioning the BASE evaluation", m))
    m = copy.deepcopy(doc)
    for s in m["jobs"]["select"]["steps"]:
        if s.get("name") == "Evaluate the shelf / build extension with MSBuild":
            s["run"] = s["run"].replace("worktree add", "worktree skip")
    out.append(("the BASE tree is no longer evaluated", m))
    for job_id in ("select", "build-workspace"):
        m = copy.deepcopy(doc)
        m["jobs"][job_id].get("env", {}).pop("DOTNET_INSTALL_DIR", None)
        out.append((f"{job_id}'s setup-dotnet loses its writable install dir", m))
    m = copy.deepcopy(doc)
    m["jobs"]["select"]["env"]["DOTNET_INSTALL_DIR"] = "${{ inputs.runner != 'ubuntu-latest' && '/home/runner/.dotnet' || '' }}"
    out.append(("select's install dir keyed on ANOTHER job's runner", m))
    m = copy.deepcopy(doc)
    for s in m["jobs"]["select"]["steps"]:
        if s.get("name") == "Evaluate the shelf / build extension with MSBuild":
            s["run"] = s["run"].replace('--pin-sdk "$HEAD_SDK"', "")
    out.append(("the BASE tree is evaluated under whatever SDK it resolves", m))
    m = copy.deepcopy(doc)
    for s in m["jobs"]["pack"]["steps"]:
        if s.get("id") == "newer":
            s["run"] = s["run"].replace("msbuild-evaluation-tip.json", "nothing.json")
    out.append(("the supersession check decides from this run's tree alone", m))
    m = copy.deepcopy(doc)
    for s in m["jobs"]["select"]["steps"]:
        if s.get("name") == "Consult the module build ledger":
            s["run"] = s["run"].replace("--msbuild-evaluation", "--no-evaluation")
    out.append(("the ledger's keys stop reading the evaluation", m))
    m = copy.deepcopy(doc)
    for s in m["jobs"]["pack"]["steps"]:
        if s.get("id") == "newer":
            s["run"] = s["run"].replace("--msbuild-evaluation", "--no-evaluation")
    out.append(("the pack job's newer-commit check stops reading the evaluation", m))
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
