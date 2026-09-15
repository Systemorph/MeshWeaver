#!/usr/bin/env python3
"""resolve-platform.py — the RELEASED platform, resolved at RUN TIME by the reusable lanes. No pins.

WHY THIS LIVES IN CORE (maintainer rule, 2026-09-12: "for compile always find latest package of
platform and plugins")
-----------------------------------------------------------------------------------------------
Every satellite (MeshWeaver.Plugins / .Education / .Reinsurance / .Crm / .Manufacturing /
.SocialMedia) carries a copy of this resolver and runs it in its own `platform-ref` job and again,
as a FRESHNESS check, in the first step of every normal consumer job — so a "re-run failed jobs"
tests the newest sealed packages, not the hours-old outputs of a job GitHub did not re-run
(Education#320, run 34685497637: the 09:22Z resolution 3.0.0-ci.8405 was re-used at 12:5xZ although
8414/8416 had sealed with the fix; the shards stayed red on a fixed defect).

What a satellite CANNOT refresh is a `uses:` job: `node-repo-gate.yml` and
`node-repo-publish-bake.yml` take `image-digest` / `platform-image-digest` as `with:` inputs
evaluated when the job is CALLED, from the caller's stored `platform-ref` outputs, and GitHub offers
no hook inside a reusable workflow for the caller to re-resolve. So the two lanes resolve the
platform THEMSELVES when those inputs are EMPTY — in `plan` (the gate) and in the first step of
`publish-bake` — with THIS script, fetched from core at the lane's `scripts-ref` exactly like
compose-gate-host.sh. A caller that pins still pins: non-empty digests are taken verbatim and this
script is not run. The gate's shards run it once more against `plan`'s outputs as their baseline,
so a re-run of ONE red shard tests the newest set too (FRESHNESS below).

THE RULE
--------
  1. core's `main-cd.yml` runs on `main`, newest first (the public GitHub API, the caller's
     `GITHUB_TOKEN` — every satellite's token can read core, it is public);
  2. the newest run whose PLATFORM is SEALED — `Promote: tag the full set`, `Verify every image
     shipped` and `Bake platform content in the shipped image + publish` all succeeded. That trio
     is the whole seal: neither the run's overall conclusion (a satellite-compat leg, an alert job)
     nor the run's OWN `Plugins: bake + seal …` job decides the choice. A run that is green but
     published nothing (the trio SKIPPED, e.g. core CD run 8197 / 8420) is not a release and is
     passed over, saying so. A run still sealing is waited for on a release trigger
     (`--wait-for-seal`), and otherwise passed over, saying so;
  3. its images, resolved by TAG in ACR — `<version>-ci.<n>` first (promote's version tag), the
     seven-character sha tag second (promote's identity tag) — to the digests the lane then takes
     exactly as if the caller had pinned them. A set whose images are gone (retention purge,
     MeshWeaver#3438) is passed over, saying so, and the next-newest sealed set is taken;
  4. the PLUGINS publication is found on its own: the newest core CD run whose `Plugins: bake +
     seal …` job succeeded — which may be an OLDER run than the core set — and is reported beside
     it (`plugins-run`, `plugins-run-url`, `plugins-set`, `plugins-sealed-at`; `plugins-sealed` is
     the chosen run's own seal verdict). A Plugins commit is not in these outputs on purpose:
     Systemorph/MeshWeaver.Plugins is PRIVATE, so a consumer's GITHUB_TOKEN cannot read it. ONE
     bounded exception, measured: a set whose own Plugins seal is STILL RUNNING is passed over (or
     waited for on a release trigger) — core cuts a set every ~20 minutes and its seal lands minutes
     after promote; taking the set in that window made every consumer red on `upstream 'plugins'
     has no SEALED publication … for identity <id>` (Manufacturing, 2026-09-10). A seal that FAILED,
     was SKIPPED or CANCELLED is terminal and does not hold the set back — Plugins' own CI
     republishes for that identity when Plugins is fixed, and the lane's upstream fetch says RED, by
     name, if the registry holds no publication for it.

Every skip is PRINTED with its reason, the chosen set is written to the job log and the step
summary, and running out of candidates is a RED job naming what was looked for — never a silent
green and never a fallback to a moving tag nobody named.

A STALE LISTING IS REFUSED, NEVER RESOLVED FROM (MeshWeaver#4433)
-----------------------------------------------------------------
GitHub can serve page 1 of the run listing from a stale snapshot. Measured twice on 2026-09-15:
page 1 began ~260 runs behind the newest (#8423 and #8420 while #8676 was sealed), a re-read
minutes later was correct, and the walk above took the first sealed set it met — a set three days
old, reported as the newest. With a floor that is a red naming the floor; without one (every
satellite but Plugins) it is a SILENT compile, test and publish against an old platform. So page 1
is checked against two facts the listing cannot fake, and a listing that fails either is RED:

  * AGE — core CD runs on `main` at least hourly (an hourly `schedule` plus every main build;
    measured over 300 runs, 09-11 → 09-15: the widest gap was 1.7 h). A page whose newest main run
    is older than LISTING_MAX_AGE_HOURS cannot be the newest page;
  * the CEILING, when one was asked for — the run this repository's `main` has already PASSED on
    exists, so a page whose newest run is older than it is provably stale.

A freeze is exempt (it names one set, and an incident is when it must keep working), and a
freshness check keeps its baseline on this refusal like on any other. The resolver does not
re-read: the red is the harmless answer, and a re-run of the job reads the listing again.

FRESHNESS — A RE-RUN MUST NOT TEST A STALE SET
----------------------------------------------
With `PLATFORM_BASELINE` set (JSON: the outputs of the job that resolved first — `plan` in the gate,
`platform-ref` in a satellite) a call is a freshness check instead of a resolution:

  * a NEWER sealed set than the baseline ⇒ this job takes it (`override=true`) and says so;
  * the same set, or an older one (the baseline's images purged, an API hiccup) ⇒ the baseline
    is written back verbatim (`override=false`) — one platform per run stays the normal state;
  * the re-resolution itself fails ⇒ the baseline is written back with a `::warning` — the API
    being down is not the platform moving, and a re-run must not go red on it.

THE FREEZE (incident use only — not a pin)
------------------------------------------
`--freeze <core sha | X.Y.Z[-prerelease]-ci.N>` selects ONE run instead of the newest sealed one —
for a bisect or an upstream outage. The lanes pass the CALLER's repository variable
`MW_PLATFORM_REF` (in a reusable workflow `vars` resolves from the caller's repository — GitHub
docs, "Variables reference": "For reusable workflows, the variables from the caller workflow's
repository are used"), the same variable the satellite's own jobs honour, so one freeze binds a
whole run. A frozen run that is not sealed or whose images are gone is an ERROR, never a silent
substitution: a freeze is an instruction, not a preference.

API-CALL BUDGET (the caller's GITHUB_TOKEN: 1,000 requests/hour per repository)
------------------------------------------------------------------------------
One resolution: 1 (runs page) + 1 per run examined until the chosen set + 1 props read, + at most
PLUGINS_LOOKBACK (40) when the chosen run's own seal is not green — typically 5-12 calls. The
lanes add one resolution in `plan` / `publish-bake` plus one freshness check per gate shard, and
one `contents` read per job to fetch this script. The registry HEADs are not GitHub calls.

USAGE
-----
    python3 resolve-platform.py                     # newest sealed set, images resolved
    python3 resolve-platform.py --wait-for-seal 900 # release trigger: wait for the set sealing now
    python3 resolve-platform.py --no-registry       # core sha + set name only
    python3 resolve-platform.py --freeze 3.0.0-ci.8195
    python3 resolve-platform.py --self-test         # prove every branch, offline

Env: GH_TOKEN (or GITHUB_TOKEN); ACR_USERNAME / ACR_PASSWORD unless --no-registry;
PLATFORM_BASELINE (optional JSON, see FRESHNESS). The registry credential supports ACR and the
documented ghcr.io image overrides.
Writes `sha`, `set`, `run-number`, `run-url`, `image-digest`, `portal-image-digest`,
`tester-image`, `portal-image` (name@digest), `plugins-sealed`, `plugins-run`, `plugins-run-url`,
`plugins-set`, `plugins-sealed-at`, `override`, `source` to $GITHUB_OUTPUT and a table to
$GITHUB_STEP_SUMMARY when set. With --migration-image, also writes `migration-image-digest` and
`migration-image` for that same sealed set.

The satellites' copies are ports of the same rule (the reference implementation is
MeshWeaver.Plugins, `Hosting/PlatformResolution.md`; the design of record for the consumer side is
MeshWeaver.Education `.github/platform-resolution.md`). Keep the output keys identical across them.
"""
from __future__ import annotations

import argparse
import base64
import json
import os
import re
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime
from typing import Callable, NamedTuple

CORE_REPO = "Systemorph/MeshWeaver"
CORE_BRANCH = "main"
CORE_CD_WORKFLOW = "main-cd.yml"
GITHUB_API = "https://api.github.com"

DEFAULT_TESTER_IMAGE = "meshweaver.azurecr.io/mw-plugin-test"
DEFAULT_PORTAL_IMAGE = "meshweaver.azurecr.io/memex-portal-ai"

# 🚨 THE SEAL, as core's CD names it. Matched by PREFIX so a matrix suffix (`… (linux-x64)`) or a
# reusable-workflow suffix (`… / Register …`) still matches. If core renames one of these, EVERY
# run reads "absent" and this script fails RED listing the names it looked for — loud, not silent.
REQUIRED_JOBS: tuple[tuple[str, str], ...] = (
    ("promote", "Promote: tag the full set"),
    ("verify", "Verify every image shipped"),
    ("platform bake", "Bake platform content in the shipped image + publish"),
)
PLUGINS_SEAL_JOB = ("plugins seal", "Plugins: bake + seal the publication for this identity")

SHA = re.compile(r"^[0-9a-f]{40}$")
SET_NAME = re.compile(r"^(\d+\.\d+\.\d+(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?)[.-]ci\.(\d+)$")
PLATFORM_VERSION = re.compile(
    r"<PlatformVersion\b[^>]*>\s*(\d+\.\d+\.\d+[^<\s]*)\s*</PlatformVersion>")
MAX_RUN_PAGES = 3          # 300 runs ≈ several days of core CD at the observed cadence
POLL_SECONDS = 30
# How many runs OLDER than the chosen core set are read looking for a sealed Plugins publication
# when the chosen run's own seal is not green — one `jobs` call each, so this bounds the budget.
PLUGINS_LOOKBACK = 40

# GitHub REST: path → JSON, or TEXT. A `list` because the check-run ANNOTATIONS endpoint answers a
# bare array; a `str` because a job LOGS endpoint answers plain text (read only when a caller opts
# into `--verify-source`). Every other path this script reads answers an object, so callers that
# expect one keep reading `.get(...)` unchanged.
Fetch = Callable[[str], dict | list | str]
Resolve = Callable[[str, str], str | None]         # registry: (repo, tag) → digest or None (absent)


class ResolutionError(RuntimeError):
    """A RED verdict: what was looked for and what stood in the way. Never a silent pass."""


class ProvenanceUnavailable(ResolutionError):
    """The set cannot be attributed to a source; normal selection may try an older verified set."""


# Bounds the ONE text read this script can make (`--verify-source`, off by default). A job log is a
# stream with no declared length, so a cap is the difference between a bounded read and an OOM on a
# runner; over the cap is a refusal, never a truncated parse that could match the wrong receipt.
MAX_LOG_BYTES = 16 * 1024 * 1024
FINAL_BAKE_RECEIPT = re.compile(
    r"^(?:\d{4}-\d{2}-\d{2}T[\d:.]+Z )?bake published: ([^\r\n]+)$", re.MULTILINE)


# ───────────────────────────── GitHub REST (public core repo) ─────────────────────────────

def github_fetch_with(token: str) -> Fetch:
    def fetch(path: str) -> dict | list | str:
        last: Exception | None = None
        for attempt in range(4):
            request = urllib.request.Request(
                f"{GITHUB_API}{path}",
                headers={
                    "Accept": "application/vnd.github+json",
                    "X-GitHub-Api-Version": "2022-11-28",
                    "User-Agent": "meshweaver-lane-resolve-platform",
                })
            # 🚨 UNREDIRECTED. A job-logs path answers a 302 to SIGNED storage, and urllib forwards
            # ordinary headers across a redirect — which would hand this token to a host that is not
            # GitHub and does not need it. The signed URL carries its own authorisation. No JSON
            # endpoint this script reads redirects, so nothing else changes.
            request.add_unredirected_header("Authorization", f"Bearer {token}")
            try:
                with urllib.request.urlopen(request, timeout=30) as response:
                    # The ONE text read, and only for the path that has one (`--verify-source`).
                    if path.endswith("/logs"):
                        raw = response.read(MAX_LOG_BYTES + 1)
                        if len(raw) > MAX_LOG_BYTES:
                            raise ProvenanceUnavailable(f"job log {path} exceeds {MAX_LOG_BYTES} bytes")
                        try:
                            return raw.decode("utf-8", "strict")
                        except UnicodeDecodeError as error:
                            raise ProvenanceUnavailable(f"job log {path} is not UTF-8 text") from error
                    return json.load(response)
            except ProvenanceUnavailable:
                raise
            except urllib.error.HTTPError as error:
                # A log that is GONE is a provenance answer, not a transport verdict: logs expire
                # on their own retention while the run stays listed, so an older set must be tried
                # rather than the whole resolution going red.
                if path.endswith("/logs") and error.code in (404, 410):
                    raise ProvenanceUnavailable(
                        f"job log {path} is unavailable (HTTP {error.code})") from error
                # A rate limit is transient by definition; wait for it, bounded. Anything else is
                # a verdict about the path or the token.
                if error.code in (403, 429) and attempt < 3:
                    reset = error.headers.get("x-ratelimit-reset")
                    wait = 30.0
                    if reset and reset.isdigit():
                        wait = min(max(int(reset) - time.time() + 1, 5.0), 120.0)
                    print(f"GET {path} → HTTP {error.code} (rate limited) — waiting {wait:.0f}s")
                    time.sleep(wait)
                    last = error
                    continue
                raise ResolutionError(
                    f"GET {path} → HTTP {error.code}. {CORE_REPO} is public, so this is a revoked "
                    "token, an exhausted rate limit, or a renamed workflow — not a missing "
                    "release.") from error
            except Exception as error:                    # noqa: BLE001 — transport
                last = error
                if attempt < 3:
                    time.sleep(5 * (attempt + 1))
        raise ResolutionError(f"GET {path}: {last}")
    return fetch


def cd_runs(fetch: Fetch, page: int) -> list[dict]:
    data = fetch(f"/repos/{CORE_REPO}/actions/workflows/{CORE_CD_WORKFLOW}/runs"
                 f"?branch={CORE_BRANCH}&per_page=100&page={page}")
    return list(data.get("workflow_runs") or [])


# Core CD runs on `main` at least hourly (see the module docstring: the widest gap in 300 measured
# runs was 1.7 h), so a page 1 whose newest main run is older than this is a stale snapshot. Wide on
# purpose: a false refusal reds every satellite at once, and the stale pages measured were ~3 days
# behind.
LISTING_MAX_AGE_HOURS = 12


def _created(run: dict) -> float | None:
    stamp = str(run.get("created_at") or "")
    if not stamp:
        return None
    try:
        return datetime.fromisoformat(stamp.replace("Z", "+00:00")).timestamp()
    except ValueError:
        return None


def stale_listing(runs: list[dict], passed_ceiling: int | None, now: float) -> str | None:
    """Why page 1 of the main-cd listing cannot be the newest page, or None when nothing proves it.

    Both facts are independent of the page itself: the clock, and a run number this repository's
    own `main` has already passed on. Rows without `created_at` cannot be aged; a page with none
    is judged by the ceiling alone (the live API always sends it — the self-test fixtures don't)."""
    main_runs = [r for r in runs if r.get("head_branch") in (None, CORE_BRANCH)]
    if not main_runs:
        return None
    newest = max(int(r["run_number"]) for r in main_runs)
    if passed_ceiling is not None and newest < passed_ceiling:
        return (f"its newest run is main-cd #{newest}, but this repository's `main` has already "
                f"passed on core CD #{passed_ceiling}, which the page does not contain")
    stamps = [(stamp, r) for r in main_runs for stamp in [_created(r)] if stamp is not None]
    if not stamps:
        return None
    stamp, run = max(stamps, key=lambda pair: pair[0])
    hours = (now - stamp) / 3600
    if hours > LISTING_MAX_AGE_HOURS:
        return (f"its newest run, main-cd #{int(run['run_number'])}, was created "
                f"{run.get('created_at')} — {hours:.0f} h ago, while core CD runs on {CORE_BRANCH} at "
                f"least hourly (refused beyond {LISTING_MAX_AGE_HOURS} h)")
    return None


