#!/usr/bin/env python3
"""Find a conservative publication baseline from a successful trunk publishing workflow.

The caller opts in by naming a workflow whose successful main PUSH publishes all selected
outputs. Only such a run ADVANCES the baseline. A failed, cancelled or in-flight run after it does
NOT reset it (per-module deploy, maintainer 2026-09-11: "no huge runs ever"): the caller narrows by
the history UNION from the baseline (`git log baseline..HEAD`, node-repo-scope.py), which carries
every change those runs held, so the next run rebuilds whatever they left unpublished. Resetting to
FULL instead made one cancelled run cost the next run the whole catalog — and under a steady merge
rate every full run was cancelled in turn, so Plugins main published NO module for nine hours.

What DOES reset it — a full build, never an omitted publication:
  * a run after the baseline that attested a DIFFERENT toolchain (platform, images, build logic):
    it may have published some modules with it, which git history cannot show;
  * a successful run that is not an ancestor of HEAD (history rewritten — a union from any older
    baseline would miss what that run published from the rewritten commits);
  * no successful run in the page read, too many unsettled runs to attest, an unavailable API, or
    an unreadable attestation.

What does NOT reset it, and does not even enter the walk:
  * a run that NEVER STARTED — completed (cancelled, startup failure) with zero jobs in any
    attempt. It ran nothing, attested nothing and published nothing; counting it as an
    unattested "later publishing run" turned every Plugins main push into a full build
    (2026-10-26: five green main runs, baseline none each time, behind zero-job cancelled runs).

A PARTIAL publication (`--carry-withheld`, a caller whose green runs may withhold SOME packages and
say which in the `--withheld-marker` artifact): such a run IS a baseline, and its withheld packages
come back as `carry` (`publication-carry`). The caller hands the carry to the module lane, which
selects those packages on top of the diff, so a withheld package is rebuilt until a run publishes
it — it is never treated as published. A marker that does not name a non-empty package list is
unreadable as partial and the run is walked past, as before.

TOOLCHAIN EQUALITY ignores the build-logic ref (`logic`, core main's HEAD, which moves several times
an hour) when BOTH attestations carry `recipe` — the module lane's build recipe
(module-build-key.py RECIPE_VERSION, bumped on every byte-changing lane edit; `--print-recipe`). The
platform itself is compared as attested (the resolved set's core commit and digests, plus `set`
when the caller attests it). Without `recipe` on both sides, `logic` is compared, as before.

An explicit --artifact-store changes where attestations are read, never the run-identity check.
Missing historical attestations conservatively rebuild. An unavailable/corrupt declared store is
a hard failure, not a reason to consult GitHub artifact storage or silently launch another build.
"""
import argparse
import io
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import zipfile
from urllib.parse import quote

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ghrest  # noqa: E402 — the shared GitHub REST client beside this file

# Beyond this many unsettled runs since the last success, attesting each one costs more than the
# full build it would save — and that many reds in a row is itself worth a full rebuild.
MAX_UNSETTLED = 30


class ArtifactStoreError(RuntimeError):
    """A declared artifact store failed; this must not become a GitHub fallback or green build."""


class ArtifactContentError(ArtifactStoreError):
    """The store DELIVERED the artifact (listed, downloaded, integrity-checked), but the file inside
    cannot be read as what it should be. Still a hard failure for an attestation; a withheld
    MARKER alone may read it as "withheld, set unknown" — its contents are unusable, the store
    is not."""


def store_command(store, repo, run_id, command, *arguments, expected_store_id=None):
    """Use the named-artifact adapter without leaking its action outputs into this step's outputs.

    Cross-run consumers deliberately omit --attempt: the source run's latest publication, not
    this consumer's GITHUB_RUN_ATTEMPT, supplies the attestation. The adapter still chooses one
    complete manifest per name, including a successful producer from an earlier partial rerun.
    """
    helper = Path(__file__).with_name("ci-run-artifacts.py")
    identity = [] if expected_store_id is None else ["--expect-store-id", expected_store_id]
    try:
        result = subprocess.run(
            [sys.executable, str(helper), command, "--store", store, "--repository", repo,
             "--run-id", str(run_id), *identity, *arguments], capture_output=True, text=True, timeout=180,
            check=True, env={k: v for k, v in os.environ.items() if k != "GITHUB_OUTPUT"})
    except (OSError, subprocess.SubprocessError) as exc:
        detail = getattr(exc, "stderr", "") or str(exc)
        raise ArtifactStoreError(f"declared artifact store failed: {detail.strip()[:600]}") from exc
    return result.stdout


