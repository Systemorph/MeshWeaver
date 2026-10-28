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

# 🎯 A RUN THAT PUBLISHES NOTHING ONLY REUSES (MeshWeaver.Plugins Hosting/CiBuildTimeAnalysis.md #3).
# The runs NEWER than the baseline are walked and toolchain-checked because a publishing run that
# narrows by the baseline must not miss what a later run may have published with another toolchain.
# A pull request (or merge group) publishes nothing: the baseline only offers it bundles to REUSE, and
# for that the baseline's OWN attested toolchain is the whole question — the newer runs cannot change
# what that run built. Checking them anyway made every pull request build everything whenever ONE main
# run was still pending (no attestation yet: "a later publishing run has no attestation in the declared
# store — full build", Plugins run 40629844509), which on a busy main is nearly always.
REUSE_ONLY_EVENTS = frozenset({"pull_request", "merge_group"})

# 🎯 …AND IT REUSES FROM THE NEWEST GREEN MAIN PUBLICATION ON ITS OWN TOOLCHAIN, of any publishing
# event. Taking only the newest green main PUSH, then refusing it when its toolchain differed, gave
# every Plugins pull request of 2026-10-28 no baseline at all — 62 of 62 printed "publication
# toolchain changed since the baseline run": the `latest` platform set moved five times that day
# (3.1.11889 → 11899 → 11909 → 11911 → 11913 → 11919) and main's newest green push was always on the
# set before (e.g. PR run 40743963637 on 3.1.11913 against main run 40688826732 on 3.1.11899, while
# main run 40743626120 on 3.1.11913 was still running). A pull request only REUSES, so any green main
# run that published on its exact toolchain — a push, the daily `schedule` poll, or a publication
# `repository_dispatch` — is a sound source, and the walk takes the newest such run, naming why each
# newer one was not taken. The toolchain is still compared field by field (platform, images, set,
# recipe), so a bundle is reused only from a run that built it against this run's platform.
REUSE_SOURCE_EVENTS = frozenset({"push", "schedule", "repository_dispatch"})
# Attestations a reuse-only walk reads before it gives up. Each costs one store read; ten covers a
# busy day of main runs on one set and bounds a run whose toolchain no main run has used.
MAX_REUSE_CANDIDATES = 10


def reuse_only(event):
    """Whether a run of `event` publishes nothing, so only the baseline's own toolchain matters. Pure."""
    return event in REUSE_ONLY_EVENTS


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


def _named_packages(held):
    """`held` (a `withheld(run)` answer) as the sorted package list it names, `[]` for "published
    everything", or None for "withheld an unknown set" — the one reading baseline() also applies."""
    if not held and not isinstance(held, (list, tuple, set, frozenset)):
        return []
    if (isinstance(held, (list, tuple, set, frozenset)) and held
            and all(isinstance(p, str) and re.fullmatch(r"[\w.-]+", p) for p in held)):
        return sorted(set(held))
    return None


def reuse_baseline(runs, branch, ancestor, current_run="", withheld=lambda run: False,
                   mismatch=lambda run: None, limit=MAX_REUSE_CANDIDATES):
    """The baseline of a run that only REUSES (REUSE_ONLY_EVENTS): the newest successful main run of
    a REUSE_SOURCE_EVENTS event, an ancestor of HEAD, whose `mismatch(run)` is None — its attested
    toolchain equals this run's. Pure apart from the three callables.

    `mismatch(run)` answers why a candidate's bundles are not this run's (a different toolchain, no
    readable attestation), or None. Every candidate passed over comes back in `rejected` with that
    reason, so the log says why no bundle was reused. A success that is not an ancestor of HEAD stops
    the walk (history rewritten), as in baseline(); a success that withheld an unknown set is walked
    past; a partial one is a source and its withheld packages come back as `carry`. At most `limit`
    candidates are compared."""
    rejected, compared = [], 0
    for run in runs:
        if (str(run.get("id")) == str(current_run) or run.get("head_branch") != branch
                or run.get("event") not in REUSE_SOURCE_EVENTS):
            continue
        sha = run.get("head_sha", "")
        if not (run.get("status") == "completed" and run.get("conclusion") == "success"
                and re.fullmatch(r"[0-9a-f]{40}", sha)):
            continue
        if not ancestor(sha):
            break
        carry = _named_packages(withheld(run))
        if carry is None:
            rejected.append((run["id"], "it withheld its publication without naming what"))
            continue
        if compared >= limit:
            rejected.append((run["id"], f"not compared — {limit} older candidates already were"))
            break
        compared += 1
        reason = mismatch(run)
        if reason is None:
            return {"sha": sha, "run": run["id"], "url": run["html_url"], "between": [],
                    "carry": carry, "rejected": rejected}
        rejected.append((run["id"], reason))
    return {"sha": "", "run": "", "url": "", "between": [], "carry": [], "rejected": rejected}


def _short(value):
    if value is None:
        return "∅"
    text = str(value)
    if re.fullmatch(r"[0-9a-f]{40}", text):
        return text[:9]
    if text.startswith("sha256:"):
        return text[:19]
    return text


