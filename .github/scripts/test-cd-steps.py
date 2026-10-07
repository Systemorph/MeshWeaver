#!/usr/bin/env python3
"""EXECUTE main-cd's pure-shell decision steps against fixtures. No Azure, no cluster, no secret.

WHY
---
`main-cd.yml` runs on `workflow_run`, `schedule` and `workflow_dispatch` — never on
`pull_request`. So the first execution of an edit to it is in production, on a schedule, and
its decision steps are exactly the ones whose verdict everyone downstream believes.

The release-version recovery step is the worked example, and it is the reason this file exists:
it had NEVER ONCE SUCCEEDED in its life. Nine consecutive scheduled reconciles (2026-08-28
22:12Z → 08-29 05:26Z, ~33 h) died in it, and every one of them accused promote — a component
that was working perfectly. MeshWeaver#2642 fixed it; this executes it.

HOW IT STAYS HONEST
-------------------
* The step is EXTRACTED FROM THE WORKFLOW by its `id:`, never copied here. A copy passes while
  the real thing rots.
* If the step cannot be found, or has grown a `${{ }}` expression this harness cannot supply, or
  has stopped calling the command being stubbed, the harness FAILS RED. It never reports a pass
  for a step it did not run — that is the same "a skipped gate ticks like a passed one" defect the
  thing being tested was full of.
* `az` is stubbed with a script that answers from a fixture using REAL jmespath — the same engine
  the azure-cli uses — so the null-throw is reproduced rather than asserted about.

THE FIXTURE is the live registry's shape on the day of the incident: 2101 manifests in
`memex-portal-ai`, 16 of them UNTAGGED (`tags: null` — the orphaned per-platform children an
index push leaves behind), and one digest carrying `aaf95af`, `main` and `3.0.0-rc8.ci.6360`.
"""

from __future__ import annotations

import json
import os
import re
import subprocess
import sys
import tempfile
from urllib.parse import urlsplit
from pathlib import Path

WORKFLOW = ".github/workflows/main-cd.yml"
MODULE_PACK_WORKFLOW = ".github/workflows/node-repo-module-pack.yml"
AVAILABILITY = ".github/scripts/check-release-availability.sh"
STEP_ID = "release"
# 🚨 The ACR read `release` used to do MOVED to `gate` (MeshWeaver#4539) so that the seal probe and
# the bake share ONE resolution of the release version. The cases moved with it — the harness
# executes the step wherever it lives, which is the whole point of extracting by `id:`.
BAKE_VERSION_STEP_ID = "bake_version"
DECIDE_STEP_ID = "decide"
VERDICT_STEP_ID = "verdict"
HEAL_STEP_ID = "heal"
# The registry probe the ledger's evidence comes from (MeshWeaver#4687). Extracted and EXECUTED
# like every other step here: injecting its output into `decide` would leave the probe itself —
# its `--query`, its repository list, its three branches — covered by nothing, which is the gap
# Copilot named on that pull request.
ATTEMPTED_STEP_ID = "attempted"
# The step that discharges a deferral instead of promising one (MeshWeaver#4652): when a run lets
# go of the delivery lane it asks whether main's HEAD still has a publisher, and creates one if not.
HANDOFF_STEP_ID = "handoff"

SHORT_SHA = "aaf95af"
VERSION = "3.0.0-rc8.ci.6360"

# ── the stub `az` ───────────────────────────────────────────────────────────────────────────
# Reproduces the three behaviours that matter: a successful jmespath query, a query that THROWS
# on a null field (az's own message, exit 1), and a call that fails for an unrelated reason
# (auth). Nothing else about `az` is modelled, and the harness asserts the step still calls the
# subcommand being stubbed, so a step that grew a second az call cannot pass on this one.
AZ_STUB = r'''#!/usr/bin/env python3
import json, os, sys
import jmespath
from jmespath.exceptions import JMESPathError

if os.environ.get("AZ_FAIL"):
    sys.stderr.write("ERROR: Please run 'az login' to setup account.\n")
    sys.exit(1)

argv = sys.argv[1:]

# `acr repository show-tags` — the `attempted` step's staging-marker probe (MeshWeaver#4687).
# Modelled PER REPOSITORY, because the whole point of that probe is that the three publishing
# legs stage independently: AZ_TAGS is "<repo>=<tag>,<tag>;<repo>=…", and AZ_TAGS_FAIL names the
# repositories whose read must FAIL, so a case can drive the unreadable arm for one leg alone.
# The step's own --query does the prefix filter, exactly as jmespath would in az.
if argv[:3] == ["acr", "repository", "show-tags"]:
    repo = None
    query = None
    for i, a in enumerate(argv):
        if a == "--repository":
            repo = argv[i + 1]
        if a == "--query":
            query = argv[i + 1]
    if repo is None or query is None:
        sys.stderr.write("stub az: show-tags without --repository/--query\n")
        sys.exit(97)
    if repo in os.environ.get("AZ_TAGS_FAIL", "").split(","):
        sys.stderr.write("ERROR: (ResponseError) the request could not be completed\n")
        sys.exit(1)
    tags = []
    for entry in os.environ.get("AZ_TAGS", "").split(";"):
        if not entry:
            continue
        name, _, values = entry.partition("=")
        if name == repo:
            tags = [v for v in values.split(",") if v]
    try:
        result = jmespath.compile(query).search(tags)
    except JMESPathError as e:
        sys.stderr.write(f"{e}\n")
        sys.exit(1)
    for row in result or []:
        print(row)
    sys.exit(0)

if argv[:3] != ["acr", "manifest", "list-metadata"]:
    sys.stderr.write(f"stub az: unmodelled invocation {argv!r}\n")
    sys.exit(97)

query = None
for i, a in enumerate(argv):
    if a == "--query":
        query = argv[i + 1]
if query is None:
    sys.stderr.write("stub az: no --query\n")
    sys.exit(97)

data = json.load(open(os.environ["AZ_FIXTURE"]))
try:
    result = jmespath.compile(query).search(data)
except JMESPathError as e:
    # az surfaces a jmespath failure verbatim on stderr and exits non-zero.
    sys.stderr.write(f"{e}\n")
    sys.exit(1)

for row in result or []:
    print(row)
'''


def fixture(tagged: bool) -> list[dict]:
    """memex-portal-ai as it stood on 2026-08-29: 2101 manifests, 16 with `tags: null`."""
    rows: list[dict] = [{"digest": f"sha256:{i:064x}", "tags": None} for i in range(16)]
    rows += [{"digest": f"sha256:{i:064x}", "tags": [f"ci.{i}"]} for i in range(16, 2100)]
    rows.append(
        {
            "digest": "sha256:6c3abd508033db59865e8bedf68076bdb75b4c044e3fa1159c01eff98b2a1089",
            # Phase A tags the short sha; Phase C arms the release on the SAME digest.
            "tags": [SHORT_SHA, "main", VERSION] if tagged else [SHORT_SHA, "main"],
        }
    )
    return rows


# ── extraction ──────────────────────────────────────────────────────────────────────────────
# ── the stub `gh` ───────────────────────────────────────────────────────────────────────────
# 🚨 THIS IS A SAFETY DEVICE, not a convenience. The `decide` step posts a heal COMMENT to the
# CD-failure issue on a reconcile attempt, and `run_step` inherits the caller's environment —
# so on a developer machine with a live `gh` login, running this harness POSTS REAL COMMENTS TO
# REAL ISSUES. That is not hypothetical: it happened while these cases were being written, three
# times, to Systemorph/MeshWeaver#2810, and the comments had to be deleted by hand.
#
# A test harness must not be able to mutate anything outside its temp directory. The stub records
# what was asked and answers nothing, so the step's control flow is unchanged and its side effect
# is not. The credentials are cleared as well (below) — either alone would do, and one of them
# will still be there after someone edits the other.
GH_STUB = """#!/usr/bin/env bash
echo "gh $*" >> "$GH_CALLS"
# Two reads are modelled, because two steps make them:
#  * `actions/runs?head_sha=…` — "is a run of this workflow live on this sha?", asked by `decide`
#    (older runs only, MeshWeaver#3376) and by `verdict` (any live run, MeshWeaver#3513). A case
#    supplies the answer the real `--jq` would have produced in GH_RUNS_RESULT.
#  * `commits/main` — main's tip, which `verdict` uses to tell "superseded" from "stuck".
# GH_RUNS_FAIL makes ONLY the first one fail, so a case can drive the fail-closed arm without
# also breaking the tip resolution. Every other invocation stays silent and succeeds, exactly as
# before — the safety property (this stub can mutate nothing) is unchanged.
#
# 🚨 GH_RUNS_FIXTURE runs the step's OWN `--jq` program, with real jq, over a real
# `workflow_runs` payload. That is deliberately stronger than answering GH_RUNS_RESULT: the whole
# discriminator lives in that filter, and its most dangerous defect — dropping `.id != $RUN_ID`,
# so every reconcile finds ITSELF "in flight" and the alarm is silenced forever — is invisible to
# any case that hands the step a pre-computed answer.
#  * `issue list` — "is there an open ci-failure issue?", asked by the `heal` step. A case supplies
#    the number the real `--jq` would have produced in GH_ISSUE_RESULT (empty = none open).
#  * `issue close` — the ledger close (#3176). GH_CLOSE_FAIL makes ONLY that call fail, so a case
#    can drive the "a failed close must not red a run that delivered" arm without also breaking
#    the comment that has to survive it.
case "$*" in
  # GH_LIST_FAIL makes the listing FAIL (Copilot on #4565): without `-e` an empty `$(gh issue list)`
  # read as "no ledger yet", so the seal step CREATED a second ledger at count zero every hour.
  *"issue list"*)
    if [ -n "${GH_LIST_FAIL:-}" ]; then
      echo "gh: HTTP 502 (https://api.github.com/repos/x/y/issues)" >&2
      exit 1
    fi
    printf '%s' "${GH_ISSUE_RESULT:-}" ;;
  # The seal step's attempt ledger (MeshWeaver#4539): `issue view --json comments --jq "[…] | length"`
  # counts its marker comments. A case supplies the number the real `--jq` would have produced.
  # Two markers are counted with two different needles, so the answers are separate: GH_SEAL_COUNT
  # for the attempt marker, GH_STOPPED_COUNT for the one-shot "stopped" marker. Matching on the
  # NEEDLE rather than on call order means a case cannot pass by accident if the step reorders them.
  # 🚨 `${VAR-0}`, NOT `${VAR:-0}`. With the colon an explicitly EMPTY value would be replaced by
  # the default, so the case that drives "the ledger could not be read" would silently hand the step
  # a perfectly good `0` and pass having tested the opposite branch. (It did, once — which is why
  # that case exists and why this comment does.) Without the colon, set-but-empty stays empty, which
  # is exactly what `$(gh …)` yields when the call fails.
  *"issue view"*)
    if [ -n "${GH_VIEW_FAIL:-}" ]; then
      echo "gh: HTTP 502 (https://api.github.com/repos/x/y/issues/1)" >&2
      exit 1
    fi
    case "$*" in
      # The seal step's RANK read (claim-then-rank, Copilot on #4565): where this run's claim sits among
      # the pair's claims. Matched BEFORE `*cd-seal*`, whose needle the rank query also contains.
      # Defaults to the count — "my claim is the newest" — and `${VAR-…}` keeps set-but-empty EMPTY, so
      # a case can drive the unrankable arm.
      *"index(true)"*)   printf '%s' "${GH_SEAL_RANK-${GH_SEAL_COUNT-0}}" ;;
      *cd-seal-stopped*) printf '%s' "${GH_STOPPED_COUNT-0}" ;;
      *cd-seal*)         printf '%s' "${GH_SEAL_COUNT-0}" ;;
      *)                 printf '%s' "${GH_VIEW_RESULT-}" ;;
    esac
    ;;
  # `gh issue create` prints the new issue's URL; the step takes the number off its tail.
  *"issue create"*) printf '%s\n' "${GH_CREATE_RESULT:-https://github.com/o/r/issues/4242}" ;;
  *"label create"*) ;;
  # `gh workflow run` — the ONLY mutating call the `handoff` step makes (MeshWeaver#4652). Matched
  # before the reads so a dispatch can never be answered by a run-list fixture, and recorded in
  # GH_CALLS like everything else: "did this step actually ask GitHub to create a publisher" is the
  # whole subject of its cases, and it is invisible in stdout. GH_DISPATCH_FAIL drives the arm where
  # the dispatch is refused — the one state where the step must go RED rather than warn.
  *"workflow run"*)
    if [ -n "${GH_DISPATCH_FAIL:-}" ]; then
      echo "gh: HTTP 403 (https://api.github.com/repos/x/y/actions/workflows/main-cd.yml/dispatches)" >&2
      exit 1
    fi ;;
  # `commits/<sha>/check-runs` — HEAD's required check, which decides whether HEAD is publishable
  # at all. Matched BEFORE `*commits/main*`: the handoff reads it by SHA, and a future edit that
  # read it by ref would otherwise be answered by the tip stub and test nothing.
  #
  # 🚨 THERE IS NO PRE-COMPUTED ANSWER HERE, for the same reason GH_RUNS_FIXTURE exists: the whole
  # discriminator is the step's own `--jq` — select by NAME, sort by `started_at`, take the LAST —
  # and handing the step a ready-made "completed success" would leave every one of those decisions
  # untested. A re-run appends a check run, so "take the last" is what stops an old red from
  # shadowing a green (and an old green from shadowing a red, which is the direction that publishes
  # an untested tree). Refuse rather than default: a stub that can answer without the filter is the
  # defect. (Caught in review.)
  *check-runs*)
    if [ -n "${GH_CHECK_FAIL:-}" ]; then
      echo "gh: HTTP 502 (https://api.github.com/repos/x/y/commits/abc/check-runs)" >&2
      exit 1
    fi
    if [ -z "${GH_CHECKS_FIXTURE:-}" ]; then
      echo "stub gh: a check-runs call with no GH_CHECKS_FIXTURE — the harness models the filter, not a verdict" >&2
      exit 97
    fi
    prog=""
    while [ $# -gt 0 ]; do
      [ "$1" = "--jq" ] && prog="$2"
      shift
    done
    if [ -z "$prog" ]; then
      echo "stub gh: a check-runs call with no --jq — the harness models the filter, not the payload" >&2
      exit 97
    fi
    jq -r "$prog" < "$GH_CHECKS_FIXTURE"
    exit 0 ;;
  # Matched explicitly and BEFORE the reads below: a heal comment quotes a run URL, and a body
  # containing `actions/runs` would otherwise fall into the run-list arm and answer a fixture.
  # GH_COMMENT_FAIL makes the comment write FAIL (Copilot on #4565): the seal step's attempt marker is
  # its budget, and an unwritten marker must stop the repair — never launch it uncounted.
  *"issue comment"*)
    if [ -n "${GH_COMMENT_FAIL:-}" ]; then
      echo "gh: HTTP 403 (https://api.github.com/repos/x/y/issues/1/comments)" >&2
      exit 1
    fi
    ;;
  *"issue close"*)
    if [ -n "${GH_CLOSE_FAIL:-}" ]; then
      echo "gh: HTTP 403 (https://api.github.com/repos/x/y/issues/1)" >&2
      exit 1
    fi
    ;;
  *actions/runs*)
    if [ -n "${GH_RUNS_FAIL:-}" ]; then
      echo "gh: HTTP 502 (https://api.github.com/repos/x/y/actions/runs)" >&2
      exit 1
    fi
    if [ -n "${GH_RUNS_FIXTURE:-}" ]; then
      prog=""
      while [ $# -gt 0 ]; do
        [ "$1" = "--jq" ] && prog="$2"
        shift
      done
      if [ -z "$prog" ]; then
        echo "stub gh: an actions/runs call with no --jq — the harness models the filter, not the payload" >&2
        exit 97
      fi
      jq -r "$prog" < "$GH_RUNS_FIXTURE"
      exit 0
    fi
    printf '%s' "${GH_RUNS_RESULT:-}"
    ;;
  *commits/main*) printf '%s' "${GH_TIP_RESULT:-}" ;;
esac
exit 0
"""


