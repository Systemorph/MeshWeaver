#!/usr/bin/env python3
"""check-workflow-expression-length.py: no workflow string that holds a `${{ }}` may near GitHub's cap.

WHY (Systemorph/MeshWeaver#8340, measured 2026-11-16)
GitHub compiles every workflow string that contains a `${{ }}` (a whole `run:` script included)
into ONE expression and refuses the workflow when that expression exceeds 21,000 characters:
"Exceeded max expression length 21000". The refusal happens at load, so the run has no job, no
log and no check-run. GitHub names the file instead of the workflow and says only that a workflow
file has an issue. actionlint does not check this limit.

#8322 grew the node-repo-gate.yml gate step's script, which held one `${{ inputs.capture-coredumps }}`,
from 18,886 to 20,589 raw characters. GitHub counts the compiled form, so that was past the cap.
Every repository calling the lane @main (MeshWeaver.Plugins and every satellite) had a job-less
`ci.yml` failure from the merge on.

THE RULE
A string holding `${{` must stay at or under CAP raw characters. The cap leaves a wide margin
because the compiled length is larger than the raw one by an amount this script does not model.
The fix is never to raise the cap. Pass the value through the step's `env:` and read `$NAME` in
the script: a script without `${{` is plain text, and the cap does not apply to it at all.

Usage: check-workflow-expression-length.py --root <repo> | --self-test
"""
import argparse
import sys
import tempfile
from pathlib import Path

CAP = 15000


def walk(node, path):
    if isinstance(node, dict):
        for k, v in node.items():
            yield from walk(v, path + [str(k)])
    elif isinstance(node, list):
        for i, v in enumerate(node):
            yield from walk(v, path + [str(i)])
    elif isinstance(node, str) and "${{" in node:
        yield len(node), "/".join(path)


def violations(root: Path, cap: int = CAP) -> tuple[list[str], int]:
    import yaml  # noqa: PLC0415 (preinstalled on the hosted runner, as for the sibling checks)

    found, files = [], 0
    for f in sorted((root / ".github" / "workflows").glob("*.y*ml")):
        files += 1
        try:
            doc = yaml.safe_load(f.read_text(encoding="utf-8"))
        except yaml.YAMLError as e:
            found.append(f"{f.name}: does not parse ({str(e).splitlines()[0]})")
            continue
        for n, where in walk(doc, []):
            if n > cap:
                found.append(f"{f.name}: {where} holds a `${{{{ }}}}` expression in {n:,} characters "
                             f"(cap {cap:,}; GitHub refuses the whole workflow past 21,000 compiled). "
                             f"Move each `${{{{ }}}}` in it to the step's env: and read $NAME instead.")
    return found, files


def self_test() -> int:
    failures = []
    with tempfile.TemporaryDirectory() as tmp:
        wf = Path(tmp) / ".github" / "workflows"
        wf.mkdir(parents=True)
        big = "echo x\n" * 3000  # 21,000 characters
        (wf / "a.yml").write_text(
            "on: push\njobs:\n  j:\n    runs-on: x\n    steps:\n      - run: |\n"
            + "".join(f"          {l}\n" for l in (big + 'echo "${{ inputs.v }}"').splitlines()),
            encoding="utf-8")
        found, _ = violations(Path(tmp))
        if len(found) != 1:
            failures.append(f"a long script WITH an expression must be found, got {found}")
        (wf / "a.yml").write_text(
            "on: push\njobs:\n  j:\n    runs-on: x\n    steps:\n      - env:\n          V: ${{ inputs.v }}\n        run: |\n"
            + "".join(f"          {l}\n" for l in (big + 'echo "$V"').splitlines()),
            encoding="utf-8")
        found, _ = violations(Path(tmp))
        if found:
            failures.append(f"the same script reading env must pass, got {found}")
        _, files = violations(Path(tmp) / "nothing-here")
        if files != 0:
            failures.append(f"a root with no workflows must count 0 files (main refuses it), got {files}")
    if failures:
        for f in failures:
            print(f"SELF-TEST FAILED: {f}", file=sys.stderr)
        return 1
    print("check-workflow-expression-length.py self-test: all cases pass (long+expression found, long+env passes, empty root counts 0)")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", type=Path)
    ap.add_argument("--self-test", action="store_true")
    a = ap.parse_args()
    if a.self_test:
        return self_test()
    if not a.root:
        ap.error("--root or --self-test")
    found, files = violations(a.root)
    if files == 0:
        print(f"::error::no workflow file under {a.root / '.github' / 'workflows'} — a check over nothing is not a pass")
        return 1
    for v in found:
        print(f"::error::{v}")
    if found:
        return 1
    print(f"✓ {files} workflow file(s): no string holding a `${{{{ }}}}` exceeds {CAP:,} characters")
    return 0


if __name__ == "__main__":
    sys.exit(main())