def run_jobs_of(fetch: Fetch, repo: str, run_id: int) -> list[dict]:
    """Every job of one run in `repo`. The repo is a PARAMETER because the optional main-ceiling
    below reads the CALLING repository's own runs, not core's; `run_jobs` keeps the core-pinned
    spelling every existing caller uses."""
    jobs: list[dict] = []
    page = 1
    while True:
        data = fetch(f"/repos/{repo}/actions/runs/{run_id}/jobs"
                     f"?filter=latest&per_page=100&page={page}")
        rows = list(data.get("jobs") or [])
        jobs += rows
        if len(rows) < 100 or len(jobs) >= int(data.get("total_count") or 0):
            return jobs
        page += 1


def run_jobs(fetch: Fetch, run_id: int) -> list[dict]:
    return run_jobs_of(fetch, CORE_REPO, run_id)


def platform_version(fetch: Fetch, sha: str, log: Callable[[str], None] = print) -> str | None:
    """The line (`3.0.0`) core's Directory.Build.props declares AT this commit, or None. The set's
    name is `<line>-ci.<run number>` (core: "derives the ci.<n> suffix from $GITHUB_RUN_NUMBER").
    A None only costs the version tag — the seven-character sha tag names the same manifest."""
    try:
        data = fetch(f"/repos/{CORE_REPO}/contents/Directory.Build.props?ref={sha}")
        text = base64.b64decode(data.get("content") or "").decode("utf-8", "replace")
    except ResolutionError as error:
        log(f"  (Directory.Build.props at {sha[:9]} unreadable — {error}; using the sha tag)")
        return None
    match = PLATFORM_VERSION.search(text)
    return match.group(1) if match else None


# ───── OPTIONAL: the publication's OWN SOURCE, read from the bake receipt (#4171) ─────
#
# 🚨 OFF UNLESS ASKED (`--verify-source`). Nothing below runs, and no job LOG is ever fetched,
# without the flag; `choose(..., verify_source=False)` — every caller that does not ask — keeps
# taking the run's `head_sha` plus the version `Directory.Build.props` declares at it.
#
# 🚨 WHY IT IS MORE CORRECT WHEN YOU DO ASK. The publishing lane reuses CONTENT-ADDRESSED builds
# from earlier runs, so the run that published a set is not necessarily the run that BUILT its
# bytes — the same defect MeshWeaver#4158 exists for one level up, where a `[ModuleLoad]` line
# stamped with `github.sha` would put a newer commit on older bytes. `publish-bake-bundles.sh`
# writes its final receipt AFTER publication, convergence and the release-marker writes, and that
# line names the gate-selected SOURCE and RELEASE. Workflow metadata does not.
#
# 🚨 AND IT IS A REFUSAL, never a silent downgrade: a set whose receipt is missing, duplicated,
# malformed or inconsistent raises ProvenanceUnavailable and the set is PASSED OVER with the reason
# recorded, so the resolution continues at an older VERIFIED set instead of taking an unattributable
# one. Under a freeze the same condition is fatal — a freeze names one set and may not substitute.

class PublicationSource(NamedTuple):
    sha: str
    version: str
    set_name: str


def publication_source(fetch: Fetch, jobs: list[dict], run_number: int,
                       receipts: dict[tuple[int, int], PublicationSource]) -> PublicationSource:
    """Read the producer's final publication receipt, once per successful platform-bake job.

    publish-bake-bundles.sh writes this AFTER publication/convergence and release-marker writes.
    The receipt names the actual gate-selected source and release; workflow metadata does not.
    Architecture identities may differ, but source and release must agree across every leg.
    """
    sources: set[PublicationSource] = set()
    for job in jobs:
        if not str(job.get("name", "")).startswith(REQUIRED_JOBS[2][1]):
            continue
        if job.get("status") != "completed" or job.get("conclusion") != "success":
            continue
        job_id = job.get("id")
        if not isinstance(job_id, int) or job_id <= 0:
            raise ProvenanceUnavailable("the successful platform bake has no usable job id")
        key = job_id, run_number
        if key in receipts:
            sources.add(receipts[key])
            continue
        body = fetch(f"/repos/{CORE_REPO}/actions/jobs/{job_id}/logs")
        if not isinstance(body, str):
            raise ProvenanceUnavailable(f"platform-bake job {job_id} returned no text log")
        records = FINAL_BAKE_RECEIPT.findall(body)
        if len(records) != 1:
            raise ProvenanceUnavailable(
                f"platform-bake job {job_id} has {len(records)} final publication receipts; expected one")
        fields: dict[str, str] = {}
        for token in records[0].split():
            key, separator, value = token.partition("=")
            if not separator or not value or key in fields:
                raise ProvenanceUnavailable(f"platform-bake job {job_id} has a malformed final receipt")
            fields[key] = value
        sha = fields.get("source-sha", "")
        release = SET_NAME.fullmatch(fields.get("release", ""))
        counts = [fields.get(key, "") for key in
                  ("bundles", "targets-published", "targets-converged", "release-markers")]
        # 🚨 A TARGET THAT ALREADY HELD THIS PUBLICATION REACHED IT (#4247). `targets-already` counts
        # the targets that were already sealed ON THIS CONTENT when the bake asked — the ordinary
        # shape when two runs bake one commit, and of any re-bake. It used to be recorded NOWHERE,
        # so such a receipt read `targets-published=0 targets-converged=0` — byte-identical to a
        # publication that reached nothing — and this check PASSED THE SET OVER as unattributable.
        # Measured on main-cd #8457 and #8459 (both cdb2878bb): the bake log says twice "holds a
        # COMPLETE publication of THIS content … already published; skipping", and two otherwise
        # sealed sets were skipped for it. OPTIONAL, defaulting to 0: a receipt written before
        # #4247 does not carry the field, and must keep parsing exactly as it did.
        already = fields.get("targets-already", "0")
        if (fields.get("source") != "meshweaver-content" or not SHA.fullmatch(sha)
                or not release or int(release.group(2)) != run_number
                or fields.get("arch") not in ("linux-x64", "linux-arm64")
                or not fields.get("identity") or fields["identity"] == "unknown"
                or not all(re.fullmatch(r"[0-9]+", value) for value in counts)
                or not re.fullmatch(r"[0-9]+", already)
                or int(counts[0]) == 0 or int(counts[1]) + int(counts[2]) + int(already) == 0
                or int(counts[3]) == 0):
            raise ProvenanceUnavailable(
                f"platform-bake job {job_id} has an incomplete or inconsistent final publication receipt")
        version = release.group(1)
        receipt = PublicationSource(sha, version, f"{version}-ci.{run_number}")
        receipts[job_id, run_number] = receipt
        sources.add(receipt)
    # 🚨 ZERO AND TWO ARE DIFFERENT SENTENCES (#4242). This said "successful platform bakes
    # disagree on source/release (found 0 distinct receipts)" for a run that has NO successful
    # platform bake at all — an ABSENCE reported in the vocabulary of a DISAGREEMENT. `choose`
    # never asks about such a run (it skips an unsealed one first), but anything that probes runs
    # directly does, and on 2026-09-13 that sentence was read off eleven ordinary non-publishing
    # runs and reported as a fleet-wide bake defect. Measured the same day over main-cd 8505–8531:
    # 20 of 27 runs are unsealed and answer this, while ALL SEVEN sealed runs are attributable and
    # NONE disagrees. Saying which of the two states it is costs one branch.
    if not sources:
        raise ProvenanceUnavailable(
            "this run published nothing: it has no SUCCESSFUL platform bake, so there is no "
            "publication receipt to read. That is the ordinary shape of a main-cd run that did not "
            "publish — it is not a disagreement, and not a bake defect")
    if len(sources) > 1:
        raise ProvenanceUnavailable(
            f"successful platform bakes DISAGREE on source/release — {len(sources)} distinct "
            "receipts in one run, where every leg must name the same source and release: "
            + "; ".join(sorted(f"{s.sha[:9]}/{s.set_name}" for s in sources)))
    return next(iter(sources))


# ──────────────── WHAT THIS REPO'S OWN `main` HAS ALREADY PASSED ON (#3842 → Roland, 2026-09-12)
#
# 🚨 A PULL REQUEST RESOLVES THE NEWEST SEALED SET **THAT `main` HAS ALREADY PASSED**, not simply
# the newest sealed set. The failure mode is the whole point of the rule: when core seals a set
# that regresses this repo, `main` goes red on it and **every open pull request keeps building on
# the last set main passed**. Before this, one such set reddened every open PR at once, for a
# reason no author's diff could reach — measured four times in 24 h, 91 PR-hours exposed
# (Hosting/PullRequestDrain.md).
#
# The evidence is each run's OWN answer, not a second bookkeeping mechanism: every run publishes
# `::notice title=Platform for this run::<set> — core <sha9>` from `write_outputs` below, and an
# annotation survives with its check run. So the ceiling is read back from the newest SUCCESSFUL
# push runs of this repo's ci.yml on main. A run whose annotation cannot be read is SKIPPED and
# said so — never treated as "main passed nothing".
SATELLITE_CD_WORKFLOW = "ci.yml"
MAIN_RUNS_EXAMINED = 12          # ~a day of merges; deep enough to survive a red patch on main
PLATFORM_REF_JOB = "Resolve the released platform"
NOTICE_TITLE = "Platform for this run"
NOTICE_SET = re.compile(r"(\d+\.\d+\.\d+)[.-]ci\.(\d+)")


# ───── OPTIONAL: what the CALLING repo's own `main` has already passed on (#3842, #4171) ─────
#
# 🚨 OFF UNLESS ASKED. Nothing below runs without `--passed-on-main` or `--passed-ceiling`, and
# `choose(..., passed_ceiling=None)` — every caller that does not pass one — takes exactly the
# path it took before this existed. That is the whole shape of the option: this lives in the
# canonical so a repository that wants the rule does not have to FORK the resolver to get it
# (MeshWeaver#4171 — a vendored copy that drifts is named by `check-resolver-copy.py`, and "a
# deliberate difference belongs in the canonical as an option, never in a fork").
#
# 🚨 What the rule IS. A pull request resolves the newest sealed set **that this repository's
# `main` has already passed on**, not simply the newest sealed set. The failure mode is the point:
# when core seals a set that regresses the repository, `main` goes red on it and every open pull
# request keeps building on the last set main passed. Before it, one such set reddened every open
# PR at once, for a reason no author's diff could reach — measured four times in 24 h, 91 PR-hours
# exposed (MeshWeaver.Plugins, Hosting/PullRequestDrain.md).
#
# The evidence is each run's OWN answer, not a second bookkeeping mechanism: `emit` below publishes
# `::notice title=Platform for this run::<set> — core <sha9>` on every run, and an annotation
# survives with its check run. So the ceiling is read back from the newest SUCCESSFUL push runs of
# the repository's own CD workflow on main. A run whose annotation cannot be read is SKIPPED and
# said so — never read as "main passed nothing", which would silently take the newest set again.
SATELLITE_CD_WORKFLOW = "ci.yml"
MAIN_RUNS_EXAMINED = 12          # ~a day of merges; deep enough to survive a red patch on main
PLATFORM_REF_JOB = "Resolve the released platform"
NOTICE_TITLE = "Platform for this run"
NOTICE_SET = re.compile(r"(\d+\.\d+\.\d+[0-9A-Za-z.\-]*)[.-]ci\.(\d+)")


# ───── THE FLOOR: the oldest set this TREE can compile against (MeshWeaver.Plugins#1826) ─────
#
# 🚨 A CEILING AND A FREEZE ARE NOT ENOUGH. The ceiling says which set is VOUCHED; the freeze says
# which set to TAKE. Neither says which sets this repository's own source can still COMPILE against,
# and that is a third, independent fact — one the repository learns the moment it adopts a symbol
# from a newer platform set.
#
# Measured (Plugins#1821): `src/Memex.LocalMesh` adopted core's `OperationSentinel`. Its pull request
# compiled clean and merged, because `MW_PLATFORM_REF` was frozen to `3.0.0-ci.8506`, a set carrying
# the type. The freeze was then lifted — a repo VARIABLE edit, no gate, no diff — and the resolver
# fell back to its ceiling `3.0.0-ci.8492`, fifty newer sealed sets passed over. Every open pull
# request went red on `CS0103: The name 'OperationSentinel' does not exist in the current context`,
# a fault none of their diffs could reach.
#
# The floor makes that fact DECLARED, in a file that moves in the diff that needs it, so the
# adoption and the requirement land together. It binds EVERY path, a freeze included: a freeze is an
# instruction about WHICH of the sets that can build this tree to take, and it cannot make an older
# set carry a symbol that set does not have. Resolving below the floor has exactly one outcome, a
# compile error in a file nobody touched — so this refuses early, naming the set, the floor, the
# reason and the one way down (remove the adoption and lower the floor in the same commit).
#
# An ABSENT declaration is no floor at all — the other five vendored copies carry no such file and
# are unaffected. A MALFORMED one is refused, never read as "no floor": a declaration the reader
# silently ignores is a guard that passes having checked nothing.
#
# 🚨 THE BOUNDARY IS DELIBERATE: the floor binds `main()`, which is every CI resolution (all four
# `ci.yml` call sites, the freeze path and the kept-baseline path alike). It does NOT bind
# `fetch-refs.py`, which calls `choose` directly for local tooling — and that is not a hole, it is
# the shape of the defect: `fetch-refs` takes the NEWEST sealed set with no ceiling and no freeze,
# so it has no mechanism to resolve backwards past an adoption. Extending it there needs its own
# fixtures (its cases resolve `3.0.0-ci.8207`, below the real floor) and buys nothing this issue is
# about. Written down so the next reader does not have to guess whether it was considered.
PLATFORM_FLOOR_BASENAME = "platform-floor.json"
# How the file is NAMED in diagnostics — the path a reader of a satellite repository will type.
PLATFORM_FLOOR_FILE = "scripts/" + PLATFORM_FLOOR_BASENAME


def read_floor_text(text: str | None) -> tuple[int | None, str]:
    """`(minimum core-CD run number, why)` from the declaration's text; `(None, "")` if absent."""
    if text is None:
        return None, ""
    try:
        doc = json.loads(text)
    except ValueError as error:
        raise ResolutionError(f"{PLATFORM_FLOOR_FILE} is not readable JSON ({error}). A floor this "
                              "script cannot parse is NOT 'no floor'; fix or delete the file.")
    minimum = doc.get("minimum") if isinstance(doc, dict) else None
    because = doc.get("because") if isinstance(doc, dict) else None
    if not isinstance(minimum, int) or isinstance(minimum, bool):
        raise ResolutionError(f"{PLATFORM_FLOOR_FILE} must carry an integer `minimum` (the oldest "
                              f"core-CD run number this tree compiles against); got {minimum!r}.")
    if not isinstance(because, str) or not because.strip():
        raise ResolutionError(f"{PLATFORM_FLOOR_FILE} must carry a non-empty `because` naming what "
                              "the tree adopted. A floor with no reason cannot be lowered safely "
                              "by anyone who did not raise it.")
    return minimum, because.strip()


def read_floor(path: str) -> tuple[int | None, str]:
    try:
        with open(path, encoding="utf-8") as handle:
            return read_floor_text(handle.read())
    except FileNotFoundError:
        return None, ""


def current_floor() -> tuple[int | None, str]:
    """This repository's declared floor. The ONE seam — there is deliberately no flag and no
    environment variable to point it elsewhere, because either would be a way to resolve below the
    floor without changing the file that states it."""
    # BESIDE THE RESOLVER, explicitly — not derived from the repository root. The canonical lives
    # at `.github/scripts/` in the platform and at `scripts/` in every repository that vendors it,
    # and a root-relative path would have to be right for both. A sibling is right for both by
    # construction, and stays right if either layout moves.
    return read_floor(os.path.join(os.path.dirname(os.path.abspath(__file__)), PLATFORM_FLOOR_BASENAME))