def extract_step(root: Path, step_id: str) -> str:
    import yaml

    doc = yaml.safe_load((root / WORKFLOW).read_text())
    # 🚨 ONE step per id, across EVERY job. Two steps sharing an id would hand this harness
    # whichever it met first — silently, and the cases would then test a step the workflow does
    # not run where the reader thinks it does.
    owners = [name for name, job in (doc.get("jobs") or {}).items()
              for step in (job.get("steps") or [])
              if isinstance(step, dict) and step.get("id") == step_id]
    if len(owners) > 1:
        die(f"step id `{step_id}` appears in {len(owners)} steps of {WORKFLOW} (jobs: {', '.join(owners)}) — "
            "the harness cannot tell which one it is testing. Give each step a distinct id.")
    for job in (doc.get("jobs") or {}).values():
        for step in job.get("steps") or []:
            if isinstance(step, dict) and step.get("id") == step_id:
                body = step.get("run")
                if not body:
                    die(f"step id `{step_id}` in {WORKFLOW} has no `run:` — nothing to execute.")
                if "${{" in body:
                    die(
                        f"step id `{step_id}`'s `run:` now contains a ${{{{ }}}} expression, which "
                        "this harness cannot supply. It is refusing to execute a body it would "
                        "have to rewrite — pass the value through `env:` instead (every input it "
                        "takes today already is)."
                    )
                return body
    die(
        f"no step with `id: {step_id}` in {WORKFLOW}. It was renamed, moved or deleted — and this "
        "harness will not report a pass for a step it could not find. Re-point STEP_ID."
    )


def die(msg: str):
    print(f"::error::{msg}")
    sys.exit(1)


# ── running one case ────────────────────────────────────────────────────────────────────────
def run_step(body: str, env: dict[str, str], rows: list[dict] | None, az_fail: bool = False,
             runs: list[dict] | None = None, calls_out: list[str] | None = None,
             cwd: str | None = None, checks: list[dict] | None = None):
    """Execute one extracted step. `cwd` runs it in a prepared tree — the seal step invokes
    `.github/scripts/check-release-availability.sh` BY PATH, so a case that wants to drive the
    probe's three outcomes puts its own script there. Nothing else about the step is rewritten;
    the real body decides, from the real exit code and the real log."""
    with tempfile.TemporaryDirectory() as td:
        tmp = Path(td)
        binp = tmp / "bin"
        binp.mkdir()
        (binp / "az").write_text(AZ_STUB)
        (binp / "az").chmod(0o755)
        (binp / "gh").write_text(GH_STUB)
        (binp / "gh").chmod(0o755)
        calls = tmp / "gh_calls"
        calls.touch()
        fx = tmp / "fixture.json"
        fx.write_text(json.dumps(rows if rows is not None else []))
        out = tmp / "github_output"
        out.touch()
        # The runner provides these to every step; a step that writes its decision to the job
        # summary (the `decide` step does) dies on `set -u` without them. Supplying them here is
        # not indulgence — omitting them would make the harness fail for a reason that has nothing
        # to do with the logic under test, which is how a harness gets disabled.
        summary = tmp / "github_step_summary"
        summary.touch()

        e = dict(os.environ)
        e["PATH"] = f"{binp}:{e['PATH']}"
        e["AZ_FIXTURE"] = str(fx)
        e["GITHUB_OUTPUT"] = str(out)
        e["GITHUB_STEP_SUMMARY"] = str(summary)
        e.setdefault("GITHUB_REPOSITORY", "Systemorph/MeshWeaver")
        # The decide step reads these from `env:` on the runner (MeshWeaver#3376); a case may
        # override them, and GH_RUNS_RESULT is what the stub `gh` answers for the in-flight probe.
        e.setdefault("SHA", "abc1234000000000000000000000000000000000")
        e.setdefault("RUN_ID", "1000")
        e.setdefault("WORKFLOW_NAME", "Continuous Delivery (main)")
        e.setdefault("REPO", "Systemorph/MeshWeaver")
        e.pop("GH_RUNS_RESULT", None)
        e.pop("GH_RUNS_FAIL", None)
        e.pop("GH_RUNS_FIXTURE", None)
        e.pop("GH_TIP_RESULT", None)
        # Same discipline as the four above: a knob left over from the caller's environment would
        # silently change what a case measures.
        e.pop("GH_ISSUE_RESULT", None)
        e.pop("GH_CLOSE_FAIL", None)
        for knob in ("AZ_TAGS", "AZ_TAGS_FAIL",
                     "GH_SEAL_COUNT", "GH_STOPPED_COUNT", "GH_VIEW_RESULT", "GH_CREATE_RESULT",
                     "GH_VIEW_FAIL", "GH_LIST_FAIL", "GH_COMMENT_FAIL", "GH_SEAL_RANK",
                     "GH_DISPATCH_FAIL", "GH_CHECK_FAIL", "GH_CHECKS_FIXTURE"):
            e.pop(knob, None)
        # `RUNNER_TEMP` is where the seal step writes the probe's log. The runner always provides
        # it; inherited from a developer's shell it would be absent and the step would fall back to
        # /tmp, which is shared — two concurrent cases would read each other's log.
        e["RUNNER_TEMP"] = str(tmp)
        e["GH_CALLS"] = str(calls)
        # Belt AND braces: the stub above shadows `gh` on PATH, and these leave a real `gh` — if one
        # is ever reached another way — with no credential to write with.
        for cred in ("GH_TOKEN", "GITHUB_TOKEN", "GH_ENTERPRISE_TOKEN"):
            e[cred] = ""
        e["GH_CONFIG_DIR"] = str(tmp / "gh-config")
        if az_fail:
            e["AZ_FAIL"] = "1"
        else:
            e.pop("AZ_FAIL", None)
        if runs is not None:
            rf = tmp / "runs.json"
            rf.write_text(json.dumps({"workflow_runs": runs}))
            e["GH_RUNS_FIXTURE"] = str(rf)
        # Same discipline as `runs`: a real `check_runs` payload the step's OWN `--jq` reduces.
        if checks is not None:
            cf = tmp / "checks.json"
            cf.write_text(json.dumps({"check_runs": checks}))
            e["GH_CHECKS_FIXTURE"] = str(cf)
        e.update(env)

        p = subprocess.run(["bash", "-c", body], env=e, cwd=cwd, capture_output=True, text=True)
        # What the step ASKED GitHub to do is the subject of the ledger cases: a step that prints
        # "closing" and calls no `gh issue close` is exactly the defect #3176 records, and it is
        # invisible in stdout.
        if calls_out is not None:
            calls_out.extend(calls.read_text().splitlines())
        # The job summary is part of what a decision step SAYS, so a case asserting on the
        # decision's wording must be able to see it.
        return p.returncode, p.stdout + p.stderr + summary.read_text(), out.read_text()