def store_listing(store, repo, run_id, name="", expected_store_id=None):
    try:
        listing = json.loads(store_command(store, repo, run_id, "list", "--name", name,
                                           "--include-expired", "true", expected_store_id=expected_store_id))
        if not isinstance(listing, list):
            raise ValueError("artifact listing is not an array")
        return listing
    except (ValueError, TypeError) as exc:
        raise ArtifactStoreError(f"declared artifact store returned an invalid listing: {exc}") from exc


def baseline(runs, branch, ancestor, current_run="", withheld=lambda run: False,
             started=lambda run: True):
    """The newest successful main push that is an ancestor of HEAD, plus the ids of the publishing
    runs NEWER than it (failed, cancelled, in flight) whose toolchain the caller must still check,
    and `carry`: the packages the baseline run WITHHELD. `runs` is the API's newest-first listing.
    Pure: `ancestor(sha)`, `withheld(run)` and `started(run)` are the only questions asked.

    `withheld(run)` answers for a SUCCESSFUL run: falsy — it published everything; a non-empty
    collection of package ids — it published everything BUT those (a partial publication: the
    baseline, carrying them); anything else truthy — it withheld an unknown set and is walked past.

    🚨 A SUCCESSFUL run that WITHHELD its publication without saying WHAT is not a baseline. A caller
    whose main runs may deliberately publish nothing (MeshWeaver.Plugins: a merge whose manifest
    locks main has not settled yet publishes only once its settle pull request lands) marks such a
    run, and it is walked past exactly like a failed one — the history union from the older baseline
    carries its changes into the next run that does publish. Counting it as a baseline with no carry
    would narrow that next run to the lock-only settle diff and publish none of the merge's modules.

    `started(run)` is asked only of a COMPLETED run that did not succeed: False (it never ran a job)
    drops it from the walk entirely — it published nothing, so it is neither a baseline nor a run
    whose toolchain must be checked."""
    publishing = {"push", "repository_dispatch", "schedule"}
    between = []
    for run in runs:
        if str(run.get("id")) == str(current_run) or run.get("head_branch") != branch or run.get("event") not in publishing:
            continue
        sha = run.get("head_sha", "")
        if (run.get("event") == "push" and run.get("status") == "completed"
                and run.get("conclusion") == "success" and re.fullmatch(r"[0-9a-f]{40}", sha)):
            if not ancestor(sha):
                break
            held = withheld(run)
            # An EMPTY collection is not "published everything": a marker naming nobody is unreadable.
            if not held and not isinstance(held, (list, tuple, set, frozenset)):
                return {"sha": sha, "run": run["id"], "url": run["html_url"], "between": between, "carry": []}
            if (isinstance(held, (list, tuple, set, frozenset)) and held
                    and all(isinstance(p, str) and re.fullmatch(r"[\w.-]+", p) for p in held)):
                return {"sha": sha, "run": run["id"], "url": run["html_url"], "between": between,
                        "carry": sorted(set(held))}
        elif (run.get("status") == "completed" and run.get("conclusion") != "success"
              and not started(run)):
            continue
        between.append(run["id"])
    return {"sha": "", "run": "", "url": "", "between": between, "carry": []}


def comparable(inputs, other):
    """`inputs` as the toolchain comparison sees it against `other`: the build-logic ref (`logic`)
    is dropped when BOTH carry the lane's `recipe` — the recipe is what of the build logic changes
    bytes, and `logic` (core main's HEAD) moves with every core merge. Anything else is compared."""
    if not isinstance(inputs, dict):
        return inputs
    if isinstance(other, dict) and "recipe" in inputs and "recipe" in other:
        return {k: v for k, v in inputs.items() if k != "logic"}
    return inputs


def toolchain_verdict(current, prior, between):
    """None when the baseline may narrow; otherwise the reason for a full build. `prior` is what
    the baseline run attested; `between` pairs each later run with its attestation, or None when
    it never reached the scope job (no artifact — it built and published nothing)."""
    if comparable(prior, current) != comparable(current, prior):
        return "publication toolchain changed since the baseline run — full build"
    for run_id, inputs in between:
        if inputs is not None and comparable(inputs, current) != comparable(current, inputs):
            return (f"run {run_id}, after the baseline, attested a different toolchain and may have "
                    "published with it — full build")
    return None


