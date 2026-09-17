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
from pathlib import Path

WORKFLOW = ".github/workflows/main-cd.yml"
MODULE_PACK_WORKFLOW = ".github/workflows/node-repo-module-pack.yml"
AVAILABILITY = ".github/scripts/check-release-availability.sh"
STEP_ID = "release"
# 🚨 The ACR read `release` used to do MOVED to `gate` (MeshWeaver#4539) so that the seal probe and
# the bake share ONE resolution of the release version. The cases moved with it — the harness
# executes the step wherever it lives, which is the whole point of extracting by `id:`.
BAKE_VERSION_STEP_ID = "bake_version"
SEAL_STEP_ID = "seal"
DECIDE_STEP_ID = "decide"
VERDICT_STEP_ID = "verdict"
HEAL_STEP_ID = "heal"

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
             cwd: str | None = None):
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
        for knob in ("GH_SEAL_COUNT", "GH_STOPPED_COUNT", "GH_VIEW_RESULT", "GH_CREATE_RESULT",
                     "GH_VIEW_FAIL", "GH_LIST_FAIL", "GH_COMMENT_FAIL", "GH_SEAL_RANK"):
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

# ── the SEAL step: "does this sealed set still owe its plugins publication?" (#4539) ─────────
#
# 🚨 THE BROKEN STATE IS CONSTRUCTED, NOT ASSUMED. The lane being fixed "works" whenever nothing
# is missing, so a case that only exercises the healthy path proves nothing. Every outcome below
# is produced by a stub `check-release-availability.sh` that prints the REAL script's sentences and
# exits with the REAL script's codes, and the step under test is extracted from the workflow and
# executed against it — exit code, log parsing, ledger calls and all.
#
# 🚨 AND THE STUB IS PINNED TO THE REAL SCRIPT. A stub is only evidence about what it stubs: if the
# probe ever reworded these lines, every case here would keep passing while the step stopped
# recognising a definite absence and silently answered "nothing due" — the exact failure this whole
# change exists to remove. So the phrases are asserted to still exist in the real script.
SEAL_PHRASES = (
    # the step parses the identity out of this line, and refuses to act without one
    "identity resolved: ",
    # the ONLY outcome that licenses a re-attempt
    "are not available for framework identity",
    # the refusal that means "the platform bake has not published this release yet" — keyed on its
    # OWN wording: an empty marker shares the "CANNOT RESOLVE" prefix and must NOT read as pending
    "has no marker at",
    # the refusal that means "the marker's existence could not be established" (#4539 review) — the
    # case the old script reported as "no marker", which the step then answered with a green
    "exists could not be established",
    # the refusal that means "the producer wrote a marker and recorded no identity" — a defect
    "is empty — the producer recorded none",
    # the refusal that means "the store could not be read" — a red, never a verdict
    "CANNOT DETERMINE release availability",
)

IDENTITY = "s5ec352bb102e5a2275e3831a08ac0c8d"
SEAL_VERSION = "3.0.0-ci.8765"