# ── the DECIDE step: the path that only runs when main goes quiet ────────────────────────────
def run_decide_cases(root, case) -> None:
    """
    🚨 <b>#2643 — a code path gated on INACTIVITY gets no coverage from ordinary traffic.</b>

    `decide` chooses between building an image set and re-asserting the content bake
    (`bake_only`). The bake-only branch is reached only on a reconcile that finds a COMPLETE image
    set and nothing to build — i.e. only when nobody is pushing. On a busy trunk it never runs; its
    first execution was in production at 22:12 on a Friday, and it then failed nine times out of
    nine over 33 h before anyone noticed, because every fix-verifying push took the other branch.

    #2642 fixed the specific defect and this harness executes THAT step. What it did not do is
    remove the shape: the branch is still only exercised by silence. These cases exercise it on
    every run instead — the deliberate exercise the issue asks for, without waiting for the trunk
    to fall quiet or adding a dispatch input nobody remembers to use.

    The inputs are the ones the step reads from `env:`, so this drives the real decision logic
    rather than a restatement of it.
    """
    body = extract_step(root, DECIDE_STEP_ID)

    # A stub is only evidence about what it stubs; the same rule as the release step. If the
    # branch under test stops writing this output, every case below would pass vacuously.
    if "bake_only=true" not in body:
        die(
            f"step `{DECIDE_STEP_ID}` no longer writes `bake_only=true` — the branch these cases "
            "exercise is gone or renamed, so they would pass having tested nothing. Update the "
            "harness with the step."
        )

    # A RECONCILE (not a push): no workflow_run payload, so the step takes the reconcile path.
    reconcile = {
        "GREEN": "true", "PENDING": "false", "CONCL": "success",
        "SHORT": "abc1234", "REASON": "reconcile", "RELEVANT": "true",
        "AGE_MIN": "120", "FRESH_AGE_MIN": "120", "BATCH_WINDOW": "",
        "MAX_ATTEMPTS": "3", "RUN_URL": "https://example.invalid/run",
        "GH_TOKEN": "", "COMPLETE": "true",
    }

    rc, log, outputs = run_step(body, reconcile, None)
    case("a reconcile with a COMPLETE image set takes the bake-only branch",
         rc == 0 and "bake_only=true" in outputs, f"rc={rc} out={outputs!r} log={log}")
    case("...and it does NOT also publish an image set",
         "publish=true" not in outputs.replace("publish=true\n", "publish=true\n") or "publish=false" in outputs,
         f"out={outputs!r}")
    case("...and it says WHY, in the decision log",
         "complete image set" in log, f"log={log}")

    # The neighbour that must NOT be confused with it: an INCOMPLETE set on the same event is a
    # real build. If these two ever collapse into one, delivery either stops or doubles.
    rc, log, outputs = run_step(body, {**reconcile, "COMPLETE": "false"}, None)
    case("an INCOMPLETE image set on the same event still builds",
         rc == 0 and "bake_only=true" not in outputs, f"rc={rc} out={outputs!r} log={log}")

    # 🚨 The operator override (#2622). A complete image set for CORE's sha says nothing about
    # whether the portal HOSTS — which live in MeshWeaver.Plugins — changed. Without this, a
    # dispatch is a structural no-op for that case: reason=reconcile, COMPLETE=true, bake_only.
    rc, log, outputs = run_step(body, {**reconcile, "FORCE_REBUILD": "true"}, None)
    case("rebuild=true overrides a COMPLETE image set and publishes",
         rc == 0 and "publish=true" in outputs and "bake_only=true" not in outputs,
         f"rc={rc} out={outputs!r} log={log}")
    case("...and it says it was an explicit rebuild, not an ordinary publish",
         "rebuild" in log, f"log={log}")

    # The default must be unchanged: absent or false, the bake-only branch still wins. Without
    # this, "the override works" would also pass if the override were always on.
    rc, log, outputs = run_step(body, {**reconcile, "FORCE_REBUILD": "false"}, None)
    case("rebuild=false leaves the bake-only branch exactly as it was",
         rc == 0 and "bake_only=true" in outputs, f"rc={rc} out={outputs!r} log={log}")
    # 🚨 ONE COMMIT, ONE IMAGE SET (MeshWeaver#3376). The reconcile tick that fires while the
    # push-triggered run is still building must defer to it, on BOTH event paths, before any
    # other verdict — and it must say which run it deferred to.
    older = "https://example.invalid/actions/runs/999"
    for reason, extra in (("reconcile", {"COMPLETE": "false"}),
                          ("push", {"COMPLETE": "false", "FRESH_AGE_MIN": "999999"})):
        rc, log, outputs = run_step(body, {**reconcile, "REASON": reason, **extra, "GH_RUNS_RESULT": older}, None)
        case(f"an OLDER run still publishing this sha defers the {reason} path (#3376)",
             rc == 0 and "publish=false" in outputs and older in log and "bake_only=true" not in outputs,
             f"rc={rc} out={outputs!r} log={log}")
    # Exercise the REAL jq discriminator, not a pre-computed stub answer. GitHub reports a run held
    # behind a concurrency group as `pending`; that exact status escaped the old queued/in_progress
    # enumeration and let the scheduled reconcile duplicate the same commit's delivery.
    pending = {
        "id": 999,
        "name": "Continuous Delivery (main)",
        "status": "pending",
        "html_url": "https://example.invalid/actions/runs/999",
    }
    rc, log, outputs = run_step(
        body,
        {**reconcile, "REASON": "reconcile", "COMPLETE": "false"},
        None,
        runs=[pending],
    )
    case("a PENDING older delivery defers the reconcile instead of duplicating it",
         rc == 0 and "publish=false" in outputs and pending["html_url"] in log,
         f"rc={rc} out={outputs!r} log={log}")

    # And the probe must be inert when nothing is in flight: the same inputs with an empty answer
    # publish exactly as they did before the probe existed. Without this, "defers" would also
    # pass if the step deferred unconditionally.
    rc, log, outputs = run_step(body, {**reconcile, "REASON": "reconcile", "COMPLETE": "false", "GH_RUNS_RESULT": ""}, None)
    case("...and with nothing in flight the same reconcile still builds",
         rc == 0 and "publish=true" in outputs, f"rc={rc} out={outputs!r} log={log}")

    # ── THE LEDGER RECORDS A FAILURE, NOT A CADENCE (MeshWeaver#4687) ────────────────────────
    #
    # 🚨 <b>109 issues of a 109-issue population recorded a delivery that had not failed.</b>
    # `CD: main <sha> has an incomplete image set` says, in its body, *every self-updating install
    # stays on the previous image*. Measured over every one ever filed: 28 were opened while the
    # deployable set was COMPLETE and only the plugins pair tag was behind; of the 25 most recent
    # of the rest, 19 were opened before any CD run for that commit had been created at all.
    # Neither is a failure. The two cases below are the ones that would have failed before the
    # fix, and each is paired with the control that fires the alarm — so "quieter" can never be
    # mistaken for "muted".
    #
    # The subject is what the step ASKED GITHUB TO DO. A decision that prints nothing alarming and
    # still calls `gh issue create` is exactly the defect, and it is invisible in stdout — so these
    # read GH_CALLS.
    ledger = {**reconcile, "REASON": "reconcile", "GH_VIEW_RESULT": "0", "GH_ISSUE_RESULT": ""}

    # 🚨 THE PAIR TAG IS RETIRED (policy `platform-module-deploy-separate`), and with it the
    # `HOSTS_STALE` branch that rebuilt the portal whenever MeshWeaver.Plugins `main` had moved
    # (#2622, #4688). A complete set is bake-only whatever a stale caller still passes; a host
    # change reaches the image only through an explicit `rebuild` (Plugins' relevance-classified
    # dispatch). Fed the retired input on purpose: a decide step that still read it would build.
    calls: list[str] = []
    rc, log, outputs = run_step(body, {**ledger, "COMPLETE": "true", "HOSTS_STALE": "true"}, None,
                                calls_out=calls)
    filed = [c for c in calls if "issue create" in c or "issue comment" in c]
    case("a COMPLETE set is bake-only even if the retired HOSTS_STALE input is set — no plugins-move rebuild",
         rc == 0 and "bake_only=true" in outputs and "publish=true" not in outputs,
         f"rc={rc} out={outputs!r} log={log}")
    case("...and files NOTHING on the ci-failure ledger",
         not filed, f"calls={filed!r} log={log}")
    # MUTATION CONTROL: put the retired branch back and the same inputs must build — otherwise the
    # case above would pass on a step that never looked at the variable at all.
    retired_branch = (
        'if [ "$COMPLETE" = "true" ] && [ "${HOSTS_STALE:-false}" = "true" ] \\\n'
        '   && [ "$GREEN" = "true" ] && [ "${FORCE_REBUILD:-false}" != "true" ]; then\n'
        '  decision true "host pairing behind"\n'
        '  exit 0\n'
        'fi\n')
    # `body` is the YAML-dedented `run:` block, so the anchor carries no workflow indentation.
    anchor = 'if [ "$COMPLETE" = "true" ] && [ "${FORCE_REBUILD:-false}" != "true" ]; then\n'
    mutated = body.replace(anchor, retired_branch + anchor, 1)
    rc, log, outputs = run_step(mutated, {**ledger, "COMPLETE": "true", "HOSTS_STALE": "true"}, None)
    case("MUTATION CONTROL: the retired HOSTS_STALE branch, put back, DOES build on the same inputs",
         mutated != body and rc == 0 and "publish=true" in outputs, f"rc={rc} out={outputs!r} log={log}")
    rc, log, outputs = run_step(body, {**ledger, "COMPLETE": "true", "FORCE_REBUILD": "true"}, None)
    case("...and an explicit `rebuild` (Plugins' host-change dispatch) still builds a complete set",
         rc == 0 and "publish=true" in outputs, f"rc={rc} out={outputs!r} log={log}")

    calls = []
    rc, log, outputs = run_step(body, {**ledger, "COMPLETE": "false", "ATTEMPTED": "false"}, None,
                                calls_out=calls)
    filed = [c for c in calls if "issue create" in c or "issue comment" in c]
    case("a green commit NO publisher has staged layers for is published, not alarmed about",
         rc == 0 and "publish=true" in outputs, f"rc={rc} out={outputs!r} log={log}")
    case("...and nothing is written to the ci-failure ledger",
         not filed, f"calls={filed!r} log={log}")

    # 🚨 THE NEGATIVE CONTROL, and the reason this is not a mute. The SAME inputs, with the one
    # fact that distinguishes a failure — a publisher already staged layers for this commit and
    # never reached `promote` — must still open the issue and record the attempt.
    calls = []
    rc, log, outputs = run_step(body, {**ledger, "COMPLETE": "false", "ATTEMPTED": "true"}, None,
                                calls_out=calls)
    case("an ATTEMPTED delivery that left staging layers behind still files the ci-failure issue",
         rc == 0 and any("issue create" in c for c in calls), f"calls={calls!r} log={log}")
    case("...and still records the attempt against the budget",
         any("issue comment" in c for c in calls) and "attempt 1/3" in log,
         f"calls={calls!r} log={log}")
    case("...and still publishes",
         "publish=true" in outputs, f"out={outputs!r}")

    # An UNANSWERED probe must fail towards the ledger: a registry read that did not complete is
    # not evidence that nothing was attempted, and the cost of a missed delivery hole is higher
    # than the cost of one extra comment.
    calls = []
    rc, log, outputs = run_step(body, {**ledger, "COMPLETE": "false", "ATTEMPTED": ""}, None,
                                calls_out=calls)
    case("an EMPTY attempt probe fails towards the ledger, never towards silence",
         rc == 0 and any("issue create" in c for c in calls), f"calls={calls!r} log={log}")

    # And the branch must not swallow the case it was never about: a commit whose required check
    # settled RED still gets the blocked report, whatever the attempt probe says.
    calls = []
    rc, log, outputs = run_step(body, {**ledger, "COMPLETE": "false", "ATTEMPTED": "false",
                                       "GREEN": "false", "CONCL": "failure", "AGE_MIN": "300",
                                       "GH_ISSUE_RESULT": "77"}, None, calls_out=calls)
    case("a settled-RED required check is still reported, attempt probe or not",
         rc == 0 and "publish=false" in outputs and any("issue comment" in c for c in calls),
         f"rc={rc} out={outputs!r} calls={calls!r} log={log}")

    # 🚨 <b>A STAGING TAG IS NOT EVIDENCE OF FAILURE WHILE ITS RUN IS ALIVE.</b>
    # (Copilot on MeshWeaver#4687.) The in-flight tie-break filters to LOWER run ids so that two
    # runs deciding at once cannot both defer — which means a NEWER run can be mid-publish here.
    # Measured on `0dadacc`: the scheduled run was created 34 s BEFORE the genuine delivery run.
    # Both probes run their OWN `--jq` over ONE real payload, so the id filters are executed, not
    # asserted about.
    newer_live = {
        "id": 2000,  # > RUN_ID (1000): invisible to the older-only tie-break, visible to the ledger's
        "name": "Continuous Delivery (main)",
        "status": "in_progress",
        "html_url": "https://example.invalid/actions/runs/2000",
    }
    calls = []
    rc, log, outputs = run_step(body, {**ledger, "COMPLETE": "false", "ATTEMPTED": "true"}, None,
                                runs=[newer_live], calls_out=calls)
    case("a NEWER live run on this sha does NOT defer the publish (the tie-break is unchanged)",
         rc == 0 and "publish=true" in outputs, f"rc={rc} out={outputs!r} log={log}")
    case("...and its staged layers are read as a publication in progress, so nothing is filed",
         not [c for c in calls if "issue create" in c or "issue comment" in c],
         f"calls={calls!r} log={log}")
    case("...and the decision names the run it deferred the LEDGER to",
         newer_live["html_url"] in log, f"log={log}")

    # The control on that pair: the identical inputs with NO live run must still file. Without it,
    # "does not file" would also pass if the ledger had simply been removed.
    calls = []
    rc, log, outputs = run_step(body, {**ledger, "COMPLETE": "false", "ATTEMPTED": "true"}, None,
                                runs=[], calls_out=calls)
    case("MUTATION CONTROL: with no live run the same staged layers DO open the issue",
         rc == 0 and any("issue create" in c for c in calls), f"calls={calls!r} log={log}")


# ── the ATTEMPTED step: the registry probe the ledger's evidence comes from ───────────────────
def run_attempted_cases(root, case) -> None:
    """
    🚨 <b>A probe whose output is injected into the next step is covered by nothing.</b>

    Every `decide` case above hands `ATTEMPTED` in as an environment value, so a malformed
    `--query`, the wrong repository, or a broken empty/error branch would pass all of them while
    the production probe answered the wrong thing every hour. (Copilot on MeshWeaver#4687.)
    These execute the real step against a stub `az` that models `acr repository show-tags` per
    repository, so the prefix filter and the repository list are the things under test.
    """
    body = extract_step(root, ATTEMPTED_STEP_ID)

    # A stub is only evidence about what it stubs.
    if "show-tags" not in body:
        die(f"step `{ATTEMPTED_STEP_ID}` no longer reads the registry's tags — these cases would "
            "pass having tested nothing. Update the harness with the step.")

    env = {"SHORT": "abc1234"}

    rc, log, outputs = run_step(body, env, None)
    case("no staging tag anywhere answers `attempted=false` — nothing was tried",
         rc == 0 and "attempted=false" in outputs, f"rc={rc} out={outputs!r} log={log}")

    # 🚨 EACH publishing repository on its own. The three .NET legs stage in PARALLEL, so a run
    # whose migration leg staged and whose portal leg died before its push leaves NO portal marker
    # — and a probe that looked only at `memex-portal-ai` would answer "nothing was attempted"
    # over exactly the torn set the ledger exists to record.
    for repo in ("memex-portal-ai", "memex-migration", "mw-plugin-test"):
        rc, log, outputs = run_step(
            body, {**env, "AZ_TAGS": f"{repo}=staging-abc1234-777"}, None)
        case(f"a staging tag in {repo} ALONE answers `attempted=true`",
             rc == 0 and "attempted=true" in outputs, f"rc={rc} out={outputs!r} log={log}")

    # The prefix filter is the step's own `--query`, executed by real jmespath: a staging tag for a
    # DIFFERENT commit must not be read as this one's attempt.
    rc, log, outputs = run_step(
        body, {**env, "AZ_TAGS": "memex-portal-ai=staging-def5678-777,abc1234,3.0.0-ci.42"}, None)
    case("a staging tag for ANOTHER commit is not this commit's attempt",
         rc == 0 and "attempted=false" in outputs, f"rc={rc} out={outputs!r} log={log}")

    # 🚨 FAILS TOWARDS THE ALARM, and only towards it: one unreadable repository is enough, because
    # the cost of a spurious issue is a comment and the cost of a missed one is a silent hole.
    rc, log, outputs = run_step(body, {**env, "AZ_TAGS_FAIL": "memex-migration"}, None)
    case("an UNREADABLE repository answers `attempted=true`, never `false`",
         rc == 0 and "attempted=true" in outputs, f"rc={rc} out={outputs!r} log={log}")
    case("...and says so as a warning naming the repository it could not read",
         "::warning::" in log and "memex-migration" in log, f"log={log}")