def self_test():
    good = dict(event="push", head_branch="main", status="completed", conclusion="success",
                head_sha="a" * 40, id=1, html_url="https://example.test/runs/1")

    def newer(**change):
        return {**good, "id": 2, "head_sha": "b" * 40, "html_url": "https://example.test/runs/2", **change}

    yes = lambda _: True  # noqa: E731
    passed = 0

    def check(condition, what):
        nonlocal passed
        assert condition, what
        passed += 1

    # WALKED PAST: the baseline stays the older success and the newer run comes back to be
    # toolchain-checked — a run that did not succeed carries changes the union already contains.
    for change in [dict(conclusion="failure"), dict(conclusion="cancelled"),
                   dict(status="in_progress", conclusion=None), dict(event="repository_dispatch"),
                   dict(event="schedule"), dict(head_sha="short")]:
        got = baseline([newer(**change), good], "main", yes)
        check(got["run"] == 1 and got["between"] == [2], f"walk past {change}: {got}")
    # IGNORED: not a publishing run of this branch — neither the baseline nor between.
    for change in [dict(event="workflow_dispatch"), dict(event="pull_request"), dict(head_branch="other")]:
        got = baseline([newer(**change), good], "main", yes)
        check(got["run"] == 1 and got["between"] == [], f"ignore {change}: {got}")
    got = baseline([newer(), good], "main", yes, current_run="2")
    check(got["run"] == 1 and got["between"] == [], "the current run is never its own baseline")
    check(baseline([newer(), good], "main", yes)["run"] == 2, "the newest success wins")
    got = baseline([newer(), good], "main", lambda sha: sha != "b" * 40)
    check(got["sha"] == "" and got["run"] == "", "a success off HEAD's line stops the walk (full)")
    got = baseline([newer(conclusion="failure")], "main", yes)
    check(got["sha"] == "" and got["between"] == [2], "no success at all is a full build")
    check(baseline([], "main", yes)["sha"] == "", "an empty listing is a full build")
    # WITHHELD: a successful run that published nothing on purpose is walked past like a failure.
    held = lambda run: str(run.get("id")) == "2"  # noqa: E731
    got = baseline([newer(), good], "main", yes, withheld=held)
    check(got["run"] == 1 and got["between"] == [2], f"a withheld success is walked past: {got}")
    got = baseline([newer()], "main", yes, withheld=held)
    check(got["sha"] == "" and got["between"] == [2], "only withheld successes is a full build")
    got = baseline([newer(), good], "main", yes, withheld=lambda run: False)
    check(got["run"] == 2, "control: the same run NOT withheld is the baseline")
    got = baseline([newer(), good], "main", lambda sha: sha != "b" * 40, withheld=held)
    check(got["sha"] == "", "a withheld run off HEAD's line still stops the walk (full), never skips it")

    # PARTIAL: a success that names what it withheld is the baseline, CARRYING those packages.
    partial = lambda run: ["Mail", "Hosting", "Mail"] if str(run.get("id")) == "2" else False  # noqa: E731
    got = baseline([newer(), good], "main", yes, withheld=partial)
    check(got["run"] == 2 and got["carry"] == ["Hosting", "Mail"] and got["between"] == [],
          f"a partial success is the baseline and carries its withheld packages: {got}")
    check(baseline([newer(), good], "main", yes)["carry"] == [], "a complete baseline carries nothing")
    for unknown in ([], (), set(), ["ok", ""], ["bad name"], [3], "Mail"):
        got = baseline([newer(), good], "main", yes, withheld=lambda run, u=unknown: u if run["id"] == 2 else False)
        check(got["run"] == 1 and got["between"] == [2] and got["carry"] == [],
              f"NEGATIVE CONTROL: withheld={unknown!r} names no usable package set — walked past: {got}")
    got = baseline([newer(), good], "main", lambda sha: sha != "b" * 40, withheld=partial)
    check(got["sha"] == "", "a partial success off HEAD's line still stops the walk (full)")
    # NEVER STARTED: a completed non-success with no job ran nothing — dropped from the walk.
    idle = lambda run: str(run.get("id")) != "2"  # noqa: E731
    for change in [dict(conclusion="cancelled"), dict(conclusion="startup_failure"), dict(conclusion="failure")]:
        got = baseline([newer(**change), good], "main", yes, started=idle)
        check(got["run"] == 1 and got["between"] == [], f"a never-started {change} is not 'between': {got}")
    asked = []
    got = baseline([newer(status="in_progress", conclusion=None), newer(id=3), good], "main", yes,
                   started=lambda run: asked.append(run["id"]) or False)
    check(got["run"] == 3 and got["between"] == [2] and asked == [],
          f"NEGATIVE CONTROL: an in-flight run and a success are never asked whether they started: {got} {asked}")
    got = baseline([newer(conclusion="cancelled"), good], "main", yes, started=lambda run: True)
    check(got["between"] == [2], "NEGATIVE CONTROL: a cancelled run that DID start stays between")

    same = {"platform": "p1", "portal": "d1", "tester": "t1", "logic": "l1"}
    # TOOLCHAIN: `logic` (core main's HEAD) is ignored only when both sides attest the recipe.
    rec = {**same, "set": "3.1.9898", "recipe": "4"}
    check(toolchain_verdict({**rec, "logic": "l2"}, rec, []) is None,
          "a moved build-logic ref narrows when both attest the recipe")
    check(toolchain_verdict({**rec, "logic": "l2"}, rec, [(2, {**rec, "logic": "l3"})]) is None,
          "…and a later run on another build-logic ref with the same recipe narrows too")
    check(toolchain_verdict({**rec, "logic": "l2", "recipe": "5"}, rec, []) is not None,
          "NEGATIVE CONTROL: a moved recipe is full")
    check(toolchain_verdict({**rec, "logic": "l2", "set": "3.1.9901"}, rec, []) is not None,
          "NEGATIVE CONTROL: a moved platform set is full")
    check(toolchain_verdict({**same, "logic": "l2"}, same, []) is not None,
          "NEGATIVE CONTROL: without a recipe on either side, a moved build-logic ref is still full")
    check(toolchain_verdict({**rec, "logic": "l2"}, {**same}, []) is not None,
          "NEGATIVE CONTROL: a baseline that attested no recipe is full against one that did")
    moved = {**same, "platform": "p2"}
    check(toolchain_verdict(same, same, []) is None, "same toolchain narrows")
    check(toolchain_verdict(same, moved, []) is not None, "a baseline on another toolchain is full")
    check(toolchain_verdict(same, None, []) is not None, "a baseline with no attestation is full")
    check(toolchain_verdict(same, same, [(2, None)]) is None,
          "a later run that never reached the scope job published nothing — narrows")
    check(toolchain_verdict(same, same, [(2, same), (3, same)]) is None, "later runs on the same toolchain narrow")
    check("run 3" in (toolchain_verdict(same, same, [(2, same), (3, moved)]) or ""),
          "a later run on another toolchain is full, and names the run")
    print(f"publication baseline: {passed} assertions passed")


