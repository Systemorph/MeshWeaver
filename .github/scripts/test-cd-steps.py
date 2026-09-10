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
STEP_ID = "release"
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
  *"issue list"*) printf '%s' "${GH_ISSUE_RESULT:-}" ;;
  # Matched explicitly and BEFORE the reads below: a heal comment quotes a run URL, and a body
  # containing `actions/runs` would otherwise fall into the run-list arm and answer a fixture.
  *"issue comment"*) ;;
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
             runs: list[dict] | None = None, calls_out: list[str] | None = None):
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

        p = subprocess.run(["bash", "-c", body], env=e, capture_output=True, text=True)
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
         "NO run of this workflow is queued or in progress" in log, f"log={log}")
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
    # A stub is only evidence about the call it stubs. If the step stopped making that call, the
    # cases below would all pass while testing nothing.
    if "az acr manifest list-metadata" not in body:
        die(
            f"step `{STEP_ID}` no longer calls `az acr manifest list-metadata` — the stub these "
            "cases rely on is no longer the seam under test, so every case below would pass "
            "vacuously. Update the harness with the step."
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

    base = {"RELEASE_VERSION": "", "BAKE_ONLY": "true", "SHORT_SHA": SHORT_SHA}

    # 1 ── A full run: portal-image minted the version, so nothing is read back.
    rc, log, outputs = run_step(body, {**base, "RELEASE_VERSION": "3.0.0-rc9.ci.1"}, fixture(True))
    case("a minted version passes straight through",
         rc == 0 and "version=3.0.0-rc9.ci.1" in outputs, f"rc={rc} out={outputs!r} log={log}")

    # 2 ── THE CASE THAT HAD NEVER SUCCEEDED. Bake-only reconcile, and the repository holds 16
    #      untagged manifests whose `tags` is null. Before #2642 the query threw on the first of
    #      them, jmespath aborted the WHOLE query, az exited non-zero, `2>/dev/null || true`
    #      turned that into an empty tag list, and the step blamed promote.
    rc, log, outputs = run_step(body, base, fixture(True))
    case("a bake-only reconcile recovers the version DESPITE 16 untagged manifests",
         rc == 0 and f"version={VERSION}" in outputs, f"rc={rc} out={outputs!r} log={log}")

    # 3 ── The genuine "promote never armed it" case must still be loud, and must still say so.
    rc, log, outputs = run_step(body, base, fixture(False))
    case("a digest with no version tag is still a loud, accurate stop",
         rc != 0 and "carries no version tag" in log, f"rc={rc} log={log}")

    # 4 ── 🚨 THE REGRESSION GUARD. When az itself fails, the step must fail with az's message —
    #      it must NOT convert the failure into "there is no version tag" and blame promote.
    #      Reintroduce `2>/dev/null || true` on that read and this case goes red: `tags` becomes
    #      empty, `version` becomes empty, and the step prints the promote accusation.
    rc, log, outputs = run_step(body, base, fixture(True), az_fail=True)
    case("a FAILING az fails the step", rc != 0, f"rc={rc} log={log}")
    case("a FAILING az is never reported as promote's fault",
         "Fix promote" not in log and "carries no version tag" not in log,
         f"the step blamed a healthy component for its own failed call:\n{log}")
    case("a FAILING az surfaces az's own message", "az login" in log, f"log={log}")

    # 5 ── A non-bake-only run with no version is a refusal, not a read.
    rc, log, outputs = run_step(body, {**base, "BAKE_ONLY": "false"}, fixture(True))
    case("no version on a non-bake-only run refuses rather than guessing",
         rc != 0 and "unknown release" in log, f"rc={rc} log={log}")

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
    print(f"all cases passed against {WORKFLOW} steps `{STEP_ID}` + `{DECIDE_STEP_ID}` "
          f"+ `{VERDICT_STEP_ID}` + `{HEAL_STEP_ID}` (extracted, not copied)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
