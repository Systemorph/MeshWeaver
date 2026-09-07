#!/usr/bin/env python3
"""check-workflow-yaml-keys.py — no mapping in a workflow file may write the same key twice.

(The name on this first line is load-bearing: node-repo-validate.yml fetches this file at platform-ref
and refuses a body whose first 400 bytes do not name it — the same shape as check-workflow-timeouts.py.)

WHY THIS EXISTS (Systemorph/MeshWeaver#3579 — a near-miss measured 2026-09-07)
------------------------------------------------------------------------------
`yaml.safe_load` accepts a duplicate mapping key SILENTLY, and the LAST one wins. Every guard in
this fleet parses workflows with it, so a file carrying a duplicate key parses, every shape check
passes, the job runs — and it uses a value the diff never shows.

Adopting #3560's `centralized-gen-manifests` input needed a `with:` block on each satellite's
`validate:` job. The patcher found the job's end with `s.index("\\n  ", i)`, which also matches
`"\\n    with:"` — so it cut the block after `uses:` and appended a SECOND `with:`. In
MeshWeaver.Education and MeshWeaver.Crm the file came out as:

    validate:
      uses: Systemorph/MeshWeaver/.github/workflows/node-repo-validate.yml@<NEW>
      with:
        platform-ref: <NEW>
        centralized-gen-manifests: true
      with:                                   # ← silently wins
        platform-ref: <OLD>

The `uses:` sha had moved and the effective `platform-ref` had not, so the lane would have fetched
its central guard scripts — and the canonical `gen-manifests.py` — at the OLD ref while running the
NEW lane. It was caught by reading the rendered block, not by any gate.

WHAT ALREADY EXISTED, AND WHY IT IS NOT THIS
--------------------------------------------
`check-pin-set-consistency.py` ("Platform pins name one build") models the *consequence* — a `uses:`
sha and a `platform-ref` that disagree — and reds on it. It is strictly narrower on three axes:

  * it only sees a duplicate whose effect is a PIN mismatch (a duplicated `if:`, `env:`, `needs:`
    or `timeout-minutes:` is invisible to it, and to everything else);
  * it only runs where `Platform pins name one build` is a required context (measured 2026-09-07:
    MeshWeaver.SocialMedia and MeshWeaver.Crm yes; MeshWeaver.Reinsurance and .Education no);
  * it reports after a full CI run, not at the first job.

This gate is the general form: any duplicate key, any mapping, any workflow, at the first job, with
no credential — so it also runs on forks and on Dependabot pull requests.

THE RULE
--------
For every workflow YAML under `.github/workflows/` and every composite-action `action.yml` /
`action.yaml` in the tree, no mapping may contain the same key twice. Two keys are the same when

  * they are written identically (`with` and `with`), **or**
  * they are written differently but RESOLVE to the same key (`on:` / `yes:` / `true:` are all the
    boolean True under PyYAML's YAML-1.1 resolver, so `safe_load` silently merges them while
    GitHub's YAML-1.2 parser keeps them apart — a disagreement between the gate's view and the
    runner's view is exactly the hazard this file exists to remove).

The second arm compares with Python's own `==`, deliberately: it asks what `safe_load` would MERGE,
and a dict merges `True` with `1` and `1` with `1.0` exactly as `==` does. The first arm covers the
mirror image — GitHub coerces every mapping key to a string, so `'1':` and `1:` are one key to the
runner while `safe_load` keeps them apart. Between them the two arms cover a collision in EITHER
parser, which is what "the gates and the runner see the same file" requires.

Both arms are reported with BOTH line numbers, so the shadowed value can be found by reading.

ANCHORS, ALIASES AND MERGE KEYS — a deliberate decision
-------------------------------------------------------
The scan walks the composed NODE GRAPH (`yaml.compose_all`), never a constructed dict, so:

  * an **anchor/alias** (`&base` … `*base`) is legitimate YAML and stays SILENT. An aliased mapping
    is the same node object at every use site, so it is checked once and never double-reported.
  * a **merge key** (`<<: *base`) is legitimate and stays SILENT — including the case that looks
    most like a duplicate, where the merged mapping supplies a key the local mapping also writes
    explicitly. YAML defines the explicit key as the winner there; it is a documented override,
    not a shadowed value, and redding it would be redding correct YAML.
  * **two `<<:` keys in one mapping** FIRE. That is a literal duplicate key, and the YAML merge
    spec expresses multiple merges as one key with a sequence value (`<<: [*a, *b]`), so the
    two-key form has no defined meaning.

GitHub Actions itself does not expand anchors in workflow files, so none of this appears in the
fleet today — it is proven in `--self-test` so that the decision is a measurement rather than a
claim, and so that a future composite action using them is not redded by surprise.

USAGE
-----
  check-workflow-yaml-keys.py [--root DIR]       gate the tree at DIR (default: cwd)
  check-workflow-yaml-keys.py --self-test        prove the gate fires and stays silent

Node repos do not copy this file: their `validate` lane (node-repo-validate.yml) fetches it from the
platform at the pinned platform ref and runs it against the caller's tree — the same centralization
as compile-check.py and the two guards beside it. Exit 1 on any violation; every violation is a
`::error` annotation carrying the file, the mapping, the key and both line numbers.
"""
from __future__ import annotations

