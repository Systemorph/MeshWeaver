#!/usr/bin/env python3
"""check-green-marker-order.py — a step that RECORDS a tree as green must follow every gate verdict.

WHY THIS EXISTS (MeshWeaver#5988)
---------------------------------
`dotnet-test.yml`'s `collect-results` job publishes the required context `Consolidate test results`
and, when it is green, pushes `refs/ci-green/<tree>/…`. The precheck of a LATER run on the same tree
honours that marker as proof for the WHOLE tree and takes the reuse path, on which the gate verdict
steps are skipped. So the marker vouches for every verdict this job translates, not only the test
shards.

Until #5988 the step `Record green tree` sat BEFORE the gate verdict steps (doc gate, licence,
binary compatibility, shell gate, shared rules, cross-repo pair, package pins, interface additions,
mirror sync, closing keywords, clients gate). Its `if:` named only build and test, so a tree whose
tests were green and whose GATE was red was still marked green — observed on pull request #5852,
run 36480427008: `Record green tree` succeeded at step 13 and a gate verdict failed at step 19.

The code fix moved the step to the end of the job, where GitHub's implicit `success()` keeps it from
running after ANY failed step. Nothing held it there: the next verdict step added to the collector,
or an `always()` added to the marker's `if:`, would reopen the hole with every check green. This
gate holds the shape.

THE RULE
--------
For the subject job (`dotnet-test.yml` → `collect-results`):

  1. every declared MARKER step exists — a marker renamed out from under this gate is STALE and
     fails, because a guard whose subject moved and that answers green has checked nothing;
  2. a marker's `if:` carries no status function that drops the implicit `success()` (`always()`,
     `cancelled()`, `failure()`, `success() ||` …) — any of them lets it run after a red verdict;
  3. no step AFTER the first marker can turn the job red: each carries `continue-on-error: true`
     — later MARKERS included, because a later marker's push failing turns the collector red over
     a tree the first marker already recorded green. A step that can still fail after the marker
     was written is a verdict the marker did not wait for — the #5988 shape;
  4. every job in the collector's `needs:` is ENFORCED for EACH marker, by one of:
       a. a VERDICT step before the first marker — one that can fail the job (`continue-on-error`
          absent or literally false; an EXPRESSION is decided at run time and never credited),
          whose `if:` is a plain conjunction with `needs.<job>.result != 'success'` as a TOP-LEVEL
          conjunct (so `!( … )`, a top-level `||` and a constant-false `&& false` are refused), and
          whose `run:` ends in `exit <non-zero>` with no earlier `exit` of any kind (an early
          `exit "$status"` may exit zero, so it is refused, not evaluated). A step that merely
          MENTIONS the result (`echo ${{ needs.x.result }}`), or a commented-out `# exit 1`,
          translates nothing;
       b. or that marker's OWN `if:` carrying `needs.<job>.result == 'success'` as a top-level
          conjunct under the same rules. Another marker's condition never counts: a later marker
          skipped on a red gate does not un-write the first.
     A gate job whose verdict nothing enforces can be red while the job, and therefore the marker,
     is green. Event/reuse exemptions on a verdict's `if:` (`github.event_name == …`,
     `needs.precheck.outputs.skip != 'true'`) are legitimate and preserved.

USAGE
-----
  check-green-marker-order.py [--root DIR]   gate the tree at DIR (default: cwd)
  check-green-marker-order.py --self-test    prove every rule fires on its defect, stays silent on
                                             its fix, and fires on the REAL workflow when a marker is
                                             moved back in front of a verdict (negative control)

Exit 1 on any violation; each is an `::error::` annotation naming the workflow, the job and the step.
"""
from __future__ import annotations

import argparse
import copy
import re
import sys
from pathlib import Path

try:
    import yaml
except ImportError:  # pragma: no cover - CI installs PyYAML; locally `pip install pyyaml`
    print("::error::check-green-marker-order.py needs PyYAML (pip install pyyaml)")
    sys.exit(2)

