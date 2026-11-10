#!/usr/bin/env python3
"""Drive `acr-retention-tasks.sh apply` against a FAKE `az`, offline (MeshWeaver#3438).

The first governed `RegistryRetention` apply (Ops/Actions/registry-retention-apply-3438 on the
control instance, Memex run 40555776172) died before changing anything:

    ERROR: unrecognized arguments: --schedule 0 3 * * *

`az acr task update` has no `--schedule` — a task's schedule is a TIMER TRIGGER, set through
`az acr task timer update|add`. Nothing had ever run `apply`, because the only place it runs is a
governed, approved mutation of the shared registry. This self-test is the run nobody could make:

  * the fake `az` accepts, per subcommand, exactly the options the real CLI accepts for it
    (`az acr task update --help`, azure-cli 2.90) and refuses any other with the real CLI's message,
    so a call shape the CLI would refuse fails HERE;
  * it keeps the registry's state (the encoded definition, status, timer triggers) in a file, so
    the apply's own re-verify reads back what the apply wrote.

Scenarios: a task that carries a timer gets it UPDATED; a task that carries none gets one ADDED;
a timer already at the recorded schedule is LEFT ALONE (ACR refuses an update to the same schedule:
`DuplicateTimerTriggersNotSupported`, Memex run 41303901662);
the apply ends green and names the now-stale `recordAheadOfRegistry` as the next step, while a
stand-alone `verify` still reds on it; and the fake really refuses `task update --schedule`
(the instrument can fail).

🚨 OFFLINE MEANS NO LIVE EVIDENCE EITHER. `apply` consults `acr-retention-protection.py` whenever the
record enables a task, and that script fetches the lock lane's newest run from the GitHub API. Run
against the committed tree, this test therefore went network- and state-dependent the moment the
record recorded a task `Enabled` (#7834 review): an unreachable API, a rate limit or a red lock lane
would red every unrelated pull request before the fake registry was exercised. So every scenario
runs a COPY of `.github/scripts` + `.github/acr-retention` in a temp tree, with the copy's
`acr-retention-protection.py` replaced by a stub whose verdict the scenario chooses. The production
interlock gains no override: the stub exists only inside the harness's own temp tree. Scenario 6
proves the stub is consulted — a red verdict refuses the apply before any registry write.

    python3 .github/scripts/acr-retention-apply-selftest.py
"""
from __future__ import annotations

import base64
import json
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
SCRIPT = HERE / "acr-retention-tasks.sh"
RECORD = HERE.parent / "acr-retention"
TASKS = ("purge-old-images", "purge-old-ci-releases")