# ── the VERDICT step: the one that told main it was broken when it was not ───────────────────
# The runs the cases below replay, verbatim from the API on 2026-09-07. 7962/7964 are the
# `workflow_run` twins that were mid-publish; 7963/7965 are the scheduled reconciles that accused
# them of a stuck delivery.
CD = "Continuous Delivery (main)"
SHA_7962 = "6577052de389ff15ad3c94c78cb132e17d542590"
SHA_7964 = "93dd88941" + "0" * 31


def wf_run(rid: int, status: str, started: str, name: str = CD) -> dict:
    return {
        "id": rid,
        "name": name,
        "status": status,
        "run_started_at": started,
        "html_url": f"https://github.com/Systemorph/MeshWeaver/actions/runs/{rid}",
    }


def run_verdict_cases(root, case) -> None:
    """
    🚨 <b>MeshWeaver#3513 — the hourly reconcile red on its own twin's in-flight publish.</b>

    `delivery-verdict` used to conclude "delivery is stuck" from four gate outputs that describe
    main at ONE INSTANT (`reason=reconcile publish=false complete=false green=true`). That triple
    is also what a reconcile looks like while its `workflow_run` twin is halfway through creating
    the image set — so on 2026-09-07 runs 7963 and 7965 each failed main's CD with an accusation
    that was false forty minutes before the same commits sealed.

    The step now asks its own question before making the claim, and these cases are the two arms
    that make the answer worth anything: it must STILL go red when nothing is publishing (the
    coverage that must not be lost), and it must NOT go red on the 7963/7965 shape. Every case
    drives the step's REAL `--jq` filter over a real `workflow_runs` payload, because the filter
    IS the discriminator — an answer handed to it would prove nothing about it.
    """
    body = extract_step(root, VERDICT_STEP_ID)

    # A stub is only evidence about what it stubs. If the step stops probing the run list, or stops
    # being able to say "stuck" at all, every case below would pass having tested nothing.
    for needle, why in (
        ("actions/runs", "no longer probes the run list, so the in-flight arm tests nothing"),
        ("Delivery is stuck", "no longer carries the stuck accusation, so the red arm tests nothing"),
    ):
        if needle not in body:
            die(f"step `{VERDICT_STEP_ID}` {why} (missing `{needle}`). Update the harness with the step.")

    # The 7963 shape, exactly: a scheduled reconcile, main green, image set incomplete, published
    # nothing. Everything below varies ONLY what the run list answers.
    shape = {
        "REASON": "reconcile", "PUBLISH": "false", "COMPLETE": "false", "GREEN": "true",
        "CONCLUSION": "completed/success", "SHA": SHA_7962, "SHORT": "6577052",
        "GATE_RESULT": "success", "UPSTREAM_EVENT": "", "UPSTREAM_BRANCH": "",
        "GITHUB_EVENT_NAME": "schedule", "RUN_ID": "34071948741", "GH_TOKEN": "",
    }
    me = wf_run(34071948741, "in_progress", "2026-09-07T01:06:58Z")          # run 7963 itself
    twin = wf_run(34069402974, "in_progress", "2026-09-07T00:18:33Z")        # run 7962, mid-publish
    done = wf_run(34067890019, "completed", "2026-09-06T23:48:02Z")
    other = wf_run(34069999999, "in_progress", "2026-09-07T01:00:00Z", name="MeshWeaver Build and Test")

    # ── ARM 1: THE COVERAGE THAT MUST NOT BE LOST ────────────────────────────────────────────
    # A genuinely incomplete set with NO publish in flight. This is the state the job exists for,
    # and it is the arm that would silently disappear if the fix had been "skip while busy".
    rc, log, outputs = run_step(body, shape, None, runs=[me, done])
    case("a green, incomplete main with NOTHING in flight is still RED",
         rc != 0 and "Delivery is stuck" in log, f"rc={rc} log={log}")
    case("...and the red now carries the negative finding that makes it a measurement",
         "NO non-completed run of this workflow" in log, f"log={log}")
    case("...and it claims no deferral",
         "deferred_to=" not in outputs, f"out={outputs!r}")

    # 🚨 THE SELF-EXCLUSION CASE. The reconcile is ITSELF `in_progress` in the very list it reads.
    # Drop `.id != $RUN_ID` from the filter and this alarm is disabled forever, in every state,
    # with a green tick — strictly worse than the false red it was fixed for. ARM 1 above already
    # contains `me`; this asserts the point on a list where the self run is the ONLY candidate.
    rc, log, outputs = run_step(body, shape, None, runs=[me])
    case("a reconcile does NOT count ITSELF as the publication in flight",
         rc != 0 and "Delivery is stuck" in log, f"rc={rc} log={log}")

    # A live run of a DIFFERENT workflow on the same commit is not a publication either — a
    # re-run of Build-and-Test must never silence CD's delivery alarm.
    rc, log, outputs = run_step(body, shape, None, runs=[me, other])
    case("a live run of ANOTHER workflow on the same sha does not count",
         rc != 0 and "Delivery is stuck" in log, f"rc={rc} log={log}")

    # A run that has FINISHED is not in flight. Without the status filter, every commit that ever
    # had a CD run would read as "publishing".
    rc, log, outputs = run_step(body, shape, None, runs=[me, done, wf_run(34068000000, "completed", "x")])
    case("a COMPLETED run on the same sha does not count as in flight",
         rc != 0 and "Delivery is stuck" in log, f"rc={rc} log={log}")

    # ── ARM 2: THE 7963 / 7965 SHAPE ─────────────────────────────────────────────────────────
    rc, log, outputs = run_step(body, shape, None, runs=[me, twin, done])
    case("run 7963's exact conditions no longer red — its twin 7962 was mid-publish",
         rc == 0, f"rc={rc} log={log}")
    case("...and the step does not accuse delivery of being stuck",
         "Delivery is stuck" not in log, f"log={log}")
    case("...and it names the run, its status, and since when — not a bare 'skipping'",
         all(s in log for s in ("34069402974", "in_progress", "2026-09-07T00:18:33Z")), f"log={log}")
    case("...and it states its own falsification condition, so nobody reads it as a disabled alarm",
         "suppressed ONLY while" in log and "goes RED" in log, f"log={log}")
    case("...and it hands the run id to the summary step as a POSITIVE finding",
         "deferred_to=34069402974" in outputs and "deferred_status=in_progress" in outputs,
         f"out={outputs!r}")

    # Run 7965, the second false red of the night, on the other commit.
    twin_7964 = wf_run(34072269844, "in_progress", "2026-09-07T01:12:30Z")
    me_7965 = wf_run(34073867519, "in_progress", "2026-09-07T01:42:12Z")
    rc, log, outputs = run_step(body,
                                {**shape, "SHA": SHA_7964, "SHORT": "93dd889", "RUN_ID": "34073867519"},
                                None, runs=[me_7965, twin_7964])
    case("run 7965's exact conditions no longer red either — twin 7964 was mid-publish",
         rc == 0 and "34072269844" in log and "Delivery is stuck" not in log, f"rc={rc} log={log}")

    # A run WAITING for a runner is not stuck delivery either, and the message must say which of
    # the two it saw rather than flattening them into one word.
    rc, log, outputs = run_step(body, shape, None,
                                runs=[me, wf_run(34069402974, "queued", "2026-09-07T01:05:00Z")])
    case("a QUEUED run counts as a live publication, and is reported as queued",
         rc == 0 and "queued" in log, f"rc={rc} log={log}")

    # A run held behind a concurrency group is `pending`, not `queued`. This is the exact state
    # that escaped both the decision and verdict probes on 2026-09-10.
    rc, log, outputs = run_step(body, shape, None,
                                runs=[me, wf_run(34069402974, "pending", "2026-09-10T17:07:55Z")])
    case("a PENDING run counts as a live publication, and is reported as pending",
         rc == 0 and "pending" in log and "deferred_status=pending" in outputs,
         f"rc={rc} out={outputs!r} log={log}")

    # A NEWER live run counts too. The gate's #3376 probe looks only at OLDER runs because it has
    # to break a deferral tie; this step decides nothing, so any live run falsifies "nobody is
    # publishing it". Encoded as a case so a future edit cannot quietly narrow it back.
    rc, log, outputs = run_step(body, shape, None,
                                runs=[me, wf_run(34079999999, "in_progress", "2026-09-07T01:07:30Z")])
    case("a NEWER live run on this sha falsifies 'stuck' just as an older one does",
         rc == 0 and "34079999999" in log, f"rc={rc} log={log}")

    # ── ARM 3: THE PROBE MUST NOT BECOME A SKIP-TRAPDOOR ─────────────────────────────────────
    # An unanswerable probe is not reassurance. If this ever passes, the fix has reintroduced the
    # exact defect AGENTS.md names: a gate that cannot run looking like a gate that passed.
    rc, log, outputs = run_step(body, {**shape, "GH_RUNS_FAIL": "1"}, None, runs=[me, twin])
    case("a FAILING probe fails the step — it never silences the alarm",
         rc != 0, f"rc={rc} log={log}")
    case("...and it names the probe as what went unanswered, not the delivery",
         "probe" in log and "FAILED" in log, f"log={log}")
    case("...and it claims no deferral it could not observe",
         "deferred_to=" not in outputs, f"out={outputs!r}")

    # ── THE PROBE IS NOT A BLANKET SILENCER ──────────────────────────────────────────────────
    # It sits inside ONE branch. Every other verdict must be exactly as loud as before, even with
    # a publication live on the commit — otherwise "a run is in flight" becomes a universal excuse.
    rc, log, outputs = run_step(body, {**shape, "REASON": "push", "GREEN": "false",
                                       "CONCLUSION": "completed/failure"}, None, runs=[me, twin])
    case("a push path on a RED main is still red, live run or not",
         rc != 0 and "settled RED" in log, f"rc={rc} log={log}")
    rc, log, outputs = run_step(body, {**shape, "REASON": "", "GATE_RESULT": "failure"},
                                None, runs=[me, twin])
    case("an EMPTY gate verdict is still red, live run or not",
         rc != 0 and "NO verdict" in log, f"rc={rc} log={log}")
    rc, log, outputs = run_step(body, {**shape, "REASON": "", "GATE_RESULT": "skipped",
                                       "GITHUB_EVENT_NAME": "workflow_run",
                                       "UPSTREAM_EVENT": "pull_request", "UPSTREAM_BRANCH": "feat/x"},
                                None, runs=[me, twin])
    case("a PR-triggered workflow_run is still the legitimate no-op it always was",
         rc == 0 and "not applicable" in log, f"rc={rc} log={log}")

    # The superseded-burst branch still resolves the tip through the API this step also uses for
    # the probe — proof the two reads did not get crossed when REPO moved into `env:`.
    rc, log, outputs = run_step(body, {**shape, "REASON": "push", "GREEN": "false",
                                       "CONCLUSION": "completed/cancelled",
                                       "GH_TIP_RESULT": "f" * 40}, None, runs=[me])
    case("a cancelled, superseded push is still a green no-op",
         rc == 0 and "superseded" in log, f"rc={rc} log={log}")
    rc, log, outputs = run_step(body, {**shape, "REASON": "push", "GREEN": "false",
                                       "CONCLUSION": "completed/cancelled",
                                       "GH_TIP_RESULT": SHA_7962}, None, runs=[me])
    case("a cancelled TIP is still red",
         rc != 0 and "is the TIP" in log, f"rc={rc} log={log}")


