#!/usr/bin/env python3
"""EXECUTE the publish-bake lane's `epoch-leg` line check — the real step.

WHY
---
A `repository_dispatch` always runs the caller's DEFAULT branch, and the control instance routes a
publication to every declared dependent whatever line it was sealed on. Between 2026-11-18 and
11-20 every satellite's `main` (the 4.x line) was woken by `main3` (3.x) publications, baked its
4.x tree against the 3.x image, and went red at install on its own 4.x floors
(`PackagePlatformFloorException`). The `leg` step of `epoch-leg` now compares the wake's major
(`client_payload.version`) with the major the caller's line declares (`<PlatformVersion>` in
core's `Directory.Build.props` at `scripts-ref`, read by `cd-line.py platform-major`) and ends the
call by name on a mismatch.

HOW IT STAYS HONEST
-------------------
* The step is EXTRACTED from the workflow by `id:` (`workflow_yaml.step_run`), never copied.
* `mw-core-file` is a stub that serves fixture files by path — the REAL `cd-line.py` from this
  tree, and a `Directory.Build.props` per case — and records what it was asked for.
* A falsification arm removes the major comparison from the real step and asserts the cross-line
  wake then bakes, proving the cross-line case is green because of that check.
"""

from __future__ import annotations

import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import workflow_yaml  # noqa: E402 — the ONE step extractor every harness shares

ROOT = Path(__file__).resolve().parents[2]
WORKFLOW = ROOT / ".github/workflows/node-repo-publish-bake.yml"
CDLINE = ROOT / ".github/scripts/cd-line.py"
COMPARISON = 'if [ "$wake_major" != "$line_major" ]; then'

FAILURES: list[str] = []


def props(version: str | None) -> str:
    inner = (f'    <PlatformVersion Condition="\'$(PlatformVersion)\' == \'\'">{version}</PlatformVersion>\n'
             if version else "")
    return f"<Project>\n  <PropertyGroup>\n{inner}  </PropertyGroup>\n</Project>\n"


def run(body: str, *, event: str, version: str = "", line_props: str | None = props("4.0.0"),
        scripts_ref: str = "main", leg: str = "current", serve_cdline: bool = True) -> dict:
    with tempfile.TemporaryDirectory() as tmp:
        work = Path(tmp)
        temp = work / "runner-temp"
        served = work / "served"
        temp.mkdir()
        (served / ".github/scripts").mkdir(parents=True)
        if serve_cdline:
            shutil.copy2(CDLINE, served / ".github/scripts/cd-line.py")
        if line_props is not None:
            (served / "Directory.Build.props").write_text(line_props, encoding="utf-8")
        calls = work / "calls"
        stub = temp / "mw-core-file"
        stub.write_text(
            "#!/usr/bin/env bash\n"
            f'echo "$1 $2" >> "{calls}"\n'
            f'f="{served}/$2"\n'
            '[ -f "$f" ] || { echo "not found: $2" >&2; exit 1; }\n'
            'cat "$f"\n', encoding="utf-8")
        stub.chmod(0o755)
        out, summary = work / "out", work / "summary"
        out.touch()
        summary.touch()
        script = work / "step.sh"
        script.write_text(body, encoding="utf-8")
        env = dict(os.environ)
        env.update({
            "RUNNER_TEMP": str(temp), "GITHUB_OUTPUT": str(out), "GITHUB_STEP_SUMMARY": str(summary),
            "LEG": leg, "SCRIPTS_REF": scripts_ref, "IMAGE_DIGEST": "", "PLATFORM_DIGEST": "",
            "ALLOW_UNPINNED": "false", "EVENT_NAME": event,
            "EVENT_ACTION": "meshweaver-upstream-published" if event == "repository_dispatch" else "",
            "WAKE_VERSION": version,
        })
        proc = subprocess.run(["bash", str(script)], env=env, capture_output=True, text=True, timeout=60)
        outputs = dict(line.split("=", 1) for line in out.read_text().splitlines() if "=" in line)
        return {
            "code": proc.returncode, "out": proc.stdout + proc.stderr, "outputs": outputs,
            "calls": calls.read_text().splitlines() if calls.exists() else [],
            "summary": summary.read_text(),
        }