# The fake `az`. Options per subcommand are the real CLI's (global ones included); anything else is
# refused exactly as argparse in azure-cli refuses it.
FAKE_AZ = r'''#!/usr/bin/env python3
import base64, json, os, sys
STATE = os.environ["FAKE_AZ_STATE"]
GLOBAL = {"-o", "--output", "--query", "--subscription", "--only-show-errors", "--debug", "--verbose",
          "--registry", "-r", "--name", "-n", "--resource-group", "-g"}
ALLOWED = {
    ("acr", "task", "show"): set(),
    ("acr", "task", "update"): {"--file", "-f", "--status", "--timeout", "--cmd", "--context", "-c",
                               "--platform", "--cpu", "--agent-pool", "--arg", "--secret-arg", "--set",
                               "--set-secret", "--values", "--image", "-t", "--no-push", "--no-cache",
                               "--target", "--auth-mode", "--log-template", "--source-acr-auth-id",
                               "--commit-trigger-enabled", "--pull-request-trigger-enabled",
                               "--base-image-trigger-enabled", "--base-image-trigger-type",
                               "--git-access-token"},
    ("acr", "task", "timer", "update"): {"--timer-name", "--schedule", "--enabled"},
    ("acr", "task", "timer", "add"): {"--timer-name", "--schedule", "--enabled"},
    ("acr", "task", "list-runs"): {"--top", "--run-status", "--image"},
    ("acr", "task", "logs"): {"--run-id", "--no-format", "--image"},
}
FLAGS = {"--only-show-errors", "--debug", "--verbose", "--no-push", "--no-cache", "--no-format"}
argv = sys.argv[1:]
cmd = tuple(a for a in argv[:4] if not a.startswith("-"))
key = cmd if cmd in ALLOWED else cmd[:3]
if key not in ALLOWED:
    print(f"ERROR: fake az does not model {' '.join(cmd)}", file=sys.stderr); sys.exit(2)
rest = argv[len(key):]
opts, i, bad = {}, 0, []
while i < len(rest):
    a = rest[i]
    if a in FLAGS:
        opts[a] = True; i += 1; continue
    if a not in GLOBAL and a not in ALLOWED[key]:
        vals = [a]
        j = i + 1
        while j < len(rest) and not rest[j].startswith("-"):
            vals.append(rest[j]); j += 1
        bad.append(" ".join(vals)); i = j; continue
    opts[a] = rest[i + 1]; i += 2
if bad:
    print("ERROR: unrecognized arguments: " + " ".join(bad), file=sys.stderr); sys.exit(2)
with open(STATE) as f:
    st = json.load(f)
with open(STATE + ".log", "a") as f:
    f.write(json.dumps(argv) + "\n")
if key == ("acr", "task", "logs"):
    run = next((r for t in st["tasks"].values() for r in t.get("runs", [])
                if r["runId"] == opts.get("--run-id")), None)
    if run is None:
        print(f"ERROR: run '{opts.get('--run-id')}' not found", file=sys.stderr); sys.exit(1)
    if "--no-format" not in opts:
        print("ERROR: fake az: the reader must ask for --no-format (colour codes break the parse)",
              file=sys.stderr); sys.exit(2)
    sys.stdout.write(run["log"]); sys.exit(0)
name = opts.get("--name") or opts.get("-n")
task = st["tasks"][name]
if key == ("acr", "task", "list-runs"):
    # The reader's query asks for the newest TERMINAL run as `runId status createTime`; the fake
    # answers that question from its state and refuses any other query, so a changed query is a red.
    want_q = ("[?status=='Succeeded' || status=='Failed' || status=='Error' || status=='Canceled' "
              "|| status=='Timeout'] | sort_by(@, &createTime) | [-1].[runId,status,createTime]")
    if opts.get("--query") != want_q:
        print(f"ERROR: fake az does not model --query {opts.get('--query')}", file=sys.stderr); sys.exit(2)
    done = sorted((r for r in task.get("runs", [])
                   if r["status"] in ("Succeeded", "Failed", "Error", "Canceled", "Timeout")),
                  key=lambda r: r["createTime"])
    if done:
        r = done[-1]
        print(f"{r['runId']}\t{r['status']}\t{r['createTime']}")
    sys.exit(0)
if key == ("acr", "task", "show"):
    q = opts.get("--query")
    timers = task["timers"]
    answers = {
        "step.encodedTaskContent": task["encoded"],
        "status": task["status"],
        "trigger.timerTriggers[0].schedule": timers[0]["schedule"] if timers else "",
        "trigger.timerTriggers[0].name": timers[0]["name"] if timers else "",
    }
    if q not in answers:
        print(f"ERROR: fake az does not model --query {q}", file=sys.stderr); sys.exit(2)
    print(answers[q])
elif key == ("acr", "task", "update"):
    if "--file" in opts:
        with open(opts["--file"], "rb") as f:
            task["encoded"] = base64.b64encode(f.read()).decode()
    if "--status" in opts:
        task["status"] = opts["--status"]
elif key == ("acr", "task", "timer", "update"):
    t = next((t for t in task["timers"] if t["name"] == opts["--timer-name"]), None)
    if t is None:
        print(f"ERROR: timer '{opts['--timer-name']}' does not exist", file=sys.stderr); sys.exit(1)
    # The registry refuses a timer whose schedule another trigger — or THIS one — already carries
    # (measured: Memex run 41303901662, an update naming the trigger's own unchanged schedule).
    if any(x["schedule"] == opts.get("--schedule") for x in task["timers"]):
        print("ERROR: (DuplicateTimerTriggersNotSupported) Cannot create multiple timer triggers "
              f"with the same schedule: '{opts['--schedule']}'.", file=sys.stderr); sys.exit(1)
    t["schedule"] = opts["--schedule"]
elif key == ("acr", "task", "timer", "add"):
    if any(t["name"] == opts["--timer-name"] for t in task["timers"]):
        print("ERROR: timer exists", file=sys.stderr); sys.exit(1)
    task["timers"].append({"name": opts["--timer-name"], "schedule": opts["--schedule"]})
with open(STATE, "w") as f:
    json.dump(st, f)
'''