WORKFLOW = ".github/workflows/dotnet-test.yml"
JOB = "collect-results"
# The steps that write a marker a later run treats as evidence.
MARKERS = ("Record green tree", "Record executed-and-green main commit")

# Any status function in a marker's `if:` replaces the implicit `success()`.
STATUS_FUNCTION = re.compile(r"\b(always|cancelled|failure|success)\s*\(\s*\)")


def _load(path: Path) -> dict:
    # PyYAML (YAML 1.1) reads a bare `on:` key as True; irrelevant here, jobs are what we read.
    return yaml.safe_load(path.read_text(encoding="utf-8"))


def _truthy(value) -> bool:
    return value is True or (isinstance(value, str) and value.strip().lower() == "true")


def _literally_false(value) -> bool:
    """`continue-on-error` that certainly does NOT tolerate failure: absent, or literally false. An
    EXPRESSION (`${{ true }}`, `${{ needs.x.result != 'success' }}`) is decided at run time, so it is
    never credited as failable — it may well evaluate true and swallow the verdict."""
    return value is None or value is False or (isinstance(value, str) and value.strip().lower() == "false")


def _conjuncts(cond) -> list[str] | None:
    """Split an `if:` into its TOP-LEVEL `&&` conjuncts, or None when the expression is not a plain
    conjunction (a top-level `||`, unbalanced parentheses). Only a top-level conjunct is ENFORCED by
    the expression: a comparison under `!( … )` or beside an `|| …` branch is merely present."""
    text = str(cond or "").strip()
    if text.startswith("${{") and text.endswith("}}"):
        text = text[3:-2].strip()
    parts, depth, quote, start, i = [], 0, False, 0, 0
    while i < len(text):
        c = text[i]
        if c == "'":
            quote = not quote
        elif not quote:
            if c == "(":
                depth += 1
            elif c == ")":
                depth -= 1
                if depth < 0:
                    return None
            elif depth == 0 and text.startswith("||", i):
                return None
            elif depth == 0 and text.startswith("&&", i):
                parts.append(text[start:i].strip())
                start = i + 2
                i += 1
        i += 1
    if depth or quote:
        return None
    parts.append(text[start:].strip())
    return [p for p in parts if p]


# A conjunct that is constantly false makes the whole `if:` false — the step never runs.
CONSTANT_FALSE = re.compile(r"^\(*\s*(false|0|''|!\s*true|!\s*\(\s*true\s*\))\s*\)*$", re.IGNORECASE)


def _enforces(cond, comparison: re.Pattern) -> bool:
    """The `if:` is a plain conjunction, one of its top-level conjuncts IS `comparison`, and none is a
    constant false. Negation, `|| …` bypasses and `&& false` are therefore refused, not credited."""
    parts = _conjuncts(cond)
    if parts is None or any(CONSTANT_FALSE.match(p) for p in parts):
        return False
    return any(comparison.fullmatch(p) for p in parts)


def _always_exits_red(run) -> bool:
    """The body's LAST non-comment line is `exit <non-zero>` and NO earlier line exits at all. A
    substring test is not enough: `# exit 1`, `exit 0`, or a variable-valued `exit "$status"` before
    it can make the body exit zero, so any earlier `exit` is refused rather than reasoned about."""
    lines = [l.strip() for l in str(run or "").splitlines()]
    lines = [l for l in lines if l and not l.startswith("#")]
    if not lines or not re.fullmatch(r"exit\s+[1-9][0-9]*", lines[-1]):
        return False
    # `exit` in COMMAND position (line start, or after ; & | ( { then do else) — prose such as
    # "its exit status" inside an echo is not a command and must not refuse a real verdict.
    exit_cmd = re.compile(r"(^|[;&|({]|\bthen\b|\bdo\b|\belse\b)\s*exit\b")
    return not any(exit_cmd.search(l) for l in lines[:-1])