# ── the HEAL step: the one that told a human to close an issue and then never closed one ─────
def run_handoff_cases(root, case) -> None:
    """
    🚨 <b>MeshWeaver#4652 — the deferral nobody ever discharged.</b>

    The push lane keeps ONE run in flight and ONE pending, and every arrival REPLACES the pending
    one. That supersede rule is correct — measured over the 21.7 h to 2026-09-17T19:29Z, 77 push
    arrivals against 14 runs that executed (35–107 min each, median ~67): not discarding would
    serialize ~86 h of lane time into a 21.7 h window and delivery would fall behind without bound.

    What the discard broke is the two liveness probes. A `pending` run has executed nothing
    (`jobs.total_count == 0`) and GitHub may delete it, yet `gate` DEFERS to it and `verdict`
    SUPPRESSES the stuck-delivery alarm on it — and neither ever learns it was discarded. Measured
    on three consecutive hourly reconciles on 2026-09-17 (16:31Z, 17:26Z, 18:35Z): every one
    deferred to a run cancelled minutes later with zero jobs, so the only backstop stood down on
    every tick.

    Narrowing the probe is not the fix — `pending` genuinely means "will build" when the run
    survives (2026-09-10), and nothing observable AT PROBE TIME separates the two. The `handoff`
    step moves the question to the one moment it IS answerable: when a run lets go of the lane, it
    asks whether main's HEAD still has a publisher and creates one if not.

    These cases pin the two things that make it worth anything — it must FIRE in the hole (a green
    HEAD nobody is publishing) and it must NOT fire anywhere else, above all not on itself — plus
    the two mutation controls that prove the cases could have failed.
    """
    body = extract_step(root, HANDOFF_STEP_ID)

    # A stub is only evidence about what it stubs. If the step stops dispatching, stops probing for
    # a successor, or stops reading HEAD's check, every case below would pass having tested nothing.
    for needle, why in (
        ("workflow run", "no longer dispatches, so the hole-closing arm tests nothing"),
        ("actions/runs", "no longer probes for a successor, so the cost control tests nothing"),
        ("check-runs", "no longer reads HEAD's required check, so the not-green arm tests nothing"),
        ("sort_by(.started_at)", "no longer takes the LATEST check run, so a re-run reads as its first attempt"),
        ("DELIVERY_LEGS", "no longer reads the shipping legs, so a run that promoted and then failed tests nothing"),
        ("HANDOFF_ELIGIBLE", "lost the one-hop bound, so a deterministic failure loops forever"),
    ):
        if needle not in body:
            die(f"step `{HANDOFF_STEP_ID}` {why} (missing `{needle}`). Update the harness with the step.")

    HEAD = "aaaa111" + "0" * 33          # main's tip in every case below
    BUILT = "bbbb222" + "0" * 33         # what the terminating run targeted
    ME = 9000
    REQUIRED = "Consolidate test results"
    CLEAN_LEGS = "success skipped skipped skipped success success success"

    def chk(status: str, concl, started: str, name: str = REQUIRED) -> dict:
        return {"name": name, "status": status, "conclusion": concl, "started_at": started}

    GREEN = [chk("completed", "success", "2026-09-17T18:00:00Z")]

    def handoff(**over):
        """The shape of a run that has just let go of the lane without publishing HEAD."""
        env = {
            "SHA": BUILT, "PUBLISH": "false", "COMPLETE": "false", "PROMOTE_RESULT": "failure",
            "DELIVERY_LEGS": CLEAN_LEGS,
            "HANDOFF_ELIGIBLE": "true", "RUN_ID": str(ME), "REPO": "Systemorph/MeshWeaver",
            "WORKFLOW_NAME": CD, "WORKFLOW_FILE": "main-cd.yml", "GH_TOKEN": "",
            "GH_TIP_RESULT": HEAD,
        }
        env.update(over)
        return env

    def drive(env, runs, mutate=None, checks=None):
        calls: list[str] = []
        b = body
        if mutate is not None:
            needle, repl = mutate
            if needle not in b:
                die(f"the mutation control for `{HANDOFF_STEP_ID}` cannot apply: `{needle}` is no "
                    "longer in the step. A control that cannot mutate proves nothing — re-point it.")
            b = b.replace(needle, repl)
        rc, log, outputs = run_step(b, env, None, runs=runs, calls_out=calls,
                                    checks=GREEN if checks is None else checks)
        dispatched = any("workflow run" in c for c in calls)
        return rc, log, dispatched, calls

    me = wf_run(ME, "in_progress", "2026-09-17T19:00:00Z")

    # ── ARM 1: THE HOLE IT EXISTS TO CLOSE ───────────────────────────────────────────────────
    # 2026-09-17T19:25:49Z, run 35261268480: it FAILED, main's HEAD was green, and the hourly tick
    # would not have healed it — it defers whenever anything is queued. Nothing is queued here.
    rc, log, dispatched, calls = drive(handoff(), [me])
    case("a green HEAD with NO run queued or running gets a publisher dispatched",
         rc == 0 and dispatched and "HANDED ON" in log, f"rc={rc} dispatched={dispatched} log={log}")
    case("...and the dispatch names this workflow and main, not a guess",
         any("workflow run main-cd.yml" in c and "--ref main" in c for c in calls), f"calls={calls}")

    # 🚨 THE SELF-EXCLUSION CASE, and it is the one that would disable the drain forever. The
    # terminating run is itself non-completed in the very list it reads (it is still running THIS
    # step). Drop `.id != $RUN_ID` and every run finds itself a successor, hands nothing on, and
    # the step becomes a green no-op in every state — strictly worse than not existing.
    rc, log, dispatched, _ = drive(handoff(), [me], mutate=(".id != $RUN_ID and ", ""))
    case("MUTATION CONTROL: without `.id != $RUN_ID` the step finds ITSELF and dispatches nothing",
         not dispatched, f"dispatched={dispatched} log={log}")

    # ── ARM 2: THE COST CONTROL — an inherited obligation is not a missing one ────────────────
    # A queued run counts, and deliberately so: whichever run finally executes runs THIS step when
    # it terminates. This is what keeps the common, saturated case at zero extra runs.
    for status in ("pending", "queued", "in_progress"):
        rc, log, dispatched, _ = drive(handoff(), [me, wf_run(9100, status, "2026-09-17T19:20:00Z")])
        case(f"a {status} run on HEAD inherits the obligation — nothing is dispatched",
             rc == 0 and not dispatched and "9100" in log, f"rc={rc} dispatched={dispatched} log={log}")

    # A COMPLETED run on HEAD is not a successor — without the status filter every commit that ever
    # had a CD run would read as covered.
    rc, log, dispatched, _ = drive(handoff(), [me, wf_run(9100, "completed", "2026-09-17T18:00:00Z")])
    case("a COMPLETED run on HEAD is not a successor", dispatched, f"dispatched={dispatched} log={log}")

    # A live run of a DIFFERENT workflow on HEAD is not a publisher either — a Build-and-Test
    # re-run must never be mistaken for one.
    rc, log, dispatched, _ = drive(
        handoff(), [me, wf_run(9100, "in_progress", "x", name="MeshWeaver Build and Test")])
    case("a live run of ANOTHER workflow on HEAD is not a publisher",
         dispatched, f"dispatched={dispatched} log={log}")

    # ── ARM 3: NOTHING IS OWED WHEN THIS RUN IS HEAD'S PUBLICATION ───────────────────────────
    rc, log, dispatched, _ = drive(
        handoff(SHA=HEAD, PUBLISH="true", PROMOTE_RESULT="success"), [me])
    case("a run that PUBLISHED main's HEAD hands nothing on",
         rc == 0 and not dispatched, f"rc={rc} dispatched={dispatched} log={log}")

    # 🚨 PROMOTE IS ONLY PHASE A. A run can tag the set and then fail its platform bake or its
    # verification; keyed on promote alone this step would have called that HEAD's publication and
    # handed nothing on, in the exact case it exists for. (The legs are the step's DELIVERY_LEGS in
    # order: portal-image, mirror, migration, plugin-test, promote, verify-images, publish-bake.
    # No `plugins-*` leg is among them — policy `platform-backwards-compatibility`; the structural
    # half of that is `PlatformDeliveryNeverWaitsOnPluginsGuard`.)
    for leg, why in ((6, "publish-bake"), (5, "verify-images")):
        legs = CLEAN_LEGS.split()
        legs[leg] = "failure"
        rc, log, dispatched, _ = drive(
            handoff(SHA=HEAD, PUBLISH="true", PROMOTE_RESULT="success",
                    DELIVERY_LEGS=" ".join(legs)), [me])
        case(f"a run that promoted and then FAILED a later shipping leg still hands HEAD on ({why})",
             rc == 0 and dispatched, f"rc={rc} dispatched={dispatched} log={log}")

    # A CANCELLED shipping leg is the same fact as a failed one — the set was not finished.
    legs = CLEAN_LEGS.split(); legs[0] = "cancelled"
    rc, log, dispatched, _ = drive(
        handoff(SHA=HEAD, PUBLISH="true", PROMOTE_RESULT="success", DELIVERY_LEGS=" ".join(legs)), [me])
    case("a CANCELLED shipping leg counts as incomplete too", dispatched, f"log={log}")

    # 🚨 …and `skipped` must NOT. Most legs skip on a bake-only or no-publish run; reading that as a
    # failure would dispatch a publisher after every ordinary quiet tick.
    rc, log, dispatched, _ = drive(
        handoff(SHA=HEAD, PUBLISH="true", PROMOTE_RESULT="success",
                DELIVERY_LEGS="success skipped skipped skipped success success skipped"), [me])
    case("a SKIPPED leg is not a failure — an ordinary publication still hands nothing on",
         not dispatched, f"dispatched={dispatched} log={log}")

    # 🚨 THE ANTI-LOOP ARM. A reconcile that found the set already complete takes the bake-only
    # branch: publish=false, promote=skipped, complete=true, target == HEAD. Without the
    # `COMPLETE` arm it would hand ITSELF on, and the dispatched run would do the same, forever.
    rc, log, dispatched, _ = drive(
        handoff(SHA=HEAD, PUBLISH="false", COMPLETE="true", PROMOTE_RESULT="skipped",
                DELIVERY_LEGS="skipped skipped skipped skipped skipped skipped skipped"),
        [me])
    case("a BAKE-ONLY run on HEAD hands nothing on (it would otherwise dispatch itself forever)",
         rc == 0 and not dispatched, f"rc={rc} dispatched={dispatched} log={log}")

    # ── ARM 4: THE ONE-HOP BOUND ────────────────────────────────────────────────────────────
    # Exactly ARM 1's state, but this run arrived BY a handoff. A deterministic promote failure
    # would otherwise dispatch a successor that fails the same way, without end.
    rc, log, dispatched, _ = drive(handoff(HANDOFF_ELIGIBLE="false"), [me])
    case("a run that was ITSELF dispatched never dispatches again (the chain is bounded at one hop)",
         rc == 0 and not dispatched and "one hop" in log, f"rc={rc} dispatched={dispatched} log={log}")

    rc, log, dispatched, _ = drive(handoff(HANDOFF_ELIGIBLE="false"), [me],
                                   mutate=('if [ "$HANDOFF_ELIGIBLE" != "true" ]; then', "if false; then"))
    case("MUTATION CONTROL: without the one-hop bound that same run DOES dispatch again",
         dispatched, f"dispatched={dispatched} log={log}")

    # ── ARM 5: ONLY A GREEN HEAD IS OWED ANYTHING — and the step's OWN --jq decides ──────────
    # Every case here drives the real filter over a real `check_runs` payload: select by NAME,
    # sort by `started_at`, take the LAST. A pre-computed verdict would test none of those.
    for payload, why, want in (
        ([chk("in_progress", None, "2026-09-17T18:00:00Z")], "still testing", False),
        ([chk("completed", "failure", "2026-09-17T18:00:00Z")], "settled red", False),
        ([], "no check run at all", False),
        ([chk("completed", "success", "2026-09-17T18:00:00Z", name="Some other gate")],
         "only ANOTHER check exists — the required one is absent", False),
        ([chk("completed", "success", "2026-09-17T18:00:00Z"),
          chk("completed", "failure", "2026-09-17T17:00:00Z", name="Some other gate")],
         "an unrelated check is red but the required one is green", True),
    ):
        rc, log, dispatched, _ = drive(handoff(), [me], checks=payload)
        case(f"HEAD with {why}: {'dispatches' if want else 'is not owed a publisher'}",
             rc == 0 and dispatched == want, f"rc={rc} dispatched={dispatched} log={log}")

    # 🚨 A RE-RUN APPENDS A CHECK RUN, so "take the LAST by started_at" is the whole of the
    # re-run story. Both directions, because both are wrong in a way that matters: reading the
    # older GREEN would publish a tree whose re-run went red.
    rerun_green = [chk("completed", "failure", "2026-09-17T17:00:00Z"),
                   chk("completed", "success", "2026-09-17T18:00:00Z")]
    rerun_red = [chk("completed", "success", "2026-09-17T17:00:00Z"),
                 chk("completed", "failure", "2026-09-17T18:00:00Z")]
    rc, log, dispatched, _ = drive(handoff(), [me], checks=rerun_green)
    case("a red check RE-RUN green is green — the LATEST run decides", dispatched, f"log={log}")
    rc, log, dispatched, _ = drive(handoff(), [me], checks=rerun_red)
    case("a green check RE-RUN red is RED — the latest decides in that direction too",
         not dispatched, f"dispatched={dispatched} log={log}")
    rc, log, dispatched, _ = drive(handoff(), [me], checks=rerun_red,
                                   mutate=("sort_by(.started_at) | last", "sort_by(.started_at) | first"))
    case("MUTATION CONTROL: taking the FIRST check run instead publishes a tree whose re-run went red",
         dispatched, f"dispatched={dispatched} log={log}")

    # ── ARM 6: AN UNANSWERED PROBE NEVER DISPATCHES, AND IS NEVER SILENT ────────────────────
    # The opposite posture to `verdict`'s fail-closed, and for a reason worth stating: this step
    # CREATES work. Guessing on an unanswered probe would cost a duplicate image set (#3376), and
    # not dispatching degrades to the hourly reconcile — where delivery stood before it existed —
    # rather than to silence. So: warn loudly, name the unanswered question, create nothing.
    for knob, needle in (("GH_TIP_RESULT", "main's tip"),
                         ("GH_CHECK_FAIL", "required check"),
                         ("GH_RUNS_FAIL", "successor probe")):
        env = handoff()
        env[knob] = "" if knob == "GH_TIP_RESULT" else "1"
        rc, log, dispatched, _ = drive(env, [me])
        case(f"an unanswered {needle} dispatches nothing",
             not dispatched, f"dispatched={dispatched} log={log}")
        case(f"...and says so as a warning naming what went unanswered ({needle})",
             "::warning::" in log and needle.split()[-1] in log, f"log={log}")

    # ── ARM 7: A FAILED DISPATCH IS RED, NEVER A SHRUG ──────────────────────────────────────
    # It only fires in the state where HEAD is green and nobody is publishing it, so a dispatch
    # that could not be made leaves delivery with no producer. That is the stuck state, and it must
    # page rather than decorate a green run.
    rc, log, dispatched, _ = drive(handoff(GH_DISPATCH_FAIL="1"), [me])
    case("a dispatch that FAILS reds the run rather than passing quietly",
         rc != 0 and "::error::" in log, f"rc={rc} log={log}")
    case("...and it names the permission to check and the manual remedy",
         "actions: write" in log and "by hand" in log, f"log={log}")

    # ── ARM 8: A RUN THAT NEVER HELD THE LANE OWES NOTHING ──────────────────────────────────
    # A PR-triggered workflow_run, or a gate that died before resolving a target.
    rc, log, dispatched, _ = drive(handoff(SHA=""), [me])
    case("a run whose gate resolved no target hands nothing on",
         rc == 0 and not dispatched, f"rc={rc} dispatched={dispatched} log={log}")