# A module attribute, not an inline call: test_publication_artifact_store.py patches `gh_json`.
gh_json = ghrest.gh_json


def carries_marker(repo, run_id, name, artifact_store="", expected_store_id=None):
    """True when run `run_id` uploaded an artifact called `name` (expired ones included — the
    marker's PRESENCE is the fact, not its bytes). Read from the same store as the attestations."""
    if artifact_store and artifact_store != "gha":
        return bool(store_listing(artifact_store, repo, run_id, name, expected_store_id))
    listing = gh_json(f"repos/{repo}/actions/runs/{run_id}/artifacts?name={quote(name, safe='')}")
    return bool(listing.get("total_count"))


def withheld_packages(repo, run_id, name, artifact_store="", expected_store_id=None):
    """The packages a run's withheld marker names: False when the run carries no marker, the
    marker's `withheld` list when it names a non-empty one, True (withheld, set unknown) when the
    marker exists but its CONTENTS cannot be read as such — expired, unparseable, or naming nothing.
    🚨 A STORAGE failure (download, integrity check) is not "contents unknown": ArtifactStoreError
    propagates to main()'s hard failure, exactly as for the attestations — it must never become an
    older baseline or a full build that exits green."""
    if not carries_marker(repo, run_id, name, artifact_store, expected_store_id):
        return False
    try:
        body = artifact_json(repo, run_id, name, f"{name}.json", artifact_store, expected_store_id)
    except (OSError, ValueError, KeyError, zipfile.BadZipFile, ArtifactContentError):
        return True
    listed = body.get("withheld") if isinstance(body, dict) else None
    if isinstance(listed, list) and listed and all(isinstance(x, str) and x for x in listed):
        return listed
    return True