def _is_verdict_for(step: dict, need: str) -> bool:
    """A step that turns a red `need` into a red job: it can fail the job (`continue-on-error` absent or
    literally false), its `if:` is a conjunction ENFORCING `needs.<need>.result != 'success'`, and its
    body ends in `exit <non-zero>`. Merely mentioning `needs.<need>.result` translates nothing."""
    if not _literally_false(step.get("continue-on-error")):
        return False
    fires_on_red = re.compile(rf"needs\.{re.escape(need)}\.result\s*!=\s*'success'")
    return _enforces(step.get("if"), fires_on_red) and _always_exits_red(step.get("run"))


def check_job(job: dict, label: str) -> list[str]:
    """Return one message per violation for a collector job."""
    errors: list[str] = []
    steps = job.get("steps") or []
    names = [str(s.get("name", "")) for s in steps]

    for m in MARKERS:
        if m not in names:
            errors.append(f"{label}: marker step '{m}' not found — renamed or removed; update MARKERS "
                          f"in check-green-marker-order.py so this gate keeps checking something.")
    # EVERY step carrying a marker name, not the first of each: GitHub permits duplicate step names, so a
    # second `Record green tree` with `always()` would otherwise be a marker nothing here ever checked.
    marker_idx = [i for i, n in enumerate(names) if n in MARKERS]
    if not marker_idx:
        return errors
    first = min(marker_idx)

    for i in marker_idx:
        cond = str(steps[i].get("if", ""))
        hit = STATUS_FUNCTION.search(cond)
        if hit:
            errors.append(f"{label}: marker '{names[i]}' has `{hit.group(0)}` in its if: — that drops the "
                          f"implicit success(), so the marker can be written after a red gate verdict (#5988).")

    # EVERY step after the first marker — later markers included: a later marker's push failing turns
    # the collector red after the first marker already published the tree as green.
    for i in range(first + 1, len(steps)):
        if not _truthy(steps[i].get("continue-on-error")):
            errors.append(f"{label}: step '{names[i] or f'#{i}'}' comes AFTER marker '{names[first]}' and can "
                          f"still fail the job — move the marker below it, or the tree is recorded green "
                          f"before this verdict is known (#5988).")

    needs = job.get("needs") or []
    if isinstance(needs, str):
        needs = [needs]
    for need in needs:
        if any(_is_verdict_for(steps[i], need) for i in range(first)):
            continue
        required = re.compile(rf"needs\.{re.escape(need)}\.result\s*==\s*'success'")
        for i in sorted(marker_idx):
            if not _enforces(steps[i].get("if"), required):
                errors.append(f"{label}: needed job '{need}' is not enforced for marker '{names[i]}' — no "
                              f"failable verdict step before the first marker fires on "
                              f"needs.{need}.result != 'success' and exits non-zero, and the marker's own "
                              f"if: does not require needs.{need}.result == 'success'. It can be red while "
                              f"the tree is recorded green (#5988).")
    return errors


def check_tree(root: Path) -> list[str]:
    path = root / WORKFLOW
    if not path.is_file():
        return [f"{WORKFLOW}: not found under {root} — the subject of this gate moved."]
    jobs = (_load(path) or {}).get("jobs") or {}
    if JOB not in jobs:
        return [f"{WORKFLOW}: job '{JOB}' not found — the subject of this gate moved."]
    return check_job(jobs[JOB], f"{WORKFLOW} → {JOB}")


# ─────────────────────────────── self-test ───────────────────────────────

def _fixture() -> dict:
    return {
        "needs": ["build", "test", "doc-gate"],
        "if": "always()",
        "steps": [
            {"name": "Fail if any shard job did not succeed",
             "if": "always() && needs.test.result != 'success'", "run": "exit 1"},
            {"name": "Fail if the doc gate failed", "if": "needs.doc-gate.result != 'success'", "run": "exit 1"},
            {"name": "Record green tree",
             "if": "needs.build.result == 'success' && needs.test.result == 'success'",
             "continue-on-error": True, "run": "git push"},
            {"name": "Record executed-and-green main commit",
             "if": "github.ref == 'refs/heads/main' && needs.build.result == 'success'"
                   " && needs.test.result == 'success'",
             "continue-on-error": True, "run": "git push"},
            {"name": "State the bisect window", "if": "failure()", "continue-on-error": True, "run": "echo"},
        ],
    }


