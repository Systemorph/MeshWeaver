#!/usr/bin/env python3
"""test-module-pack-host.py — the module-pack lane's HOST arm (H7), pinned and EXECUTED.

(Doc/Architecture/BuildHosts §5.) `node-repo-module-pack.yml` takes `executor` / `host-verdict`
like compile-check and gate: with `executor: host` the warm job, the workspace build, the pack legs
and the tests legs do not run, and the required `All selected bundles built` carries the host's
followed verdict. `build-host-publication.yml` is the host graph's source for the publisher. This
file holds both lanes to that, in two halves:

  STRUCTURE  every heavy job is gated off a host run; `verify` needs every one of them (it must SEE
             they skipped) and every step of it except the host arm is gated off a host run; the
             executor is refused unless known; the ledger is not consulted on a host.
  BEHAVIOUR  the inline bash of the new steps is EXTRACTED from the workflow GitHub would load and
             RUN under each case — a followed `success` is the only green, a skipped/cancelled/
             failed/empty verdict is red, a leg that ran anyway is red, an unknown executor is red,
             a publishing host run in `direct` mode is red — and the lane-key step's bash is run
             against `module-publication.py lane-key`, so a host stamping records with the Python
             key stamps exactly what the lane's verifier and publisher compare.

Each structural rule is also run against a MUTATED copy of the workflow and must go red: a check
that cannot fail is not a check.

Usage: python3 .github/scripts/test-module-pack-host.py
"""
from __future__ import annotations

import copy
import importlib.util
import json
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

import yaml

ROOT = Path(__file__).resolve().parents[2]
PACK = ROOT / ".github" / "workflows" / "node-repo-module-pack.yml"
HOSTPUB = ROOT / ".github" / "workflows" / "build-host-publication.yml"
HEAVY = ("prepare", "build-workspace", "pack", "tests")
OFF_HOST = "inputs.executor != 'host'"

failures: list[str] = []


def check(label: str, ok: bool, detail: str = "") -> None:
    print(("  ok   " if ok else "  FAIL ") + label + (f" — {detail}" if detail and not ok else ""))
    if not ok:
        failures.append(label)


def load(path: Path) -> dict:
    doc = yaml.safe_load(path.read_text(encoding="utf-8"))
    # PyYAML reads the bare key `on` as the boolean True.
    if True in doc and "on" not in doc:
        doc["on"] = doc.pop(True)
    return doc


def step(job: dict, name: str) -> dict:
    for s in job.get("steps") or []:
        if s.get("name") == name:
            return s
    raise KeyError(f"no step named {name!r}")