def run_heal_cases(root, case) -> None:
    """
    🚨 <b>MeshWeaver#3176 — an alert that cannot be resolved stops being an alert.</b>

    `verify-images` reports a successful heal onto whichever `ci-failure` issue is open, and the
    comment used to end *"Close this issue if nothing else is outstanding"* — advice, to nobody in
    particular, from a bot. Nobody ever did. Meanwhile `gate` and `alert-on-failure` both append to
    whichever `ci-failure` issue is OPEN, so the first one filed absorbed every attempt and every
    heal from then on: #3176 reached 324 comments over four days and a dozen unrelated causes, and
    its title had been false since the first day.

    The two properties an alert has — its EXISTENCE means delivery is broken, its AGE is the
    outage's age — are both destroyed by that. These cases hold the fix to the only standard that
    matters here: the step must actually CALL `gh issue close`. A step that merely prints the word
    "closing" is the defect, not the fix, and stdout cannot tell them apart — so every case asserts
    on the recorded `gh` invocations.
    """
    body = extract_step(root, HEAL_STEP_ID)

    # A stub is only evidence about what it stubs, and this needle is the whole subject: if the
    # step stops closing, these cases must not keep passing while the ledger goes immortal again.
    for needle, why in (
        ("gh issue close", "no longer closes the ledger, which IS #3176 — every case below would pass vacuously"),
        ("gh issue comment", "no longer records the heal, so the delivery record these cases assert is gone"),
    ):
        if needle not in body:
            die(f"step `{HEAL_STEP_ID}` {why} (missing `{needle}`). Update the harness with the step.")

    env = {
        "SHORT_SHA": "1a2b3c4",
        "PORTAL_VERSION": "3.0.0-ci.7989",
        # Deliberately free of `actions/runs`: the stub answers that shape from a fixture, and a
        # comment body is not a read.
        "RUN_URL": "https://example.invalid/run/1",
        "GH_TOKEN": "",
    }

    # ── ARM 1: THE FIX. An open ledger is commented on AND CLOSED. ────────────────────────
    calls: list[str] = []
    rc, log, _ = run_step(body, {**env, "GH_ISSUE_RESULT": "3176"}, None, calls_out=calls)
    joined = "\n".join(calls)
    case("a successful heal records itself on the open ci-failure issue",
         rc == 0 and "gh issue comment 3176" in joined, f"rc={rc} calls={calls} log={log}")
    case("...and CLOSES it, rather than asking a human to (#3176)",
         "gh issue close 3176" in joined, f"the ledger was left open: calls={calls} log={log}")
    case("...and says the next failure gets a FRESH issue, so the advice is not merely dropped",
         "FRESH" in joined, f"calls={calls}")

    # ── ARM 2: THE COVERAGE THAT MUST NOT BE LOST. No open issue ⇒ touch nothing. ─────────
    # Without this, "it closes the ledger" would also pass if the step closed something on every
    # green run — and the number it would close is whatever `--jq` answered, i.e. nothing sane.
    calls = []
    rc, log, _ = run_step(body, {**env, "GH_ISSUE_RESULT": ""}, None, calls_out=calls)
    joined = "\n".join(calls)
    case("no open ci-failure issue is a silent no-op, not a close of nothing",
         rc == 0 and "issue close" not in joined and "issue comment" not in joined,
         f"rc={rc} calls={calls} log={log}")

    # ── ARM 3: a failed CLOSE must not red a run that DELIVERED. ────────────────────
    # `verify-images` is a delivery leg: `delivery-verdict` refuses a publish whose legs did not all
    # succeed, and `alert-on-failure` files an issue on any failure. So a 403 on the close would
    # file an alert about the alerting, on a run that shipped. The heal COMMENT must still be
    # written — that is the record — and the run must stay green with a warning.
    calls = []
    rc, log, _ = run_step(body, {**env, "GH_ISSUE_RESULT": "3176", "GH_CLOSE_FAIL": "1"},
                          None, calls_out=calls)
    joined = "\n".join(calls)
    case("a REFUSED close leaves the run green (it would otherwise alert about the alerting)",
         rc == 0, f"rc={rc} log={log}")
    case("...and says so as a warning rather than silently",
         "::warning::" in log and "close" in log, f"log={log}")
    case("...and the heal comment is still written, so the delivery record survives",
         "gh issue comment 3176" in joined, f"calls={calls}")

def run_bake_version_cases(root, case) -> None:
    body = extract_step(root, BAKE_VERSION_STEP_ID)
    if "az acr manifest list-metadata" not in body:
        die(f"step `{BAKE_VERSION_STEP_ID}` no longer calls `az acr manifest list-metadata` — the "
            "stub these cases rely on is no longer the seam under test, so every case below would "
            "pass vacuously. Update the harness with the step.")

    base = {"SHORT_SHA": SHORT_SHA}

    # 🚨 THE CASE THAT HAD NEVER SUCCEEDED, carried over verbatim from the step's old home. The
    # repository holds 16 untagged manifests whose `tags` is null; before #2642 the query threw on
    # the first of them and the step blamed promote.
    rc, log, outputs = run_step(body, base, fixture(True))
    case("the moved read recovers the version DESPITE 16 untagged manifests",
         rc == 0 and f"version={VERSION}" in outputs, f"rc={rc} out={outputs!r} log={log}")

    rc, log, outputs = run_step(body, base, fixture(False))
    case("a digest with no version tag is still a loud, accurate stop",
         rc != 0 and "carries no version tag" in log, f"rc={rc} log={log}")

    # 🚨 THE REGRESSION GUARD. A failing az must fail the step with AZ's message — never be
    # converted into "there is no version tag" and blamed on promote.
    rc, log, outputs = run_step(body, base, fixture(True), az_fail=True)
    case("a FAILING az fails the moved read", rc != 0, f"rc={rc} log={log}")
    case("a FAILING az is never reported as promote's fault",
         "Fix promote" not in log and "carries no version tag" not in log,
         f"the step blamed a healthy component for its own failed call:\n{log}")
    case("a FAILING az surfaces az's own message", "az login" in log, f"log={log}")


def separation_problems(workflow_text: str) -> list[str]:
    """🚨 Policy `platform-module-deploy-separate` + `platform-deploy-control-first`, held structurally.

    "we separate platform deploy 100% from module deploy" — so `main-cd.yml` (the PLATFORM deploy):
      1. calls NO module lane: no `node-repo-module-pack.yml`, `node-repo-publish-bake.yml` or
         `node-repo-module-publish.yml` (packing, baking or sealing a module is the module's own lane);
      2. reads NO MeshWeaver.Plugins verdict and holds no Plugins credential (`refs/core-candidate`,
         `PLUGINS_TOKEN`, the content App) — what guards a platform roll is the platform verdict;
      3. checks MeshWeaver.Plugins out ONLY in the jobs that build the portal HOST (it lives there);
      4. `arm` needs the ladder (`platform-ladder-compat`) and nothing module-shaped;
      5. `control-first` gives control the build from `control-promote`, and never needs `arm`;
      6. a module-input job that carries `always()` PAYS for it: every need whose output it consumes,
         and `preflight`, is asserted `== 'success'` (else it runs on an EMPTY digest/identity)."""
    import yaml

    problems: list[str] = []
    doc = yaml.safe_load(workflow_text)
    jobs = doc.get("jobs") or {}
    # The portal HOST lives in MeshWeaver.Plugins: `gate` resolves the host commit (ls-remote), the
    # three image jobs check it out to build the host. That is platform provenance, not a module.
    host_builders = {"gate", "portal-image", "migration-image", "control-image"}
    for name, job in jobs.items():
        body = json.dumps(job)
        for lane in ("node-repo-module-pack.yml", "node-repo-publish-bake.yml", "node-repo-module-publish.yml"):
            if lane in str(job.get("uses", "")):
                problems.append(f"{name} calls the module lane `{lane}` — the platform deploy packs, bakes and seals no module")
        for needle in ("refs/core-candidate", "core-candidate/", "PLUGINS_TOKEN", "DEPENDENT_DISPATCH_APP"):
            if needle in body:
                problems.append(f"{name} reads `{needle}` — the platform deploy reads no MeshWeaver.Plugins verdict or credential")
        if "Systemorph/MeshWeaver.Plugins" in body and name not in host_builders:
            problems.append(f"{name} checks MeshWeaver.Plugins out — only the portal HOST resolver/builders may ({sorted(host_builders)})")
    arm = jobs.get("arm")
    if arm is None:
        problems.append("`arm` is missing — the guard's subject moved; re-point it rather than let it pass on nothing")
    else:
        needs = arm.get("needs") or []
        needs = [needs] if isinstance(needs, str) else needs
        if "platform-ladder-compat" not in needs:
            problems.append("`arm` does not need `platform-ladder-compat` — the ladder is half of the platform verdict")
        for n in needs:
            if n.startswith("plugins-") or n in ("published-modules", "satellite-compat"):
                problems.append(f"`arm` needs `{n}` — a module job must never gate the platform arm")
        # `select` reads `.github/control-instance.json`; a sparse checkout without it refuses every
        # arming (review on #6143) — the file sits BESIDE the scripts, so it must be named.
        checkout = next((s for s in arm.get("steps") or [] if str(s.get("uses", "")).startswith("actions/checkout")), {})
        sparse = str((checkout.get("with") or {}).get("sparse-checkout", ""))
        if sparse and ".github/control-instance.json" not in sparse:
            problems.append("`arm`'s sparse checkout omits `.github/control-instance.json` — `select` cannot name control and refuses every arming")
    cf = jobs.get("control-first")
    if cf is None:
        problems.append("`control-first` is missing — control is no longer deployed first")
    else:
        needs = cf.get("needs") or []
        needs = [needs] if isinstance(needs, str) else needs
        if "arm" in needs or "notify-platform-update" in needs:
            problems.append("`control-first` waits for the fleet's arming — control must be deployed FIRST")
        if "control-promote" not in needs:
            problems.append("`control-first` does not follow `control-promote` — it would tag an image that was never accepted")
        if "control-promote.result == 'success'" not in " ".join(str(cf.get("if", "")).split()):
            problems.append("`control-first`'s `if:` does not assert `needs.control-promote.result == 'success'`")
        # 7. ...and DELIVERS it: tagging memex-control rolls nothing. A step must sign the
        #    `control-announcement` body to the control plane's inbox, or control is handed a build
        #    nothing ever rolls it to (control sat on ci.9939 for 22 h, 2026-10-06).
        steps = cf.get("steps") or []
        hand = [st for st in steps if "arm-promoted-set.py control-announcement" in str(st.get("run", ""))]
        if not hand:
            problems.append("`control-first` tags memex-control but never hands the build to control's roll lane "
                            "(no step POSTs the `control-announcement` body) — control would never roll to it")
        elif not any("vars.CONTROL_WEBHOOK_URL" in json.dumps(st.get("env") or {})
                     and "secrets.CONTROL_WEBHOOK_SECRET" in json.dumps(st.get("env") or {}) for st in hand):
            problems.append("`control-first`'s handover is not signed to the control plane's inbox "
                            "(vars.CONTROL_WEBHOOK_URL + secrets.CONTROL_WEBHOOK_SECRET)")
    for name in ("published-modules", "platform-ladder-compat"):
        job = jobs.get(name)
        if job is None:
            problems.append(f"`{name}` is missing — the ladder has no input / no verdict")
            continue
        cond = " ".join(str(job.get("if", "")).split())
        if not cond.startswith("always()"):
            continue
        consumed = {m for m in re.findall(r"needs\.([A-Za-z0-9_-]+)\.(?:outputs|result)", json.dumps(job.get("steps") or []))}
        consumed |= {m for m in re.findall(r"needs\.([A-Za-z0-9_-]+)\.outputs\.", json.dumps(job))}
        for dep in sorted(consumed):
            asserted_in_if = f"needs.{dep}.result == 'success'" in cond
            asserted_in_step = f"needs.{dep}.result" in json.dumps(job.get("steps") or [])
            if not (asserted_in_if or asserted_in_step):
                problems.append(f"{name} carries `always()` and consumes `needs.{dep}.*` without asserting it succeeded")
        needs = job.get("needs") or []
        needs = [needs] if isinstance(needs, str) else needs
        if "preflight" in needs and "needs.preflight.result == 'success'" not in cond:
            problems.append(f"{name} carries `always()` and needs `preflight`, but never asserts `needs.preflight.result == 'success'`")
    return problems


