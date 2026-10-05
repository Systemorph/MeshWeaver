#!/usr/bin/env python3
"""Execute resolve-settled-content.sh against throw-away content repositories.

The script decides which MeshWeaver.Plugins commit a sealed set may bind to (main-cd's `gate`).
Each case builds a real git repository whose `scripts/gen-manifests.py` / `scripts/mesh-floors.py`
are STUBS driven by a `settle-state` file committed alongside — so a commit's settledness is part
of the commit, exactly as it is in the real repository — and runs the REAL script on it.

Cases: a settled tip binds to the tip; an unsettled tip binds to its newest settled first-parent
ancestor and NAMES the tip and every lock that would move; pending floors are unsettled; a settled
commit reachable only through a SECOND parent is never chosen; no settled commit inside the walk,
a failing settle check and a missing settle script are each RED; the clone is left clean.

NEGATIVE CONTROL: a mutant of the script that ignores moved locks must FAIL the unsettled-tip case
(it binds to the tip — the 2026-10-04 incident). If the mutant passes, this harness measures
nothing and goes red itself.
"""
from __future__ import annotations

import os
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / ".github/scripts/resolve-settled-content.sh"

GEN = r'''#!/usr/bin/env python3
import sys, pathlib
state = pathlib.Path("settle-state").read_text().split()
if "fail" in state:
    print("cannot read the baseline"); sys.exit(3)
for word in state:
    if word.startswith("unsettled:"):
        lock = pathlib.Path(word.split(":", 1)[1]) / "manifest.lock"
        lock.write_text(lock.read_text() + "moved\n")
print("ok")
'''
FLOORS = r'''#!/usr/bin/env python3
import sys, pathlib
sys.exit(1 if "floors-pending" in pathlib.Path("settle-state").read_text().split() else 0)
'''

failures: list[str] = []


def git(repo: Path, *args: str) -> str:
    return subprocess.run(["git", "-C", str(repo), *args], check=True, capture_output=True, text=True).stdout.strip()


def commit(repo: Path, state: str, message: str, *, gen: bool = True) -> str:
    (repo / "settle-state").write_text(state + "\n")
    scripts = repo / "scripts"
    scripts.mkdir(exist_ok=True)
    gen_path = scripts / "gen-manifests.py"
    if gen:
        gen_path.write_text(GEN)
    elif gen_path.exists():
        gen_path.unlink()
    (scripts / "mesh-floors.py").write_text(FLOORS)
    git(repo, "add", "-A")
    git(repo, "commit", "-q", "-m", message)
    return git(repo, "rev-parse", "HEAD")


def new_repo(tmp: Path, name: str) -> Path:
    repo = tmp / name
    repo.mkdir()
    git(repo, "init", "-q", "-b", "main")
    git(repo, "config", "user.email", "t@example.invalid")
    git(repo, "config", "user.name", "t")
    for pkg in ("AI", "Hosting"):
        (repo / pkg).mkdir()
        (repo / pkg / "manifest.lock").write_text(f"{pkg}\n")
    return repo


def run(script: Path, repo: Path, tip: str, walk: int = 10) -> tuple[int, str, dict[str, str]]:
    out = repo.parent / f"{repo.name}.out"
    out.unlink(missing_ok=True)
    proc = subprocess.run(["bash", str(script), "--repo-dir", str(repo), "--tip", tip,
                           "--max-walk", str(walk), "--output", str(out)],
                          capture_output=True, text=True)
    values = dict(line.split("=", 1) for line in out.read_text().splitlines()) if out.exists() else {}
    return proc.returncode, proc.stdout + proc.stderr, values


def expect(cond: bool, what: str, log: str = "") -> None:
    if not cond:
        failures.append(what + ("\n    " + log.replace("\n", "\n    ") if log else ""))
        print(f"✗ {what}")
    else:
        print(f"✓ {what}")