def _probe_stub(kind: str) -> str:
    """A stand-in for check-release-availability.sh reproducing one of its outcomes verbatim."""
    resolved = f'echo "identity resolved: {IDENTITY} — from the release marker at acct/share/_releases/$1"'
    bodies = {
        # exit 0 — `plugins` IS sealed for this identity. The control.
        "sealed": f'{resolved}\necho "release availability: all 1 source(s) are published for '
                  f'identity {IDENTITY} (release $1)."\nexit 0',
        # exit 1 — the measured #4539 state: the set is sealed, the publication is not there.
        "absent": f'{resolved}\necho "::error::release availability: 1 of 1 source(s) are not '
                  f'available for framework identity {IDENTITY} (release $1)."\nexit 1',
        # exit 1 — no `_releases` marker at all: the platform bake has not published this release.
        "unmarked": 'echo "::error::CANNOT RESOLVE a framework identity: release \'$1\' has no '
                    'marker at acct/share/prebuilt-bundles/_releases/$1."\nexit 1',
        # exit 1 — the MARKER's existence could not be established (auth / throttling / network).
        # Before the #4539 review fix the real script reported this as "has no marker", and the step
        # answered it NOT MEASURED — a storage outage read as a pending bake.
        "marker-unreadable": 'echo "::error::CANNOT DETERMINE release availability: whether the '
                             'release marker at acct/share/prebuilt-bundles/_releases/$1 exists could '
                             'not be established (az returned \'<nothing>\')."\nexit 1',
        # exit 1 — a marker EXISTS but carries no identity: the producer's defect, not a pending bake.
        "empty": 'echo "::error::CANNOT RESOLVE a framework identity: the release marker for \'$1\' '
                 'is empty — the producer recorded none."\nexit 1',
        # exit 1 — the store could not be read. A refusal, in neither direction.
        "unreadable": f'{resolved}\necho "::error::CANNOT DETERMINE release availability for '
                      f'framework identity {IDENTITY} (release $1): 1 of 1 source(s) could not be '
                      f'queried."\nexit 1',
        # exit 1 — absent, but the identity line never printed. Must refuse, not act.
        "nameless": 'echo "::error::release availability: 1 of 1 source(s) are not available for '
                    'framework identity (release $1)."\nexit 1',
    }
    return "#!/usr/bin/env bash\n" + bodies[kind] + "\n"