import argparse
import sys
import tempfile
from pathlib import Path

try:
    import yaml
except ImportError:  # pragma: no cover - the CI step installs PyYAML; locally `pip install pyyaml`
    print("::error::check-workflow-yaml-keys.py needs PyYAML (pip install pyyaml)")
    sys.exit(2)

STR_TAG = "tag:yaml.org,2002:str"
MERGE_TAG = "tag:yaml.org,2002:merge"


def workflow_files(root: Path) -> list[Path]:
    wf = root / ".github" / "workflows"
    if not wf.is_dir():
        return []
    return sorted(p for p in wf.iterdir() if p.suffix in (".yml", ".yaml") and p.is_file())


def action_files(root: Path) -> list[Path]:
    """Composite actions are workflow YAML too — same parser, same defect, same blast radius.

    They are scanned when present and are never required to exist: a repo with no composite action
    is not a repo with a missing gate. `.git` and `node_modules` are skipped so the walk stays cheap.
    """
    out: list[Path] = []
    skip = {".git", "node_modules", "bin", "obj", ".venv"}
    for path in root.rglob("action.y*ml"):
        if path.name not in ("action.yml", "action.yaml") or not path.is_file():
            continue
        if any(part in skip for part in path.relative_to(root).parts[:-1]):
            continue
        out.append(path)
    return sorted(out)


def _resolved(node: yaml.Node) -> tuple[bool, object]:
    """The key as `yaml.safe_load` would see it — the view every OTHER guard in the fleet holds.

    Returns (resolvable, value). A non-scalar key is not resolvable: workflows do not use them, and
    saying so explicitly keeps two of them from comparing equal through a shared sentinel.
    """
    if not isinstance(node, yaml.ScalarNode):
        return False, None
    if node.tag == STR_TAG:
        return True, node.value
    try:
        value = yaml.constructor.SafeConstructor().construct_object(node)
    except Exception:
        return True, node.value
    if isinstance(value, (str, int, float, bool)) or value is None:
        return True, value
    return True, node.value


def _as_written(node: yaml.Node) -> str:
    """The key as a human reads it in the file. Non-scalar keys are canonicalised structurally."""
    if isinstance(node, yaml.ScalarNode):
        return node.value
    if isinstance(node, yaml.SequenceNode):
        return "[" + ", ".join(_as_written(v) for v in node.value) + "]"
    if isinstance(node, yaml.MappingNode):
        return "{" + ", ".join(f"{_as_written(k)}: {_as_written(v)}" for k, v in node.value) + "}"
    return repr(node)  # pragma: no cover - PyYAML has no fourth node kind


def _child_path(parent: str, key: str) -> str:
    return key if parent == "" else f"{parent}.{key}"