# The protection verdict, chosen per scenario. Same CLI contract as the real script (`--check
# "<tasks>"`, exit 0 = current, 1 = refuse) and it records that it was asked.
FAKE_PROTECTION = r'''#!/usr/bin/env python3
import os, sys
with open(os.environ["FAKE_PROTECTION_LOG"], "a") as f:
    f.write(" ".join(sys.argv[1:]) + "\n")
if os.environ.get("FAKE_PROTECTION") == "red":
    print("::error::fake protection verdict: the lock lane is red", file=sys.stderr); sys.exit(1)
print("protection is current (fake verdict)")
'''


def setup(root: Path, with_timer: dict[str, bool], live_schedule: dict[str, str] | None = None,
          protection: str = "green", record_edit=None, applied: bool = False,
          runs: dict[str, list] | None = None) -> dict:
    # A private copy of the scripts and the record, so neither the committed record's statuses nor
    # live GitHub state decide what this test exercises.
    tree = root / "tree"
    shutil.copytree(HERE, tree / "scripts")
    shutil.copytree(RECORD, tree / "acr-retention")
    (tree / "scripts" / "acr-retention-protection.py").write_text(FAKE_PROTECTION)
    if record_edit is not None:
        tasks_json = tree / "acr-retention" / "tasks.json"
        tasks_json.write_text(json.dumps(record_edit(json.loads(tasks_json.read_text())), indent=2))
    bindir = root / "bin"
    bindir.mkdir()
    az = bindir / "az"
    az.write_text(FAKE_AZ)
    az.chmod(0o755)
    stale = base64.b64encode(b"version: v1.1.0\nsteps:\n  - cmd: acr purge --filter 'x:.*' --ago 7d --keep 10 --untagged\n").decode()
    state = {"tasks": {t: {"encoded": stale, "status": "Disabled",
                           "timers": [{"name": "t1", "schedule": (live_schedule or {}).get(t, "0 1 * * *")}]
                                     if with_timer[t] else []}
                       for t in TASKS}}
    if applied:
        # The registry as a successful apply leaves it: every task carries the recorded definition,
        # status and schedule — the state a stand-alone `verify` reads on the governed path.
        rec = json.loads((tree / "acr-retention" / "tasks.json").read_text())
        for t in rec["tasks"]:
            state["tasks"][t["name"]].update(
                encoded=base64.b64encode((tree / "acr-retention" / t["file"]).read_bytes()).decode(),
                status=t["status"], timers=[{"name": "t1", "schedule": t["schedule"]}])
    for task, rs in (runs or {}).items():
        state["tasks"][task]["runs"] = rs
    (root / "state.json").write_text(json.dumps(state))
    env = dict(os.environ)
    env.update(PATH=f"{bindir}{os.pathsep}{env['PATH']}", FAKE_AZ_STATE=str(root / "state.json"),
               MW_ACR_APPLY_CONFIRM="meshweaver", TMPDIR=str(root),
               FAKE_PROTECTION=protection, FAKE_PROTECTION_LOG=str(root / "protection.log"),
               SELFTEST_SCRIPT=str(tree / "scripts" / SCRIPT.name))
    return env


def run(env: dict, verb: str) -> subprocess.CompletedProcess:
    return subprocess.run(["bash", env["SELFTEST_SCRIPT"], verb], env=env, capture_output=True,
                          text=True, stdin=subprocess.DEVNULL)


