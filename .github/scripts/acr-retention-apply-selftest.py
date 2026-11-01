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

    python3 .github/scripts/acr-retention-apply-selftest.py
"""
from __future__ import annotations

import base64
import json
import os
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
}
FLAGS = {"--only-show-errors", "--debug", "--verbose", "--no-push", "--no-cache"}
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
name = opts.get("--name") or opts.get("-n")
task = st["tasks"][name]
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


def setup(root: Path, with_timer: dict[str, bool], live_schedule: dict[str, str] | None = None) -> dict:
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
    (root / "state.json").write_text(json.dumps(state))
    env = dict(os.environ)
    env.update(PATH=f"{bindir}{os.pathsep}{env['PATH']}", FAKE_AZ_STATE=str(root / "state.json"),
               MW_ACR_APPLY_CONFIRM="meshweaver", TMPDIR=str(root))
    return env


def run(env: dict, verb: str) -> subprocess.CompletedProcess:
    return subprocess.run(["bash", str(SCRIPT), verb], env=env, capture_output=True, text=True,
                          stdin=subprocess.DEVNULL)


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

    if failures:
        print("acr-retention apply self-test FAILED:")
        for f in failures:
            print(f"  ✗ {f}")
        return 1
    print("acr-retention apply self-test: 5 scenarios held (timer updated, timer added, apply green "
          "naming the stale declaration while verify still reds on it, a timer already at the "
          "recorded schedule left alone, the fake refuses --schedule and a same-schedule update)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