def scan_node(node: yaml.Node, rel: Path, path: str, seen_nodes: set[int], violations: list[str]) -> None:
    """Depth-first over the composed graph. An aliased node is visited once (id-deduped), which also
    makes a recursive anchor terminate instead of blowing the stack."""
    if id(node) in seen_nodes:
        return
    seen_nodes.add(id(node))

    if isinstance(node, yaml.SequenceNode):
        for i, item in enumerate(node.value):
            scan_node(item, rel, f"{path}[{i}]", seen_nodes, violations)
        return
    if not isinstance(node, yaml.MappingNode):
        return

    # (as-written, resolvable, resolved, line, is_merge) for every key already written in THIS mapping.
    prior: list[tuple[str, bool, object, int, bool]] = []
    for key_node, value_node in node.value:
        written = _as_written(key_node)
        resolvable, resolved = _resolved(key_node)
        line = key_node.start_mark.line + 1
        is_merge = getattr(key_node, "tag", None) == MERGE_TAG
        where = path or "<document root>"
        for prev_written, prev_resolvable, prev_resolved, prev_line, prev_merge in prior:
            same_text = prev_written == written
            # Deliberately Python's own `==`, not a type-matched comparison: this arm asks what
            # `yaml.safe_load` would MERGE, and a dict merges `True` with `1` and `1` with `1.0`
            # exactly as `==` does. Matching types here would let those two shapes through.
            same_value = not same_text and resolvable and prev_resolvable and prev_resolved == resolved
            if not (same_text or same_value):
                continue
            if same_text and is_merge and prev_merge:
                violations.append(
                    f"::error file={rel},line={line}::mapping `{where}` writes the merge key `<<` twice "
                    f"(lines {prev_line} and {line}). YAML expresses several merges as ONE key with a "
                    f"sequence value (`<<: [*a, *b]`); two `<<` keys have no defined meaning and the "
                    f"loader keeps only the last"
                )
            elif same_text:
                violations.append(
                    f"::error file={rel},line={line}::duplicate key `{written}` in mapping `{where}` — "
                    f"first written at line {prev_line}, shadowed by the one at line {line}. YAML takes "
                    f"the LAST, so everything under line {prev_line} is dead; `yaml.safe_load` accepts "
                    f"this silently, so the file parses, the shape gates pass and the job runs with the "
                    f"value the diff does not show (#3579)"
                )
            else:
                violations.append(
                    f"::error file={rel},line={line}::keys `{prev_written}` (line {prev_line}) and "
                    f"`{written}` (line {line}) in mapping `{where}` are written differently but resolve "
                    f"to the SAME key ({resolved!r}) under YAML 1.1 — `yaml.safe_load` merges them and "
                    f"keeps the last, while GitHub's YAML 1.2 parser keeps them apart. Every guard in "
                    f"the fleet then holds a different view of this file than the runner does (#3579)"
                )
            break  # one verdict per key; the first collision already names the shadowed line
        prior.append((written, resolvable, resolved, line, is_merge))
        scan_node(value_node, rel, _child_path(path, written), seen_nodes, violations)


def check_file(path: Path, rel: Path, violations: list[str]) -> None:
    try:
        docs = list(yaml.compose_all(path.read_text(encoding="utf-8"), Loader=yaml.SafeLoader))
    except yaml.YAMLError as e:  # a workflow that does not parse cannot be proven duplicate-free
        violations.append(f"::error file={rel}::not valid YAML: {e}")
        return
    for doc in docs:
        if doc is not None:
            scan_node(doc, rel, "", set(), violations)


def check_tree(root: Path) -> tuple[list[str], int, int]:
    """Return (violations, workflow_files_checked, action_files_checked)."""
    violations: list[str] = []
    workflows = workflow_files(root)
    if not workflows:
        violations.append(
            f"::error::{root}/.github/workflows has no workflow files — nothing to gate is a failure, not a pass"
        )
        return violations, 0, 0
    actions = action_files(root)
    for path in workflows + actions:
        check_file(path, path.relative_to(root), violations)
    return violations, len(workflows), len(actions)


# --------------------------------------------------------------------------------------------
# Self-test. Every case must FIRE on its defect and stay SILENT on its fix, and the two arms that
# carry the whole point of the gate additionally assert that the MESSAGE names the file and the key
# — a verdict nobody can act on is the same defect as no verdict.
# --------------------------------------------------------------------------------------------
CLEAN = """\
name: ci
on:
  pull_request:
jobs:
  validate:
    uses: Systemorph/MeshWeaver/.github/workflows/node-repo-validate.yml@abc
    with:
      platform-ref: abc
      centralized-gen-manifests: true
"""

# The measured near-miss, byte-for-byte in shape: a second `with:` appended after the first.
DUP_WITH = """\
name: ci
on:
  pull_request:
jobs:
  validate:
    uses: Systemorph/MeshWeaver/.github/workflows/node-repo-validate.yml@NEW
    with:
      platform-ref: NEW
      centralized-gen-manifests: true
    with:
      platform-ref: OLD
"""