def main() -> int:
    failures: list[str] = []
    record = json.loads((RECORD / "tasks.json").read_text())
    want = {t["name"]: t for t in record["tasks"]}
    ahead = (record.get("recordAheadOfRegistry") or {})
    declared = set(ahead.get("tasks") or []) if ahead.get("inForce") is True else set()

    # 1+2. One task carries a timer (updated), the other none (added). The apply must end green.
    with tempfile.TemporaryDirectory() as t:
        root = Path(t)
        env = setup(root, {"purge-old-images": True, "purge-old-ci-releases": False})
        r = run(env, "apply")
        out = r.stdout + r.stderr
        st = json.loads((root / "state.json").read_text())
        if r.returncode != 0:
            failures.append(f"apply exited {r.returncode} against a CLI-faithful az:\n{out[-1500:]}")
        for task in TASKS:
            live = base64.b64decode(st["tasks"][task]["encoded"])
            if live != (RECORD / want[task]["file"]).read_bytes():
                failures.append(f"{task}: the applied definition is not the recorded file")
            timers = st["tasks"][task]["timers"]
            if len(timers) != 1 or timers[0]["schedule"] != want[task]["schedule"]:
                failures.append(f"{task}: timer triggers after apply are {timers}, "
                                f"want one at {want[task]['schedule']!r}")
            if st["tasks"][task]["status"] != want[task]["status"]:
                failures.append(f"{task}: status {st['tasks'][task]['status']} != record")
        logf = root / "state.json.log"
        log = logf.read_text() if logf.is_file() else ""
        if '"timer", "update"' not in log or '"timer", "add"' not in log:
            failures.append("apply did not update the existing timer AND add the missing one")
        if r.returncode == 0:
            for task in declared:
                if f"{task}: the record is now APPLIED" not in out:
                    failures.append(f"apply did not name {task}'s stale recordAheadOfRegistry as the next step")
            # 3. …while a stand-alone verify still refuses the stale declaration.
            if declared:
                v = run(env, "verify")
                if v.returncode == 0 or "declares recordAheadOfRegistry" not in v.stdout:
                    failures.append("a stand-alone verify after apply no longer reds on the stale "
                                    "recordAheadOfRegistry declaration")

    # 5. Both timers already at the recorded schedule — the registry's real state. The apply must
    #    leave them alone (an update to the same schedule is refused by ACR) and end green.
    with tempfile.TemporaryDirectory() as t:
        root = Path(t)
        env = setup(root, {k: True for k in TASKS},
                    live_schedule={k: want[k]["schedule"] for k in TASKS})
        r = run(env, "apply")
        logf = root / "state.json.log"
        log = logf.read_text() if logf.is_file() else ""
        if r.returncode != 0:
            failures.append(f"apply with the timers already at the recorded schedule exited "
                            f"{r.returncode}:\n{(r.stdout + r.stderr)[-1500:]}")
        if '"timer", "update"' in log or '"timer", "add"' in log:
            failures.append("apply wrote a timer whose schedule was already the recorded one")
        # …and the fake really refuses that no-op update (the instrument can fail).
        n = subprocess.run(["az", "acr", "task", "timer", "update", "--registry", "meshweaver",
                            "--name", TASKS[0], "--timer-name", "t1", "--schedule",
                            want[TASKS[0]]["schedule"], "-o", "none"],
                           env=env, capture_output=True, text=True)
        if n.returncode == 0 or "DuplicateTimerTriggersNotSupported" not in n.stderr:
            failures.append("the fake az accepted a timer update to the trigger's own schedule — it "
                            "would not catch the defect scenario 5 exists for")

    # 4. The instrument can fail: the fake refuses `task update --schedule` as the real CLI does.
    with tempfile.TemporaryDirectory() as t:
        env = setup(Path(t), {k: True for k in TASKS})
        r = subprocess.run(["az", "acr", "task", "update", "--registry", "meshweaver", "--name",
                            TASKS[0], "--schedule", "0 3 * * *", "-o", "none"],
                           env=env, capture_output=True, text=True)
        if r.returncode == 0 or "unrecognized arguments: --schedule 0 3 * * *" not in r.stderr:
            failures.append(f"the fake az accepted `task update --schedule` (exit {r.returncode}) — "
                            "it would not catch the defect this test exists for")

    # 6. The interlock is CONSULTED: a record enabling a task, with the protection verdict red, must
    #    refuse before any registry write — and with it green the same record applies.
    def enable_images(d: dict) -> dict:
        d.pop("pause", None)
        for task in d["tasks"]:
            if task["name"] == "purge-old-images":
                task["status"] = "Enabled"
        return d
    for verdict in ("red", "green"):
        with tempfile.TemporaryDirectory() as t:
            root = Path(t)
            env = setup(root, {k: True for k in TASKS}, protection=verdict, record_edit=enable_images)
            r = run(env, "apply")
            plog = root / "protection.log"
            asked = plog.read_text() if plog.is_file() else ""
            logf = root / "state.json.log"
            writes = [line for line in (logf.read_text().splitlines() if logf.is_file() else [])
                      if '"show"' not in line]
            if "purge-old-images" not in asked:
                failures.append(f"apply enabling purge-old-images never asked the protection "
                                f"interlock (verdict {verdict})")
            if verdict == "red" and (r.returncode == 0 or writes):
                failures.append(f"apply went ahead on a RED protection verdict (exit {r.returncode}, "
                                f"{len(writes)} registry write(s))")
            if verdict == "green" and r.returncode != 0:
                failures.append(f"apply refused on a GREEN protection verdict:\n"
                                f"{(r.stdout + r.stderr)[-1500:]}")

    # 7. THE NIGHTLY DRY RUN IS READ BACK (MeshWeaver#3438). The committed record runs
    #    purge-old-images as an ENABLED dry-run report, so a stand-alone `verify` — the governed
    #    action's read-only mode — must fetch the newest completed run and print, per repository,
    #    what it would delete. Before this, `verify` compared definitions only and no governed
    #    instrument read a task RUN, so the read-back `dryRun.liftWhen` asks for had no path.
    dry_tasks = [t for t in (record.get("dryRun") or {}).get("tasks") or []
                 if (record.get("dryRun") or {}).get("inForce") is True
                 and str(want[t]["status"]).lower() == "enabled"]
    if not dry_tasks:
        failures.append("scenario 7 needs the committed record to run a task as an enabled dry run "
                        "— the record no longer does; retire or re-target this scenario")
    login = "meshweaver.azurecr.io"

    def step_log(repo: str, tags: list[str], manifests: list[str], dry: bool = True,
                 deleted: bool = False) -> str:
        out = []
        if dry:
            out.append("DRY RUN: The following output shows what WOULD be deleted if the purge "
                       "command was executed. Nothing is deleted.")
        out.append(f"Would delete tags for repository: {repo}")
        out += [f"Would delete: {login}/{repo}:{t}" for t in tags]
        out.append(f"Would delete manifests for repository: {repo}")
        out += [f"Would delete: {login}/{repo}@{d} (reason: untagged)" for d in manifests]
        if deleted:
            out += ["", f"Number of deleted tags: {len(tags)}", f"Number of deleted manifests: {len(manifests)}"]
        else:
            out += ["", f"Number of tags to be deleted: {len(tags)}",
                    f"Number of manifests to be deleted: {len(manifests)}"]
        return "\n".join(out) + "\n"

    good = (step_log("mw-plugin-test", ["staging-1f652b7-34040888432", "8358471"], ["sha256:" + "a" * 64])
            + step_log("memex-portal", ["3.0.0-ci.7903"], []))

    def verify_with(runs: list) -> subprocess.CompletedProcess:
        with tempfile.TemporaryDirectory() as t:
            env = setup(Path(t), {k: True for k in TASKS}, applied=True,
                        runs={task: runs for task in dry_tasks})
            return run(env, "verify")

    for task in dry_tasks:
        r = verify_with([{"runId": "ca1", "status": "Succeeded", "createTime": "2026-11-08T03:00:01Z",
                          "log": "stale run, must not be read\n"},
                         {"runId": "ca2", "status": "Succeeded", "createTime": "2026-11-09T03:00:01Z",
                          "log": good},
                         {"runId": "ca3", "status": "Running", "createTime": "2026-11-10T03:00:01Z",
                          "log": ""}])
        out = r.stdout + r.stderr
        if r.returncode != 0:
            failures.append(f"verify over a good nightly dry run exited {r.returncode}:\n{out[-1500:]}")
        for needle in ("newest completed run ca2", "mw-plugin-test: 2 tag(s), 1 manifest(s)",
                       "memex-portal: 1 tag(s), 0 manifest(s)",
                       "would delete 3 tag(s) and 1 manifest(s) across 2 repository(ies)"):
            if needle not in out:
                failures.append(f"verify did not report `{needle}` for {task}'s nightly dry run")

    # 8. …and the read-back REFUSES every reading it cannot stand behind (each must red verify).
    bad_runs = {
        "no completed run": [{"runId": "cb1", "status": "Running", "createTime": "2026-11-09T03:00:01Z", "log": ""}],
        "a failed run": [{"runId": "cb2", "status": "Failed", "createTime": "2026-11-09T03:00:01Z", "log": good}],
        "a step not marked DRY RUN": [{"runId": "cb3", "status": "Succeeded", "createTime": "2026-11-09T03:00:01Z",
                                       "log": step_log("mw-plugin-test", ["8358471"], [])
                                       + step_log("memex-portal", ["3.0.0-ci.7903"], [], dry=False)}],
        "a step that DELETED": [{"runId": "cb4", "status": "Succeeded", "createTime": "2026-11-09T03:00:01Z",
                                 "log": step_log("mw-plugin-test", ["8358471"], [])
                                 + step_log("memex-portal", ["3.0.0-ci.7903"], [], deleted=True)}],
        "a step that never reported": [{"runId": "cb5", "status": "Succeeded", "createTime": "2026-11-09T03:00:01Z",
                                        "log": step_log("mw-plugin-test", ["8358471"], [])}],
        "a purge that failed part-way": [{"runId": "cb6", "status": "Succeeded", "createTime": "2026-11-09T03:00:01Z",
                                          "log": good + "Failed to complete purge: 403 \n"}],
        "summaries that disagree with the list": [{"runId": "cb7", "status": "Succeeded",
                                                   "createTime": "2026-11-09T03:00:01Z",
                                                   "log": good.replace("Number of tags to be deleted: 1",
                                                                       "Number of tags to be deleted: 9")}],
        "an empty log": [{"runId": "cb8", "status": "Succeeded", "createTime": "2026-11-09T03:00:01Z", "log": ""}],
    }
    for label, runs in bad_runs.items():
        if not dry_tasks:
            break
        r = verify_with(runs)
        if r.returncode == 0:
            failures.append(f"verify went GREEN over {label} — the dry-run read-back cannot fail on it")

    # 9. An apply's own re-verify does NOT read a run (the newest one predates what it just wrote),
    #    so enabling the report never fails on the run that has not happened yet.
    with tempfile.TemporaryDirectory() as t:
        env = setup(Path(t), {k: True for k in TASKS}, live_schedule={k: want[k]["schedule"] for k in TASKS})
        r = run(env, "apply")
        if r.returncode != 0 or "not read in an apply's re-verify" not in r.stdout:
            failures.append(f"apply's re-verify tried to read a dry-run that predates it (exit "
                            f"{r.returncode}):\n{(r.stdout + r.stderr)[-1500:]}")

    if failures:
        print("acr-retention apply self-test FAILED:")
        for f in failures:
            print(f"  ✗ {f}")
        return 1
    print("acr-retention apply self-test: 9 scenarios held (timer updated, timer added, apply green "
          "naming the stale declaration while verify still reds on it, a timer already at the "
          "recorded schedule left alone, the fake refuses --schedule and a same-schedule update, "
          "a red protection verdict refuses the apply before any registry write, verify reads the "
          f"nightly dry run back per repository, {len(bad_runs)} unreadable run shapes each red it, "
          "and an apply's re-verify leaves the run alone) — offline, "
          "against a private copy of the record and a stubbed protection verdict")
    return 0


if __name__ == "__main__":
    sys.exit(main())