def run_seal_cases(root, case) -> None:
    body = extract_step(root, SEAL_STEP_ID)

    # A stub is only evidence about the call it stubs.
    if AVAILABILITY.split("/")[-1] not in body:
        die(f"step `{SEAL_STEP_ID}` no longer calls {AVAILABILITY} — the probe these cases drive is "
            "no longer the seam under test, so every case below would pass vacuously.")
    if "plugins_seal_due=" not in body:
        die(f"step `{SEAL_STEP_ID}` no longer writes `plugins_seal_due` — the output the three "
            "`plugins-*` jobs gate on is gone or renamed, so these cases test nothing.")
    real = (root / AVAILABILITY).read_text()
    for phrase in SEAL_PHRASES:
        if phrase not in real:
            die(f"{AVAILABILITY} no longer emits {phrase!r}, but step `{SEAL_STEP_ID}` still "
                "classifies its answer by that text. The stubs below would keep passing while the "
                "real lane stopped telling a definite absence from a refusal — re-point both.")

    def seal(kind: str, **env):
        """Run the step against one probe outcome, in a tree holding that stub."""
        with tempfile.TemporaryDirectory() as td:
            tree = Path(td)
            scripts = tree / ".github" / "scripts"
            scripts.mkdir(parents=True)
            probe = tree / AVAILABILITY
            probe.write_text(_probe_stub(kind))
            probe.chmod(0o755)
            calls: list[str] = []
            base = {
                "RELEASE_VERSION": SEAL_VERSION, "SHORT": "836d447", "PSHORT": "07bcf72",
                "REPO": "Systemorph/MeshWeaver", "GH_TOKEN": "",
                "RUN_URL": "https://example.invalid/run", "MAX_SEAL_ATTEMPTS": "3",
                "BAKE_PUBLISH_TARGETS": "acct/share",
            }
            rc, log, outputs = run_step(body, {**base, **env}, None,
                                        calls_out=calls, cwd=str(tree))
            return rc, log, outputs, "\n".join(calls)

    # ── 1. THE BROKEN STATE: sealed set, no `plugins` publication for its identity ──
    rc, log, outputs, calls = seal("absent")
    case("a sealed set whose `plugins` publication is ABSENT re-attempts the seal",
         rc == 0 and "plugins_seal_due=true" in outputs, f"rc={rc} out={outputs!r} log={log}")
    # 🚨 Assert on the step's OWN verdict line, never on `log` as a whole: `log` also carries the
    # probe's relayed "identity resolved: <id>" line, so `IDENTITY in log` held WHATEVER the step
    # extracted — a mutation reading the wrong field (identity `—`) stayed green on 86 of 86.
    case("...and it NAMES the framework identity it is acting on",
         f"NOT sealed for `{IDENTITY}`" in log,
         f"the verdict named the wrong identity, or none:\n{log}")
    case("...and it records the attempt on the ledger before acting",
         "issue comment" in calls and "cd-seal:836d447-p07bcf72" in calls,
         f"no attempt marker was written; gh calls were:\n{calls}")

    # ── 2. THE CONTROL: the publication IS there, so the lane must do NOTHING ──
    # Without this, "it re-attempts" would also pass if it re-attempted unconditionally — which is
    # the expensive, alarm-every-hour shape the probe exists to prevent.
    rc, log, outputs, calls = seal("sealed")
    case("a set whose `plugins` publication IS sealed re-attempts NOTHING",
         rc == 0 and "plugins_seal_due=false" in outputs, f"rc={rc} out={outputs!r} log={log}")
    case("...and it touches no ledger at all",
         "issue comment" not in calls and "issue create" not in calls,
         f"a healthy tick wrote to the ledger; gh calls were:\n{calls}")

    # ── 3. REFUSALS ARE NOT VERDICTS, in either direction ──
    rc, log, outputs, calls = seal("unreadable")
    case("an UNREADABLE store fails RED rather than answering",
         rc != 0, f"rc={rc} out={outputs!r} log={log}")
    case("...and it never claims the publication is fine",
         "plugins_seal_due=true" not in outputs, f"out={outputs!r}")
    case("...and it says the store could not be read, not that Plugins is at fault",
         "CANNOT DETERMINE" in log or "could not ANSWER" in log, f"log={log}")

    # A release with no marker yet is the platform bake's turn, not a defect: this tick writes it.
    rc, log, outputs, calls = seal("unmarked")
    case("a release with no `_releases` marker yet is NOT MEASURED, and is not a red",
         rc == 0 and "plugins_seal_due=false" in outputs, f"rc={rc} out={outputs!r} log={log}")
    case("...and it says so instead of folding into a silent pass",
         "NOT MEASURED" in log, f"log={log}")
    case("...and it writes no ledger entry for a measurement it did not take",
         "issue comment" not in calls, f"gh calls were:\n{calls}")

    # 🚨 The #4539 review finding. The marker's EXISTENCE could not be read — the case the old script
    # folded into "has no marker". It must be RED, never NOT MEASURED.
    rc, log, outputs, calls = seal("marker-unreadable")
    case("a marker whose existence CANNOT be read fails RED, not NOT MEASURED",
         rc != 0 and "plugins_seal_due=true" not in outputs, f"rc={rc} out={outputs!r} log={log}")
    case("...and it is not reported as a pending bake",
         "NOT MEASURED" not in log, f"a storage outage was answered as a pending bake:\n{log}")

    # An EMPTY marker shares the "CANNOT RESOLVE" prefix with the benign absent case. Keyed on the
    # prefix, the step read a producer defect as "the bake has not run yet" and went green.
    rc, log, outputs, calls = seal("empty")
    case("an EMPTY release marker (producer recorded no identity) fails RED",
         rc != 0 and "plugins_seal_due=true" not in outputs, f"rc={rc} out={outputs!r} log={log}")
    case("...and it is not reported as a pending bake",
         "NOT MEASURED" not in log, f"a producer defect was answered as a pending bake:\n{log}")

    # Absent, but the probe named no identity: acting would be acting on the wrong identity.
    rc, log, outputs, calls = seal("nameless")
    case("an absence with NO resolved identity refuses rather than acting",
         rc != 0 and "plugins_seal_due=true" not in outputs, f"rc={rc} out={outputs!r} log={log}")

    # ── 3b. THE LEDGER FAILS CLOSED (Copilot on #4565) ──
    # The attempt ledger IS the budget. A failed read must not look like "no ledger yet", and a failed
    # write must not launch a repair that consumed no attempt — either way the three-per-pair bound is
    # gone, silently, for as long as the API is unhappy. `set -e` is what enforces both.
    rc, log, outputs, calls = seal("absent", GH_LIST_FAIL="1")
    case("a FAILED ledger listing stops the step instead of reading as 'no ledger yet'",
         rc != 0 and "plugins_seal_due=true" not in outputs, f"rc={rc} out={outputs!r} log={log}")
    case("...and it creates no second ledger issue",
         "issue create" not in calls, f"a failed listing still created a ledger; gh calls were:\n{calls}")
    rc, log, outputs, calls = seal("absent", GH_COMMENT_FAIL="1")
    case("a FAILED attempt-marker write stops the repair — it never launches uncounted",
         rc != 0 and "plugins_seal_due=true" not in outputs, f"rc={rc} out={outputs!r} log={log}")

    # ── 3c. THE BUDGET IS ATOMIC: claim, then rank (Copilot on #4565) ──
    # Two overlapping reconciles both read a count of 2. Read-then-append let BOTH launch "3/3".
    rc, log, outputs, calls = seal("absent", GH_SEAL_COUNT="2", GH_SEAL_RANK="3")
    case("a reconcile whose claim ranks PAST the budget stands down, though it read a count under it",
         rc == 0 and "plugins_seal_due=false" in outputs and "standing down" in log,
         f"rc={rc} out={outputs!r} log={log}")
    rc, log, outputs, calls = seal("absent", GH_SEAL_COUNT="2", GH_SEAL_RANK="2")
    case("...while the reconcile whose claim ranks inside it proceeds as 3/3",
         rc == 0 and "plugins_seal_due=true" in outputs and "3/3" in log, f"rc={rc} out={outputs!r} log={log}")
    rc, log, outputs, calls = seal("absent", GH_SEAL_RANK="")
    case("a claim that cannot be RANKED refuses rather than proceeding unbounded",
         rc != 0 and "plugins_seal_due=true" not in outputs, f"rc={rc} out={outputs!r} log={log}")
    rc, log, outputs, calls = seal("absent")
    # Split the log into CALLS, not lines: a `--body` spans several lines, so `issue comment` and its
    # `cd-seal-run:` claim sit on DIFFERENT lines and a line-based search never finds the write.
    records = re.split(r"\n(?=gh )", calls)
    claim_at = next((i for i, r in enumerate(records)
                     if r.startswith("gh issue comment") and "cd-seal-run:" in r), -1)
    rank_at = next((i for i, r in enumerate(records)
                    if r.startswith("gh issue view") and "index(true)" in r), -1)
    case("...and the claim is WRITTEN before it is RANKED — read-then-write is the race itself",
         0 <= claim_at < rank_at, f"claim at {claim_at}, rank read at {rank_at}; gh calls were:\n{calls}")

    # ── 4. BOUNDED. The budget stops the re-attempt; it does not stop the reporting ──
    rc, log, outputs, calls = seal("absent", GH_SEAL_COUNT="3")
    case("a spent re-attempt budget stops re-attempting",
         rc == 0 and "plugins_seal_due=false" in outputs, f"rc={rc} out={outputs!r} log={log}")
    case("...and it says so ONCE, with the stopped marker",
         "cd-seal-stopped:836d447-p07bcf72" in calls, f"gh calls were:\n{calls}")
    rc, log, outputs, calls = seal("absent", GH_SEAL_COUNT="3", GH_STOPPED_COUNT="1")
    case("...and having said it once, it does not say it again",
         "issue comment" not in calls, f"it repeated the stop comment; gh calls were:\n{calls}")
    # One BELOW the budget must still act — otherwise "bounded" would also pass if it never acted.
    rc, log, outputs, calls = seal("absent", GH_SEAL_COUNT="2")
    case("...and the last attempt inside the budget still runs",
         rc == 0 and "plugins_seal_due=true" in outputs and "3/3" in log,
         f"rc={rc} out={outputs!r} log={log}")

    # An unreadable ledger must not silently grant a fresh attempt every hour. Two shapes: the API
    # call FAILS (gh exits non-zero, `$(…)` is empty), and it answers something that is not a count.
    rc, log, outputs, calls = seal("absent", GH_VIEW_FAIL="1")
    case("an unreadable attempt ledger refuses rather than defaulting the count to zero",
         rc != 0 and "plugins_seal_due=true" not in outputs, f"rc={rc} out={outputs!r} log={log}")
    rc, log, outputs, calls = seal("absent", GH_SEAL_COUNT="null")
    case("...and a non-numeric ledger answer is refused too, not coerced",
         rc != 0 and "plugins_seal_due=true" not in outputs, f"rc={rc} out={outputs!r} log={log}")

    # An empty release version cannot name an identity — refuse before probing anything.
    rc, log, outputs, calls = seal("sealed", RELEASE_VERSION="")
    case("an empty release version refuses instead of probing an unnamed release",
         rc != 0 and "plugins_seal_due" not in outputs, f"rc={rc} out={outputs!r} log={log}")