def toolchain_difference(current, prior):
    """None when `prior` attests this run's toolchain (as comparable() sees the two); otherwise the
    fields that differ, each as `field prior → current`. Pure."""
    if not isinstance(prior, dict):
        return "it attested no toolchain"
    mine, theirs = comparable(current, prior), comparable(prior, current)
    if mine == theirs:
        return None
    fields = sorted(set(mine) | set(theirs))
    return "toolchain differs: " + ", ".join(
        f"{k} {_short(theirs.get(k))} → {_short(mine.get(k))}" for k in fields if mine.get(k) != theirs.get(k))


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
        return (f"publication toolchain changed since the baseline run "
                f"({toolchain_difference(current, prior)}) — full build")
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

    def partial_of(run_id):
        return lambda run: ["Hosting"] if run["id"] == run_id else False

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
    # REUSE SOURCE: a run that only reuses takes the newest green main publication on ITS toolchain.
    ours = {**rec, "set": "3.1.11913"}
    attested = {1: ours, 2: rec, 3: ours}

    def differs(run):
        return toolchain_difference(ours, attested.get(run["id"]))

    got = reuse_baseline([newer(), good], "main", yes, mismatch=differs)
    check(got["run"] == 1 and [r for r, _ in got["rejected"]] == [2]
          and "set 3.1.9898 → 3.1.11913" in got["rejected"][0][1],
          f"the newest green run on ANOTHER set is passed over, named with its set, for an older one on ours: {got}")
    for event in ("schedule", "repository_dispatch"):
        got = reuse_baseline([newer(event=event, id=3), newer()], "main", yes, mismatch=differs)
        check(got["run"] == 3, f"a green {event} publication on our toolchain is a reuse source: {got}")
    for change in (dict(event="workflow_dispatch"), dict(event="pull_request"), dict(head_branch="other"),
                   dict(conclusion="failure"), dict(status="in_progress", conclusion=None), dict(head_sha="short")):
        got = reuse_baseline([newer(id=3, **change), good], "main", yes, mismatch=differs)
        check(got["run"] == 1 and got["rejected"] == [], f"never a reuse source, never compared: {change} {got}")
    got = reuse_baseline([newer(), good], "main", yes, mismatch=lambda run: differs({"id": 2}))
    check(got["run"] == "" and len(got["rejected"]) == 2,
          f"NEGATIVE CONTROL: no main run on our toolchain reuses nothing and names every candidate: {got}")
    got = reuse_baseline([newer(id=3), good], "main", lambda sha: sha != "b" * 40, mismatch=differs)
    check(got["run"] == "", f"a success off HEAD's line stops the walk, never skipped over: {got}")
    got = reuse_baseline([newer(id=3), good], "main", yes, withheld=lambda run: run["id"] == 3, mismatch=differs)
    check(got["run"] == 1 and got["rejected"][0][0] == 3,
          f"a run that withheld an unknown set is walked past: {got}")
    got = reuse_baseline([newer(id=3), good], "main", yes, withheld=partial_of(3), mismatch=differs)
    check(got["run"] == 3 and got["carry"] == ["Hosting"], f"a partial publication is a source with carry: {got}")
    got = reuse_baseline([newer(id=4), newer(id=5), good], "main", yes, mismatch=differs, limit=2)
    check(got["run"] == "" and len(got["rejected"]) == 3, f"at most `limit` candidates are compared: {got}")
    check(toolchain_difference(ours, ours) is None, "the same toolchain has no difference")
    check(toolchain_difference(ours, {**ours, "logic": "l9"}) is None,
          "the build-logic ref is no difference when both attest the recipe")
    check(toolchain_difference(ours, None) == "it attested no toolchain", "an unattested run is named as such")
    check(toolchain_difference({**ours, "platform": "a" * 40}, {**ours, "platform": "b" * 40})
          == f"toolchain differs: platform {'b' * 9} → {'a' * 9}", "a sha is shortened, field named")
    # REUSE ONLY: a run that publishes nothing reads only the baseline's own toolchain.
    check(reuse_only("pull_request") and reuse_only("merge_group"), "a pull request / merge group only reuses")
    for event in ("push", "schedule", "repository_dispatch", "workflow_dispatch", ""):
        check(not reuse_only(event), f"NEGATIVE CONTROL: {event or 'no event'} keeps the between-run checks")
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

        def ancestor(sha):
            return subprocess.run(["git", "merge-base", "--is-ancestor", sha, "HEAD"], cwd=a.root,
                                  capture_output=True, timeout=30).returncode == 0

        event = os.environ.get("GITHUB_EVENT_NAME", "")
        if reuse_only(event):
            def mismatch(run):
                try:
                    prior = attested_inputs(a.repo, run["id"], a.artifact_store, a.expect_store_id)
                except ArtifactStoreError:
                    raise
                except (OSError, ValueError, KeyError, zipfile.BadZipFile) as exc:
                    return f"its attestation cannot be read ({type(exc).__name__}: {str(exc)[:120]})"
                return toolchain_difference(current, prior)

            result = reuse_baseline(runs, a.branch, ancestor, os.environ.get("GITHUB_RUN_ID", ""),
                                    withheld, mismatch)
            for run_id, why in result.pop("rejected"):
                print(f"main run {run_id} not a reuse source: {why}", file=sys.stderr)
            if result["run"]:
                print(f"this {event} run publishes nothing — it reuses from main run {result['run']}, the "
                      "newest green main publication on this run's own toolchain", file=sys.stderr)
            else:
                print(f"no green main publication on this run's toolchain (set {current.get('set', '?')}) "
                      "— every selected bundle is built", file=sys.stderr)
            if result["carry"]:
                print(f"baseline run {result['run']} withheld {len(result['carry'])} package(s) — carried "
                      f"into this run's selection until published: {' '.join(result['carry'])}", file=sys.stderr)
        else:
            result = baseline(runs, a.branch, ancestor, os.environ.get("GITHUB_RUN_ID", ""),
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