def floor_refusal(floor: tuple[int | None, str], set_name: str, run_number: int) -> str | None:
    """The refusal text when `set_name` is below the declared floor, else None."""
    minimum, because = floor
    if minimum is None or run_number >= minimum:
        return None
    return (f"{set_name} is core CD #{run_number}, BELOW the floor #{minimum} this repository "
            f"declares in {PLATFORM_FLOOR_FILE}.\n"
            f"Why the floor exists: {because}\n"
            "Resolving below it compiles this tree against a set that does not carry a symbol the "
            "tree has already adopted, which reds every open pull request on a fault no author's "
            "diff can reach (MeshWeaver.Plugins#1821 — 50 newer sets were passed over and every "
            "open PR failed on one CS0103).\n"
            "A freeze does NOT exempt it: a freeze chooses among the sets that can build this tree; "
            "it cannot make an older set carry a symbol that set does not have.\n"
            f"To go below deliberately, remove the adoption and lower `minimum` in "
            f"{PLATFORM_FLOOR_FILE} in the SAME commit.")


def ceiling_for(fetch: Fetch, repo: str, freeze: str | None,
                log: Callable[[str], None] = print) -> tuple[int | None, list[str], bool]:
    """`(ceiling, notes, fatal)` for a run that asked to follow its own main.

    🚨 A FREEZE OVERRIDES THE CEILING, INCLUDING AN UNREADABLE ONE. `MW_PLATFORM_REF` is an
    instruction for an incident — a bisect, an upstream outage — and the likeliest moment to need
    it is precisely when `main` is red and has passed nothing recently. Computing the ceiling first
    and refusing on it would take the freeze away exactly then, so a freeze skips the ceiling
    entirely rather than being checked against it.
    """
    if freeze:
        return None, [f"freeze {freeze} overrides the main ceiling — not consulted"], False
    ceiling, notes = main_passed_ceiling(fetch, repo, log=log)
    return ceiling, notes, ceiling is None


def main_passed_ceiling(fetch: Fetch, repo: str, limit: int = MAIN_RUNS_EXAMINED,
                        log: Callable[[str], None] = print) -> tuple[int | None, list[str]]:
    """`(highest core-CD run number this repo's main has PASSED on, one note per run examined)`.

    `None` means it could not be established from the newest `limit` successful main runs — which
    is a RED verdict for the caller, never a licence to take the newest sealed set: that silent
    fallback would put every pull request back on an unvouched set, which is exactly what this
    rule exists to prevent.
    """
    notes: list[str] = []
    best: int | None = None
    data = fetch(f"/repos/{repo}/actions/workflows/{SATELLITE_CD_WORKFLOW}/runs"
                 f"?branch=main&event=push&status=success&per_page={limit}")
    runs = list(data.get("workflow_runs") or [])
    if not runs:
        notes.append(f"no successful push run of {SATELLITE_CD_WORKFLOW} on {repo} main in the "
                     f"newest {limit} — main has published no passing run to follow")
        return None, notes
    for run in runs:
        run_id = int(run["id"])
        jobs = [j for j in run_jobs_of(fetch, repo, run_id) if j.get("name") == PLATFORM_REF_JOB]
        if not jobs:
            notes.append(f"main run {run_id}: no `{PLATFORM_REF_JOB}` job — skipped")
            continue
        try:
            annotations = fetch(f"/repos/{repo}/check-runs/{int(jobs[0]['id'])}/annotations")
        except ResolutionError as error:
            notes.append(f"main run {run_id}: annotations unreadable ({error}) — skipped")
            continue
        rows = annotations if isinstance(annotations, list) else annotations.get("annotations") or []
        # 🚨 EVERY matching annotation is read, and DISAGREEMENT is a refusal (#1826). This used to
        # take the FIRST match and break — and the list it reads is one the job's own self-test
        # steps write into: `test-platform-resolution.py` went through `emit`, so its FIXTURE set
        # ids (`3.0.0-ci.8207 — core aaaaaaaaa`) were published under this very title. It answered
        # correctly only because the verdict happened to sort first, which the API does not promise
        # and which is not even emission order. Picking a different one of them is not the fix:
        # if a run publishes two different sets under the production title, its own verdict cannot
        # be told from a fixture, so the RUN is skipped and said. `best` is a max over the other
        # runs examined, so one poisoned run costs a data point, never a wrong ceiling.
        named = [int(match.group(2))
                 for row in rows if NOTICE_TITLE in str(row.get("title") or "")
                 for match in [NOTICE_SET.search(str(row.get("message") or ""))] if match]
        distinct = sorted(set(named))
        if len(distinct) > 1:
            notes.append(
                f"main run {run_id}: {len(distinct)} DIFFERENT sets published under "
                f"`{NOTICE_TITLE}` ({', '.join(f'#{n}' for n in distinct)}) — a run's own verdict "
                "cannot be told from another annotation on the same job, so this run is SKIPPED "
                "rather than guessed at (#1826). A self-test or probe that emits under the "
                "production title is the usual cause; it must use a title of its own.")
            continue
        found = distinct[0] if distinct else None
        if found is None:
            notes.append(f"main run {run_id}: no `{NOTICE_TITLE}` annotation — skipped")
            continue
        notes.append(f"main run {run_id} passed on core CD #{found}")
        best = found if best is None else max(best, found)
        if len(notes) >= limit:
            break
    if best is None:
        # 🚨 Say WHEN the runs examined are from, because the one cause of this refusal that is not
        # the caller's fault is invisible without it. Measured 2026-09-14 on MeshWeaver.Plugins
        # (run 34822109263): GitHub's `status=success` listing served a page from 2026-08-19 for
        # ONE call — twelve runs that all predate this job's existence — while the same query
        # issued seconds later returned the real newest runs. Every one of the twelve was
        # correctly skipped ("no `Resolve the released platform` job"), the refusal was right, and
        # the reader still spent five minutes establishing that the listing was stale rather than
        # main being broken. This is NOT a retry: a resolver that decides its own input must be
        # wrong and asks again is a gate testing its own inputs. It is one line so the next
        # reader knows which of the two things the red means, and re-runs the job.
        newest = max((str(r.get("created_at") or "") for r in runs), default="")
        notes.append(
            f"none of the {len(runs)} successful main run(s) named the set it resolved "
            f"(newest run examined was created {newest or 'at an unknown time'}). If a "
            f"successful `{SATELLITE_CD_WORKFLOW}` push run on main exists that is NEWER than that, GitHub's "
            "run listing served a stale page for this call — re-run this job; the resolver does "
            "not retry on its own, because a run listing is its input and a gate never tests its "
            "own inputs.")
    return best, notes


# ───────────────────────────────── the seal verdict ──────────────────────────────────────

class Verdict(NamedTuple):
    sealed: bool             # the platform trio (promote, verify, platform bake) all succeeded
    pending: bool            # a trio job has not finished yet (and none has failed)
    reasons: list[str]       # why the platform is not sealed, one line per trio job not `success`
    plugins: str             # this run's OWN Plugins seal: success / failure / skipped / in progress / absent
    plugins_pending: bool    # its own Plugins seal is still running (or not created yet, run in flight)
    plugins_sealed_at: str   # ISO `completed_at` of the seal group when it succeeded, else ""


def _group(jobs: list[dict], prefix: str) -> str:
    rows = [j for j in jobs if str(j.get("name", "")).startswith(prefix)]
    if not rows:
        return "absent"
    failed = {str(j.get("conclusion") or "unknown") for j in rows
              if j.get("status") == "completed" and j.get("conclusion") != "success"}
    if failed:
        return ", ".join(sorted(failed))
    if any(j.get("status") != "completed" for j in rows):
        return "in progress"
    return "success"


def _completed_at(jobs: list[dict], prefix: str) -> str:
    stamps = [str(j.get("completed_at") or "") for j in jobs
              if str(j.get("name", "")).startswith(prefix)]
    return max(stamps) if stamps else ""


def verdict(jobs: list[dict], run: dict) -> Verdict:
    """The platform seal is the TRIO and nothing else (maintainer rule, 2026-09-12). The run's own
    Plugins seal is reported beside it and holds the set back only while it is still running —
    the bounded window in which the publication does not exist yet (see the module docstring)."""
    in_flight = run.get("status") != "completed"
    reasons: list[str] = []
    pending = False
    terminal_failure = False
    for label, prefix in REQUIRED_JOBS:
        state = _group(jobs, prefix)
        if state == "success":
            continue
        # A run still in flight may not have created the job yet: absent is pending, not terminal.
        pending |= state == "in progress" or (state == "absent" and in_flight)
        terminal_failure |= state not in ("in progress", "absent")
        reasons.append(f"{label} (`{prefix}`) {state}")
    plugins = _group(jobs, PLUGINS_SEAL_JOB[1])
    plugins_pending = plugins == "in progress" or (plugins == "absent" and in_flight)
    return Verdict(not reasons, pending and not terminal_failure, reasons, plugins,
                   plugins_pending,
                   _completed_at(jobs, PLUGINS_SEAL_JOB[1]) if plugins == "success" else "")


# ───────────────────────────────── the registry ───────────────────────────────────────────

ACCEPT = ",".join((
    "application/vnd.docker.distribution.manifest.list.v2+json",
    "application/vnd.oci.image.index.v1+json",
    "application/vnd.docker.distribution.manifest.v2+json",
    "application/vnd.oci.image.manifest.v1+json",
))


def registry_resolver(user: str, password: str) -> Resolve:
    """`<registry>/<repo>:<tag>` → its manifest digest, or None when the tag does not exist.
    PULL scope only — the credential CI holds answered 401 to `metadata_read` (run 33495452756),
    and pull is the stronger question anyway: it proves the bytes are fetchable."""
    basic = base64.b64encode(f"{user}:{password}".encode()).decode()

    def resolve(image: str, tag: str) -> str | None:
        registry, _, repo = image.partition("/")
        # The documented GHCR override uses /token and may return `token`. Keep credentials
        # on the caller's registry host; never forward Basic auth to an arbitrary challenge realm.
        token_path = "/token" if registry == "ghcr.io" else "/oauth2/token"
        last: Exception | None = None
        for attempt in range(3):
            phase = "token endpoint"
            try:
                url = (f"https://{registry}{token_path}?" + urllib.parse.urlencode(
                    {"service": registry, "scope": f"repository:{repo}:pull"}))
                request = urllib.request.Request(url, headers={"Authorization": f"Basic {basic}"})
                with urllib.request.urlopen(request, timeout=30) as response:
                    data = json.load(response)
                token = data.get("access_token") or data.get("token")
                if not isinstance(token, str) or not token.strip():
                    raise ResolutionError(f"{image}:{tag}: token endpoint answered no usable pull token")
                request = urllib.request.Request(
                    f"https://{registry}/v2/{repo}/manifests/{urllib.parse.quote(tag)}",
                    headers={"Authorization": f"Bearer {token}", "Accept": ACCEPT}, method="HEAD")
                phase = "manifest"
                with urllib.request.urlopen(request, timeout=30) as response:
                    digest = response.headers.get("Docker-Content-Digest", "")
                if not digest.startswith("sha256:"):
                    raise ResolutionError(f"{image}:{tag} answered no Docker-Content-Digest header")
                return digest
            except urllib.error.HTTPError as error:
                if error.code == 404 and phase == "manifest":
                    return None                       # the tag is not there: absent, not an error
                raise ResolutionError(
                    f"{image}:{tag} {phase} → HTTP {error.code}. A 401/403 is the registry credential "
                    "(ACR_USERNAME / ACR_PASSWORD) — not a missing release.") from error
            except ResolutionError:
                raise
            except Exception as error:                # noqa: BLE001 — transport
                last = error
                if attempt < 2:
                    time.sleep(2 * (attempt + 1))
        raise ResolutionError(f"{image}:{tag}: {last}")
    return resolve


def resolve_images(resolve: Resolve, tester: str, portal: str, version: str | None,
                   short_sha: str, run_number: int, migration: str | None = None) -> tuple[dict[str, str], str] | str:
    """Required digests of one promoted set, or the reason they could not be had. The version tag is
    tried in both historical shapes, then the identity tag promote writes in phase A."""
    tags = [f"{version}-ci.{run_number}", f"{version}.ci.{run_number}"] if version else []
    out: dict[str, str] = {}
    via = ""
    images = [(tester, "image-digest"), (portal, "portal-image-digest")]
    if migration:
        images.append((migration, "migration-image-digest"))
    for image, key in images:
        found = None
        for tag in [t for t in tags] + [short_sha]:
            digest = resolve(image, tag)
            if digest:
                found, via = digest, tag
                break
        if not found:
            return (f"{image} carries neither a version tag nor the identity tag `{short_sha}` "
                    "for this set — purged by retention (MeshWeaver#3438) or never promoted")
        out[key] = found
    return out, via


# ───────────────────────────────── the choice ─────────────────────────────────────────────

class PluginsPublication(NamedTuple):
    """The newest sealed Plugins publication the GitHub API can see: the newest core CD run whose
    `Plugins: bake + seal …` job succeeded. It may be OLDER than the chosen core set."""
    run_number: int
    run_url: str
    set_name: str
    sealed_at: str


class Chosen(NamedTuple):
    sha: str
    run_number: int
    run_url: str
    set_name: str
    digests: dict[str, str]
    plugins: str                              # the chosen run's OWN Plugins seal verdict
    source: str
    publication: PluginsPublication | None = None   # the newest sealed publication, found on its own
    # Why a NEWER sealed set was not taken, naming both set ids. Empty unless the optional main
    # ceiling (`--passed-on-main`) held this run back — so it answers "why did my core fix not
    # show up in my PR?" from the log alone, and is inert for every caller that does not use it.
    lag: str = ""


def parse_freeze(value: str) -> tuple[str, str]:
    value = value.strip()
    if SHA.match(value):
        return "sha", value
    match = SET_NAME.match(value)
    if match:
        return "set", f"{match.group(1)}-ci.{int(match.group(2))}"
    raise ResolutionError(
        f"the freeze `{value}` is neither a 40-character core commit nor a set name of the shape "
        "`X.Y.Z[-prerelease]-ci.<n>` (also accepting `.ci.<n>`). A freeze names ONE sealed "
        "platform set exactly; it is not a branch and not a prefix.")