# ── the BAKE_VERSION step: the ACR read that moved out of `publish-bake` (#4539) ──────────────
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


def plugins_leg_problems(workflow_text: str) -> list[str]:
    """🚨 On a seal re-attempt `plugin-test-image` is SKIPPED, so `needs.plugin-test-image.outputs.version`
    is the EMPTY STRING — and `test-image` / `platform-image` are REQUIRED inputs of the bake lane,
    so an empty value does not even fail loudly: it yields `…/mw-plugin-test:` and the lane dies on
    a manifest that can never resolve. `gate.image_tag` exists precisely so there is ONE place to
    get this right; the workflow's own comment records SEVEN times a per-reference conditional got
    it wrong. So: no `plugins-*` job may name the tester job's version output."""
    import yaml

    problems: list[str] = []
    doc = yaml.safe_load(workflow_text)
    for name, job in (doc.get("jobs") or {}).items():
        if not name.startswith("plugins-"):
            continue
        if "plugin-test-image.outputs.version" in json.dumps(job):
            problems.append(
                f"{name} still reads `needs.plugin-test-image.outputs.version`, which is EMPTY on a "
                "seal re-attempt (that job is skipped there). Use `needs.gate.outputs.image_tag`.")
        cond = " ".join(str(job.get("if", "")).split())
        if "plugins_seal_due" not in cond:
            problems.append(
                f"{name} has no `plugins_seal_due` arm in its `if:`, so the reconcile cannot repair "
                "a sealed set whose plugins publication is missing (MeshWeaver#4539).")
        elif not cond.startswith("always()"):
            problems.append(
                f"{name}'s `if:` does not start with `always()`, so it inherits `promote`'s skip on "
                "the reconcile path and the repair is inert — the exact shape it is fixing.")
        # 🚨 `always()` HAS TO BE PAID FOR. It stops a job inheriting a SKIP — and a skip is also
        # what GitHub gives a job whose need FAILED. So once a job carries `always()`, every need
        # whose OUTPUT it consumes must be asserted `== 'success'` by hand, or the job runs with
        # that output EMPTY. For these legs that is not a loud failure: `platform-image-digest` and
        # `tester-image-digest` are OPTIONAL inputs of the bake lane, which resolves the platform
        # ITSELF when they are empty — so the bundles would be packed, quietly, against a set this
        # run did not promote.
        if cond.startswith("always()"):
            consumed = {m for m in re.findall(r"needs\.([A-Za-z0-9_-]+)\.outputs\.", json.dumps(job))}
            for dep in sorted(consumed):
                if f"needs.{dep}.result == 'success'" not in cond:
                    problems.append(
                        f"{name} carries `always()` and consumes `needs.{dep}.outputs.*`, but its "
                        f"`if:` never asserts `needs.{dep}.result == 'success'` — so a failed "
                        f"`{dep}` lets this job run with that output empty instead of skipping.")
        # `preflight` gates the run — it proves the external inputs exist — but exposes no output
        # these legs READ, so the consumed-output rule above never saw it: an unasserted preflight
        # stayed green while a FAILED preflight no longer stopped the leg (Copilot on #4565).
        if cond.startswith("always()"):
            needs = job.get("needs") or []
            needs = [needs] if isinstance(needs, str) else needs
            if "preflight" in needs and "needs.preflight.result == 'success'" not in cond:
                problems.append(
                    f"{name} carries `always()` and needs `preflight`, but its `if:` never asserts "
                    "`needs.preflight.result == 'success'` — so a FAILED preflight (the inputs this run "
                    "was never proven to have) no longer stops the leg.")
    return problems