def expect(name: str, result: dict, *, code: int, run_value: str | None = None,
           contains: str | None = None, fetched: bool | None = None) -> None:
    problems = []
    if result["code"] != code:
        problems.append(f"exit {result['code']} (expected {code})")
    if run_value is not None and result["outputs"].get("run") != run_value:
        problems.append(f"run={result['outputs'].get('run')!r} (expected {run_value!r})")
    if contains is not None and contains not in result["out"] + result["summary"]:
        problems.append(f"output lacks {contains!r}")
    if fetched is not None and bool(result["calls"]) != fetched:
        problems.append(f"core files fetched={bool(result['calls'])} (expected {fetched})")
    if problems:
        FAILURES.append(name)
        print(f"  FAIL  {name}: {'; '.join(problems)}\n{result['out']}")
    else:
        print(f"  ok    {name}")


def main() -> int:
    body = workflow_yaml.step_run(WORKFLOW, "leg")
    if COMPARISON not in body:
        print(f"error: the `leg` step carries no `{COMPARISON}` — the line check is gone", file=sys.stderr)
        return 1

    expect("v3 wake on the v4 line ends by name, nothing baked",
           run(body, event="repository_dispatch", version="3.1.13703"),
           code=0, run_value="false", contains="Wake for another line")
    expect("v4 wake on the v3 line ends by name, nothing baked",
           run(body, event="repository_dispatch", version="4.0.13721", line_props=props("3.1.0"),
               scripts_ref="main3"),
           code=0, run_value="false", contains="major-3 line")
    expect("v4 wake on the v4 line bakes",
           run(body, event="repository_dispatch", version="4.0.13721"),
           code=0, run_value="true", contains="is this line's")
    expect("a dispatch without a version is not judged here (the bake's own refusals apply)",
           run(body, event="repository_dispatch", version=""),
           code=0, run_value="true", fetched=False)
    expect("a push bakes and fetches nothing",
           run(body, event="push"), code=0, run_value="true", fetched=False)
    expect("a schedule bakes and fetches nothing",
           run(body, event="schedule"), code=0, run_value="true", fetched=False)
    expect("a version that is not a platform version is RED",
           run(body, event="repository_dispatch", version="latest"),
           code=1, contains="is not a platform version")
    expect("a line that declares no PlatformVersion is RED, never guessed",
           run(body, event="repository_dispatch", version="3.1.13703", line_props=props(None)),
           code=1, contains="never guessed")
    expect("an unfetchable Directory.Build.props is RED",
           run(body, event="repository_dispatch", version="3.1.13703", line_props=None),
           code=1, contains="could not fetch Directory.Build.props")
    expect("an unfetchable cd-line.py is RED",
           run(body, event="repository_dispatch", version="3.1.13703", serve_cdline=False),
           code=1, contains="could not fetch cd-line.py")
    expect("an empty scripts-ref is RED on a versioned wake",
           run(body, event="repository_dispatch", version="3.1.13703", scripts_ref=""),
           code=1, contains="cannot be read")

    # Falsification: without the comparison the cross-line wake bakes — so the first case is green
    # BECAUSE of the check, not because of something else in the step.
    mutated = body.replace(COMPARISON, "if false; then")
    result = run(mutated, event="repository_dispatch", version="3.1.13703")
    if result["outputs"].get("run") == "true":
        print("  ok    falsification: without the comparison a v3 wake bakes the v4 tree")
    else:
        FAILURES.append("falsification")
        print(f"  FAIL  falsification: the mutated step still answered run={result['outputs'].get('run')!r}")

    if FAILURES:
        print(f"✗ {len(FAILURES)} case(s) failed: {', '.join(FAILURES)}")
        return 1
    print("✓ epoch-leg line check: a wake for another major ends by name, this line's wake bakes, every unreadable input is RED")
    return 0


if __name__ == "__main__":
    sys.exit(main())