# ══════════════════════════════ STRUCTURE ══════════════════════════════
def structure_problems(doc: dict) -> list[str]:
    problems: list[str] = []
    inputs = doc["on"]["workflow_call"]["inputs"]
    if (inputs.get("executor") or {}).get("default") != "actions":
        problems.append("input `executor` must exist with default `actions` (a caller passing nothing is unchanged)")
    if (inputs.get("host-verdict") or {}).get("default") != "":
        problems.append("input `host-verdict` must exist with default ''")
    if "executed-on" not in (doc["on"]["workflow_call"].get("outputs") or {}):
        problems.append("output `executed-on` is missing")
    jobs = doc["jobs"]
    for name in HEAVY:
        cond = str(jobs[name].get("if") or "")
        if OFF_HOST not in cond:
            problems.append(f"job `{name}` runs on a host run — its if: must carry `{OFF_HOST}`")
    verify = jobs["verify"]
    if str(verify.get("if")).strip() != "always()":
        problems.append("`verify` must run `if: always()` — it is the required context in both arms")
    need = set(verify.get("needs") or [])
    for name in ("select", *HEAVY):
        if name not in need:
            problems.append(f"`verify` does not need `{name}` — on a host run it cannot SEE that job skipped")
    host_steps = [s for s in verify["steps"] if s.get("id") == "host"]
    if len(host_steps) != 1:
        problems.append(f"`verify` must carry exactly one host-arm step (id: host), found {len(host_steps)}")
    else:
        cond = str(host_steps[0].get("if") or "")
        if "always()" not in cond or "inputs.executor == 'host'" not in cond:
            problems.append("the host-arm step must run `if: ${{ always() && inputs.executor == 'host' }}`")
    for s in verify["steps"]:
        if s.get("id") == "host":
            continue
        cond = str(s.get("if") or "")
        if OFF_HOST not in cond and "inputs.executor == 'actions'" not in cond:
            problems.append(f"verify step `{s.get('name') or s.get('uses')}` would run on a host run — gate it off the host arm")
    outs = verify.get("outputs") or {}
    if "steps.host.outputs.bundles_built" not in str(outs.get("bundles-built")):
        problems.append("verify's `bundles-built` must read the host arm's answer too")
    sel = jobs["select"]["steps"]
    names = [s.get("name") for s in sel]
    if "The executor is one this lane knows" not in names:
        problems.append("`select` has no executor step — an unknown executor would fall through to an arm")
    elif names.index("The executor is one this lane knows") > names.index("Read the publication mode"):
        problems.append("the executor step must run before the publication mode is read")
    ledger = step(jobs["select"], "Consult the module build ledger")
    if 'if [ "$EXECUTOR" = "host" ]' not in ledger["run"] or ledger["env"].get("EXECUTOR") != "${{ inputs.executor }}":
        problems.append("the ledger step does not pass a host run through — a claim no leg of the run finishes")
    return problems


def mutations(doc: dict) -> list[tuple[str, dict]]:
    out = []
    for name in HEAVY:
        m = copy.deepcopy(doc)
        m["jobs"][name]["if"] = str(m["jobs"][name]["if"]).replace(f" && {OFF_HOST}", "").replace(f"\n&& {OFF_HOST}", "").replace(f"&& {OFF_HOST}", "")
        out.append((f"`{name}` no longer gated off a host run", m))
    m = copy.deepcopy(doc)
    m["jobs"]["verify"]["needs"] = ["select", "pack", "tests"]
    out.append(("`verify` no longer needs prepare/build-workspace", m))
    m = copy.deepcopy(doc)
    for s in m["jobs"]["verify"]["steps"]:
        if s.get("id") == "verify":
            s.pop("if", None)
    out.append(("the Actions verifier runs on a host run", m))
    m = copy.deepcopy(doc)
    m["on"]["workflow_call"]["inputs"]["executor"]["default"] = "host"
    out.append(("the executor default flipped to host", m))
    m = copy.deepcopy(doc)
    m["jobs"]["select"]["steps"] = [s for s in m["jobs"]["select"]["steps"] if s.get("name") != "The executor is one this lane knows"]
    out.append(("the executor step dropped", m))
    m = copy.deepcopy(doc)
    led = step(m["jobs"]["select"], "Consult the module build ledger")
    led["env"].pop("EXECUTOR", None)
    out.append(("the ledger consulted on a host run", m))
    return out


# ══════════════════════════════ BEHAVIOUR ══════════════════════════════
def run_bash(body: str, env: dict, tmp: Path) -> tuple[int, str, dict]:
    assert "${{" not in body, "a step under test must read its inputs from env, never an inline expression"
    out_file, summary = tmp / "out.txt", tmp / "summary.md"
    out_file.write_text("", encoding="utf-8")
    script = tmp / "step.sh"
    script.write_text(body, encoding="utf-8")
    full = {"PATH": os.environ["PATH"], "GITHUB_OUTPUT": str(out_file), "GITHUB_STEP_SUMMARY": str(summary),
            "RUNNER_TEMP": str(tmp), **env}
    r = subprocess.run(["bash", "-e", str(script)], env=full, capture_output=True, text=True, cwd=tmp)
    outputs: dict[str, str] = {}
    for line in out_file.read_text(encoding="utf-8").splitlines():
        if "=" in line and "<<" not in line.split("=", 1)[0]:
            k, v = line.split("=", 1)
            outputs[k] = v
    return r.returncode, r.stdout + r.stderr, outputs