def choose(fetch: Fetch, resolve: Resolve | None, tester: str, portal: str,
           wait_for_seal: float = 0.0, freeze: str | None = None,
           sleep: Callable[[float], None] = time.sleep, log: Callable[[str], None] = print,
           now: Callable[[], float] = time.time, migration: str | None = None,
           passed_ceiling: int | None = None, verify_source: bool = False) -> Chosen:
    """`passed_ceiling` — OPTIONAL, and `None` (every caller that does not ask for it) leaves this
    function on exactly the path it took before the option existed. When given, it is the highest
    core-CD run number the CALLING repository's own `main` has passed on: a sealed set NEWER than
    it is passed over and said so, so a set that regresses that repository reds `main` alone
    instead of every open pull request (see `main_passed_ceiling`). A freeze overrides it, because
    a freeze is an instruction rather than a preference.

    `verify_source` — OPTIONAL, and `False` (every caller that does not ask) reads no job log and
    keeps taking the run's `head_sha` plus the version `Directory.Build.props` declares at it. When
    set, the set's sha and release come from the platform bake's own final publication receipt
    instead, and a set whose receipt is missing, duplicated, malformed or inconsistent is PASSED
    OVER with the reason recorded rather than taken unattributed (see `publication_source`)."""
    freeze_kind = freeze_value = None
    if freeze:
        freeze_kind, freeze_value = parse_freeze(freeze)
        log(f"freeze requested: {freeze_kind} {freeze_value} (repo VARIABLE MW_PLATFORM_REF)")

    deadline = now() + wait_for_seal
    examined = 0
    skipped: list[str] = []
    newer_than_main: str | None = None     # the newest sealed set main has NOT passed, if any
    receipts: dict[tuple[int, int], PublicationSource] = {}   # one log read per bake job, at most
    chosen: Chosen | None = None
    publication: PluginsPublication | None = None
    lookback = 0
    for page in range(1, MAX_RUN_PAGES + 1):
        if chosen is not None and (publication is not None or lookback >= PLUGINS_LOOKBACK):
            break
        runs = cd_runs(fetch, page)
        if not runs:
            break
        # 🚨 PAGE 1 IS WHERE "NEWEST" IS DECIDED, so it is the page checked (#4433). A freeze names
        # one set and must keep working in an incident, so it is not refused here.
        why_stale = stale_listing(runs, passed_ceiling, now()) if page == 1 and not freeze_kind else None
        if why_stale:
            raise ResolutionError(
                f"GitHub served a STALE run listing (MeshWeaver#4433): page 1 of {CORE_CD_WORKFLOW} "
                f"runs on {CORE_REPO} {CORE_BRANCH} cannot be the newest — {why_stale}. Resolving "
                "from it would take an old set and report it as the newest (measured 2026-09-15: "
                "page 1 began ~260 runs behind, twice, and a re-read minutes later was correct). "
                "Re-run this job; the resolver refuses rather than re-reading, because this red is "
                "the harmless answer and a silently old platform is not.")
        for run in runs:
            if run.get("head_branch") not in (None, CORE_BRANCH):
                continue
            number = int(run["run_number"])
            sha = str(run["head_sha"])
            label = f"main-cd #{number} (core {sha[:9]})"

            # ── the core set is chosen: keep reading OLDER runs only for the Plugins publication ──
            if chosen is not None:
                if publication is not None or lookback >= PLUGINS_LOOKBACK:
                    break
                lookback += 1
                jobs = run_jobs(fetch, int(run["id"]))
                v = verdict(jobs, run)
                if v.plugins == "success":
                    version = platform_version(fetch, sha, log=log)
                    publication = PluginsPublication(
                        number, str(run.get("html_url", "")),
                        f"{version}-ci.{number}" if version else f"ci.{number} (line unknown)",
                        v.plugins_sealed_at)
                    log(f"  plugins publication: {label} sealed it at {v.plugins_sealed_at or '?'}"
                        f" ({publication.set_name}) — an older set than the platform chosen")
                continue

            # 🚨 Only when the head sha IS the answer. Under `--verify-source` the set's real sha
            # is the bake receipt's, which is not known until the run's jobs have been read — so
            # filtering on `head_sha` here would drop exactly the runs the option exists to
            # attribute correctly. The same check is re-applied below, against the receipt.
            if freeze_kind == "sha" and not verify_source and sha != freeze_value:
                continue
            if freeze_kind == "set" and number != int(SET_NAME.fullmatch(freeze_value).group(2)):
                continue
            examined += 1

            # 🚨 DOES THE FREEZE NAME *THIS* RUN? (#4242) Every "a freeze is an instruction, not a
            # preference" escalation below must be asked about the run the freeze NAMES, never
            # about whatever run the scan happens to be on.
            #
            # It used to be spelled `if freeze_kind:` — correct only because the two filters above
            # had already narrowed the scan to one run. `--verify-source` deliberately does NOT
            # apply the head-sha filter (the set's real sha is the bake receipt's, unknown until
            # the jobs are read), so the scan reaches runs the freeze does not name — and the FIRST
            # unsealed one aborted the whole resolution with a sentence that was simply false:
            #
            #   --freeze 7ee11bc7… --verify-source
            #   ::error:: the freeze names main-cd #8530 (core e0e4aeff3), which is not a sealed set
            #
            # 7ee11bc7 is the head of #8506. #8530 was merely the newest run in the scan. Measured
            # against live core CD, 2026-09-13 — and it makes --verify-source unusable during an
            # incident freeze, which is exactly when resolution has to keep working.
            #
            # 🚨 It NARROWS and never widens. A set freeze is already down to one run number, and a
            # sha freeze without verification is already down to one head sha; both keep answering
            # exactly as before. The only case that changes is a sha freeze WITH verification, where
            # the honest answer for a run that cannot produce a receipt is "no evidence that this is
            # the frozen run" — so the scan continues, and if no run's receipt matches, the terminal
            # "the freeze matched no verified sealed set" says that, which is true.
            freeze_names_this_run = bool(freeze_kind) and (
                freeze_kind == "set" or not verify_source or sha == freeze_value)

            jobs = run_jobs(fetch, int(run["id"]))
            v = verdict(jobs, run)
            # A run that is still sealing is worth waiting for on a release trigger: the dispatch
            # arrives the moment `promote` finishes, minutes before the platform bake seals — and
            # the Plugins seal for the same identity lands minutes after that.
            while (v.pending or (v.sealed and v.plugins_pending)) \
                    and run.get("status") != "completed" and now() < deadline:
                remaining = int(deadline - now())
                what = "; ".join(v.reasons) if v.reasons else "its Plugins publication is sealing"
                log(f"  {label} is still sealing ({what}) — waiting up to {remaining}s for it")
                sleep(min(POLL_SECONDS, max(remaining, 1)))
                fresh = fetch(f"/repos/{CORE_REPO}/actions/runs/{run['id']}")
                run = {**run, **{k: fresh[k] for k in ("status", "conclusion") if k in fresh}}
                jobs = run_jobs(fetch, int(run["id"]))
                v = verdict(jobs, run)
            # The Plugins publication is found on its own, over the same runs: the newest one whose
            # seal succeeded — even where the platform trio did not (a red platform bake beside a
            # green seal leaves a publication for that identity, and it is newer than the set
            # chosen below). Recorded once; the props read is shared with the set name below.
            version: str | None | Ellipsis = ...  # type: ignore[valid-type]
            if publication is None and v.plugins == "success":
                version = platform_version(fetch, sha, log=log)
                publication = PluginsPublication(
                    number, str(run.get("html_url", "")),
                    f"{version}-ci.{number}" if version else f"ci.{number} (line unknown)",
                    v.plugins_sealed_at)
                log(f"  plugins publication: {label} sealed it at {v.plugins_sealed_at or '?'}"
                    f" ({publication.set_name})")
            if not v.sealed:
                why = "; ".join(v.reasons)
                if run.get("status") == "completed" and run.get("conclusion") != "success" \
                        and all(_group(jobs, prefix) == "absent"
                                for _, prefix in (*REQUIRED_JOBS, PLUGINS_SEAL_JOB)):
                    why = (f"the run itself was {run.get('conclusion')} before any seal job ran "
                           "(superseded by a newer commit, or a build leg failed)")
                if v.pending:
                    why += " — still sealing; the next run (or the daily poll) follows it"
                skipped.append(f"{label}: NOT sealed — {why}")
                log(f"  skip {skipped[-1]}")
                if freeze_names_this_run:
                    raise ResolutionError(
                        f"the freeze names {label}, which is not a sealed set ({why}). A freeze is "
                        "an instruction, not a preference: refusing to substitute another set.")
                continue
            # 🚨 The ONE bounded exception: platform sealed, its own Plugins seal still running.
            # The publication for this identity does not exist yet (minutes); taking the set now
            # is a certain red on the upstream fetch. A freeze is an instruction and goes through.
            if v.plugins_pending and not freeze_names_this_run:
                skipped.append(f"{label}: platform sealed, but its Plugins publication is still "
                               f"sealing (`{PLUGINS_SEAL_JOB[1]}` {v.plugins}) — the newest set "
                               "with a sealed publication is taken; the next run follows this one")
                log(f"  skip {skipped[-1]}")
                continue
            if v.plugins_pending and freeze_names_this_run:
                log(f"  ::warning title=Frozen set is still sealing its plugins::{label}: the freeze "
                    "names a set whose Plugins publication is still sealing; taken as instructed — "
                    "the upstream fetch says whether the registry holds it yet")

            if verify_source:
                # The publication's OWN statement of what it published. A set that cannot make it
                # is passed over, not taken unattributed; under a freeze it is fatal, because a
                # freeze names one set and may never substitute another.
                try:
                    sha, version, set_name = publication_source(fetch, jobs, number, receipts)
                except ProvenanceUnavailable as error:
                    skipped.append(f"{label}: source/release unverified — {error}")
                    log(f"  skip {skipped[-1]}")
                    # 🚨 The same narrowing, and here it is load-bearing twice over: measured on
                    # live core CD 2026-09-13, ELEVEN of the newest FOURTEEN main-cd runs raise
                    # ProvenanceUnavailable. Escalating on `freeze_kind` alone would abort a sha
                    # freeze on the first of them, which is almost always a run the freeze does not
                    # name. (That the rate is 11/14 at all is a separate bake-side defect, filed on
                    # its own — it is not this bug.)
                    if freeze_names_this_run:
                        raise ResolutionError(
                            f"the freeze names {label}, but its source/release is unverified: "
                            f"{error}. Refusing to substitute another set.") from error
                    continue
                label = f"main-cd #{number} (core {sha[:9]} from the final platform bake)"
                if freeze_kind == "sha" and sha != freeze_value:
                    continue
            else:
                if version is ...:
                    version = platform_version(fetch, sha, log=log)
                set_name = f"{version}-ci.{number}" if version else f"ci.{number} (line unknown)"
            if freeze_kind == "set" and set_name != freeze_value:
                raise ResolutionError(
                    f"the freeze names {freeze_value}, but {label} resolves to {set_name}. "
                    "Refusing a different or unverifiable release version.")
            # 🚨 SEALED IS NOT ENOUGH WHEN THE CALLER FOLLOWS ITS OWN MAIN. Take the set only if
            # `main` has already passed on it. Inert when no ceiling was asked for.
            if passed_ceiling is not None and not freeze_kind and number > passed_ceiling:
                if newer_than_main is None:
                    newer_than_main = set_name
                skipped.append(f"{label} = {set_name}: sealed, but this repo's `main` has not "
                               f"passed on it yet (main's newest passed set is core CD "
                               f"#{passed_ceiling})")
                log(f"  skip {skipped[-1]}")
                continue
            digests: dict[str, str] = {}
            if resolve is not None:
                images = resolve_images(resolve, tester, portal, version, sha[:7], number, migration)
                if isinstance(images, str):
                    skipped.append(f"{label} = {set_name}: sealed, but {images}")
                    log(f"  skip {skipped[-1]}")
                    if freeze_kind:
                        raise ResolutionError(
                            f"the freeze names {label} = {set_name}: {images}. Refusing to "
                            "substitute another set.")
                    continue
                digests, via = images
                log(f"  {label} = {set_name}: sealed, images resolved by tag `{via}`")
            else:
                log(f"  {label} = {set_name}: sealed (registry not consulted)")

            source = (f"frozen by the repo VARIABLE MW_PLATFORM_REF={freeze}" if freeze
                      else "the newest sealed platform set this repo's `main` has passed"
                      if passed_ceiling is not None
                      else "the newest sealed platform set")
            if verify_source and not freeze:
                source += "; source verified by the final platform bake"

            if skipped and not freeze:
                source += f" — {len(skipped)} newer run(s) passed over, see the log"
            lag = ""
            if newer_than_main and newer_than_main != set_name:
                lag = (f"this run resolved {set_name}, not the newer sealed {newer_than_main}: "
                       "pull requests follow `main`, and main has not passed on it yet. A core "
                       "change lands here once main's own run goes green on the set carrying it.")
                log(f"  {lag}")
            chosen = Chosen(sha, number, str(run.get("html_url", "")), set_name, digests,
                            v.plugins, source, lag=lag)
            # The publication found on THIS run was named from the props at `head_sha`; when the
            # receipt was read, the verified release is the better name for the same thing.
            if verify_source and publication is not None and publication.run_number == number:
                publication = publication._replace(set_name=set_name)
            if publication is not None:
                break
            log(f"  {label}: its own Plugins seal is `{v.plugins}` — the platform is taken anyway "
                f"(the trio is the seal); looking for the newest sealed Plugins publication in the "
                f"{PLUGINS_LOOKBACK} runs before it")
        if chosen is not None and (publication is not None or lookback >= PLUGINS_LOOKBACK):
            break

    if chosen is not None:
        if publication is None:
            log(f"  no sealed Plugins publication in the {lookback} run(s) before {chosen.set_name} — "
                "the lane's upstream fetch decides, by identity, whether the registry holds one")
        return chosen._replace(publication=publication)

    looked_for = ", ".join(f"`{prefix}`" for _, prefix in REQUIRED_JOBS)
    if freeze_kind:
        raise ResolutionError(
            f"the freeze `{freeze}` matched no {CORE_CD_WORKFLOW} run on {CORE_REPO} {CORE_BRANCH} "
            f"in the newest {MAX_RUN_PAGES * 100} runs. Check the value, or clear the variable.")
    detail = "\n".join(f"  - {s}" for s in skipped) or "  (no run on main examined at all)"
    raise ResolutionError(
        f"no sealed platform set in the newest {examined} {CORE_CD_WORKFLOW} run(s) on "
        f"{CORE_REPO} {CORE_BRANCH} whose images are still in the registry. A sealed set is a "
        f"run whose jobs {looked_for} all succeeded; if core renamed one of those jobs every run "
        "reads `absent` above and the matching REQUIRED_JOBS prefix must follow it. Skipped:\n" + detail)


# ───────────────────────────────── freshness (the baseline) ──────────────────────────────

BASELINE_ENV = "PLATFORM_BASELINE"


def load_baseline(text: str | None) -> dict[str, str] | None:
    """`PLATFORM_BASELINE`: the run's `platform-ref` outputs (`toJSON(needs.platform-ref.outputs)`),
    or nothing. A baseline that names no `run-number` is not a baseline and is said so."""
    if not text or not text.strip() or text.strip() == "null":
        return None
    try:
        data = json.loads(text)
    except ValueError as error:
        raise ResolutionError(f"{BASELINE_ENV} is not JSON: {error}") from error
    if not isinstance(data, dict) or not str(data.get("run-number") or "").isdigit():
        raise ResolutionError(
            f"{BASELINE_ENV} carries no `run-number` — pass toJSON(needs.platform-ref.outputs) of a "
            "platform-ref job that exports it, or unset the variable to resolve without a baseline")
    return {str(k): str(v) for k, v in data.items()}


def refresh(baseline: dict[str, str], choose_fn: Callable[[], Chosen],
            log: Callable[[str], None] = print,
            rows_of: Callable[[Chosen], dict[str, str]] = lambda c: output_rows(c)) -> tuple[dict[str, str], bool]:
    """`(rows, override)` for a consumer job: the FRESH set's rows when it is newer than the
    baseline, else the baseline's rows verbatim. A failed re-resolution keeps the baseline with a
    warning — the API being down is not the platform moving."""
    base_number = int(baseline["run-number"])
    base_set = baseline.get("set", f"ci.{base_number}")
    try:
        chosen = choose_fn()
    except ResolutionError as error:
        text = str(error).replace("\n", " ")[:600]
        log(f"::warning title=Platform re-resolution failed — baseline kept::{text}")
        rows = dict(baseline)
        rows["override"] = "false"
        rows["source"] = f"the run's baseline {base_set} (re-resolution failed, see the warning)"
        return rows, False
    if chosen.run_number > base_number:
        rows = rows_of(chosen)
        rows["override"] = "true"
        rows["baseline-set"] = base_set
        rows["source"] = (f"NEWER than this run's baseline {base_set}: {chosen.set_name} sealed "
                          "since, and a re-run must not test a stale set")
        log(f"::notice title=Platform refreshed::{base_set} → {chosen.set_name} — this job takes "
            "the newer sealed set (Education#320)")
        return rows, True
    rows = dict(baseline)
    rows["override"] = "false"
    why = ("the same set" if chosen.run_number == base_number
           else f"an OLDER set ({chosen.set_name}) — the baseline's images or run went away; the "
                "baseline is kept, one platform per run")
    rows["source"] = f"the run's baseline {base_set} (re-resolved: {why})"
    log(f"  re-resolved {chosen.set_name}: {why}")
    return rows, False


# ───────────────────────────────── outputs ────────────────────────────────────────────────

def output_rows(chosen: Chosen, tester: str = "", portal: str = "",
                migration: str | None = None) -> dict[str, str]:
    pub = chosen.publication
    rows = {
        "sha": chosen.sha,
        "set": chosen.set_name,
        "run-number": str(chosen.run_number),
        "run-url": chosen.run_url,
        "image-digest": chosen.digests.get("image-digest", ""),
        "portal-image-digest": chosen.digests.get("portal-image-digest", ""),
        "plugins-sealed": chosen.plugins,
        "plugins-run": str(pub.run_number) if pub else "",
        "plugins-run-url": pub.run_url if pub else "",
        "plugins-set": pub.set_name if pub else "",
        "plugins-sealed-at": pub.sealed_at if pub else "",
        "override": "false",
        "source": chosen.source,
        # Always present, empty unless the optional main ceiling held this run back — a key that
        # appears only sometimes is one no consumer can read.
        "lag": chosen.lag,
    }
    if migration:
        rows["migration-image-digest"] = chosen.digests.get("migration-image-digest", "")
    if tester and chosen.digests:
        rows["tester-name"] = tester
        rows["tester-image"] = f"{tester}@{rows['image-digest']}"
    if portal and chosen.digests:
        rows["portal-name"] = portal
        rows["portal-image"] = f"{portal}@{rows['portal-image-digest']}"
    if migration and chosen.digests:
        rows["migration-name"] = migration
        rows["migration-image"] = f"{migration}@{rows['migration-image-digest']}"
    return rows