def self_test() -> int:
    cases: list[tuple[str, str, bool]] = [
        # (name, workflow yaml, expect_violation)
        ("clean", CLEAN, False),
        ("dup-with-block", DUP_WITH, True),
        ("dup-scalar-in-with",
         "on: push\njobs:\n  a:\n    uses: x/y/.github/workflows/z.yml@abc\n    with:\n      platform-ref: NEW\n      platform-ref: OLD\n", True),
        ("dup-top-level-key",
         "name: ci\non: push\nname: ci2\njobs:\n  a:\n    runs-on: ubuntu-latest\n    steps: [{run: echo}]\n", True),
        ("dup-inside-step-of-sequence",
         "on: push\njobs:\n  a:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo one\n      - name: two\n        run: echo a\n        run: echo b\n", True),
        ("dup-job-id",
         "on: push\njobs:\n  a:\n    runs-on: ubuntu-latest\n    steps: [{run: echo}]\n  a:\n    runs-on: ubuntu-latest\n    steps: [{run: echo}]\n", True),
        ("dup-in-flow-mapping",
         "on: push\njobs:\n  a:\n    runs-on: ubuntu-latest\n    steps: [{run: echo, run: echo2}]\n", True),
        # `on:` resolves to the boolean True under PyYAML; `\"on\":` is the string. safe_load keeps
        # both, GitHub keeps one. The as-written arm catches it.
        ("dup-on-quoted-and-bare",
         "on: push\n\"on\": pull_request\njobs:\n  a:\n    runs-on: ubuntu-latest\n    steps: [{run: echo}]\n", True),
        # Written differently, identical after YAML 1.1 resolution — the arm that protects every
        # OTHER guard in the fleet, all of which read this file through safe_load.
        ("resolve-collision-yes-true",
         "on: push\njobs:\n  a:\n    runs-on: ubuntu-latest\n    env:\n      yes: 1\n      true: 2\n    steps: [{run: echo}]\n", True),
        # A dict merges `True` with `1` and `1` with `1.0`; so does this arm, by using the same `==`.
        ("resolve-collision-true-one",
         "on: push\njobs:\n  a:\n    runs-on: ubuntu-latest\n    env:\n      true: 1\n      1: 2\n    steps: [{run: echo}]\n", True),
        ("resolve-collision-int-float",
         "on: push\njobs:\n  a:\n    runs-on: ubuntu-latest\n    env:\n      1: a\n      1.0: b\n    steps: [{run: echo}]\n", True),
        # A quoted and an unquoted scalar with the same TEXT is the `on:` / `"on":` shape again:
        # safe_load keeps them apart (str vs int) while GitHub coerces every mapping key to a
        # string and merges them. The as-written arm fires, which is the direction that matters.
        ("quoted-and-plain-same-text-fires",
         "on: push\njobs:\n  a:\n    runs-on: ubuntu-latest\n    env:\n      '1': x\n      1: y\n    steps: [{run: echo}]\n", True),
        # Genuinely different keys must stay silent — the arm is not simply \"any two scalars\".
        ("distinct-scalars-silent",
         "on: push\njobs:\n  a:\n    runs-on: ubuntu-latest\n    env:\n      A: 1\n      B: 2\n      '3': x\n      4: y\n    steps: [{run: echo}]\n", False),
        # Anchors and aliases are legitimate YAML and must not red.
        ("anchor-alias-silent",
         "on: push\nx-base: &base\n  runs-on: ubuntu-latest\n  timeout-minutes: 5\njobs:\n  a:\n    <<: *base\n    steps: [{run: echo}]\n  b:\n    <<: *base\n    steps: [{run: echo}]\n", False),
        # A merge key supplying a key the mapping also writes explicitly is a DOCUMENTED override,
        # not a shadowed value. Silent by design.
        ("merge-key-override-silent",
         "on: push\nx-base: &base\n  runs-on: ubuntu-latest\n  timeout-minutes: 45\njobs:\n  a:\n    <<: *base\n    timeout-minutes: 5\n    steps: [{run: echo}]\n", False),
        # Two `<<` keys in one mapping is a literal duplicate with no defined meaning.
        ("double-merge-key-fires",
         "on: push\nx-a: &a\n  runs-on: ubuntu-latest\nx-b: &b\n  timeout-minutes: 5\njobs:\n  j:\n    <<: *a\n    <<: *b\n    steps: [{run: echo}]\n", True),
        # The sequence form of a merge is the spec's way to merge several — never a duplicate.
        ("sequence-merge-silent",
         "on: push\nx-a: &a\n  runs-on: ubuntu-latest\nx-b: &b\n  timeout-minutes: 5\njobs:\n  j:\n    <<: [*a, *b]\n    steps: [{run: echo}]\n", False),
        ("not-yaml", "jobs: [unclosed\n  - ::\n", True),
        ("repeated-key-in-different-mappings-is-fine",
         "on: push\njobs:\n  a:\n    runs-on: ubuntu-latest\n    steps: [{run: echo}]\n  b:\n    runs-on: ubuntu-latest\n    steps: [{run: echo}]\n", False),
    ]
    failures = 0
    for name, body, expect in cases:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / ".github" / "workflows").mkdir(parents=True)
            (root / ".github" / "workflows" / "t.yml").write_text(body, encoding="utf-8")
            violations, _, _ = check_tree(root)
            fired = bool(violations)
            if fired != expect:
                failures += 1
            print(f"self-test {'ok' if fired == expect else 'FAIL':4} {name:38} "
                  f"expected={'fire' if expect else 'silent'} got={'fire' if fired else 'silent'}")

    # The near-miss must be reproducible FROM THE MESSAGE: file, key, and both line numbers.
    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        (root / ".github" / "workflows").mkdir(parents=True)
        (root / ".github" / "workflows" / "ci.yml").write_text(DUP_WITH, encoding="utf-8")
        violations, _, _ = check_tree(root)
        text = "\n".join(violations)
        wanted = [".github/workflows/ci.yml", "`with`", "line 7", "line 10", "jobs.validate"]
        missing = [w for w in wanted if w not in text]
        if missing:
            failures += 1
        print(f"self-test {'ok' if not missing else 'FAIL':4} {'message-names-file-key-and-lines':38} "
              f"{'names ' + ', '.join(wanted) if not missing else 'MISSING ' + ', '.join(missing)}")

    # A composite action is scanned too — and its absence is never a failure.
    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        (root / ".github" / "workflows").mkdir(parents=True)
        (root / ".github" / "workflows" / "ci.yml").write_text(CLEAN, encoding="utf-8")
        (root / ".github" / "actions" / "thing").mkdir(parents=True)
        (root / ".github" / "actions" / "thing" / "action.yml").write_text(
            "name: thing\nruns:\n  using: composite\n  steps:\n    - run: echo\n      shell: bash\n      shell: sh\n",
            encoding="utf-8")
        violations, wf, act = check_tree(root)
        ok = bool(violations) and act == 1 and "action.yml" in "\n".join(violations)
        failures += 0 if ok else 1
        print(f"self-test {'ok' if ok else 'FAIL':4} {'composite-action-is-scanned':38} "
              f"expected=fire got={'fire' if violations else 'silent'} action_files={act}")

    # A tree with no workflows at all must FAIL, never pass vacuously (AGENTS.md — a gate that
    # cannot fail is not a gate).
    with tempfile.TemporaryDirectory() as tmp:
        violations, _, _ = check_tree(Path(tmp))
        fired = bool(violations)
        failures += 0 if fired else 1
        print(f"self-test {'ok' if fired else 'FAIL':4} {'no-workflows-dir':38} "
              f"expected=fire got={'fire' if fired else 'silent'}")

    if failures:
        print(f"::error::check-workflow-yaml-keys.py self-test: {failures} case(s) did not behave — the gate is not proven")
        return 1
    print("self-test: every case fired on its defect and stayed silent on its fix")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0])
    ap.add_argument("--root", default=".", help="repository root holding .github/workflows (default: cwd)")
    ap.add_argument("--self-test", action="store_true", help="prove the gate is non-vacuous and exit")
    args = ap.parse_args()
    if args.self_test:
        return self_test()
    root = Path(args.root).resolve()
    violations, workflows, actions = check_tree(root)
    for v in violations:
        print(v)
    print(f"check-workflow-yaml-keys: {workflows} workflow file(s) and {actions} composite action(s) checked, "
          f"{len(violations)} violation(s), root={root}")
    return 1 if violations else 0


if __name__ == "__main__":
    sys.exit(main())