def sha256sum_on_path(tmp: Path) -> str:
    """The lane runs on Linux (`sha256sum`); a macOS workstation has `shasum`. Shim only if absent."""
    if shutil.which("sha256sum"):
        return os.environ["PATH"]
    shim = tmp / "bin"
    shim.mkdir(exist_ok=True)
    (shim / "sha256sum").write_text('#!/bin/sh\nexec shasum -a 256 "$@"\n', encoding="utf-8")
    (shim / "sha256sum").chmod(0o755)
    return f"{shim}:{os.environ['PATH']}"


def behaviour(doc: dict, hostpub: dict) -> None:
    sel = doc["jobs"]["select"]
    verify = doc["jobs"]["verify"]
    with tempfile.TemporaryDirectory() as td:
        tmp = Path(td)

        print("== select: the executor is one this lane knows")
        body = step(sel, "The executor is one this lane knows")["run"]
        for executor, publish, mode, want in (
                ("actions", "true", "direct", 0), ("host", "false", "direct", 0), ("host", "true", "staged", 0),
                ("host", "true", "direct", 1), ("ftp", "false", "direct", 1), ("", "false", "direct", 1)):
            rc, out, _ = run_bash(body, {"EXECUTOR": executor, "PUBLISH": publish, "MODE": mode}, tmp)
            check(f"executor={executor!r} publish={publish} mode={mode} -> {'green' if want == 0 else 'RED'}",
                  rc == want, out)
        # A build extension the host would never load: refused on a host run, by name.
        ext = "tools/Acme.Razor/Acme.Razor.csproj"
        rc, out, _ = run_bash(body, {"EXECUTOR": "host", "PUBLISH": "true", "MODE": "staged",
                                     "EXTENSIONS_PROJECT": ext}, tmp)
        check("executor=host WITH build-extensions-project -> RED, naming the input and the fix",
              rc == 1 and "build-extensions-project" in out and "executor: actions" in out, out)
        rc, out, _ = run_bash(body, {"EXECUTOR": "actions", "PUBLISH": "true", "MODE": "direct",
                                     "EXTENSIONS_PROJECT": ext}, tmp)
        check("NEGATIVE CONTROL — executor=actions WITH build-extensions-project -> green", rc == 0, out)
        rc, out, _ = run_bash(body, {"EXECUTOR": "host", "PUBLISH": "true", "MODE": "staged",
                                     "EXTENSIONS_PROJECT": ""}, tmp)
        check("NEGATIVE CONTROL — executor=host with NO extension -> green", rc == 0, out)

        print("== verify: the host arm carries the followed verdict")
        body = next(s for s in verify["steps"] if s.get("id") == "host")["run"]
        green = {"HOST_VERDICT": "success", "SELECT_RESULT": "success", "LANE": "catalog-481e1ec92571",
                 "COUNT": "3", "PREPARE_RESULT": "skipped", "WORKSPACE_RESULT": "skipped",
                 "PACK_RESULT": "skipped", "TESTS_BUILD_RESULT": "skipped", "TESTS_RESULT": "skipped"}
        rc, out, outs = run_bash(body, green, tmp)
        check("a followed success with every leg skipped is GREEN, executed_on=host, bundles_built=false",
              rc == 0 and outs.get("executed_on") == "host" and outs.get("bundles_built") == "false", out)
        reds = [("HOST_VERDICT", v) for v in ("failure", "cancelled", "skipped", "")]
        reds += [("SELECT_RESULT", "failure"), ("LANE", "")]
        reds += [(k, v) for k in ("PREPARE_RESULT", "WORKSPACE_RESULT", "PACK_RESULT", "TESTS_BUILD_RESULT", "TESTS_RESULT")
                 for v in ("success", "failure")]
        for key, value in reds:
            rc, out, outs = run_bash(body, {**green, key: value}, tmp)
            check(f"{key}={value!r} is RED and claims no executor",
                  rc == 1 and "executed_on" not in outs and outs.get("bundles_built") == "false", out)

        print("== select: the ledger is not consulted on a host run")
        led = step(sel, "Consult the module build ledger")["run"]
        base = {"COUNT": "2", "SCOPE_MODULES": '[{"module":"A"},{"module":"B"}]', "LEDGER": "required",
                "MW_LEDGER_TOKEN": ""}
        rc, out, outs = run_bash(led, {**base, "EXECUTOR": "host"}, tmp)
        check("host: the selection passes through untouched, with no ledger token",
              rc == 0 and outs.get("modules") == base["SCOPE_MODULES"] and outs.get("build") == base["SCOPE_MODULES"], out)
        rc, out, _ = run_bash(led, {**base, "EXECUTOR": "actions"}, tmp)
        check("NEGATIVE CONTROL: actions with ledger required and no token is still RED (the host arm is what passed)",
              rc == 1 and "ledger-token" in out, out)

        print("== select: the lane key, against module-publication.py lane-key")
        spec = importlib.util.spec_from_file_location("mp", ROOT / ".github" / "scripts" / "module-publication.py")
        mp = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(mp)
        lane_body = step(sel, "This call's lane key")["run"]
        path = sha256sum_on_path(tmp)
        for lane_id, modules in (("catalog", '[{"package":"AI","module":"MeshWeaver.AI"}]'), ("", "[]"),
                                 ("mod ule/über:lane-with-a-very-long-label", '[{"module": "X"}]\n')):
            rc, out, outs = run_bash(lane_body, {"LANE": lane_id, "DECLARED": modules, "PATH": path}, tmp)
            want = mp.lane_key(lane_id, modules)
            check(f"lane key for {lane_id!r}: the step's bash == lane_key() ({want})",
                  rc == 0 and outs.get("id") == want, f"{outs.get('id')} vs {want}: {out}")

        print("== build-host-publication: who built the bundles")
        hp = hostpub["jobs"]["stage"]
        mode = step(hp, "Who built the bundles, and is anything owed")["run"]
        for env, want_rc, want_fetch in (
                ({"EXECUTOR": "host", "LANE": "L", "DECLARED": "", "SELECTED": ""}, 0, "true"),     # original contract
                ({"EXECUTOR": "host", "LANE": "L", "DECLARED": "[]", "SELECTED": "A B", "PLAN": '{"lane":"L"}'}, 0, "true"),
                # 🧾 Plugins#4478 slice 3: a host fetch under the host-graph contract with NO plan is RED.
                ({"EXECUTOR": "host", "LANE": "L", "DECLARED": "[]", "SELECTED": "A B"}, 1, None),
                ({"EXECUTOR": "host", "LANE": "L", "DECLARED": "[]", "SELECTED": "A B", "PLAN": "  "}, 1, None),
                ({"EXECUTOR": "host", "LANE": "L", "DECLARED": "[]", "SELECTED": "  "}, 0, "false"),  # zero selection
                ({"EXECUTOR": "actions", "LANE": "L", "DECLARED": "[]", "SELECTED": "A"}, 0, "false"),
                ({"EXECUTOR": "actions", "LANE": "L", "DECLARED": "", "SELECTED": "A"}, 1, None),
                ({"EXECUTOR": "ftp", "LANE": "L", "DECLARED": "[]", "SELECTED": "A"}, 1, None),
                ({"EXECUTOR": "host", "LANE": "", "DECLARED": "[]", "SELECTED": "A"}, 1, None)):
            rc, out, outs = run_bash(mode, env, tmp)
            check(f"host-publication mode {env} -> rc {want_rc}, fetch={want_fetch}",
                  rc == want_rc and outs.get("fetch") == want_fetch, out)
        out_body = step(hp, "Hand the pack call's lane and selection to the publisher")["run"]
        rc, out, _ = run_bash(out_body, {"LANE": "L", "SELECTED": "A B", "DECLARED": '[\n{"module":"A"}\n]',
                                         "PLAN": '{"lane":"L",\n"modules":{}}'}, tmp)
        written = (tmp / "out.txt").read_text(encoding="utf-8")
        check("host-publication hands lane, selection, a MULTI-LINE declared matrix and the plan through intact",
              rc == 0 and "lane=L\n" in written and "selected=A B\n" in written
              and '[\n{"module":"A"}\n]\n' in written and '{"lane":"L",\n"modules":{}}\n' in written, written)
        steps_hp = hp["steps"]
        gated = [s for s in steps_hp if s.get("id") not in ("mode", "out")]
        check("every fetching step of host-publication is gated on the mode step's `fetch`",
              all("steps.mode.outputs.fetch == 'true'" in str(s.get("if") or "") for s in gated),
              str([(s.get("name") or s.get("uses"), s.get("if")) for s in gated]))
        names = [s.get("name") for s in steps_hp]
        check("the publisher's check runs BEFORE the bytes are staged, and the outputs are emitted LAST",
              names.index("The host's records answer the pack call's selection (the publisher's check, no POST)")
              < names.index("Stage them for the publish lane") < names.index("Hand the pack call's lane and selection to the publisher")
              and names[-1] == "Hand the pack call's lane and selection to the publisher", str(names))
        # The check step, EXECUTED over a fetch that holds TWO pack calls' records (receipts are keyed
        # by repo/sha/stage, not lane — review of #7271): it must set the sibling's aside and pass,
        # and the same body without its `partition` line must refuse (the negative control).
        check_body = step(hp, "The host's records answer the pack call's selection (the publisher's check, no POST)")["run"]
        names_in_body = [check_body.find(s) for s in ('import yaml', '--self-test', ' partition ', ' apply-plan ', '--check-only')]
        check("the check step bootstraps PyYAML before the self-test, partitions and applies the plan before --check-only",
              all(i >= 0 for i in names_in_body) and names_in_body == sorted(names_in_body), str(names_in_body))
        (tmp / "s").mkdir(exist_ok=True)
        shutil.copy(ROOT / ".github" / "scripts" / "module-publication.py", tmp / "s" / "module-publication.py")

        def fetched(records=(("MeshWeaver.AI", "L1"), ("MeshWeaver.Mcp", "L2"))) -> None:
            import hashlib as _h, json as _j, zipfile as _z
            staged = tmp / "staged"
            shutil.rmtree(staged, ignore_errors=True)
            shutil.rmtree(tmp / "other-lanes", ignore_errors=True)
            shutil.rmtree(tmp / "unselected", ignore_errors=True)
            staged.mkdir()
            for module, lane in records:
                bundle = staged / f"{module}.module.nupkg"
                with _z.ZipFile(bundle, "w") as zf:
                    zf.writestr("meshweaver/manifest.json", _j.dumps(
                        {"frameworkMvid": "mvid:aaaa", "module": {"assemblyName": module}}))
                (staged / f"{module}.publication.json").write_text(_j.dumps(
                    {"lane": lane, "package": module.split(".")[-1], "module": module, "version": "1.0.0",
                     "frameworkIdentity": "mvid:aaaa", "owed": True,
                     "bundle": {"name": bundle.name, "sha256": _h.sha256(bundle.read_bytes()).hexdigest()}}),
                    encoding="utf-8")

        def plan_of(*owed_modules: str, lane: str = "L1") -> str:
            return json.dumps(mp.publication_plan(lane, [{"module": m, "package": m.split(".")[-1]} for m in owed_modules],
                                                  True, "", "off"))

        env = {"LANE": "L1", "SELECTED": "MeshWeaver.AI", "PLAN": plan_of("MeshWeaver.AI"),
               "DECLARED": '[{"module":"MeshWeaver.AI"},{"module":"MeshWeaver.Mcp"}]'}
        fetched()
        rc, out, _ = run_bash(check_body, env, tmp)
        check("EXECUTED: a fetch holding a sibling lane's records passes for THIS lane, the sibling set aside",
              rc == 0 and (tmp / "other-lanes" / "MeshWeaver.Mcp.publication.json").is_file()
              and not (tmp / "staged" / "MeshWeaver.Mcp.module.nupkg").exists(), out[-600:])
        fetched()
        no_partition = "\n".join(l for l in check_body.splitlines()
                                 if " partition " not in l and "--staged \"$RUNNER_TEMP/staged\" --lane \"$LANE\" --aside" not in l)
        rc, out, _ = run_bash(no_partition, env, tmp)
        check("NEGATIVE CONTROL: the same step WITHOUT the partition refuses the mixed fetch",
              rc == 1 and "another call" in out, out[-600:])
        fetched()
        rc, out, _ = run_bash(check_body, {**env, "SELECTED": "MeshWeaver.AI MeshWeaver.Mcp",
                                           "PLAN": plan_of("MeshWeaver.AI", "MeshWeaver.Mcp")}, tmp)
        check("EXECUTED: completeness still holds — a selected module only the sibling staged is RED",
              rc == 1 and "MeshWeaver.Mcp" in out, out[-600:])
        # 🧾 The host's CATALOG-WIDE offer (Plugins#4478 slice 3): every bundle, one lane, owed:true. The
        # plan narrows it to the selection — and the same body without apply-plan refuses (negative control).
        catalog = (("MeshWeaver.AI", "L1"), ("MeshWeaver.Mcp", "L1"), ("MeshWeaver.Maps", "L1"))
        cat_env = {"LANE": "L1", "SELECTED": "MeshWeaver.AI MeshWeaver.Maps",
                   "DECLARED": json.dumps([{"module": m} for m, _ in catalog]),
                   "PLAN": json.dumps(mp.publication_plan("L1", [{"module": "MeshWeaver.AI", "package": "AI"},
                                                                 {"module": "MeshWeaver.Maps", "package": "Maps"}],
                                                          True, "Maps", "off"))}
        fetched(catalog)
        rc, out, _ = run_bash(check_body, cat_env, tmp)
        maps = json.loads((tmp / "staged" / "MeshWeaver.Maps.publication.json").read_text()) if rc == 0 else {}
        check("EXECUTED: the host's catalog-wide offer passes once the plan sets the unselected module aside and "
              "marks the withheld one not owed",
              rc == 0 and (tmp / "unselected" / "MeshWeaver.Mcp.publication.json").is_file()
              and maps.get("owed") is False and "publish-withhold" in str(maps.get("reason"))
              and (tmp / "staged" / "MeshWeaver.AI.module.nupkg").is_file(), out[-600:])
        fetched(catalog)
        no_plan = "\n".join(l for l in check_body.splitlines() if " apply-plan " not in l and "--plan \"@$RUNNER_TEMP/plan.json\" --aside" not in l)
        rc, out, _ = run_bash(no_plan, cat_env, tmp)
        check("NEGATIVE CONTROL: the same step WITHOUT apply-plan refuses the catalog-wide offer",
              rc == 1 and "MeshWeaver.Mcp" in out, out[-600:])
        inputs = hostpub["on"]["workflow_call"]["inputs"]
        check("host-publication's executor defaults to `host` (its original behaviour for existing callers)",
              (inputs.get("executor") or {}).get("default") == "host")


def main() -> int:
    doc, hostpub = load(PACK), load(HOSTPUB)
    print("== structure")
    problems = structure_problems(doc)
    check("node-repo-module-pack.yml's host arm is wired", not problems, "; ".join(problems))
    print("== structure: every rule can say no (mutations)")
    for label, mutated in mutations(doc):
        check(f"MUTATION caught: {label}", bool(structure_problems(mutated)), "the mutated workflow passed")
    behaviour(doc, hostpub)
    print()
    if failures:
        print(f"::error::{len(failures)} case(s) FAILED: " + "; ".join(failures))
        return 1
    print("test-module-pack-host: the host arm is gated, carries only a followed success, and every rule can say no")
    return 0


if __name__ == "__main__":
    sys.exit(main())