def emit(rows: dict[str, str], title: str = "Platform for this run") -> None:
    for key, value in rows.items():
        print(f"{key}={value}")
    output = os.environ.get("GITHUB_OUTPUT")
    if output:
        with open(output, "a", encoding="utf-8") as handle:
            for key, value in rows.items():
                handle.write(f"{key}={value}\n")
    pub = (f"; plugins publication {rows['plugins-set']} (main-cd #{rows['plugins-run']})"
           if rows.get("plugins-run") else "; no sealed plugins publication seen")
    print(f"::notice title={title}::{rows.get('set', '?')} — core {rows.get('sha', '?')[:9]} "
          f"({rows.get('source', '')}){pub}")
    if rows.get("lag"):
        print(f"::notice title=Platform lag (pull requests follow main)::{rows['lag']}")
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as handle:
            handle.write("### Platform — resolved at run time, not pinned\n\n")
            handle.write("| | core set | plugins publication |\n|---|---|---|\n")
            handle.write(f"| set | `{rows.get('set', '')}` ([main-cd #{rows.get('run-number', '')}]"
                         f"({rows.get('run-url', '')})) | "
                         + (f"`{rows['plugins-set']}` ([main-cd #{rows['plugins-run']}]"
                            f"({rows.get('plugins-run-url', '')})), sealed {rows.get('plugins-sealed-at', '?')}"
                            if rows.get("plugins-run") else "none seen — the upstream fetch decides by identity")
                         + " |\n")
            handle.write(f"| core commit | [`{rows.get('sha', '')[:9]}`](https://github.com/{CORE_REPO}/"
                         f"commit/{rows.get('sha', '')}) — every `{CORE_REPO}` checkout in this run | "
                         f"own seal of the chosen run: {rows.get('plugins-sealed', '')} |\n")
            for key, label in (("tester-image", "tester image"), ("portal-image", "portal image"),
                               ("migration-image", "migration image")):
                if rows.get(key):
                    handle.write(f"| {label} | `{rows[key]}` | |\n")
            handle.write(f"| chosen because | {rows.get('source', '')} | |\n")
            if rows.get("lag"):
                handle.write(f"| lag | {rows['lag']} | |\n")
            if rows.get("override") == "true":
                handle.write(f"| **refreshed** | this job overrode the run's baseline "
                             f"`{rows.get('baseline-set', '')}` — a re-run must not test a stale set | |\n")
            # 🚨 The closing sentence follows the RULE THIS RUN USED. Saying "the newest sealed
            # set is taken on every run" under a lag row would contradict the row above it on the
            # one run where the reader most needs the rule.
            if "`main` has passed" in rows.get("source", ""):
                handle.write("\nThe caller pinned no digest. This run followed `main`: it took the "
                             "newest SEALED set this repository's `main` has already PASSED, so a "
                             "set that regresses this repo reds `main` alone; `main`, the release "
                             "dispatch and the daily poll take the newest sealed set "
                             "(MeshWeaver#3842, MeshWeaver#4171).\n")
            else:
                handle.write("\nThe caller pinned no digest, so the lane took the newest platform-sealed "
                             "set itself — and a re-run re-resolves (maintainer rule 2026-09-12: for "
                             "compile always find latest package of platform and plugins; "
                             "MeshWeaver#3842, MeshWeaver.Education#320).\n")


def write_outputs(chosen: Chosen, tester: str, portal: str, migration: str | None = None) -> None:
    emit(output_rows(chosen, tester, portal, migration))


# ───────────────────────────────── self-test ──────────────────────────────────────────────

def _run(number: int, sha: str, status: str = "completed", conclusion: str = "success",
         branch: str = "main", created_at: str | None = None) -> dict:
    # `created_at` is ABSENT unless a case sets it: a dated default would age past
    # LISTING_MAX_AGE_HOURS on the wall clock and red every case the day after it was written.
    row = {"id": 1000 + number, "run_number": number, "head_sha": sha, "status": status,
           "conclusion": conclusion, "head_branch": branch,
           "html_url": f"https://github.com/{CORE_REPO}/actions/runs/{1000 + number}"}
    if created_at is not None:
        row["created_at"] = created_at
    return row


def _jobs(promote="success", verify="success", bake="success", plugins="success") -> list[dict]:
    def job(name: str, state: str) -> dict:
        if state == "in progress":
            return {"name": name, "status": "in_progress", "conclusion": None}
        if state == "absent":
            return {}
        return {"name": name, "status": "completed", "conclusion": state,
                "completed_at": "2026-09-12T12:00:00Z"}
    rows = [job("Promote: tag the full set (all-or-nothing)", promote),
            job("Verify every image shipped", verify),
            job("Bake platform content in the shipped image + publish (linux-x64)", bake),
            job("Plugins: bake + seal the publication for this identity / Bake + publish", plugins),
            job("Plugins: bake + seal the publication for this identity / Register", plugins),
            {"name": "Assert the inputs exist", "status": "completed", "conclusion": "success"}]
    return [r for r in rows if r]


PROPS = '<Project>\n  <PropertyGroup>\n    <PlatformVersion Condition="\'$(PlatformVersion)\' == \'\'">3.0.0</PlatformVersion>\n  </PropertyGroup>\n</Project>'


def _fetch_for(runs: list[dict], jobs_by_run: dict[int, list[dict]],
               props: str | None = PROPS) -> Fetch:
    def fetch(path: str) -> dict:
        if "/runs?" in path:
            page = int(re.search(r"[?&]page=(\d+)", path).group(1))
            return {"workflow_runs": runs if page == 1 else []}
        if "/jobs" in path:
            run_id = int(re.search(r"/runs/(\d+)/jobs", path).group(1))
            rows = jobs_by_run.get(run_id, [])
            return {"total_count": len(rows), "jobs": rows}
        if "/actions/runs/" in path:
            run_id = int(path.rsplit("/", 1)[1])
            return next(r for r in runs if r["id"] == run_id)
        if "Directory.Build.props" in path:
            if props is None:
                raise ResolutionError("no props")
            return {"content": base64.b64encode(props.encode()).decode()}
        raise AssertionError(path)
    return fetch


def _registry(present: dict[tuple[str, str], str]) -> Resolve:
    def resolve(image: str, tag: str) -> str | None:
        return present.get((image.rsplit("/", 1)[1], tag))
    return resolve