def never_started(repo, run_id):
    """True when a run has NO job in ANY attempt (`filter=all`): cancelled while queued, a startup
    failure. Unreadable is False — the run stays in the walk, as before, and the attestation
    checks decide: an API failure (CalledProcessError) or a timeout (TimeoutExpired) included,
    which must not escape to main() and force a full build the attestations would not."""
    try:
        return gh_json(f"repos/{repo}/actions/runs/{run_id}/jobs?filter=all&per_page=1").get("total_count") == 0
    except (OSError, ValueError, KeyError, AttributeError, subprocess.SubprocessError):
        return False


def artifact_json(repo, run_id, name, filename, artifact_store="", expected_store_id=None):
    """`filename` inside run `run_id`'s artifact `name`, parsed; None when the run has none."""
    if artifact_store and artifact_store != "gha":
        artifacts = store_listing(artifact_store, repo, run_id, name, expected_store_id)
        if not artifacts:
            return None
        if artifacts[0].get("expired"):
            raise ValueError(f"run {run_id}'s {name} artifact has expired")
        with tempfile.TemporaryDirectory(prefix=f"{name}-") as folder:
            store_command(artifact_store, repo, run_id, "download", "--name", name,
                          "--path", folder, "--attempt", str(artifacts[0]["attempt"]),
                          expected_store_id=expected_store_id)
            try:
                with open(Path(folder) / filename, encoding="utf-8") as stream:
                    return json.load(stream)
            except (OSError, ValueError) as exc:
                raise ArtifactContentError(f"run {run_id}'s stored {name} artifact is invalid") from exc
    listing = gh_json(f"repos/{repo}/actions/runs/{run_id}/artifacts?name={quote(name, safe='')}")
    live = [x for x in listing.get("artifacts", []) if not x.get("expired")]
    if not live:
        if listing.get("total_count"):
            raise ValueError(f"run {run_id}'s {name} artifact has expired")
        return None
    blob = ghrest.gh_bytes(f"repos/{repo}/actions/artifacts/{live[0]['id']}/zip")
    with zipfile.ZipFile(io.BytesIO(blob)) as archive:
        return json.loads(archive.read(filename))


def recipe_version():
    """The module lane's build recipe (module-build-key.py RECIPE_VERSION, beside this file), read
    as text so nothing of that script runs."""
    text = Path(__file__).with_name("module-build-key.py").read_text(encoding="utf-8")
    found = re.search(r'^RECIPE_VERSION\s*=\s*"([^"]+)"', text, re.M)
    if not found:
        raise ValueError("module-build-key.py declares no RECIPE_VERSION")
    return found.group(1)