PAIR_TAG_WRITE = re.compile(r"-p\$\{?(?:PLUGINS|PLUGINS_SHORT|PLUGINS_SEL|PS)\b")


def pair_tag_problems(workflow_text: str) -> list[str]:
    """🚨 Policy `platform-module-deploy-separate`: the `<core7>-p<plugins7>` pair tag is RETIRED.

    `main-cd.yml` must not mint it (no `<sha>-p$PLUGINS…` in any executable line), must not ask the
    completeness probe about it (`check-image-set.sh` gets the core sha only), and `arm` must write
    the version tag FROM the build's staging tag — the immutable per-run identity that replaced the
    pair tag as the arming source. Readers stay tolerant of legacy pair tags; only WRITING is barred."""
    problems: list[str] = []
    for n, line in enumerate(workflow_text.splitlines(), 1):
        if line.lstrip().startswith("#"):
            continue
        # A trailing comment (whitespace, then `#`) is not code: a line that only NAMES the retired
        # tag in its comment must not trip the guard. A bare `#` stays code — `${VAR#prefix}` is a
        # shell expansion, and cutting there would hide a real write behind it (#6177 review).
        code = re.split(r"\s#", line, maxsplit=1)[0]
        if PAIR_TAG_WRITE.search(code):
            problems.append(f"main-cd.yml:{n} composes a `<core7>-p<plugins7>` pair tag — it is retired: {line.strip()[:120]}")
        if "check-image-set.sh" in code and re.search(r'check-image-set\.sh\s+"\$SHORT"\s+"\$', code):
            problems.append(f"main-cd.yml:{n} passes a second (plugins) argument to check-image-set.sh — the pair probe is retired")
    if "SRC_PORTAL_TAG=$STAGING_SEL" not in workflow_text:
        problems.append("`arm` does not arm FROM the selected record's staging tag (`SRC_PORTAL_TAG=$STAGING_SEL`)")
    return problems


def module_pack_permission_problems(workflow_text: str) -> list[str]:
    """OIDC is selected by the caller, so the called jobs must inherit its permission map."""
    import yaml

    doc = yaml.safe_load(workflow_text)
    problems = []
    if "permissions" in doc:
        problems.append("the called workflow declares permissions instead of inheriting its caller")
    for name in ("prepare", "build-workspace", "pack"):
        job = (doc.get("jobs") or {}).get(name) or {}
        if "permissions" in job:
            problems.append(
                f"{name} declares permissions instead of inheriting the caller's login mode"
            )
    return problems


def run_control_webhook_cases(root, case) -> None:
    """EXECUTE the preflight's control-webhook guard and prove the workflow still wires it."""
    import yaml

    script = root / ".github/scripts/check-control-webhook-url.py"
    declared = json.loads((root / ".github/control-instance.json").read_text())["url"].rstrip("/")
    good = declared + "/api/hooks/Hosting/PlatformBuilds"

    def check(declaration, url):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "control-instance.json"
            if declaration is not None:
                path.write_text(declaration)
            res = subprocess.run([sys.executable, str(script), str(path), url],
                                 capture_output=True, text=True)
            return res.returncode, res.stdout + res.stderr

    decl = json.dumps({"url": declared})
    rc, log = check(decl, good)
    case("the declared control inbox URL is accepted", rc == 0, f"rc={rc} log={log}")
    rc, log = check(decl, "https://memex.systemorph.com/api/hooks/Hosting/PlatformBuilds")
    case("the old/wrong portal is refused", rc == 1 and "different inbox" in log, f"rc={rc} log={log}")
    rc, log = check(decl, good + "/")
    case("a near-miss URL (trailing slash) is refused", rc == 1, f"rc={rc} log={log}")
    rc, log = check(json.dumps({"url": "http://control.example"}), good)
    case("a non-HTTPS declaration is refused", rc == 1 and "cannot read" in log, f"rc={rc} log={log}")
    rc, log = check(json.dumps({"deployment": "control"}), good)
    case("a declaration without a url is refused", rc == 1 and "cannot read" in log, f"rc={rc} log={log}")
    rc, log = check("{not json", good)
    case("a malformed declaration is refused", rc == 1 and "cannot read" in log, f"rc={rc} log={log}")
    rc, log = check(None, good)
    case("a missing declaration is refused", rc == 1 and "cannot read" in log, f"rc={rc} log={log}")

    # Wiring: the preflight must still sparse-checkout both files and invoke the checker.
    wf = yaml.safe_load((root / WORKFLOW).read_text())
    pre = wf["jobs"].get("preflight", {})
    steps = pre.get("steps", [])
    checkout = next((st for st in steps if str(st.get("uses", "")).startswith("actions/checkout")), {})
    sparse = checkout.get("with", {}).get("sparse-checkout", "")
    run_text = "\n".join(str(st.get("run", "")) for st in steps)
    case("preflight sparse-checks-out the declaration and the checker",
         ".github/control-instance.json" in sparse and ".github/scripts/check-control-webhook-url.py" in sparse,
         f"sparse-checkout={sparse!r}")
    case("preflight invokes the checker against the declaration and CONTROL_WEBHOOK_URL",
         re.search(r'check-control-webhook-url\.py\s*\\?\s*\.github/control-instance\.json\s+"\$CONTROL_WEBHOOK_URL"', run_text) is not None,
         "the preflight run script no longer calls the checker")