def self_test() -> int:
    tester, portal = DEFAULT_TESTER_IMAGE, DEFAULT_PORTAL_IMAGE
    A = "a" * 40
    B = "b" * 40
    C = "c" * 40
    D1, D2, D3, D4 = "sha256:" + "1" * 64, "sha256:" + "2" * 64, "sha256:" + "3" * 64, "sha256:" + "4" * 64
    full = {("mw-plugin-test", "3.0.0-ci.8207"): D1, ("memex-portal-ai", "3.0.0-ci.8207"): D2,
            ("mw-plugin-test", "3.0.0-ci.8203"): D3, ("memex-portal-ai", "3.0.0-ci.8203"): D4,
            ("mw-plugin-test", A[:7]): D1, ("memex-portal-ai", A[:7]): D2,
            ("mw-plugin-test", B[:7]): D3, ("memex-portal-ai", B[:7]): D4}
    two = [_run(8207, A), _run(8203, B)]
    failures: list[str] = []
    logs: list[str] = []
    total = 0

    def case(name: str, expect_ok: bool, fn: Callable[[], Chosen], check=None) -> None:
        nonlocal total
        total += 1
        logs.clear()
        try:
            chosen = fn()
        except ResolutionError as error:
            if expect_ok:
                failures.append(f"{name}: expected a choice, got RED: {error}")
            elif check and not check(str(error)):
                failures.append(f"{name}: RED as expected but the message does not say why: {error}")
            return
        if not expect_ok:
            failures.append(f"{name}: expected RED, chose {chosen.set_name}")
        elif check and not check(chosen):
            failures.append(f"{name}: chose {chosen.set_name} ({chosen.sha[:9]}) — wrong")

    # 1 — the newest run is sealed: it is the answer, by its version tag.
    case("newest sealed", True,
         lambda: choose(_fetch_for(two, {1000 + 8207: _jobs(), 1000 + 8203: _jobs()}),
                        _registry(full), tester, portal, log=logs.append),
         lambda c: c.sha == A and c.set_name == "3.0.0-ci.8207"
         and c.digests == {"image-digest": D1, "portal-image-digest": D2} and c.plugins == "success")
    # 2 — the newest run is green but published nothing (promote/bake SKIPPED): fall back, say so.
    case("newest green but unsealed → previous, said so", True,
         lambda: choose(_fetch_for(two, {1000 + 8207: _jobs("skipped", "skipped", "skipped", "skipped"),
                                         1000 + 8203: _jobs()}),
                        _registry(full), tester, portal, log=logs.append),
         lambda c: c.sha == B and "passed over" in c.source
         and any("#8207" in l and "NOT sealed" in l and "skipped" in l for l in logs))
    # 3 — the newest run is still sealing and nobody waits: fall back, say so.
    case("newest still sealing, no wait → previous", True,
         lambda: choose(_fetch_for([_run(8207, A, status="in_progress", conclusion=None), _run(8203, B)],
                                   {1000 + 8207: _jobs(bake="in progress"), 1000 + 8203: _jobs()}),
                        _registry(full), tester, portal, log=logs.append),
         lambda c: c.sha == B and any("still sealing" in l for l in logs))
    # 4 — …and on a release trigger it is waited for, and taken once the seal lands.
    state = {"polls": 0}

    def flipping_fetch() -> Fetch:
        runs = [_run(8207, A, status="in_progress", conclusion=None), _run(8203, B)]
        base = _fetch_for(runs, {1000 + 8207: _jobs(bake="in progress"), 1000 + 8203: _jobs()})

        def fetch(path: str) -> dict:
            if f"/runs/{1000 + 8207}/jobs" in path and state["polls"] >= 2:
                return {"total_count": 6, "jobs": _jobs()}
            if path.endswith(f"/actions/runs/{1000 + 8207}"):
                state["polls"] += 1
                return {**runs[0], "status": "completed" if state["polls"] >= 2 else "in_progress",
                        "conclusion": "success" if state["polls"] >= 2 else None}
            return base(path)
        return fetch
    clock = {"t": 0.0}
    case("newest still sealing, waited for → taken", True,
         lambda: choose(flipping_fetch(), _registry(full), tester, portal, wait_for_seal=600,
                        sleep=lambda s: clock.__setitem__("t", clock["t"] + s),
                        now=lambda: clock["t"], log=logs.append),
         lambda c: c.sha == A and state["polls"] >= 2)
    # 5 — the newest set's images were purged: the next sealed set whose images exist is taken.
    purged = {k: v for k, v in full.items() if k[1] not in ("3.0.0-ci.8207", A[:7])}
    case("newest images purged → previous, said so", True,
         lambda: choose(_fetch_for(two, {1000 + 8207: _jobs(), 1000 + 8203: _jobs()}),
                        _registry(purged), tester, portal, log=logs.append),
         lambda c: c.sha == B and any("purged" in l and "#8207" in l for l in logs))
    # 6 — no version tag (line unreadable) but the identity tag exists: taken by sha tag.
    sha_only = {k: v for k, v in full.items() if "ci." not in k[1]}
    case("version unknown → identity tag", True,
         lambda: choose(_fetch_for(two, {1000 + 8207: _jobs(), 1000 + 8203: _jobs()}, props=None),
                        _registry(sha_only), tester, portal, log=logs.append),
         lambda c: c.sha == A and "line unknown" in c.set_name and c.digests["image-digest"] == D1)
    # 7 — nothing sealed at all: RED, naming the jobs looked for and every skip.
    case("nothing sealed → RED naming the seal", False,
         lambda: choose(_fetch_for(two, {1000 + 8207: _jobs(promote="failure"),
                                         1000 + 8203: _jobs(verify="cancelled")}),
                        _registry(full), tester, portal, log=logs.append),
         lambda msg: "Promote: tag the full set" in msg and "#8207" in msg and "#8203" in msg)
    # 8 — core renamed its CD jobs: every run reads absent, RED says so.
    case("seal jobs renamed → RED says absent", False,
         lambda: choose(_fetch_for(two, {1000 + 8207: _jobs(promote="absent"),
                                         1000 + 8203: _jobs(promote="absent")}),
                        _registry(full), tester, portal, log=logs.append),
         lambda msg: "absent" in msg and "REQUIRED_JOBS" in msg)
    # 9 — the API is unreachable: RED, not a silent green.
    def refusing(path: str) -> dict:
        raise ResolutionError(f"GET {path} → HTTP 503")
    case("API down → RED", False,
         lambda: choose(refusing, _registry(full), tester, portal, log=logs.append),
         lambda msg: "503" in msg)
    # 10 — a credential problem at the registry is an error, never "absent".
    def unauthorised(image: str, tag: str) -> str | None:
        raise ResolutionError(f"{image}:{tag} → HTTP 401")
    case("registry 401 → RED", False,
         lambda: choose(_fetch_for(two, {1000 + 8207: _jobs(), 1000 + 8203: _jobs()}),
                        unauthorised, tester, portal, log=logs.append),
         lambda msg: "401" in msg)
    # 11 — freezes: by sha, by set name, on an unsealed run, malformed, unknown.
    case("freeze by sha", True,
         lambda: choose(_fetch_for(two, {1000 + 8207: _jobs(), 1000 + 8203: _jobs()}),
                        _registry(full), tester, portal, freeze=B, log=logs.append),
         lambda c: c.sha == B and "frozen" in c.source)
    case("freeze by set name", True,
         lambda: choose(_fetch_for(two, {1000 + 8207: _jobs(), 1000 + 8203: _jobs()}),
                        _registry(full), tester, portal, freeze="3.0.0-ci.8203", log=logs.append),
         lambda c: c.sha == B)
    case("freeze on an unsealed run → RED, no substitution", False,
         lambda: choose(_fetch_for(two, {1000 + 8207: _jobs(bake="skipped"), 1000 + 8203: _jobs()}),
                        _registry(full), tester, portal, freeze=A, log=logs.append),
         lambda msg: "freeze" in msg and "not a sealed set" in msg)
    case("freeze with purged images → RED, no substitution", False,
         lambda: choose(_fetch_for(two, {1000 + 8207: _jobs(), 1000 + 8203: _jobs()}),
                        _registry(purged), tester, portal, freeze=A, log=logs.append),
         lambda msg: "freeze" in msg and "purged" in msg)
    case("freeze malformed → RED", False,
         lambda: choose(_fetch_for(two, {}), _registry(full), tester, portal, freeze="main",
                        log=logs.append),
         lambda msg: "neither a 40-character" in msg)
    case("freeze names an unknown run → RED", False,
         lambda: choose(_fetch_for(two, {1000 + 8207: _jobs(), 1000 + 8203: _jobs()}),
                        _registry(full), tester, portal, freeze=C, log=logs.append),
         lambda msg: "matched no" in msg)
    # 12 — a run on another branch is never a candidate; --no-registry answers without digests.
    case("other-branch runs ignored", True,
         lambda: choose(_fetch_for([_run(8210, C, branch="release/x"), *two],
                                   {1000 + 8210: _jobs(), 1000 + 8207: _jobs(), 1000 + 8203: _jobs()}),
                        _registry(full), tester, portal, log=logs.append),
         lambda c: c.sha == A)
    case("--no-registry → sha and set only", True,
         lambda: choose(_fetch_for(two, {1000 + 8207: _jobs(), 1000 + 8203: _jobs()}),
                        None, tester, portal, log=logs.append),
         lambda c: c.sha == A and c.digests == {})
    # 13 — THE RULE (maintainer, 2026-09-12): the platform trio is the seal. A run whose OWN
    # Plugins seal failed / was skipped / cancelled / never ran is still the newest platform; the
    # Plugins publication is found on its own, in an older run, and reported beside it.
    for seal in ("absent", "failure", "skipped", "cancelled"):
        case(f"newest sealed, own plugins seal {seal} → chosen; publication from the previous run", True,
             lambda seal=seal: choose(
                 _fetch_for(two, {9207: _jobs(plugins=seal), 9203: _jobs()}),
                 _registry(full), tester, portal, log=logs.append),
             lambda c: c.sha == A and c.plugins in ("absent", "failure", "skipped", "cancelled")
             and c.publication is not None and c.publication.run_number == 8203
             and c.publication.set_name == "3.0.0-ci.8203" and c.publication.sealed_at
             and any("plugins publication" in line and "#8203" in line for line in logs))
    # 14 — …but a seal STILL RUNNING is the bounded exception: the publication does not exist yet,
    # so the set is passed over (no wait) and SAID.
    case("newest sealed, own plugins seal in progress, no wait → previous, said so", True,
         lambda: choose(_fetch_for([_run(8207, A, status="in_progress", conclusion=None), two[1]],
                                   {9207: _jobs(plugins="in progress"), 9203: _jobs()}),
                        _registry(full), tester, portal, log=logs.append),
         lambda c: c.sha == B and c.publication is not None and c.publication.run_number == 8203
         and any("still sealing" in line and "#8207" in line for line in logs))
    # 15 — the run's CONCLUSION no longer decides: a red satellite-compat leg or alert job with a
    # green trio + seal is a released platform.
    for status, conclusion in (("completed", "failure"), ("completed", "cancelled"),
                               ("completed", None), ("in_progress", None)):
        case(f"trio + seal green, whole CD {status}/{conclusion} → chosen", True,
             lambda status=status, conclusion=conclusion: choose(
                 _fetch_for([_run(8207, A, status=status, conclusion=conclusion), two[1]],
                            {9207: _jobs(), 9203: _jobs()}),
                 _registry(full), tester, portal, log=logs.append),
             lambda c: c.sha == A and c.publication is not None and c.publication.run_number == 8207)
    # 16 — the Plugins publication can be NEWER than the core set: promote + seal green, platform
    # bake red ⇒ the platform is the previous run, the publication is this one.
    case("plugins publication newer than the core set", True,
         lambda: choose(_fetch_for(two, {9207: _jobs(bake="failure"), 9203: _jobs()}),
                        _registry(full), tester, portal, log=logs.append),
         lambda c: c.sha == B and c.set_name == "3.0.0-ci.8203"
         and c.publication is not None and c.publication.run_number == 8207
         and c.publication.set_name == "3.0.0-ci.8207")
    # 17 — no sealed publication anywhere: the platform is still chosen, and it is SAID that the
    # upstream fetch decides by identity.
    case("no sealed plugins publication anywhere → chosen, said so", True,
         lambda: choose(_fetch_for(two, {9207: _jobs(plugins="failure"), 9203: _jobs(plugins="skipped")}),
                        _registry(full), tester, portal, log=logs.append),
         lambda c: c.sha == A and c.publication is None
         and any("no sealed Plugins publication" in line for line in logs))
    # 18 — a freeze on a run without its own seal goes through (an instruction), reporting it.
    case("freeze on a run whose own plugins seal is absent → taken, reported", True,
         lambda: choose(_fetch_for(two, {9207: _jobs(plugins="absent"), 9203: _jobs()}),
                        _registry(full), tester, portal, freeze=A, log=logs.append),
         lambda c: c.sha == A and c.plugins == "absent" and "frozen" in c.source)
    case("freeze on a run whose plugins seal is still running → taken, warned", True,
         lambda: choose(_fetch_for([_run(8207, A, status="in_progress", conclusion=None), two[1]],
                                   {9207: _jobs(plugins="in progress"), 9203: _jobs()}),
                        _registry(full), tester, portal, freeze=A, log=logs.append),
         lambda c: c.sha == A and any("::warning" in line and "still sealing" in line for line in logs))

    # 19 — on a release trigger the seal that is still running is WAITED for, then taken.
    ticks = {"polls": 0, "time": 0.0}
    waiting_run = _run(8207, A, status="in_progress", conclusion=None)
    waiting_base = _fetch_for([waiting_run, two[1]],
                              {9207: _jobs(plugins="absent"), 9203: _jobs()})
    def upstream_finishes(path: str) -> dict:
        if path.endswith("/actions/runs/9207"):
            ticks["polls"] += 1
            done = ticks["polls"] >= 2
            return {**waiting_run, "status": "completed" if done else "in_progress",
                    "conclusion": "success" if done else None}
        if "/runs/9207/jobs" in path and ticks["polls"] >= 2:
            return {"total_count": len(_jobs()), "jobs": _jobs()}
        return waiting_base(path)
    case("plugins seal absent while the run is in flight, waited for → taken once sealed", True,
         lambda: choose(upstream_finishes, _registry(full), tester, portal, wait_for_seal=90,
                        sleep=lambda seconds: ticks.__setitem__("time", ticks["time"] + seconds),
                        now=lambda: ticks["time"], log=logs.append),
         lambda c: c.sha == A and ticks["polls"] == 2 and c.plugins == "success"
         and c.publication is not None and c.publication.run_number == 8207)

    # 20 — a TERMINAL seal never waits, even on a release trigger: the platform is taken.
    def unexpected_wait(seconds: float) -> None:
        raise ResolutionError("waited after a required job already failed")
    for seal in ("failure", "skipped", "cancelled"):
        case(f"terminal plugins seal {seal} is taken without waiting", True,
             lambda seal=seal: choose(
                 _fetch_for([waiting_run, two[1]],
                            {9207: _jobs(plugins=seal), 9203: _jobs()}),
                 _registry(full), tester, portal, wait_for_seal=90,
                 sleep=unexpected_wait, log=logs.append),
             lambda c: c.sha == A and c.publication is not None and c.publication.run_number == 8203)
    mixed_jobs = _jobs(plugins="in progress")
    mixed_jobs.append({"name": PLUGINS_SEAL_JOB[1] + " / failed target",
                       "status": "completed", "conclusion": "failure"})
    case("a failed seal target outranks a pending one — terminal, taken without waiting", True,
         lambda: choose(_fetch_for([waiting_run, two[1]],
                                   {9207: mixed_jobs, 9203: _jobs()}),
                        _registry(full), tester, portal, wait_for_seal=90,
                        sleep=unexpected_wait, log=logs.append),
         lambda c: c.sha == A and c.plugins == "failure")
    case("failed run before all seal jobs says why", True,
         lambda: choose(_fetch_for([_run(8207, A, conclusion="failure"), two[1]],
                                   {9207: [], 9203: _jobs()}),
                        _registry(full), tester, portal, log=logs.append),
         lambda c: c.sha == B and any("before any seal job ran" in line for line in logs))
    # A run in flight whose trio jobs do not exist yet is PENDING (waited for / passed over), not
    # terminal — the release dispatch can arrive before core has created them.
    case("trio absent while the run is in flight → pending, passed over without a wait", True,
         lambda: choose(_fetch_for([_run(8207, A, status="in_progress", conclusion=None), two[1]],
                                   {9207: [], 9203: _jobs()}),
                        _registry(full), tester, portal, log=logs.append),
         lambda c: c.sha == B and any("still sealing" in line and "#8207" in line for line in logs))

    # 21 — FRESHNESS (Education#320): a consumer job re-resolves against the run's baseline.
    fresh_two = _fetch_for(two, {9207: _jobs(), 9203: _jobs()})
    baseline_old = {"run-number": "8203", "set": "3.0.0-ci.8203", "sha": B,
                    "image-digest": D3, "tester-image": f"{tester}@{D3}"}
    baseline_same = {"run-number": "8207", "set": "3.0.0-ci.8207", "sha": A,
                     "image-digest": D1, "tester-image": f"{tester}@{D1}"}
    baseline_newer = {"run-number": "8210", "set": "3.0.0-ci.8210", "sha": C, "image-digest": D4}
    total += 1
    logs.clear()
    rows, overridden = refresh(baseline_old,
                               lambda: choose(fresh_two, _registry(full), tester, portal, log=logs.append),
                               log=logs.append)
    if not (overridden and rows["override"] == "true" and rows["set"] == "3.0.0-ci.8207"
            and rows["sha"] == A and rows["baseline-set"] == "3.0.0-ci.8203"
            and rows["plugins-run"] == "8207" and any("::notice" in line for line in logs)):
        failures.append(f"freshness: a newer sealed set must override the baseline — got {rows}")
    total += 1
    rows, overridden = refresh(baseline_same,
                               lambda: choose(fresh_two, _registry(full), tester, portal, log=logs.append),
                               log=logs.append)
    if overridden or rows["override"] != "false" or rows["set"] != "3.0.0-ci.8207" \
            or rows["tester-image"] != f"{tester}@{D1}" or "same set" not in rows["source"]:
        failures.append(f"freshness: the same set must keep the baseline verbatim — got {rows}")
    total += 1
    rows, overridden = refresh(baseline_newer,
                               lambda: choose(fresh_two, _registry(full), tester, portal, log=logs.append),
                               log=logs.append)
    if overridden or rows["set"] != "3.0.0-ci.8210" or rows["sha"] != C or "OLDER" not in rows["source"]:
        failures.append(f"freshness: an older re-resolution must keep the baseline — got {rows}")
    total += 1
    logs.clear()
    rows, overridden = refresh(baseline_old, lambda: choose(refusing, _registry(full), tester, portal,
                                                            log=logs.append), log=logs.append)
    if overridden or rows["set"] != "3.0.0-ci.8203" or rows["override"] != "false" \
            or not any("::warning" in line and "503" in line for line in logs):
        failures.append(f"freshness: a failed re-resolution must keep the baseline and WARN — got {rows}")
    total += 1
    try:
        load_baseline('{"set": "3.0.0-ci.8203"}')
        failures.append("load_baseline: a baseline without run-number must be refused")
    except ResolutionError as error:
        if "run-number" not in str(error):
            failures.append(f"load_baseline: refusal must name run-number: {error}")
    if load_baseline("") is not None or load_baseline("null") is not None \
            or load_baseline('{"run-number": "8203", "set": "x"}') != {"run-number": "8203", "set": "x"}:
        failures.append("load_baseline: empty/null → None, a dict → str rows")

    # ── THE OPTIONAL MAIN CEILING (#4171) ───────────────────────────────────────────────────
    # It only pays for itself if a sealed-but-unvouched set is actually passed over, if the lag is
    # SAID (both set ids), and if "main has passed nothing" is a REFUSAL rather than a quiet
    # fallback to the newest sealed set — which is the behaviour it replaces.
    #
    # 🚨 And the first two cases below are a PAIR on one fixture: the SAME runs, the SAME registry,
    # one argument different, opposite answers. `passed_ceiling=None` is every caller that does not
    # ask for the rule, and if the port had leaked into the default path the negative half would
    # choose 8203 and FAIL. That is the case that can fail if this option is not opt-in.
    SATELLITE = "Systemorph/MeshWeaver.Example"
    sealed_two = {1000 + 8207: _jobs(), 1000 + 8203: _jobs()}

    case("DEFAULT (no ceiling asked): the newest sealed set, even one main has not passed", True,
         lambda: choose(_fetch_for(two, sealed_two), _registry(full), tester, portal,
                        log=logs.append),
         lambda c: c.set_name == "3.0.0-ci.8207" and c.lag == ""
         and "`main` has passed" not in c.source)
    case("…the SAME fixture WITH a ceiling: the unvouched set is passed over, lag names both", True,
         lambda: choose(_fetch_for(two, sealed_two), _registry(full), tester, portal,
                        log=logs.append, passed_ceiling=8203),
         lambda c: c.set_name == "3.0.0-ci.8203" and "8207" in c.lag and "8203" in c.lag
         and "follow" in c.lag and "`main` has passed" in c.source)
    case("a ceiling AT the newest sealed set takes it, with no lag", True,
         lambda: choose(_fetch_for(two, sealed_two), _registry(full), tester, portal,
                        log=logs.append, passed_ceiling=8207),
         lambda c: c.set_name == "3.0.0-ci.8207" and c.lag == "")
    case("a freeze OVERRIDES the ceiling — a freeze is an instruction", True,
         lambda: choose(_fetch_for(two, sealed_two), _registry(full), tester, portal,
                        freeze="3.0.0-ci.8207", log=logs.append, passed_ceiling=8203),
         lambda c: c.set_name == "3.0.0-ci.8207" and c.lag == "")

    # ── a STALE listing (#4433): page 1 served from an old snapshot is refused, never resolved ──
    made = "2026-09-12T12:00:00Z"
    made_at = datetime.fromisoformat(made.replace("Z", "+00:00")).timestamp()
    aged = [_run(8207, A, created_at=made), _run(8203, B, created_at="2026-09-12T11:00:00Z")]
    three_days = lambda: made_at + 72 * 3600        # noqa: E731
    case("a page 1 whose newest main run is 3 days old is RED, naming the staleness", False,
         lambda: choose(_fetch_for(aged, sealed_two), _registry(full), tester, portal,
                        log=logs.append, now=three_days),
         lambda message: "STALE" in message and "#4433" in message and "#8207" in message
                         and made in message)
    case("…the SAME page 11 h old is the newest page and resolves unchanged", True,
         lambda: choose(_fetch_for(aged, sealed_two), _registry(full), tester, portal,
                        log=logs.append, now=lambda: made_at + 11 * 3600),
         lambda c: c.set_name == "3.0.0-ci.8207")
    case("…a FREEZE is not refused on it — an incident is when a freeze must keep working", True,
         lambda: choose(_fetch_for(aged, sealed_two), _registry(full), tester, portal,
                        freeze="3.0.0-ci.8203", log=logs.append, now=three_days),
         lambda c: c.set_name == "3.0.0-ci.8203")
    case("a page FRESH by age whose newest run is below main's passed ceiling is RED", False,
         lambda: choose(_fetch_for(aged, sealed_two), _registry(full), tester, portal,
                        log=logs.append, now=lambda: made_at + 3600, passed_ceiling=8676),
         lambda message: "STALE" in message and "#8676" in message and "#8207" in message)
    case("…a ceiling AT the page's newest run is not staleness", True,
         lambda: choose(_fetch_for(aged, sealed_two), _registry(full), tester, portal,
                        log=logs.append, now=lambda: made_at + 3600, passed_ceiling=8207),
         lambda c: c.set_name == "3.0.0-ci.8207")
    case("the AGE is read from the newest-created row, whatever the page order", False,
         lambda: choose(_fetch_for(list(reversed(aged)), sealed_two), _registry(full), tester,
                        portal, log=logs.append, now=three_days),
         lambda message: "STALE" in message and made in message)
    total += 1
    stale_rows, stale_override = refresh(
        {"run-number": "8203", "set": "3.0.0-ci.8203"},
        lambda: choose(_fetch_for(aged, sealed_two), _registry(full), tester, portal,
                       log=logs.append, now=three_days),
        log=logs.append, rows_of=lambda c: output_rows(c, tester, portal))
    if stale_override or stale_rows.get("set") != "3.0.0-ci.8203" \
            or not any("STALE" in line for line in logs):
        failures.append("a freshness check on a STALE listing must keep its baseline and say why — "
                        f"override={stale_override}, set={stale_rows.get('set')!r}")

    # The OUTPUT of a default run must be what it was: one inert `lag=` key and the unchanged
    # closing sentence. A run that DID follow main says so instead — the summary may never carry a
    # lag row under a sentence claiming the newest set is always taken.
    total += 1
    # 🚨 Guarded: a bare call here would CRASH the suite instead of failing a named case, and a
    # traceback tells a reader which line threw but not which property broke.
    try:
        plain = output_rows(choose(_fetch_for(two, sealed_two), _registry(full), tester, portal,
                                   log=logs.append), tester, portal)
        followed = output_rows(choose(_fetch_for(two, sealed_two), _registry(full), tester, portal,
                                      log=logs.append, passed_ceiling=8203), tester, portal)
    except Exception as error:                       # noqa: BLE001 — a throw here IS the failure
        plain = followed = {"lag": f"<{type(error).__name__}: {error}>"}
        failures.append(f"output rows: resolving for the row probe threw — {error}")
    if plain.get("lag") != "" or "lag" not in plain or followed.get("lag", "") == "":
        failures.append(f"output rows: `lag` must always be present and empty by default — "
                        f"default={plain.get('lag')!r}, followed={followed.get('lag')!r}")
    total += 1
    if set(plain) != set(followed):
        failures.append("output rows: a run that followed main must produce the SAME key set as "
                        f"one that did not — {set(plain) ^ set(followed)}")

    def _fetch_main(passed_set: str | None, has_run: bool = True, job: bool = True,
                    before: list[dict] | None = None, after: list[dict] | None = None) -> Fetch:
        """`before`/`after` place EXTRA annotations around the real one, in list order.

        The annotations endpoint does not promise emission order, so a case that pins the reader's
        answer must be able to put a decoy on either side of the verdict.
        """
        core = _fetch_for(two, sealed_two)

        def fetch(path: str) -> dict:
            if f"/repos/{SATELLITE}/" not in path:
                return core(path)
            if "/actions/workflows/" in path:
                # `created_at` travels because the "none named" refusal prints the NEWEST run's
                # date — the one fact that tells a stale listing from a broken main. Two runs,
                # and the OLDER one is listed FIRST on purpose: the message must take `max`, and
                # with a single run (or the newest first) a regression to `runs[0]` would pass.
                return {"workflow_runs": [
                    {"id": 555, "created_at": "2026-08-19T06:00:00Z"},
                    {"id": 556, "created_at": "2026-09-14T08:00:00Z"},
                ] if has_run else []}
            if "/jobs" in path:
                rows = [{"id": 777, "name": PLATFORM_REF_JOB}] if job else []
                return {"total_count": len(rows), "jobs": rows}
            if "/check-runs/777/annotations" in path:
                real = ([] if passed_set is None
                        else [{"title": NOTICE_TITLE,
                               "message": f"{passed_set} — core {B[:9]} (the newest)"}])
                return {"annotations": (before or []) + real + (after or [])}
            raise AssertionError(path)
        return fetch

    def _ceiling_case(name: str, fetch: Fetch, expect: int | None, says: str = "") -> None:
        nonlocal total
        total += 1
        got, notes = main_passed_ceiling(fetch, SATELLITE, log=logs.append)
        if got != expect:
            failures.append(f"{name}: ceiling {got}, expected {expect}")
        elif says and not any(says in n for n in notes):
            failures.append(f"{name}: no note saying {says!r} — notes={notes}")

    def _freeze_case(name: str, freeze: str | None, fetch: Fetch, expect_fatal: bool,
                     says: str = "") -> None:
        nonlocal total
        total += 1
        _, notes, fatal = ceiling_for(fetch, SATELLITE, freeze, log=logs.append)
        if fatal != expect_fatal:
            failures.append(f"{name}: fatal={fatal}, expected {expect_fatal}")
        elif says and not any(says in n for n in notes):
            failures.append(f"{name}: no note saying {says!r} — notes={notes}")

    def _refuses(_path: str) -> dict:
        raise AssertionError("a freeze must not consult main at all")

    _freeze_case("a freeze skips the ceiling ENTIRELY — main is never consulted",
                 "3.0.0-ci.8207", _refuses, False, "overrides the main ceiling")
    _freeze_case("…so an unreadable ceiling under a freeze is NOT fatal",
                 "3.0.0-ci.8207", _fetch_main(None, has_run=False), False)
    _freeze_case("…while without a freeze it IS fatal",
                 None, _fetch_main(None, has_run=False), True, "no successful push run")

    _ceiling_case("main's newest passed set is read back from its own run notice",
                  _fetch_main("3.0.0-ci.8203"), 8203, "8203")
    _ceiling_case("a prerelease set name in the notice is read too",
                  _fetch_main("3.0.0-rc.1-ci.8203"), 8203, "8203")
    _ceiling_case("no successful main run ⇒ no ceiling (the caller must go RED)",
                  _fetch_main(None, has_run=False), None, "no successful push run")
    _ceiling_case("a main run that named no set ⇒ no ceiling, and the reason is recorded",
                  _fetch_main(None), None, "annotation")
    _ceiling_case("a main run without the platform-ref job ⇒ skipped, named",
                  _fetch_main("3.0.0-ci.8203", job=False), None, PLATFORM_REF_JOB)
    # 🚨 The refusal names WHEN the newest run examined was created, and says that a newer run
    # existing means the LISTING was stale (re-run), not main. Measured 2026-09-14: GitHub served
    # a page from 08-19 for one `status=success` call and the red read as "main is broken" until
    # someone re-issued the query by hand. A message nobody pins drifts; this pins both halves.
    _ceiling_case("…and the refusal names the NEWEST run's date (max, not runs[0]) so a stale "
                  "listing is legible",
                  _fetch_main("3.0.0-ci.8203", job=False), None, "created 2026-09-14T08:00:00Z")
    _ceiling_case("…and tells the reader a newer run means the listing was stale, not main",
                  _fetch_main("3.0.0-ci.8203", job=False), None, "served a stale page")

    # ── 🚨 THE CEILING IS READ OUT OF A LIST THE SELF-TEST ALSO WRITES INTO (#1826) ─────────────
    # `Resolve the released platform` runs `resolve-platform.py --self-test` and
    # `test-platform-resolution.py` in the SAME step as the real resolution, and both go through
    # `emit`, which prints `::notice title=Platform for this run::` — so their FIXTURE set ids
    # become check-run annotations on the very job this reader parses. Measured on three
    # consecutive green main runs (34800174441, 34798629153, 34798196752), each publishes:
    #
    #   3.0.0-ci.8539 — core 77451a10b (frozen by the repo VARIABLE …)   ← the verdict
    #   3.0.0-ci.8207 — core aaaaaaaaa (the newest sealed platform set)  ← a fixture
    #   3.0.0-ci.8207 — core aaaaaaaaa (the newest sealed platform set)  ← a fixture
    #
    # It happened to answer correctly only because the verdict sorted first, which the API does not
    # promise and which is NOT emission order (the fixtures are logged ~7 s earlier). A flip would
    # have put every pull request on `8207` — a set id this repository never resolved.
    #
    # The rule is therefore NOT "pick the right one": two different sets under the production title
    # means the run cannot be read, so it is SKIPPED and said. Guessing is what this was.
    _DECOY = {"title": NOTICE_TITLE, "message": "3.0.0-ci.8207 — core aaaaaaaaa (a self-test fixture)"}

    _ceiling_case("a FIXTURE set id before the verdict must NOT be taken (#1826)",
                  _fetch_main("3.0.0-ci.8539", before=[_DECOY]), None, "2 DIFFERENT sets")
    _ceiling_case("…nor after it — the reader must not depend on annotation order either",
                  _fetch_main("3.0.0-ci.8539", after=[_DECOY]), None, "2 DIFFERENT sets")
    _ceiling_case("…and the note names BOTH sets, so the poisoning is readable from the log",
                  _fetch_main("3.0.0-ci.8539", before=[_DECOY]), None, "8207")
    _ceiling_case("two annotations naming the SAME set are not ambiguous — still read",
                  _fetch_main("3.0.0-ci.8203",
                              before=[{"title": NOTICE_TITLE,
                                       "message": f"3.0.0-ci.8203 — core {B[:9]} (a retry)"}]),
                  8203, "8203")
    _ceiling_case("an annotation under a DIFFERENT title is ignored, not counted as disagreement",
                  _fetch_main("3.0.0-ci.8203",
                              before=[{"title": "Platform for this SELF-TEST",
                                       "message": "3.0.0-ci.8207 — core aaaaaaaaa (a fixture)"}]),
                  8203, "8203")

    # ── 🚨 THE FLOOR: A SET OLDER THAN ONE THIS TREE HAS ALREADY ADOPTED (#1826) ────────────────
    # The resolver had a CEILING (the newest set main passed) and a FREEZE, and nothing that stops
    # it resolving BACKWARDS past an adoption. Measured: `src/Memex.LocalMesh` adopted core's
    # `OperationSentinel` and merged green against a frozen `3.0.0-ci.8506`; the freeze was lifted,
    # the resolver fell to its ceiling `3.0.0-ci.8492` — 50 newer sets passed over — and every open
    # pull request went red on `CS0103: The name 'OperationSentinel' does not exist`, a fault no
    # author's diff could reach (Plugins#1821).
    #
    # The floor is a property of the SOURCE, not a preference about which set to take, so it binds
    # EVERY path — including a freeze. A freeze may choose among sets that can compile this tree;
    # it cannot make an older set carry a symbol that set does not have. That is why `floor_refusal`
    # takes only the resolved set, and `main` calls it after the choice however the choice was made.
    def _floor_case(name: str, floor: tuple[int | None, str], set_name: str, number: int,
                    expect_refusal: bool, says: str = "") -> None:
        nonlocal total
        total += 1
        got = floor_refusal(floor, set_name, number)
        if (got is not None) != expect_refusal:
            failures.append(f"{name}: refusal={got!r}, expected refusal={expect_refusal}")
        elif says and (got is None or says not in got):
            failures.append(f"{name}: refusal does not say {says!r} — {got!r}")

    _WHY = "src/Memex.LocalMesh calls OperationSentinel.Classify (#1767)"
    _floor_case("a set BELOW the declared floor is refused", (8506, _WHY),
                "3.0.0-ci.8492", 8492, True, "8506")
    _floor_case("…and the refusal names the set it refused", (8506, _WHY),
                "3.0.0-ci.8492", 8492, True, "3.0.0-ci.8492")
    _floor_case("…and WHY the tree needs it, so the reader is not left guessing", (8506, _WHY),
                "3.0.0-ci.8492", 8492, True, "OperationSentinel")
    _floor_case("…and says a freeze does not exempt it", (8506, _WHY),
                "3.0.0-ci.8492", 8492, True, "freeze")
    _floor_case("a set AT the floor is fine", (8506, _WHY), "3.0.0-ci.8506", 8506, False)
    _floor_case("a set ABOVE the floor is fine", (8506, _WHY), "3.0.0-ci.8539", 8539, False)
    _floor_case("no declared floor constrains nothing — every other repo's copy has no such file",
                (None, ""), "3.0.0-ci.1", 1, False)

    total += 1
    if read_floor_text(None) != (None, ""):
        failures.append("an ABSENT floor declaration must read as no floor, not as an error")
    total += 1
    if read_floor_text('{"minimum": 8506, "because": "x"}') != (8506, "x"):
        failures.append("a declared floor must be read back — got "
                        + repr(read_floor_text('{"minimum": 8506, "because": "x"}')))
    # 🚨 A malformed floor must RAISE, never read as "no floor": a declaration the reader silently
    # ignores is a guard that passes having checked nothing — the shape this repo keeps legislating
    # against, and the one that would make the floor decorative the first time someone fat-fingers it.
    for bad, label in (('{"because": "x"}', "no minimum"),
                       ('{"minimum": "soon", "because": "x"}', "a non-integer minimum"),
                       ('{"minimum": 8506}', "no reason"),
                       ("not json at all", "unparseable")):
        total += 1
        try:
            read_floor_text(bad)
        except ResolutionError:
            pass
        else:
            failures.append(f"a floor declaration with {label} must be REFUSED, not ignored")
    # The annotations endpoint answers a BARE ARRAY in the real API; a reader that only handled
    # the object form would read every main run as "named no set" and refuse every pull request.
    total += 1
    bare = _fetch_main("3.0.0-ci.8203")

    def _bare_array(path: str):
        rows = bare(path)
        return rows["annotations"] if "/annotations" in path else rows

    try:
        got, _ = main_passed_ceiling(_bare_array, SATELLITE, log=logs.append)
    except Exception as error:                       # noqa: BLE001 — a crash here IS the failure
        got, error_text = None, f" ({type(error).__name__}: {error})"
    else:
        error_text = ""
    if got != 8203:
        failures.append("annotations as a bare array must read the same — got "
                        f"{got}{error_text}")

    # ── THE OPTIONAL PUBLICATION-SOURCE VERIFICATION (#4171) ────────────────────────────────
    # The set's sha and release read from the platform bake's OWN final receipt rather than from
    # the run's head sha. Three properties carry it, and the FIRST is the one that decides whether
    # this may live in the canonical at all: **no caller that does not ask reads a job log.**
    RECEIPT_SHA = "e" * 40

    def _receipt(sha: str = RECEIPT_SHA, release: str = "3.0.0-ci.8207", **over) -> str:
        fields = {"source": "meshweaver-content", "source-sha": sha, "release": release,
                  "arch": "linux-x64", "identity": "net10.0-abc", "bundles": "3",
                  "targets-published": "3", "targets-converged": "0", "release-markers": "1"}
        fields.update(over)
        body = " ".join(f"{k}={v}" for k, v in fields.items())
        return f"2026-09-13T00:00:00.0Z bake published: {body}\n"

    def _fetch_with_logs(logs: dict[int, str], runs=None, jobs=None) -> Fetch:
        base = _fetch_for(runs or two, jobs or sealed_two)

        def fetch(path: str):
            if path.endswith("/logs"):
                job_id = int(path.rsplit("/", 2)[1])
                if job_id not in logs:
                    raise ProvenanceUnavailable(f"job log {path} is unavailable (HTTP 410)")
                return logs[job_id]
            return base(path)
        return fetch

    # `_jobs()` gives the bake job no id, so give one per run: the id the receipt is keyed on.
    def _jobs_with_bake_id(bake_id: int, **kw) -> list[dict]:
        rows = _jobs(**kw)
        for row in rows:
            if str(row["name"]).startswith(REQUIRED_JOBS[2][1]):
                row["id"] = bake_id
        return rows

    id_8207, id_8203 = 70001, 70002
    with_ids = {1000 + 8207: _jobs_with_bake_id(id_8207), 1000 + 8203: _jobs_with_bake_id(id_8203)}

    # 🚨 THE CONTROL THAT LICENSES THE OPTION. A fetch that EXPLODES on any log path: the default
    # path must never touch one, so this case fails loudly (not silently) the moment the read stops
    # being gated on the flag. It is the credential argument made executable — nothing is read on
    # behalf of a caller who did not ask.
    def _no_logs_allowed(path: str):
        if path.endswith("/logs"):
            # A plain ResolutionError, deliberately NOT a ProvenanceUnavailable: the latter is
            # CAUGHT by `choose` and turned into "passed over", so the breach would be reported as
            # an ordinary skip instead of failing this case by name.
            raise ResolutionError("a caller that did not pass --verify-source read a job LOG")
        return _fetch_for(two, with_ids)(path)

    case("DEFAULT (no --verify-source): head sha, and NO job log is fetched at all", True,
         lambda: choose(_no_logs_allowed, _registry(full), tester, portal, log=logs.append),
         lambda c: c.sha == A and c.set_name == "3.0.0-ci.8207"
         and "source verified" not in c.source)

    verified = _fetch_with_logs({id_8207: _receipt(), id_8203: _receipt(RECEIPT_SHA, "3.0.0-ci.8203")},
                                jobs=with_ids)
    case("…WITH it: the sha and release come from the bake's own receipt, not from head_sha", True,
         lambda: choose(verified, _registry(full), tester, portal, log=logs.append,
                        verify_source=True),
         lambda c: c.sha == RECEIPT_SHA and c.sha != A and c.set_name == "3.0.0-ci.8207"
         and "source verified" in c.source)

    # A set that cannot attribute itself is PASSED OVER — never taken unattributed, and never fatal
    # on its own: the resolution continues at an older VERIFIED set, with the reason recorded.
    case("a set whose receipt is missing is passed over for an older VERIFIED one, and said", True,
         lambda: choose(_fetch_with_logs({id_8203: _receipt(RECEIPT_SHA, "3.0.0-ci.8203")},
                                         jobs=with_ids),
                        _registry(full), tester, portal, log=logs.append, verify_source=True),
         lambda c: c.set_name == "3.0.0-ci.8203"
         and any("#8207" in s and "source/release unverified" in s for s in logs))
    case("…and an INCONSISTENT receipt is passed over the same way", True,
         lambda: choose(_fetch_with_logs({id_8207: _receipt(sha="not-a-sha"),
                                          id_8203: _receipt(RECEIPT_SHA, "3.0.0-ci.8203")},
                                         jobs=with_ids),
                        _registry(full), tester, portal, log=logs.append, verify_source=True),
         lambda c: c.set_name == "3.0.0-ci.8203")
    case("…and a DUPLICATED receipt is refused rather than one of them picked", False,
         lambda: choose(_fetch_with_logs({id_8207: _receipt() + _receipt(),
                                          id_8203: _receipt() + _receipt()}, jobs=with_ids),
                        _registry(full), tester, portal, log=logs.append, verify_source=True),
         lambda message: "no sealed platform set" in message)

    # 🚨 #4247 — "EVERY TARGET ALREADY HELD IT" IS A REACHED PUBLICATION, NOT A FAILED ONE. A bake
    # whose targets were already sealed on this content publishes nothing and is nonetheless the
    # publication: main-cd #8457 and #8459 (both cdb2878bb) each logged "holds a COMPLETE
    # publication of THIS content … already published; skipping" twice, wrote their two release
    # markers, and were passed over here as unattributable. The three rows below are the whole
    # distinction — reached-because-already, reached-nothing, and the pre-#4247 receipt that does
    # not carry the field at all.
    already = _receipt(**{"targets-published": "0", "targets-converged": "0", "targets-already": "2"})
    case("a receipt whose targets ALREADY held this publication is VERIFIED, not passed over", True,
         lambda: choose(_fetch_with_logs({id_8207: already,
                                          id_8203: _receipt(RECEIPT_SHA, "3.0.0-ci.8203")},
                                         jobs=with_ids),
                        _registry(full), tester, portal, log=logs.append, verify_source=True),
         lambda c: c.set_name == "3.0.0-ci.8207" and c.sha == RECEIPT_SHA and "source verified" in c.source)
    nothing = _receipt(**{"targets-published": "0", "targets-converged": "0", "targets-already": "0"})
    case("…while a receipt that reached NOTHING is still passed over", True,
         lambda: choose(_fetch_with_logs({id_8207: nothing,
                                          id_8203: _receipt(RECEIPT_SHA, "3.0.0-ci.8203")},
                                         jobs=with_ids),
                        _registry(full), tester, portal, log=logs.append, verify_source=True),
         lambda c: c.set_name == "3.0.0-ci.8203")
    case("…and a receipt written before the field existed reads exactly as it did", True,
         lambda: choose(_fetch_with_logs({id_8207: _receipt(), id_8203: _receipt(RECEIPT_SHA, "3.0.0-ci.8203")},
                                         jobs=with_ids),
                        _registry(full), tester, portal, log=logs.append, verify_source=True),
         lambda c: c.set_name == "3.0.0-ci.8207" and "source verified" in c.source)
    # The DEFAULT is load-bearing, so it is asserted rather than assumed: a pre-#4247 receipt that
    # reached nothing carries no `targets-already` at all, and must still be passed over. Absent has
    # to read as ZERO — any other default would turn every old receipt into a verified one.
    old_and_empty = _receipt(**{"targets-published": "0", "targets-converged": "0"})
    assert "targets-already" not in old_and_empty
    case("…and a PRE-#4247 receipt that reached nothing is still passed over (absent reads as zero)", True,
         lambda: choose(_fetch_with_logs({id_8207: old_and_empty,
                                          id_8203: _receipt(RECEIPT_SHA, "3.0.0-ci.8203")},
                                         jobs=with_ids),
                        _registry(full), tester, portal, log=logs.append, verify_source=True),
         lambda c: c.set_name == "3.0.0-ci.8203")

    # 🚨 #4242 — AN ABSENT RECEIPT IS NOT A DISAGREEMENT, and telling them apart is the whole
    # value of the sentence. A run with no successful platform bake used to answer "successful
    # platform bakes disagree on source/release (found 0 distinct receipts)" — an ABSENCE in the
    # vocabulary of a DISAGREEMENT — and on 2026-09-13 that was read off eleven ordinary
    # non-publishing main-cd runs and reported as a fleet-wide bake defect that measurement then
    # found no trace of (8505–8531: 20 of 27 unsealed, all 7 sealed runs attributable, 0 disagreeing).
    total += 1
    try:
        publication_source(lambda _p: "", _jobs(bake="skipped"), 8207, {})
        failures.append("a run with no successful platform bake must raise")
    except ProvenanceUnavailable as error:
        # It may SAY "not a disagreement" — what it must not do is ASSERT one.
        if "published nothing" not in str(error) or "DISAGREE" in str(error):
            failures.append(f"an ABSENT receipt must not be reported as a disagreement: {error}")
    total += 1
    # 🚨 `two` at module scope in this function is the RUN LIST. Naming a local after it shadowed
    # it for every later case and made `choose` iterate an int — caught by the suite immediately,
    # which is the point of running it after every edit.
    disagreeing = {70011: _receipt("a" * 40, "3.0.0-ci.8207"),
                   70012: _receipt("b" * 40, "3.0.0-ci.8207")}
    two_jobs = _jobs()
    bake_rows = [j for j in two_jobs if str(j["name"]).startswith(REQUIRED_JOBS[2][1])]
    bake_rows[0]["id"] = 70011
    # Two legs of ONE run naming different sources IS the disagreement, and it must say both.
    two_jobs.append({**bake_rows[0], "id": 70012,
                     "name": REQUIRED_JOBS[2][1] + " (linux-arm64)"})
    try:
        publication_source(lambda path: disagreeing[int(path.rsplit("/", 2)[1])], two_jobs, 8207, {})
        failures.append("two legs naming different sources must raise")
    except ProvenanceUnavailable as error:
        if "DISAGREE" not in str(error) or "aaaaaaaaa" not in str(error):
            failures.append(f"a real disagreement must name the receipts it found: {error}")

    # 🚨 #4242 — THE SCAN REACHES RUNS THE FREEZE DOES NOT NAME, and every "a freeze is an
    # instruction" escalation has to be asked about the run the freeze NAMES. Measured against live
    # core CD 2026-09-13: `--freeze 7ee11bc7… --verify-source` aborted with *"the freeze names
    # main-cd #8530 (core e0e4aeff3), which is not a sealed set"* — false; 7ee11bc7 is the head of
    # #8506, and #8530 was merely the newest run in the scan. It made --verify-source unusable
    # during an incident freeze, which is exactly when resolution has to keep working.
    #
    # The fixture is that shape: the frozen run is OLDER than the newest, and the runs ahead of it
    # in the scan are unsealed and unattributable. On the pre-#4242 code the first of them aborts.
    frozen_sha = "f" * 40
    scan = [_run(8530, C), _run(8520, A), _run(8506, frozen_sha)]
    id_8506 = 70006
    scan_jobs = {
        1000 + 8530: _jobs(bake="in progress"),                      # unsealed, and NEWEST
        1000 + 8520: _jobs_with_bake_id(70005),                      # sealed, receipt below
        1000 + 8506: _jobs_with_bake_id(id_8506),                    # sealed, the frozen one
    }
    scan_full = dict(full)
    scan_full[("mw-plugin-test", "3.0.0-ci.8506")] = D1
    scan_full[("memex-portal-ai", "3.0.0-ci.8506")] = D2
    scan_logs = {
        70005: _receipt("d" * 40, "3.0.0-ci.8520"),
        id_8506: _receipt(frozen_sha, "3.0.0-ci.8506"),
    }

    case("a sha freeze naming an OLDER sealed run resolves past the unsealed runs ahead of it", True,
         lambda: choose(_fetch_with_logs(scan_logs, runs=scan, jobs=scan_jobs),
                        _registry(scan_full), tester, portal, freeze=frozen_sha,
                        log=logs.append, verify_source=True),
         lambda c: c.sha == frozen_sha and c.set_name == "3.0.0-ci.8506")
    # …and the escalation it replaced is still there for the run the freeze DOES name: an
    # unsealed frozen run is RED, and the message names THAT run.
    case("…while a sha freeze naming an UNSEALED run is still RED, naming that run", False,
         lambda: choose(_fetch_with_logs(scan_logs, runs=scan, jobs={
                            **scan_jobs, 1000 + 8506: _jobs(bake="failure")}),
                        _registry(scan_full), tester, portal, freeze=frozen_sha,
                        log=logs.append, verify_source=True),
         lambda message: "not a sealed set" in message and "#8506" in message
                         and "#8530" not in message)

    # Under a FREEZE the same condition is fatal: a freeze names one set and may not substitute.
    case("an unverifiable set under a freeze is RED, never substituted", False,
         lambda: choose(_fetch_with_logs({id_8203: _receipt(RECEIPT_SHA, "3.0.0-ci.8203")},
                                         jobs=with_ids),
                        _registry(full), tester, portal, freeze="3.0.0-ci.8207",
                        log=logs.append, verify_source=True),
         lambda message: "unverified" in message and "Refusing to substitute" in message)
    # …and a freeze BY SHA is matched against the receipt's sha, which is the whole point: the
    # run's head sha is a different value and would match nothing.
    case("a freeze by sha matches the RECEIPT's sha, not the run's head sha", True,
         lambda: choose(verified, _registry(full), tester, portal, freeze=RECEIPT_SHA,
                        log=logs.append, verify_source=True),
         lambda c: c.sha == RECEIPT_SHA)
    total += 1
    if MAX_LOG_BYTES <= 0 or FINAL_BAKE_RECEIPT.search(_receipt()) is None:
        failures.append("the receipt pattern must match the line publish-bake-bundles.sh writes")

    if failures:
        print(f"✗ resolve-platform self-test: {len(failures)} failure(s)")
        for failure in failures:
            print(f"  - {failure}")
        return 1
    print(f"✓ resolve-platform self-test: {total} cases — the newest platform-sealed set is chosen "
          "regardless of its own plugins seal or run conclusion, the plugins publication is found on "
          "its own and reported beside it, a set still sealing its plugins is passed over (bounded), "
          "an unsealed or purged newer set is passed over and SAID, a sealing set is waited for on "
          "request, a freeze never substitutes, a re-run takes a newer set and keeps its baseline "
          "otherwise, the OPTIONAL main ceiling changes nothing unless asked for and refuses "
          "rather than falling back when main has passed nothing, the OPTIONAL source verification "
          "reads no job log unless asked for and passes over a set it cannot attribute, a main run "
          "whose annotations name two DIFFERENT sets is SKIPPED rather than guessed at, a set below "
          "this repository's declared FLOOR is refused on every path including under a freeze, a run "
          "listing whose page 1 is provably STALE (its newest run over 12 h old, or older than the "
          "set main has passed) is refused rather than resolved from, and every dead end is RED "
          "naming why.")
    return 0