def attested_inputs(repo, run_id, artifact_store="", expected_store_id=None):
    """What a run attested in its `publication-inputs` artifact, or None when it has none (it never
    reached the scope job on the legacy single-store path). Read from the explicitly selected
    named store, or through the GitHub artifact API rather than `gh run download`, which refuses
    a run still in flight — and in-flight runs are exactly the ones walked past."""
    return artifact_json(repo, run_id, "publication-inputs", "publication-inputs.json",
                         artifact_store, expected_store_id)


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--workflow")
    p.add_argument("--inputs", help="current resolved platform/toolchain inputs JSON")
    p.add_argument("--repo", default=os.environ.get("GITHUB_REPOSITORY", ""))
    p.add_argument("--root", default=".")
    p.add_argument("--branch", default="main")
    p.add_argument("--artifact-store", default="",
                   help="explicit named artifact store; empty/gha keeps GitHub storage, no fallback when declared")
    p.add_argument("--self-test", action="store_true")
    p.add_argument("--expect-store-id", default=None, help="physical store identity resolved by this run's first producer")
    p.add_argument("--withheld-marker", default="",
                   help="artifact name a SUCCESSFUL run uploads when it deliberately published nothing; "
                        "such a run is walked past, never taken as the baseline")
    p.add_argument("--carry-withheld", action="store_true",
                   help="a successful run whose --withheld-marker NAMES the packages it withheld is a "
                        "baseline, and those packages are emitted as publication-carry — the caller MUST "
                        "hand them to the module lane (publication-carry), which selects them on top of the "
                        "diff. Without this flag such a run is walked past, as before")
    p.add_argument("--print-recipe", action="store_true",
                   help="print the module lane's build recipe (attest it as `recipe`) and exit")
    a = p.parse_args()
    if a.self_test:
        self_test()
        return 0
    if a.print_recipe:
        print(recipe_version())
        return 0
    if not a.workflow or not a.inputs or not re.fullmatch(r"[\w.-]+/[\w.-]+", a.repo):
        p.error("--workflow, --inputs and --repo owner/name are required")
    with open(a.inputs, encoding="utf-8") as fh:
        current = json.load(fh)
    if not current or not isinstance(current, dict) or not all(current.values()):
        p.error("resolved publication inputs must be a nonempty object with no empty values")
    endpoint = (f"repos/{a.repo}/actions/workflows/{quote(a.workflow, safe='')}/runs"
                f"?branch={quote(a.branch, safe='')}&per_page=100")
    empty = dict(sha="", run="", url="", between=[], carry=[])
    try:
        own_store = bool(a.artifact_store and a.artifact_store != "gha")
        if own_store:
            # Assert the declared mount even when history has no reusable baseline. A mount
            # outage is not an excuse to silently switch storage or launch a full build.
            store_listing(a.artifact_store, a.repo, os.environ.get("GITHUB_RUN_ID", "1"),
                          expected_store_id=a.expect_store_id)
        runs = gh_json(endpoint)["workflow_runs"]
        if not isinstance(runs, list):
            raise ValueError("workflow_runs is not a list")
        if a.carry_withheld and not a.withheld_marker:
            p.error("--carry-withheld reads the packages from --withheld-marker; name the marker")
        if not a.withheld_marker:
            withheld = lambda run: False  # noqa: E731
        elif a.carry_withheld:
            withheld = lambda run: withheld_packages(a.repo, run["id"], a.withheld_marker,  # noqa: E731
                                                     a.artifact_store, a.expect_store_id)
        else:
            withheld = lambda run: carries_marker(a.repo, run["id"], a.withheld_marker,  # noqa: E731
                                                  a.artifact_store, a.expect_store_id)
        skipped = []

        def started(run):
            if never_started(a.repo, run["id"]):
                skipped.append(run["id"])
                return False
            return True

        result = baseline(runs, a.branch, lambda sha: subprocess.run(
            ["git", "merge-base", "--is-ancestor", sha, "HEAD"], cwd=a.root,
            capture_output=True, timeout=30).returncode == 0, os.environ.get("GITHUB_RUN_ID", ""),
            withheld, started)
        if skipped:
            print(f"ignored {len(skipped)} run(s) that never started a job ({', '.join(map(str, skipped))}) "
                  "— they published nothing", file=sys.stderr)
        if result["carry"]:
            print(f"baseline run {result['run']} withheld {len(result['carry'])} package(s) — carried "
                  f"into this run's selection until published: {' '.join(result['carry'])}", file=sys.stderr)
        if result["run"] and len(result["between"]) > MAX_UNSETTLED:
            print(f"{len(result['between'])} unsettled runs since the last success (more than "
                  f"{MAX_UNSETTLED}) — full build", file=sys.stderr)
            result = empty
        elif result["run"]:
            # Git source alone cannot see a repository variable overriding the platform ref.
            # Every run attests its actual resolved inputs in this run-scoped artifact.
            prior = attested_inputs(a.repo, result["run"], a.artifact_store, a.expect_store_id)
            between = [(rid, attested_inputs(a.repo, rid, a.artifact_store, a.expect_store_id))
                       for rid in result["between"]]
            reason = toolchain_verdict(current, prior, between)
            if own_store and any(inputs is None for _, inputs in between):
                # An older workflow may have used GitHub storage. No own-store attestation is
                # therefore NOT evidence that it never published, unlike the legacy single-store
                # path. Do not read GitHub artifacts; rebuild conservatively through the chosen store.
                reason = "a later publishing run has no attestation in the declared store — full build"
            if reason:
                print(reason, file=sys.stderr)
                result = empty
            elif result["between"]:
                print(f"walked past {len(result['between'])} unsettled run(s) "
                      f"({', '.join(map(str, result['between']))}) — the history union from the "
                      "baseline carries their changes, so the next build covers them", file=sys.stderr)
    except ArtifactStoreError as exc:
        print(f"::error::{exc}", file=sys.stderr)
        return 1
    except (OSError, ValueError, KeyError, zipfile.BadZipFile, subprocess.SubprocessError) as exc:
        print(f"::warning::publication baseline unavailable ({type(exc).__name__}); full build", file=sys.stderr)
        result = empty
    print(json.dumps(result))
    print(f"publication baseline: {result['url'] or 'none — full build'}", file=sys.stderr)
    if os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a") as out:
            out.write(f"publication-base={result['sha']}\npublication-run={result['run']}\n"
                      f"publication-carry={' '.join(result['carry'])}\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