def _move_marker_before(job: dict, target: str) -> dict:
    j = copy.deepcopy(job)
    steps = j["steps"]
    marker = next(s for s in steps if s.get("name") == MARKERS[0])
    steps.remove(marker)
    at = next(i for i, s in enumerate(steps) if s.get("name") == target)
    steps.insert(at, marker)
    return j


def self_test(root: Path) -> int:
    failures = 0

    def expect(name: str, job: dict, should_fire: bool, needle: str = "") -> None:
        nonlocal failures
        errs = check_job(job, "fixture")
        fired = bool(errs) and (not needle or any(needle in e for e in errs))
        if fired != should_fire or (not should_fire and errs):
            failures += 1
            print(f"::error::self-test '{name}': expected {'a violation' if should_fire else 'silence'}"
                  f"{f' containing {needle!r}' if needle else ''}, got {errs or 'silence'}")
        else:
            print(f"ok  {name}")

    good = _fixture()
    expect("the fixed shape is silent", good, False)
    expect("a marker in front of a verdict fires (the #5988 shape)",
           _move_marker_before(good, "Fail if the doc gate failed"), True, "comes AFTER marker")
    j = copy.deepcopy(good)
    j["steps"][2]["if"] = "always() && " + j["steps"][2]["if"]
    expect("always() on a marker fires", j, True, "always()")
    j = copy.deepcopy(good)
    j["steps"][2]["if"] = "!cancelled() && needs.build.result == 'success'"
    expect("!cancelled() on a marker fires", j, True, "cancelled()")
    j = copy.deepcopy(good)
    j["needs"].append("licence-gate")
    expect("a needed gate with no verdict step fires", j, True, "licence-gate")
    j = copy.deepcopy(good)
    j["steps"][2]["name"] = "Record the green tree"
    expect("a renamed marker is stale and fires", j, True, "not found")
    j = copy.deepcopy(good)
    del j["steps"][4]["continue-on-error"]
    expect("a failable diagnostic after the marker fires", j, True, "State the bisect window")
    j = copy.deepcopy(good)
    del j["steps"][3]["continue-on-error"]
    expect("a failable LATER marker after the first marker fires", j, True,
           "'Record executed-and-green main commit' comes AFTER marker")
    j = copy.deepcopy(good)
    del j["steps"][1]
    j["steps"][2]["if"] += " && needs.doc-gate.result == 'success'"  # only the SECOND marker guards it
    expect("a gate guarded only by a later marker's if: fires", j, True,
           "'doc-gate' is not enforced for marker 'Record green tree'")
    j = copy.deepcopy(good)
    j["steps"][1] = {"name": "Fail if the doc gate failed", "run": 'echo "${{ needs.doc-gate.result }}"'}
    expect("a verdict that only MENTIONS the result fires", j, True, "'doc-gate' is not enforced")
    j = copy.deepcopy(good)
    j["steps"][1]["continue-on-error"] = True
    expect("a verdict that cannot fail the job fires", j, True, "'doc-gate' is not enforced")
    j = copy.deepcopy(good)
    j["steps"][1]["run"] = "echo doc gate red"
    expect("a verdict whose body never exits non-zero fires", j, True, "'doc-gate' is not enforced")
    for coe in ("${{ true }}", "${{ needs.doc-gate.result != 'success' }}"):
        j = copy.deepcopy(good)
        j["steps"][1]["continue-on-error"] = coe
        expect(f"an EXPRESSION continue-on-error ({coe}) is not credited as failable", j, True,
               "'doc-gate' is not enforced")
    j = copy.deepcopy(good)
    j["steps"][1]["continue-on-error"] = False
    expect("a literally-false continue-on-error stays credited", j, False)
    for cond in ("!(needs.doc-gate.result != 'success')",
                 "needs.doc-gate.result != 'success' && false",
                 "needs.doc-gate.result != 'success' || github.event_name == 'push'"):
        j = copy.deepcopy(good)
        j["steps"][1]["if"] = cond
        expect(f"a verdict if: that does not ENFORCE the comparison fires ({cond})", j, True,
               "'doc-gate' is not enforced")
    j = copy.deepcopy(good)
    del j["steps"][1]
    for m in (1, 2):  # an OR branch on each marker bypasses the requirement it appears to state
        j["steps"][m]["if"] += " && (needs.doc-gate.result == 'success' || true)"
    expect("a marker requirement under an always-true OR fires", j, True, "'doc-gate' is not enforced")
    for body in ("echo doc gate red\n# exit 1", "exit 0\nexit 1", "echo red; exit 1 # \nexit",
                 'status=0\nif [ "$status" -eq 0 ]; then\n  exit "$status"\nfi\nexit 1'):
        j = copy.deepcopy(good)
        j["steps"][1]["run"] = body
        expect(f"a verdict body that can exit zero fires ({body!r})", j, True, "'doc-gate' is not enforced")
    j = copy.deepcopy(good)
    j["steps"][1]["if"] = "(github.event_name == 'pull_request' || github.event_name == 'merge_group')" \
                          " && needs.doc-gate.result != 'success'"
    expect("a parenthesised event exemption on a verdict's if: stays silent", j, False)
    j = copy.deepcopy(good)
    del j["steps"][1]
    for m in (1, 2):  # EVERY marker's own if: guards the gate — a legitimate alternative to a verdict
        j["steps"][m]["if"] += " && needs.doc-gate.result == 'success'"
    expect("a gate guarded by every marker's own if: is silent", j, False)
    j = copy.deepcopy(good)
    j["steps"][1]["if"] = "github.event_name == 'pull_request' && " + j["steps"][1]["if"]
    expect("an event exemption on a verdict's if: stays silent", j, False)
    j = copy.deepcopy(good)  # a DUPLICATE-named marker is a marker too — every occurrence is checked
    j["steps"].append({"name": "Record green tree",
                       "if": "always() && needs.build.result == 'success' && needs.test.result == 'success'",
                       "continue-on-error": True, "run": "git push"})
    expect("a second step named like a marker, with always(), fires", j, True, "always()")

    # NEGATIVE CONTROL ON THE REAL WORKFLOW: the tree must pass as it stands, and must FAIL once the
    # marker is moved back to where #5988 found it (in front of the last gate verdict).
    path = root / WORKFLOW
    if path.is_file():
        real = (_load(path) or {}).get("jobs", {}).get(JOB)
        if real is None:
            failures += 1
            print(f"::error::self-test: job '{JOB}' missing from {WORKFLOW}")
        else:
            errs = check_job(real, "real")
            if errs:
                failures += 1
                print(f"::error::self-test: the real workflow does not pass: {errs}")
            else:
                print("ok  the real workflow passes")
            verdicts = [s.get("name") for s in real.get("steps", []) if str(s.get("name", "")).startswith("Fail if")]
            if not verdicts:
                failures += 1
                print("::error::self-test: no 'Fail if …' verdict step found in the real job — control is vacuous")
            else:
                moved = _move_marker_before(real, verdicts[-1])
                if any("comes AFTER marker" in e for e in check_job(moved, "real-moved")):
                    print(f"ok  moving the marker before '{verdicts[-1]}' in the real workflow fires")
                else:
                    failures += 1
                    print("::error::self-test: moving the marker before the last verdict in the real workflow did NOT fire")
    else:
        failures += 1
        print(f"::error::self-test: {WORKFLOW} not found under {root}; the negative control cannot run")

    print(f"self-test: {'FAILED' if failures else 'passed'} ({failures} failure(s))")
    return 1 if failures else 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--root", default=".")
    ap.add_argument("--self-test", action="store_true")
    args = ap.parse_args()
    root = Path(args.root)
    if args.self_test:
        return self_test(root)
    errors = check_tree(root)
    for e in errors:
        print(f"::error::{e}")
    if errors:
        return 1
    print(f"{WORKFLOW} → {JOB}: every marker step follows every gate verdict ({len(MARKERS)} marker(s) checked).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