def main() -> int:
    root = Path(os.environ.get("GITHUB_WORKSPACE", ".")).resolve()
    try:
        import jmespath  # noqa: F401
        import yaml  # noqa: F401
    except ImportError as exc:
        die(f"this harness cannot run — {exc}. pip install jmespath pyyaml.")
    # The verdict cases run the step's OWN `--jq` filter through real jq. Without it every one of
    # them would fail on the stub's exit 127 rather than on the logic — say so plainly instead.
    if subprocess.run(["bash", "-c", "command -v jq"], capture_output=True).returncode != 0:
        die("this harness cannot run — `jq` is not on PATH, and the verdict cases execute the "
            "step's real --jq filter over a workflow_runs fixture. Install jq.")

    body = extract_step(root, STEP_ID)
    # A stub is only evidence about the seam it stubs. Since MeshWeaver#4539 this step no longer
    # READS the registry — `gate.bake_version` does, and this step CHOOSES between the minted
    # version and that recovered one, refusing rather than inventing. If it stopped reading the
    # recovered value, every case below would pass while testing nothing.
    if "RECOVERED" not in body:
        die(
            f"step `{STEP_ID}` no longer reads `RECOVERED` (gate's resolved release version) — the "
            "seam these cases drive is gone, so they would pass vacuously. Either the read moved "
            "back into this step (re-point BAKE_VERSION_STEP_ID) or the wiring broke."
        )

    failures: list[str] = []

    def case(name: str, ok: bool, detail: str = ""):
        print(f"  {'PASS' if ok else 'FAIL'}  {name}")
        if not ok:
            print(f"        {detail}")
            failures.append(name)

    workflow_text = (root / WORKFLOW).read_text()
    # The URL behavior is executed by run_control_webhook_cases below. These mutation cases
    # prove that dropping its production wiring or changing the reporter's host goes red.
    def webhook_wiring_problems(source: str) -> list[str]:
        doc = yaml.safe_load(source)
        steps = (doc.get("jobs") or {}).get("preflight", {}).get("steps") or []
        checkout = next((s for s in steps if str(s.get("uses", "")).startswith("actions/checkout")), {})
        sparse = str((checkout.get("with") or {}).get("sparse-checkout", ""))
        actions = [s for s in steps if "check-control-webhook-url.py" in str(s.get("run", ""))]
        problems = []
        for path in (".github/control-instance.json", ".github/scripts/check-control-webhook-url.py"):
            if path not in sparse:
                problems.append(f"preflight sparse checkout omits {path}")
        if len(actions) != 1 or "CONTROL_WEBHOOK_URL" not in str(actions[0].get("run", "")) \
                or "vars.CONTROL_WEBHOOK_URL" not in str((actions[0].get("env") or {}).get("CONTROL_WEBHOOK_URL", "")):
            problems.append("preflight does not invoke the checker with vars.CONTROL_WEBHOOK_URL")
        return problems

    case("preflight wires the control webhook checker", not webhook_wiring_problems(workflow_text),
         "; ".join(webhook_wiring_problems(workflow_text)))
    no_check = workflow_text.replace("python3 .github/scripts/check-control-webhook-url.py", "true", 1)
    case("the wiring guard detects a removed checker",
         no_check != workflow_text and bool(webhook_wiring_problems(no_check)))
    no_checker_file = workflow_text.replace("            .github/scripts/check-control-webhook-url.py\n", "", 1)
    case("the wiring guard detects a missing checker file",
         no_checker_file != workflow_text and bool(webhook_wiring_problems(no_checker_file)))
    reporter = root / ".github/workflows/node-repo-ci-failure.yml"
    reporter_text = reporter.read_text()

    def reporter_host_matches(source: str) -> bool:
        doc = yaml.safe_load(source)
        inputs = (doc.get("on") or doc.get(True))["workflow_call"]["inputs"]
        declared = json.loads((root / ".github/control-instance.json").read_text())["url"]
        return inputs["control-webhook-host"]["default"] == urlsplit(declared).hostname

    case("the CI failure reporter accepts the declared control host", reporter_host_matches(reporter_text))
    old_host = reporter_text.replace("default: control.systemorph.com", "default: memex.systemorph.com", 1)
    case("the reporter guard detects its former host default",
         old_host != reporter_text and not reporter_host_matches(old_host))
    # 🚨 Policy `platform-module-deploy-separate`: the platform deploy packs, bakes and seals no
    # module, reads no Plugins verdict, and deploys control first. Each detector has a mutation
    # control that re-introduces one coupling and must be caught — a guard that cannot fail is not one.
    sep = separation_problems(workflow_text)
    case("the platform deploy is separate from module deploy, and deploys control first",
         not sep, "; ".join(sep))
    back_pack = workflow_text.replace(
        "  published-modules:\n",
        "  plugins-modules:\n    uses: ./.github/workflows/node-repo-module-pack.yml\n"
        "    needs: [gate]\n    if: needs.gate.outputs.publish == 'true'\n\n  published-modules:\n", 1)
    case("...and the guard catches a module lane back inside the platform deploy",
         any("node-repo-module-pack.yml" in p for p in separation_problems(back_pack)),
         "the mutation passed with core CD packing a Plugins module again")
    back_verdict = workflow_text.replace(
        "          GH_TOKEN: ${{ github.token }}\n          ARMED_MAX:",
        "          GH_TOKEN: ${{ github.token }}\n          PLUGINS_TOKEN: ${{ steps.token.outputs.token }}\n          ARMED_MAX:", 1)
    case("...and the guard catches the arm reading a MeshWeaver.Plugins verdict again",
         back_verdict != workflow_text and any("PLUGINS_TOKEN" in p for p in separation_problems(back_verdict)),
         "the mutation passed (or could not apply) with a Plugins credential on the arm")
    no_ladder = workflow_text.replace(
        "    needs: [preflight, gate, promote, platform-ladder-compat]",
        "    needs: [preflight, gate, promote]", 1)
    case("...and the guard catches the arm losing the ladder half of its verdict",
         no_ladder != workflow_text and any("platform-ladder-compat" in p for p in separation_problems(no_ladder)),
         "the mutation passed (or could not apply) with an arm that no longer waits for the ladder")
    no_decl = workflow_text.replace(
        "          sparse-checkout: |\n            .github/scripts\n            .github/control-instance.json\n"
        "          sparse-checkout-cone-mode: false\n      - name: \"The selection can say no (self-test)\"",
        "          sparse-checkout: .github/scripts\n      - name: \"The selection can say no (self-test)\"", 1)
    case("...and the guard catches the arm checking out no control declaration",
         no_decl != workflow_text and any("control-instance.json" in p for p in separation_problems(no_decl)),
         "the mutation passed (or could not apply) with an arm that cannot read which instance is control")
    alarm = (root / ".github/workflows/control-always-latest.yml").read_text()
    case("the control-always-latest alarm checks out the control declaration it reads",
         ".github/control-instance.json" in alarm.split("steps:", 1)[1].split("- name:", 1)[0],
         "control-always-latest.yml's checkout omits .github/control-instance.json — every tick would refuse")
    # 🚨 The one-producer gate in `portal-image` READS the published module set out of this workflow
    # (check-platform-reference-set.sh). #6143 removed the job it read and CD published nothing for
    # ~30 runs — so the reader is executed here against the real workflow, and against a mutation.
    def reference_set_reader(text: str) -> tuple[int, str]:
        with tempfile.TemporaryDirectory() as tmp:
            wf = Path(tmp) / "main-cd.yml"
            wf.write_text(text)
            script = (root / ".github/scripts/check-platform-reference-set.sh").read_text()
            reader = script[script.index("composed=\"$(python3 - \"$workflow\" <<'PY'"):script.index("\nPY\n)\"") + 4]
            reader = reader.replace("composed=\"$(python3 - \"$workflow\" <<'PY'", "python3 - \"$1\" <<'PY'")
            proc = subprocess.run(["bash", "-c", reader, "reader", str(wf)], capture_output=True, text=True)
            return proc.returncode, proc.stdout + proc.stderr
    rc, out = reference_set_reader(workflow_text)
    case("the portal-image one-producer gate can read the published module set out of main-cd.yml",
         rc == 0 and "MeshWeaver.AI" in out and len(out.split()) >= 4, f"rc={rc} out={out}")
    no_set = workflow_text.replace("      PUBLISHED_MODULES: >-\n", "      PUBLISHED_MODULES_GONE: >-\n", 1)
    rc, out = reference_set_reader(no_set)
    case("...and a workflow without that declaration makes the reader RED (never an empty pass)",
         no_set != workflow_text and rc != 0 and "PUBLISHED_MODULES" in out, f"rc={rc} out={out}")
    module_gates_arm = workflow_text.replace(
        "    needs: [preflight, gate, promote, platform-ladder-compat]",
        "    needs: [preflight, gate, promote, platform-ladder-compat, published-modules]", 1)
    case("...and the guard catches a module job gating the platform arm",
         any("published-modules" in p and "arm" in p for p in separation_problems(module_gates_arm)),
         "the mutation passed with a module job in the arm's needs")
    control_last = workflow_text.replace(
        "    needs: [preflight, gate, control-image, control-promote]",
        "    needs: [preflight, gate, control-image, control-promote, arm]", 1)
    case("...and the guard catches control waiting for the fleet's arming (control LAST again)",
         control_last != workflow_text and any("FIRST" in p for p in separation_problems(control_last)),
         "the mutation passed (or could not apply) with control-first needing arm")
    undelivered = workflow_text.replace("arm-promoted-set.py control-announcement", "arm-promoted-set.py control-REMOVED", 1)
    case("...and the guard catches control-first TAGGING control without handing it the build (the 2026-10-06 outage)",
         undelivered != workflow_text and any("never hands the build" in p for p in separation_problems(undelivered)),
         "the mutation passed (or could not apply) with no handover step in control-first")
    case("...and the shipped control-first hands the build over (no handover problem on main-cd.yml)",
         not any("hands the build" in p or "handover" in p for p in separation_problems(workflow_text)),
         str(separation_problems(workflow_text)))
    unpaid = workflow_text.replace(
        "      always() && needs.gate.result == 'success' && needs.preflight.result == 'success' &&\n"
        "      needs.gate.outputs.publish == 'true' && needs.promote.result == 'success'\n"
        "    runs-on: ubuntu-latest\n    timeout-minutes: 15\n    permissions:\n      contents: read\n      id-token: write\n    env:\n"
        "      # 🚨 THE ONE DECLARATION",
        "      always() && needs.gate.result == 'success' &&\n"
        "      needs.gate.outputs.publish == 'true' && needs.promote.result == 'success'\n"
        "    runs-on: ubuntu-latest\n    timeout-minutes: 15\n    permissions:\n      contents: read\n      id-token: write\n    env:\n"
        "      # 🚨 THE ONE DECLARATION", 1)
    case("...and the guard catches `always()` that no longer stops on a FAILED preflight",
         unpaid != workflow_text and any("needs.preflight.result" in p for p in separation_problems(unpaid)),
         "the mutation passed (or could not apply) with a module-input leg that runs after preflight failed")
    plugins_checkout = workflow_text.replace(
        "          sparse-checkout: .github/scripts\n      - name: \"The input must exist",
        "          sparse-checkout: .github/scripts\n      - uses: actions/checkout@v7\n        with:\n"
        "          repository: Systemorph/MeshWeaver.Plugins\n      - name: \"The input must exist", 1)
    case("...and the guard catches a Plugins checkout outside the portal-host builders",
         plugins_checkout != workflow_text
         and any("checks MeshWeaver.Plugins out" in p for p in separation_problems(plugins_checkout)),
         "the mutation passed (or could not apply) with a module job checking out Plugins")

    # 🚨 The retired pair tag stays retired — with a mutation control per detector.
    pt = pair_tag_problems(workflow_text)
    case("main-cd mints no `<core7>-p<plugins7>` pair tag and arms from the staging tag", not pt, "; ".join(pt))
    minted = workflow_text.replace('          promote memex-portal-ai   "$SHA"\n',
                                   '          promote memex-portal-ai   "$SHA" "$SHA-p$PLUGINS_SHORT"\n', 1)
    case("...and the guard catches promote minting the pair tag again",
         minted != workflow_text and any("pair tag" in p for p in pair_tag_problems(minted)),
         "the mutation passed (or could not apply) with phase A writing <sha>-p<plugins>")
    pair_armed = workflow_text.replace('SRC_PORTAL_TAG=$STAGING_SEL', 'SRC_PORTAL_TAG=$SHA_SEL-p$PLUGINS_SEL', 1)
    case("...and the guard catches `arm` arming from a pair tag again",
         pair_armed != workflow_text and len(pair_tag_problems(pair_armed)) >= 2,
         "the mutation passed (or could not apply) with arm reading <sha>-p<plugins>")
    probe = workflow_text.replace('check-image-set.sh "$SHORT" || rc=$?', 'check-image-set.sh "$SHORT" "$PLUGINS_SHORT" || rc=$?', 1)
    case("...and the guard catches the completeness probe asking about the pair again",
         probe != workflow_text and any("second (plugins) argument" in p for p in pair_tag_problems(probe)),
         "the mutation passed (or could not apply) with gate passing the plugins sha to check-image-set.sh")

    commented = workflow_text.replace('          promote memex-portal-ai   "$SHA"\n',
                                      '          promote memex-portal-ai   "$SHA"  # no "$SHA-p$PLUGINS_SHORT" any more\n', 1)
    case("...and a pair tag named only in a trailing comment is not a write",
         commented != workflow_text and not any("pair tag" in p for p in pair_tag_problems(commented)),
         "the mutation could not apply, or an inline comment naming the retired tag tripped the guard")
    expansion = workflow_text.replace('          promote memex-portal-ai   "$SHA"\n',
                                      '          promote memex-portal-ai   "$SHA" "${SHA#x}-p$PLUGINS_SHORT"\n', 1)
    case("...and a write behind a `${VAR#…}` expansion is still caught",
         expansion != workflow_text and any("pair tag" in p for p in pair_tag_problems(expansion)),
         "the mutation passed (or could not apply) with a pair-tag write after a ${VAR#…} expansion")

    module_pack_text = (root / MODULE_PACK_WORKFLOW).read_text()
    permission_problems = module_pack_permission_problems(module_pack_text)
    case("the reusable module jobs inherit the caller's basic-or-OIDC permission map",
         not permission_problems, "; ".join(permission_problems))
    narrowed_module_pack = module_pack_text.replace(
        "    timeout-minutes: 45\n    # Deliberately inherit the caller's token permissions.",
        "    timeout-minutes: 45\n    permissions:\n      contents: read\n      id-token: write\n"
        "    # Deliberately inherit the caller's token permissions.",
        1,
    )
    narrowed_problems = module_pack_permission_problems(narrowed_module_pack)
    case("the permission guard catches a called job that tries to elevate basic callers",
         any(problem.startswith("prepare declares permissions") for problem in narrowed_problems),
         "the mutation passed with id-token: write inside the called workflow")
    workflow_narrowed_module_pack = module_pack_text.replace(
        "jobs:\n",
        "permissions:\n  contents: read\n  id-token: write\njobs:\n",
        1,
    )
    workflow_narrowed_problems = module_pack_permission_problems(workflow_narrowed_module_pack)
    case("the permission guard catches a workflow-level attempt to elevate basic callers",
         any(problem.startswith("the called workflow declares permissions")
             for problem in workflow_narrowed_problems),
         "the mutation passed with workflow-level id-token: write")

    base = {"RELEASE_VERSION": "", "BAKE_ONLY": "true", "SHORT_SHA": SHORT_SHA,
            "RECOVERED": VERSION}

    # 1 ── A full run: portal-image minted the version, so nothing is read back.
    rc, log, outputs = run_step(body, {**base, "RELEASE_VERSION": "3.0.0-rc9.ci.1"}, fixture(True))
    case("a minted version passes straight through",
         rc == 0 and "version=3.0.0-rc9.ci.1" in outputs, f"rc={rc} out={outputs!r} log={log}")

    # 2 ── A bake-only reconcile takes the version `gate` resolved, and takes it VERBATIM.
    rc, log, outputs = run_step(body, base, fixture(True))
    case("a bake-only reconcile publishes under the version gate resolved",
         rc == 0 and f"version={VERSION}" in outputs, f"rc={rc} out={outputs!r} log={log}")

    # 3 ── 🚨 An empty recovered version is a REFUSAL, not a fallback. Without this case, a wiring
    #      break between `gate` and this step would publish a release marker under no version at
    #      all — which is the shape the read it replaced was written to prevent.
    rc, log, outputs = run_step(body, {**base, "RECOVERED": ""}, fixture(True))
    case("an empty recovered version refuses rather than publishing under an unknown release",
         rc != 0 and "no release to make available" in log, f"rc={rc} log={log}")
    case("...and it points at the step that owns the resolution, not at promote",
         "gate" in log, f"log={log}")

    # 4 ── A non-bake-only run with no version is a refusal, not a read.
    rc, log, outputs = run_step(body, {**base, "BAKE_ONLY": "false", "RECOVERED": ""}, fixture(True))
    case("no version on a non-bake-only run refuses rather than guessing",
         rc != 0 and "unknown release" in log, f"rc={rc} log={log}")

    print()
    print(f"── step `{BAKE_VERSION_STEP_ID}` ──")
    run_bake_version_cases(root, case)


    print()
    print(f"── step `{DECIDE_STEP_ID}` ──")
    run_decide_cases(root, case)

    print()
    print(f"── step `{ATTEMPTED_STEP_ID}` ──")
    run_attempted_cases(root, case)

    print()
    print(f"── step `{VERDICT_STEP_ID}` ──")
    run_verdict_cases(root, case)

    print()
    print(f"── step `{HANDOFF_STEP_ID}` ──")
    run_handoff_cases(root, case)

    print()
    print(f"── step `{HEAL_STEP_ID}` ──")
    run_heal_cases(root, case)

    print()
    print("── control webhook preflight guard ──")
    run_control_webhook_cases(root, case)

    print()
    if failures:
        print(f"::error::{len(failures)} case(s) failed: {', '.join(failures)}")
        return 1
    print(f"all cases passed against {WORKFLOW} steps `{STEP_ID}` + `{BAKE_VERSION_STEP_ID}` "
          f"+ `{DECIDE_STEP_ID}` + `{ATTEMPTED_STEP_ID}` "
          f"+ `{VERDICT_STEP_ID}` + `{HANDOFF_STEP_ID}` + `{HEAL_STEP_ID}` "
          f"(extracted, not copied)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