def cases(script: Path, tmp: Path, label: str) -> list[str]:
    """Run every case against `script`; return the names of the cases that failed."""
    failed: list[str] = []

    def check(name: str, cond: bool, log: str) -> None:
        if not cond:
            failed.append(name)
        if label == "real":
            expect(cond, name, log)

    # 1. settled tip
    r = new_repo(tmp, f"{label}-settled")
    tip = commit(r, "ok", "settled")
    rc, log, v = run(script, r, tip)
    check("a settled tip binds to the tip", rc == 0 and v.get("sha") == tip and v.get("tip_settled") == "true", log)

    # 2. unsettled tip, settled parent — the incident
    r = new_repo(tmp, f"{label}-unsettled")
    parent = commit(r, "ok", "settle merge")
    tip = commit(r, "unsettled:AI unsettled:Hosting", "source merge")
    rc, log, v = run(script, r, tip)
    check("an unsettled tip binds to the newest settled ancestor, never the tip",
          rc == 0 and v.get("sha") == parent and v.get("tip_settled") == "false", log)
    check("the unsettled tip and every lock that would move are NAMED",
          tip[:10] in log and "AI/manifest.lock" in log and "Hosting/manifest.lock" in log and "::warning" in log, log)
    check("the clone is left clean (no lock the settle wrote survives)",
          git(r, "status", "--porcelain") == "", git(r, "status", "--porcelain"))

    # 3. pending floors are unsettled
    r = new_repo(tmp, f"{label}-floors")
    parent = commit(r, "ok", "stamped")
    tip = commit(r, "floors-pending", "unstamped floors")
    rc, log, v = run(script, r, tip)
    check("pending floors make a commit unsettled", rc == 0 and v.get("sha") == parent, log)

    # 4. a settled commit on a SECOND parent is never chosen; nothing settled on the first-parent walk ⇒ RED
    r = new_repo(tmp, f"{label}-sideline")
    base = commit(r, "unsettled:AI", "unsettled base")
    git(r, "checkout", "-q", "-b", "side")
    commit(r, "ok", "settled on a side branch")
    git(r, "checkout", "-q", "main")
    commit(r, "unsettled:AI trunk", "unsettled trunk")
    git(r, "merge", "-q", "--no-ff", "-m", "merge side", "side", "-X", "ours")
    (r / "settle-state").write_text("unsettled:AI final\n")
    git(r, "commit", "-q", "-am", "keep the trunk unsettled")
    tip = git(r, "rev-parse", "HEAD")
    rc, log, v = run(script, r, tip, walk=10)
    check("no settled first-parent commit ⇒ RED naming the moved lock, never a second-parent commit",
          rc == 1 and "sha" not in v and "AI/manifest.lock" in log and "No settled content commit" in log, log)
    _ = base

    # 5. the walk bound is honoured
    r = new_repo(tmp, f"{label}-bound")
    commit(r, "ok", "settled, but too far back")
    commit(r, "unsettled:AI u1", "u1")
    tip = commit(r, "unsettled:AI u2", "u2")
    rc, log, v = run(script, r, tip, walk=2)
    check("a settled commit outside --max-walk is not reached ⇒ RED", rc == 1 and "sha" not in v, log)

    # 6. the settle check failing is an instrument fault ⇒ RED, never a walk past it
    r = new_repo(tmp, f"{label}-fail")
    commit(r, "ok", "settled")
    tip = commit(r, "fail", "settle check cannot run")
    rc, log, v = run(script, r, tip)
    check("a failing settle check is RED, not a walk past", rc == 1 and "sha" not in v and "Settle check failed" in log, log)

    # 7. no settle script ⇒ RED
    r = new_repo(tmp, f"{label}-noscript")
    commit(r, "ok", "settled")
    tip = commit(r, "ok", "script removed", gen=False)
    rc, log, v = run(script, r, tip)
    check("a commit without scripts/gen-manifests.py is RED", rc == 1 and "sha" not in v, log)
    return failed


def main() -> int:
    if not SCRIPT.is_file():
        print(f"✗ {SCRIPT} is missing")
        return 1
    with tempfile.TemporaryDirectory() as t:
        tmp = Path(t)
        os.environ.setdefault("GIT_CONFIG_NOSYSTEM", "1")
        cases(SCRIPT, tmp, "real")

        # NEGATIVE CONTROL — ignore moved locks; the incident case must now fail.
        body = SCRIPT.read_text()
        needle = 'if [ -z "$moved" ] && [ "$floors" = stamped ]; then'
        if body.count(needle) != 1:
            failures.append("negative control: cannot find the settled-verdict line to mutate in the script")
        else:
            mutant = tmp / "mutant.sh"
            mutant.write_text(body.replace(needle, 'if [ "$floors" = stamped ]; then'))
            failed = cases(mutant, tmp, "mutant")
            expect("an unsettled tip binds to the newest settled ancestor, never the tip" in failed,
                   "negative control: a mutant that ignores moved locks binds to the unsettled tip and is caught")
    if failures:
        print(f"\n{len(failures)} failure(s)")
        return 1
    print("\nall cases green")
    return 0


if __name__ == "__main__":
    sys.exit(main())