# ───────────────────────────────── main ───────────────────────────────────────────────────

def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0],
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--self-test", action="store_true")
    parser.add_argument("--no-registry", action="store_true",
                        help="resolve the core commit and set name only (no ACR credential)")
    parser.add_argument("--wait-for-seal", type=float, default=0.0, metavar="SECONDS",
                        help="on a release trigger: wait this long for a set that is still sealing")
    parser.add_argument("--freeze", default="",
                        help="a core sha or X.Y.Z[-prerelease]-ci.N to select instead of the newest sealed set "
                             "(incident use; the workflow passes vars.MW_PLATFORM_REF)")
    parser.add_argument("--tester-image", default=DEFAULT_TESTER_IMAGE,
                        help="registry/repository of the tester image (tag ignored)")
    parser.add_argument("--portal-image", default=DEFAULT_PORTAL_IMAGE)
    parser.add_argument("--migration-image", default="",
                        help="optional migration image; require its digest in the same sealed set")
    parser.add_argument("--passed-on-main", default="", metavar="OWNER/REPO",
                        help="OPTIONAL: restrict the choice to a sealed set this repo's own `main` "
                             "has already passed on (pull-request and merge-group runs). A set that "
                             "regresses this repo then reds main alone, not every open PR. Omit it "
                             "and the resolution is unchanged.")
    parser.add_argument("--passed-ceiling", default="", metavar="N",
                        help="OPTIONAL: the ceiling a `platform-ref` job already established (its "
                             "`ceiling` output) — a re-resolving job on a pull request passes it "
                             "instead of re-reading main's runs (no extra API cost).")
    parser.add_argument("--verify-source", action="store_true",
                        help="OPTIONAL: take the chosen set's core commit and release from the "
                             "platform bake's OWN final publication receipt instead of the run's "
                             "head sha. Reads that one job's log (the only text this script ever "
                             "fetches, and only with this flag). A set whose receipt is missing, "
                             "duplicated, malformed or inconsistent is passed over, never taken "
                             "unattributed.")
    arguments = parser.parse_args()

    if arguments.self_test:
        return self_test()

    token = os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN") or ""
    if not token:
        print("::error title=No GitHub token::GH_TOKEN / GITHUB_TOKEN is unset — the released "
              "platform is read from core's public CD runs, and an unauthenticated read is rate "
              "limited into failure. Refusing to guess a platform.")
        return 1
    tester = arguments.tester_image.split("@", 1)[0]
    tester = tester.rsplit(":", 1)[0] if "/" in tester and ":" in tester.rsplit("/", 1)[1] else tester
    portal = arguments.portal_image.split("@", 1)[0]
    portal = portal.rsplit(":", 1)[0] if "/" in portal and ":" in portal.rsplit("/", 1)[1] else portal

    migration = arguments.migration_image.split("@", 1)[0]
    migration = (migration.rsplit(":", 1)[0]
                 if "/" in migration and ":" in migration.rsplit("/", 1)[1] else migration)

    resolve: Resolve | None = None
    if not arguments.no_registry:
        user = os.environ.get("ACR_USERNAME", "")
        password = os.environ.get("ACR_PASSWORD", "")
        if not user or not password:
            print("::error title=No registry credential::ACR_USERNAME / ACR_PASSWORD are unset, so "
                  "the released set's image digests cannot be resolved. Set them (repo secrets) "
                  "or pass --no-registry where only the core commit is needed.")
            return 1
        resolve = registry_resolver(user, password)

    fetch = github_fetch_with(token)
    # OPT-IN. Without either flag `ceiling` stays None and `choose` takes the path it always has.
    ceiling: int | None = None
    if arguments.passed_ceiling.strip():
        if not arguments.passed_ceiling.strip().isdigit():
            print(f"::error title=Bad --passed-ceiling::{arguments.passed_ceiling!r} is not a core CD run number")
            return 1
        ceiling = int(arguments.passed_ceiling)
        print(f"following main: ceiling core CD #{ceiling} (from the run's baseline)")
    elif arguments.passed_on_main:
        ceiling, notes, fatal = ceiling_for(fetch, arguments.passed_on_main,
                                            arguments.freeze or None)
        for note in notes:
            print(f"  {note}")
        if fatal:
            detail = " ".join(notes)
            print("::error title=No set this repo's main has passed::this run asked to follow "
                  "`main` (--passed-on-main), so it resolves the newest sealed set main has "
                  f"already passed, and none could be established. {detail} Fix main, or set the "
                  "repo VARIABLE MW_PLATFORM_REF to select one set explicitly. Refusing to fall "
                  "back to the newest sealed set: that is what reddened every open pull request "
                  "at once.")
            return 1
        if ceiling is not None:
            print(f"following main: the newest set main has passed is core CD #{ceiling}")
    choose_fn = lambda: choose(fetch, resolve, tester, portal,  # noqa: E731 — one call, two callers
                               wait_for_seal=arguments.wait_for_seal, freeze=arguments.freeze or None,
                               migration=migration or None, passed_ceiling=ceiling,
                               verify_source=arguments.verify_source)
    # 🚨 THE FLOOR BINDS EVERY PATH — read BEFORE the choice so a malformed declaration is a red
    # here and not a surprise after a successful resolution, and applied to whatever set was
    # chosen, by whatever route (newest sealed, ceiling, freeze, or a kept baseline).
    try:
        floor = current_floor()
    except ResolutionError as error:
        print(f"::error title=The platform floor declaration is unreadable::{error}")
        return 1

    def below_floor(set_name: str, run_number: int) -> int | None:
        """Prints and returns 1 when the resolved set is below the floor, else None."""
        refusal = floor_refusal(floor, set_name, run_number)
        if refusal is None:
            return None
        print("::error title=The resolved platform is below this repository's floor::"
              + refusal.replace("\n", "%0A"))
        summary = os.environ.get("GITHUB_STEP_SUMMARY")
        if summary:
            with open(summary, "a", encoding="utf-8") as handle:
                handle.write("### ❌ The resolved platform is below this repository's floor\n\n"
                             f"```\n{refusal}\n```\n")
        return 1

    try:
        baseline = load_baseline(os.environ.get(BASELINE_ENV))
        if baseline is not None:
            # A consumer job checking the run's baseline for freshness (Education#320): never RED
            # on the re-resolution itself — the baseline is a valid answer.
            rows, _ = refresh(baseline, choose_fn,
                              rows_of=lambda c: output_rows(c, tester, portal, migration or None))
            refused = below_floor(rows.get("set", "?"), int(rows.get("run-number") or 0))
            if refused is not None:
                return refused
            emit(rows, title="Platform for this job")
            return 0
        chosen = choose_fn()
    except ResolutionError as error:
        text = str(error).replace("\n", "%0A")
        print(f"::error title=The released platform did not resolve::{text}")
        summary = os.environ.get("GITHUB_STEP_SUMMARY")
        if summary:
            with open(summary, "a", encoding="utf-8") as handle:
                handle.write("### ❌ The released platform did not resolve\n\n"
                             f"```\n{error}\n```\n")
        return 1
    refused = below_floor(chosen.set_name, chosen.run_number)
    if refused is not None:
        return refused
    write_outputs(chosen, tester, portal, migration or None)
    output = os.environ.get("GITHUB_OUTPUT")
    if output:
        # Always written (empty without the option) so a downstream job can pass it back as
        # `--passed-ceiling` without asking whether the upstream job used the rule.
        with open(output, "a", encoding="utf-8") as handle:
            handle.write(f"ceiling={ceiling if ceiling is not None else ''}\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