def plugin_module_build_problems(workflow_text: str) -> list[str]:
    """The platform bake must have one compiler for every module it composes (#3732)."""
    import yaml

    doc = yaml.safe_load(workflow_text)
    job = (doc.get("jobs") or {}).get("plugins-modules") or {}
    inputs = job.get("with") or {}
    raw = inputs.get("modules")
    if not raw:
        return ["plugins-modules has no module catalog"]
    try:
        entries = json.loads(raw)
    except (TypeError, json.JSONDecodeError) as exc:
        return [f"plugins-modules module catalog is not valid JSON: {exc}"]
    if not entries:
        return ["plugins-modules module catalog is empty"]
    problems = []
    accepts = []
    for entry in entries:
        module = entry.get("module") or "<unnamed>"
        if entry.get("build") != "container":
            problems.append(f"{module} does not use the shared container workspace")
            continue
        accept = entry.get("accept") or ""
        accepts.append(accept)
        if "targets" not in accept.split():
            problems.append(f"{module} does not reuse the shared workspace targets")
    if len(set(accepts)) > 1:
        problems.append("plugins-modules container entries do not share one accept contract")
    for name in ("platform-image", "platform-image-digest", "tester-image", "tester-image-digest"):
        if not inputs.get(name):
            problems.append(f"plugins-modules does not pass {name} to the container workspace")
    if inputs.get("acr-login") != "oidc":
        problems.append("plugins-modules does not select its available OIDC registry login")
    secrets = job.get("secrets") or {}
    for name in ("azure-client-id", "azure-tenant-id", "azure-subscription-id"):
        if not secrets.get(name):
            problems.append(f"plugins-modules does not pass {name}")
    if (job.get("permissions") or {}).get("id-token") != "write":
        problems.append("plugins-modules does not grant id-token: write for OIDC")
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
    module_problems = plugin_module_build_problems(workflow_text)
    case("every plugin module composed by CD reuses one container workspace",
         not module_problems, "; ".join(module_problems))
    mutated_workflow = workflow_text.replace('"build": "container"', '"build": "sdk"', 1)
    mutation_problems = plugin_module_build_problems(mutated_workflow)
    case("the plugin-module workspace guard fails when one entry leaves that workspace",
         bool(mutation_problems), "the mutation passed having changed one producer")
    divergent_accept = workflow_text.replace(
        '"accept": "targets"', '"accept": "targets embedded-resource:build-output"', 1)
    accept_problems = plugin_module_build_problems(divergent_accept)
    case("the plugin-module workspace guard fails when one entry changes the accept contract",
         any("one accept contract" in problem for problem in accept_problems),
         "the mutation passed with divergent global-build acknowledgments")
    missing_targets = workflow_text.replace(
        '"accept": "targets"', '"accept": "embedded-resource:build-output"', 1)
    targets_problems = plugin_module_build_problems(missing_targets)
    case("the plugin-module workspace guard still requires target reuse",
         any("workspace targets" in problem for problem in targets_problems),
         "the mutation passed without the target-reuse contract")
    missing_digest = workflow_text.replace(
        "      platform-image-digest: ${{ needs.plugins-bake-image.outputs.platform_digest }}\n", "", 1)
    digest_problems = plugin_module_build_problems(missing_digest)
    case("the plugin-module workspace guard fails when its image pin is absent",
         any("platform-image-digest" in problem for problem in digest_problems),
         "the mutation passed without a platform image digest")

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

    # 🚨 The three `plugins-*` jobs must be able to run on the reconcile path, and must not name an
    # output that is empty there (MeshWeaver#4539). Asserted structurally, with its own mutation
    # controls below — a guard that cannot fail is not a guard.
    leg_problems = plugins_leg_problems(workflow_text)
    case("every `plugins-*` job can run on a seal re-attempt, off `gate.image_tag`",
         not leg_problems, "; ".join(leg_problems))
    reverted_tag = workflow_text.replace(
        "      test-image: meshweaver.azurecr.io/mw-plugin-test:${{ needs.gate.outputs.image_tag }}",
        "      test-image: meshweaver.azurecr.io/mw-plugin-test:${{ needs.plugin-test-image.outputs.version }}",
        1)
    case("...and the guard catches a leg reaching for the SKIPPED tester job's version",
         any("plugin-test-image.outputs.version" in p for p in plugins_leg_problems(reverted_tag)),
         "the mutation passed with a leg reading an output that is empty on a re-attempt")
    reverted_if = workflow_text.replace(
        "       needs.gate.outputs.plugins_seal_due == 'true')",
        "       needs.gate.outputs.publish == 'true')", 1)
    case("...and the guard catches a leg losing its reconcile arm",
         any("plugins_seal_due" in p for p in plugins_leg_problems(reverted_if)),
         "the mutation passed with a leg that can never repair a half-sealed set")
    unpaid_always = workflow_text.replace(
        "      needs.plugins-bake-image.result == 'success' &&\n"
        "      ((needs.gate.outputs.publish == 'true' && needs.promote.result == 'success') ||\n"
        "       needs.gate.outputs.plugins_seal_due == 'true')\n"
        "    permissions:",
        "      ((needs.gate.outputs.publish == 'true' && needs.promote.result == 'success') ||\n"
        "       needs.gate.outputs.plugins_seal_due == 'true')\n"
        "    permissions:", 1)
    case("...and the guard catches `always()` that does not assert a consumed need succeeded",
         any("result == 'success'" in p and "plugins-bake-image" in p
             for p in plugins_leg_problems(unpaid_always)),
         "the mutation passed with a leg that runs on an EMPTY image digest — which the bake lane "
         "silently replaces by resolving the platform itself")

    unguarded_preflight = workflow_text.replace(
        "      always() && needs.gate.result == 'success' && needs.preflight.result == 'success' &&",
        "      always() && needs.gate.result == 'success' &&", 1)
    case("...and the guard catches `always()` that no longer stops on a FAILED preflight",
         any("needs.preflight.result" in p for p in plugins_leg_problems(unguarded_preflight)),
         "the mutation passed with a leg that runs after preflight failed — inputs never asserted")

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
    print(f"── step `{SEAL_STEP_ID}` ──")
    run_seal_cases(root, case)

    print()
    print(f"── step `{DECIDE_STEP_ID}` ──")
    run_decide_cases(root, case)

    print()
    print(f"── step `{VERDICT_STEP_ID}` ──")
    run_verdict_cases(root, case)

    print()
    print(f"── step `{HEAL_STEP_ID}` ──")
    run_heal_cases(root, case)

    print()
    if failures:
        print(f"::error::{len(failures)} case(s) failed: {', '.join(failures)}")
        return 1
    print(f"all cases passed against {WORKFLOW} steps `{STEP_ID}` + `{BAKE_VERSION_STEP_ID}` "
          f"+ `{SEAL_STEP_ID}` + `{DECIDE_STEP_ID}` "
          f"+ `{VERDICT_STEP_ID}` + `{HEAL_STEP_ID}` (extracted, not copied)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
