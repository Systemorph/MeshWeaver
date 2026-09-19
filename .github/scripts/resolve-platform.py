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
freshness check keeps its baseline on this refusal like on any other.

🚨 THE CEILING BRANCH IS RE-READ BEFORE IT IS REFUSED (MeshWeaver#4750), THE AGE BRANCH IS NOT.
The two branches are not the same kind of fact. A ceiling shortfall is PROVEN — a run numbered at
least as high as the ceiling exists, because this repository's own `main` passed on it, so a page
1 without one is a read inconsistency and nothing else, and it has a crisp condition to re-read
FOR. The age branch is an INFERENCE from core CD's cadence: a genuinely quiet core serves the same
page every time, so re-reading burns the budget to reach the same refusal. So a provable staleness
re-reads page 1 up to STALE_REREADS times on a short backoff and refuses only if it is STILL
stale — the refusal, the conditions and the strictness are unchanged, and only the number of times
the page is asked for before it moved. Measured 2026-09-18: three occurrences in one day (two PRs
and, once, `main` itself — 17, 17 and 18 downstream jobs red), every hand re-run green minutes
later with no code change. That is the same argument already accepted for a 502, and it is NOT a
gate testing its own input: the answer a re-read is allowed to change is GitHub's, never this
script's verdict about it.

🚨 THE CEILING READS A SECOND LISTING — the CALLING repository's own main runs — and it is NOT
guarded that way, by design: there is no fact its page can be checked against (a repository's main
may genuinely be quiet for days, so age proves nothing, and the ceiling IS the thing being
established, so it cannot check itself). What it does instead is REPORT: when no ceiling can be
established, `ceiling_refusal` prints what was read — how many rows, the newest one's id, date, age
and URL, and why each was skipped — and orders its remedies by that evidence, leading with the
one-click test that decides whether the listing or `main` is at fault. Three times a stale page
produced that refusal (2026-09-14, 2026-09-17 twice over) and twice the reader went and looked at a
`main` that was fine, because the old text closed on "Fix main" (MeshWeaver#4664). The refusal's
strictness is unchanged: a stale listing still refuses rather than resolving. THIS listing does
not re-read either — #4750's re-read applies only to the core CD page, where a ceiling supplies
the crisp condition to re-read for; here the ceiling IS what is being established, so there is
nothing to re-read toward and a repeat read could only be "ask until the answer is nicer".

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
`tester-name`, `tester-image`, `portal-name`, `portal-image` (`name` is the repository alone,
`image` is name@digest), `plugins-sealed`, `plugins-run`, `plugins-run-url`, `plugins-set`,
`plugins-sealed-at`, `override`, `source` and `lag` to $GITHUB_OUTPUT, and a table to
$GITHUB_STEP_SUMMARY when set. `lag` is always present and empty unless the optional main ceiling
held this run back. With --migration-image, also writes `migration-name`, `migration-image-digest`
and `migration-image` for that same sealed set.
🚨 The `*-name` keys and `lag` were WRITTEN but not listed here (Copilot review on
MeshWeaver.SocialMedia#181). An output a consumer cannot find in the contract is one nobody may
depend on, and an undocumented key is removed by the next edit that does not know it exists.

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
                # 🚨 A 5xx is GitHub failing to answer, not an answer — the same class as the
                # transport faults below, and retried the same bounded way. It used to fall through
                # to the verdict text, which blamed a revoked token, a rate limit or a renamed
                # workflow for a single 502 and took eight downstream gates red with it
                # (MeshWeaver.Plugins run 35000489240, `…/actions/runs/34937373355/jobs → HTTP 502`).
                if error.code >= 500:
                    last = error
                    if attempt < 3:
                        print(f"GET {path} → HTTP {error.code} (GitHub server error) — retrying in "
                              f"{5 * (attempt + 1)}s")
                        time.sleep(5 * (attempt + 1))
                        continue
                    raise ResolutionError(
                        f"GET {path} → HTTP {error.code} (GitHub server error) on all {attempt + 1} "
                        "attempts — the API is failing, not the platform. Re-run this job.") from error
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


def _newest_main_run(runs: list[dict]) -> int | None:
    """The highest `run_number` among the `main` rows of one listing page, or None when it has
    none. A row with no `head_branch` counts as main — the listing is already filtered to
    `branch=main`, and the self-test fixtures leave the field off."""
    numbers = [int(r["run_number"]) for r in runs if r.get("head_branch") in (None, CORE_BRANCH)]
    return max(numbers) if numbers else None


def stale_listing(runs: list[dict], passed_ceiling: int | None, now: float) -> str | None:
    """Why page 1 of the main-cd listing cannot be the newest page, or None when nothing proves it.

    Both facts are independent of the page itself: the clock, and a run number this repository's
    own `main` has already passed on. Rows without `created_at` cannot be aged; a page with none
    is judged by the ceiling alone (the live API always sends it — the self-test fixtures don't)."""
    main_runs = [r for r in runs if r.get("head_branch") in (None, CORE_BRANCH)]
    if not main_runs:
        return None
    newest = _newest_main_run(runs)
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


def ceiling_shortfall(runs: list[dict], passed_ceiling: int | None) -> int | None:
    """The page's newest main run when it is BELOW a declared ceiling — the ONE staleness this
    script can PROVE rather than infer — or None.

    PROVEN, because the ceiling is a run number the CALLING repository's own `main` has already
    passed on: a run numbered at least that high EXISTS, so a page 1 that does not contain one is
    a GitHub read inconsistency and can be nothing else. The AGE branch of `stale_listing` is an
    INFERENCE from core CD's measured cadence instead — a genuinely quiet core serves the same
    page for hours — which is the whole reason only this branch is re-read (MeshWeaver#4750)."""
    if passed_ceiling is None:
        return None
    newest = _newest_main_run(runs)
    return newest if newest is not None and newest < passed_ceiling else None


# ─────────────── RE-READING A PROVABLY STALE PAGE 1 (MeshWeaver#4750) ───────────────
# `fetch` already retries every GitHub-side failure that ANNOUNCES itself — a 5xx, a 403/429 rate
# limit, a transport fault. A stale-but-200 listing was the one that did not, and it is the one
# whose own refusal text prescribes a retry. Measured 2026-09-18, three times in one day on
# MeshWeaver.Plugins, every hand re-run minutes later green with NO code change:
#
#   PR #2071   run 35328406174 attempt 1   17 downstream jobs red   (page 1 ~2,600 runs behind)
#   PR #2043   run 35334000904             17 downstream jobs red
#   main       run 35345612101 attempt 1   18 downstream jobs red   (left MAIN without a verdict,
#                                                                    the branch the PR ceiling
#                                                                    for the whole fleet is
#                                                                    derived from)
#
# 🚨 THE SAFETY PROPERTY IS UNCHANGED. A page that is still stale after the re-reads is still
# REFUSED, never resolved from — the same bounded retry already accepted for a 502, and for the
# same reason: GitHub failing to answer is not an answer. What a re-read must never become is a
# gate asking again until it likes the reply, which is why it runs ONLY where the staleness is
# provable (`ceiling_shortfall`) and why the refusal is unconditional once the budget is spent.
STALE_REREADS = 3
STALE_REREAD_BACKOFF_SECONDS = 20.0   # 20 s, 40 s, 60 s — 2 minutes inside the lane's 25.


def settle_page_one(fetch: Fetch, runs: list[dict], passed_ceiling: int | None, *,
                    now: Callable[[], float], sleep: Callable[[float], None],
                    log: Callable[[str], None]) -> tuple[list[dict], str | None, int, int]:
    """Page 1, why it still cannot be the newest page (or None), how many times it was RE-READ, and
    how many of those re-reads came back EMPTY.

    🚨 THE EMPTY COUNT IS RETURNED SEPARATELY because an attempt has THREE outcomes, not two:
    settled, still stale, and empty — and an empty page is not a staleness finding at all, it is a
    page that was not adopted. Collapsing the last two let the refusal claim page 1 "was still
    stale every time" over evidence that said `came back EMPTY`, which is the same class of defect
    as the refusal this function exists to soften: a summary stronger than what was measured.

    🚨 IT PROVES IT RE-READ. One line per re-read, naming how many runs page 1 held and its newest
    main run — because a function that logs only its verdict leaves "re-read twice and it cleared"
    indistinguishable from "the condition never fired", which is the same ambiguity as the refusal
    this exists to remove."""
    why_stale = stale_listing(runs, passed_ceiling, now())
    if why_stale is None or ceiling_shortfall(runs, passed_ceiling) is None:
        return runs, why_stale, 0, 0
    rereads = empties = 0
    for attempt in range(1, STALE_REREADS + 1):
        wait = STALE_REREAD_BACKOFF_SECONDS * attempt
        log(f"  page 1 is PROVABLY stale — {why_stale}. Re-reading it in {wait:.0f}s "
            f"({attempt} of {STALE_REREADS}; MeshWeaver#4750 — three measured pages cleared "
            "within minutes, with no code change)")
        sleep(wait)
        fresh = cd_runs(fetch, 1)
        rereads = attempt
        if not fresh:
            # 🚨 An EMPTY page says NOTHING about staleness — `stale_listing` has no main rows to
            # judge, so adopting it would turn a refusal into a resolution off a page holding no
            # candidates at all. Keep the page that at least had rows, and the refusal it earned.
            empties += 1
            log(f"  re-read {attempt} of {STALE_REREADS}: page 1 came back EMPTY — not adopted; "
                "the previous page and its refusal stand")
            continue
        runs = fresh
        newest = _newest_main_run(runs)
        why_stale = stale_listing(runs, passed_ceiling, now())
        log(f"  re-read {attempt} of {STALE_REREADS}: page 1 holds {len(runs)} run(s), newest "
            f"main-cd #{newest if newest is not None else '?'} — "
            + ("SETTLED, resolving from it" if why_stale is None else f"still stale ({why_stale})"))
        if why_stale is None:
            return runs, None, rereads, empties
        if ceiling_shortfall(runs, passed_ceiling) is None:
            # The ceiling cleared and the AGE branch tripped instead. That one has no crisp
            # condition to re-read FOR, so it is refused here rather than burning the budget.
            break
    return runs, why_stale, rereads, empties


def reread_note(rereads: int, empties: int = 0) -> str:
    """What is APPENDED to the #4433 refusal when page 1 was re-read before it.

    🚨 It says what the re-reads ACTUALLY SAW, and an EMPTY re-read is not a stale one. Claiming
    page 1 "was still stale every time" over a log that says `came back EMPTY` is a summary
    stronger than the evidence under it — the same defect the refusal itself had — and the two
    have different next steps, so they are counted and worded apart.

    🚨 The sentence this follows is kept BYTE-FOR-BYTE, including the clause that is no longer
    true of the ceiling branch ("the resolver refuses rather than re-reading"). A transient-retry
    steward keys its signature on that exact text (MeshWeaver.Plugins#2077/#2123), and a re-worded
    refusal would silently stop being recognised as the known transient it is — on a red nobody is
    reading, which is when a signature has to work. So the correction is appended, where it costs
    no signature, rather than edited in."""
    if rereads <= 0:
        return ""
    seconds = sum(STALE_REREAD_BACKOFF_SECONDS * n for n in range(1, rereads + 1))
    stale = rereads - empties
    if empties <= 0:
        saw = "and was still stale every time"
    elif stale <= 0:
        saw = (f"and every one of the {empties} came back EMPTY — an empty page is never adopted, "
               "so the refusal stands on the page that had rows")
    else:
        saw = (f"— {stale} came back still stale and {empties} came back EMPTY (an empty page is "
               "never adopted, so the refusal stands on the page that had rows)")
    return (f" [MeshWeaver#4750: page 1 WAS re-read {rereads} time(s) over ~{seconds:.0f}s before "
            f"this refusal {saw}; what each re-read held is logged line by line above. The "
            "sentence before this one predates the re-reads and is kept verbatim because a "
            "transient-retry signature keys on it.]")


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


def run_jobs_of_attempt(fetch: Fetch, repo: str, run_id: int, attempt: int) -> list[dict]:
    """Every job record of ONE attempt of a run.

    🚨 `/actions/runs/{id}/jobs` answers with the LATEST attempt's records, and a partial re-run
    (`rerun-failed-jobs`) re-creates a record for every job of the new attempt — including the ones
    it did not re-run — carrying NONE of the earlier attempt's annotations (#4491). An annotation
    therefore belongs to an ATTEMPT, not to a run, and a reader that wants one has to say which.
    """
    jobs: list[dict] = []
    page = 1
    while True:
        data = fetch(f"/repos/{repo}/actions/runs/{run_id}/attempts/{attempt}/jobs"
                     f"?per_page=100&page={page}")
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


def attribution_of(fetch: Fetch, jobs: list[dict], run_number: int,
                   receipts: dict[tuple[int, int], PublicationSource]
                   ) -> tuple[PublicationSource | None, ProvenanceUnavailable | None]:
    """`publication_source` as a VALUE rather than a raise — exactly one of the two is set.

    🚨 Why this exists at all (#4780): under `--verify-source` a sha freeze has to be tested against
    the RECEIPT's source-sha, which means the attribution must be read BEFORE the decisions the
    freeze governs — while a run that cannot produce a receipt must not abort the scan there, since
    measured on live core CD (2026-09-13) eleven of the newest fourteen main-cd runs cannot produce
    one and almost none of them is the frozen run. A caller therefore needs to *hold* the failure
    and decide later, which an exception crossing two decision points cannot express. The caller
    memoizes the pair, so the receipt is fetched once per run however many times it is consulted.
    """
    try:
        return publication_source(fetch, jobs, run_number, receipts), None
    except ProvenanceUnavailable as error:
        return None, error


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
# 🚨 The constants live with the ceiling reader below, and ONLY there. This block used to repeat
# them here, where the later definitions silently overrode it — including a NARROWER `NOTICE_SET`
# that would not have matched a prerelease set name had the order ever flipped. An edit made to a
# definition that never runs is the trap; there is one definition now.


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
# How the page is READ so that `MAIN_RUNS_EXAMINED` runs actually survive the event filter below.
# The page is deliberately wider than the limit (most main runs vouch, so one page is normally
# enough and the extra costs nothing), and the page count is a bound, not a target.
MAIN_PAGE_SIZE = 50
MAIN_PAGES_EXAMINED = 3
# 🚨 EVERY main run that goes through the FULL gate set vouches, not `push` alone. The daily poll,
# the release dispatch and a manual dispatch never NARROW what they build (node-repo invariant #9:
# a publishing or release-follow trigger builds EVERYTHING), and each publishes the same verdict
# annotation this reader parses — so a green one of them IS "main has passed on this set". Filtering
# to `push` made the ceiling only as fresh as the last MERGE: a repository whose main is quiet for a
# day kept every pull request on a day-old set, even though its own daily poll had passed on a newer
# one. It does NOT rescue a RED main, and must not: a red main is exactly when the ceiling has to
# hold (MeshWeaver.Plugins#1947).
MAIN_EVENTS_THAT_VOUCH = ("push", "repository_dispatch", "schedule", "workflow_dispatch")
PLATFORM_REF_JOB = "Resolve the released platform"
# 🚨 THE WALK BACK OVER ATTEMPTS IS BOUNDED, like every other walk this module makes. Each attempt
# consulted costs a jobs read AND an annotations read, and `run_attempt` has no ceiling of its own,
# so an unbounded walk spends its budget exactly during the incident that made someone re-run those
# runs. Four is deep enough for the case that licensed the fallback at all — one partial re-run
# after a flake, occasionally two — and a truncation is SAID rather than passed off as an answer.
CEILING_ATTEMPTS_WALKED = 4
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


class Ceiling(NamedTuple):
    """What the ceiling reader established, and what to print when it established nothing.

    `refusal` is EMPTY whenever the caller may proceed — a ceiling was read, or a freeze skipped the
    ceiling entirely. A non-empty `refusal` is the WHOLE red text, remedy-ordered (`ceiling_refusal`
    below); it is the only thing the annotation carries, because the per-run `notes` belong in the
    log above it and not in the sentence a reader acts on."""
    number: int | None
    notes: list[str]
    refusal: str = ""


class CeilingEvidence(NamedTuple):
    """Everything the reader SAW while failing to establish a ceiling.

    Every field comes from the ONE listing call and the job reads that followed it. Nothing here is
    re-read to write a message: the refusal explains the read it already made, and the test that
    would falsify it is handed to the READER, never performed by the resolver (a gate that decides
    its own input must be wrong and asks again is a gate testing its own inputs)."""
    repo: str
    listed: int                  # rows the listing returned, before the vouching-event filter
    total: int | None            # the listing's own `total_count`, when it sent one
    examined: int                # rows left after that filter — the runs actually read
    newest_created: str          # `created_at` of the NEWEST row examined ("" when there is none)
    newest_id: int | None
    newest_url: str
    age_hours: float | None      # of `newest_created`, at the moment of the refusal
    predating: int               # examined rows carrying no `Resolve the released platform` job
    unreadable: int              # examined rows whose annotations GitHub would not serve
    silent: int                  # rows that carried the job and published NO notice at all
    ambiguous: int               # rows that published two different sets under the notice title
    # 🚨 SEPARATE FROM `silent`, and the separation is the point (Copilot review on
    # MeshWeaver.Crm#128). A run that PUBLISHED a notice whose message names no set has answered —
    # the fault is a malformed notice or a schema change that moved the set out of the message, and
    # the remedy is to look at that notice. Counted as `silent` it was reported as a run that
    # "published no notice", which is the opposite of true and sends the reader to fix `main` when
    # main is doing its job. #4493 split the two per-run NOTES; the aggregate kept conflating them,
    # so the refusal contradicted the note three lines above it.
    unparseable: int             # rows whose notice was PRESENT and named no parseable set


def _ago(hours: float | None) -> str:
    if hours is None:
        return "age unknown"
    if hours < 48:
        return f"{hours:.0f} h ago"
    return f"{hours / 24:.0f} days ago"


REMEDY_MARK = re.compile(r"^ *(\d+) · ", re.MULTILINE)


def remedy_blocks(text: str) -> list[str]:
    """The refusal's numbered remedies, in the order a reader meets them."""
    marks = list(REMEDY_MARK.finditer(text))
    return [text[mark.start():(marks[index + 1].start() if index + 1 < len(marks) else len(text))]
            for index, mark in enumerate(marks)]


def misdirects_to_main(text: str) -> bool:
    """True when a refusal tells the reader to FIX MAIN without the evidence that implicates main.

    🚨 This is the defect MeshWeaver#4664 measured, not a style rule. The refusal below has TWO
    causes with different remedies — GitHub served this call a stale run listing, or main really
    published no passing run — and the old text put `Fix main` LAST, as the closing instruction,
    with the stale-listing sentence buried mid-paragraph behind twelve skip notes. Three readers
    met it; two spent their time on main; the third reported that no pull request in the repository
    could resolve a set while four were resolving one in the same minutes.

    So a "fix main" instruction is only ever legitimate INSIDE a remedy that carries what makes
    main the suspect: `ONLY IF` (the listing test came first and could still fall the other way) or
    `DID carry` (runs that really do carry the reporting job and still named no set). A refusal with
    no numbered remedies at all is the old paragraph shape and is rejected outright. The control
    that proves this predicate bites is `MEASURED_MISDIRECTION` in the self-test: the literal text
    this file produced on 2026-09-17, which it must reject."""
    blocks = remedy_blocks(text)
    if not blocks:
        # No ordered remedies at all — the paragraph shape. Any "fix main" in it is unguarded.
        return "fix main" in text.lower()
    first = REMEDY_MARK.search(text)
    if "fix main" in text[:first.start() if first else 0].lower():
        return True          # said before ANY remedy, so before any test that could falsify it
    blaming = [block for block in blocks if "fix main" in block.lower()]
    return not all(_cites_why_main(block) for block in blaming)


def _cites_why_main(block: str) -> bool:
    """Does this remedy carry what makes `main` the suspect?

    🚨 A `DID carry` claim over ZERO runs is not evidence, it is the same misdirection wearing the
    marker (Copilot review on #4681): a mixed page can leave every row unread and still produce the
    sentence. So the count is read, not just the phrase."""
    low = block.lower()
    if "only if" in low:
        return True
    return "did carry" in low and not re.search(r"\b0 of the\b", low)


def ceiling_refusal(evidence: CeilingEvidence) -> str:
    """The RED text for a ceiling that could not be established — evidence first, remedies ordered.

    🚨 THE ORDER IS THE FIX (MeshWeaver#4664). Two conditions produce this refusal and they have
    different remedies, so the message leads with the one the evidence points at and marks the other
    conditional. The discriminator is STRUCTURAL and carries no clock: a page whose every row
    predates the job that publishes the verdict cannot have been read for its content at all, while
    a page whose rows DO carry that job is main answering for itself. No threshold was added for
    this — a bound picked to sort two messages would be a bound nobody could ever tune, and it
    would eventually flip the wording on a quiet-but-healthy repository."""
    ev = evidence
    runs_url = (f"https://github.com/{ev.repo}/actions/workflows/{SATELLITE_CD_WORKFLOW}"
                "?query=branch%3Amain+is%3Asuccess")
    counted = [f"{ev.examined} successful `{SATELLITE_CD_WORKFLOW}` run(s) on {ev.repo} main "
               f"({'/'.join(MAIN_EVENTS_THAT_VOUCH)}) examined, from {ev.listed} row(s) listed"
               + (f" of {ev.total} the listing declares" if ev.total is not None else "")]
    if ev.newest_created:
        counted.append(f"newest examined: run {ev.newest_id}, created {ev.newest_created} "
                       f"({_ago(ev.age_hours)}) — {ev.newest_url}")
    if ev.predating:
        counted.append(f"{ev.predating} carried no `{PLATFORM_REF_JOB}` job at all — they predate "
                       "it, so not one of them COULD name a set")
    if ev.silent:
        counted.append(f"{ev.silent} DID carry that job and published no `{NOTICE_TITLE}` notice")
    if ev.unparseable:
        counted.append(f"{ev.unparseable} DID publish a `{NOTICE_TITLE}` notice that names no set "
                       f"this reader can parse — present, not absent: read the notice itself")
    if ev.ambiguous:
        counted.append(f"{ev.ambiguous} DID carry it and published two different sets under "
                       f"`{NOTICE_TITLE}` — unreadable, skipped")
    if ev.unreadable:
        counted.append(f"{ev.unreadable} had annotations GitHub would not serve")
    if ev.listed == 0 and (ev.total or 0) > 0:
        counted.append(f"🚨 the page returned NO rows while declaring total_count={ev.total} — it "
                       "contradicts itself, which is a bad read and not an empty history")

    probe = (f"If ANY successful run there is NEWER than {ev.newest_created}"
             if ev.newest_created else "If ANY successful run is listed there at all")
    listing_test = (
        f"TEST THE LISTING — one click, and it is the cause measured three times.\n"
        f"    Open {runs_url}\n"
        f"    {probe}, GitHub served THIS CALL a stale page:\n"
        f"    main is fine, and nothing about main needs fixing. Measured 2026-09-14 "
        f"(MeshWeaver.Plugins\n"
        f"    run 34822109263, a page 26 days old) and 2026-09-17 (MeshWeaver.Plugins PR #2038, a "
        f"page\n"
        f"    five weeks old, while four other pull requests resolved a set in the same minutes).\n"
        f"    REMEDY: make it read again — `Re-run all jobs` on this run, or push an empty commit.\n"
        f"    Re-run the WHOLE run, not just the failed jobs: a failed-jobs re-run can hand a later "
        f"job\n"
        f"    the earlier attempt's artefacts (MeshWeaver#4303). The resolver does NOT re-read by "
        f"itself —\n"
        f"    a run listing is its INPUT, and a gate never tests its own inputs.")
    # 🚨 THE TEST IS "WAS ANY EVIDENCE ABOUT MAIN READ AT ALL", not "which single skip reason
    # covered the whole page" (Copilot review on #4681). Counting `predating == examined` and
    # `unreadable == examined` separately left a MIXED page — 6 rows predating the job, 6 whose
    # annotations GitHub refused — leading with `main` while naming ZERO runs that could implicate
    # it. Only a row that CARRIED the reporting job and still named no set says anything about
    # main; where there is none, the listing leads.
    answered_about_main = ev.silent + ev.unparseable + ev.ambiguous
    listing_first = answered_about_main == 0
    if listing_first:
        remedies = [
            listing_test,
            (f"ONLY IF that is really main's newest success — nothing newer is listed — has main "
             f"published\n"
             f"    nothing that names a set. Then it IS main: main is red, or this repository has "
             f"not yet\n"
             f"    merged the `{PLATFORM_REF_JOB}` job that publishes the verdict. Fix main."),
        ]
    else:
        remedies = [
            (f"MAIN'S OWN ANSWER — {answered_about_main} of the {ev.examined} run(s) examined "
             f"DID carry the\n"
             f"    `{PLATFORM_REF_JOB}` job and still named no set this reader can use. That is "
             f"main's own\n"
             f"    answer and not a reading fault: open the newest ({ev.newest_url}) and read what "
             f"its\n"
             f"    `{NOTICE_TITLE}` notice says. Fix main."),
            (f"IF THAT LOOKS WRONG, test the listing too: open {runs_url}\n"
             f"    If a successful run there is NEWER than {ev.newest_created}, this call was "
             f"served a stale page\n"
             f"    instead — `Re-run all jobs`, or push an empty commit (measured three times, "
             f"MeshWeaver#4664).\n"
             f"    The resolver does not re-read by itself: a gate never tests its own inputs."),
        ]
    remedies.append(
        "TO PROCEED WITHOUT EITHER: set the repository VARIABLE MW_PLATFORM_REF to one set "
        "(`X.Y.Z-ci.N`\n"
        "    or a core sha). A freeze overrides the ceiling — it is an instruction for an incident, "
        "not a\n"
        "    way around a red main.")
    ordered = "\n".join(f"  {index} · {block}" for index, block in enumerate(remedies, start=1))
    return (
        f"this run follows `main` (--passed-on-main): it resolves the newest sealed platform set "
        f"that\n{ev.repo} `main` has ALREADY PASSED on, and no run naming one could be READ.\n"
        f"\n"
        f"🚨 TWO DIFFERENT THINGS PRODUCE THIS RED and they have different remedies — GitHub served "
        f"this\ncall a stale run listing (per call, measured three times), or main really has passed "
        f"nothing\nthat names a set. The evidence decides which; the remedies are ordered by what it "
        f"says.\n"
        f"\n"
        f"WHAT WAS READ (one call, no re-read):\n"
        + "".join(f"  · {line}\n" for line in counted)
        + f"\n{ordered}\n"
        f"\nNOT AN OPTION: falling back to the newest sealed set. That is what reddened every open "
        f"pull\nrequest at once, and it is the reason this rule exists.")


def ceiling_for(fetch: Fetch, repo: str, freeze: str | None,
                log: Callable[[str], None] = print) -> Ceiling:
    """The ceiling for a run that asked to follow its own main, or the red text that says why not.

    🚨 A FREEZE OVERRIDES THE CEILING, INCLUDING AN UNREADABLE ONE. `MW_PLATFORM_REF` is an
    instruction for an incident — a bisect, an upstream outage — and the likeliest moment to need
    it is precisely when `main` is red and has passed nothing recently. Computing the ceiling first
    and refusing on it would take the freeze away exactly then, so a freeze skips the ceiling
    entirely rather than being checked against it.
    """
    if freeze:
        return Ceiling(None, [f"freeze {freeze} overrides the main ceiling — not consulted"], "")
    return main_passed_ceiling(fetch, repo, log=log)


def main_passed_ceiling(fetch: Fetch, repo: str, limit: int = MAIN_RUNS_EXAMINED,
                        log: Callable[[str], None] = print,
                        now: Callable[[], float] = time.time) -> Ceiling:
    """The highest core-CD run number this repo's main has PASSED on, one note per run examined.

    `Ceiling.number is None` means it could not be established from the newest `limit` successful
    main runs — which is a RED verdict for the caller, never a licence to take the newest sealed
    set: that silent fallback would put every pull request back on an unvouched set, which is
    exactly what this rule exists to prevent. The red text itself is `Ceiling.refusal`.
    """
    notes: list[str] = []
    best: int | None = None
    predating = unreadable_runs = silent = ambiguous = unparseable = 0
    # The event filter is applied HERE, not in the query: the API takes ONE event, and every event
    # in MAIN_EVENTS_THAT_VOUCH counts. `branch=main` already excludes pull-request and merge-queue
    # runs; the filter says so anyway, because a run that did not go through the full gate set must
    # never vouch for a platform set.
    #
    # 🚨 SO THE PAGE MUST BE READ UNTIL `limit` runs SURVIVE THE FILTER, not once. Asking for
    # `per_page={limit}` and then filtering was a silent under-read: every non-vouching run on the
    # page consumed one of the twelve slots, so a page carrying enough of them yields fewer examined
    # runs than intended — and, in the limit, NONE, which this function reports as "main has
    # published no passing run to follow". That refusal is a RED on every satellite pull request,
    # and it would be describing the page's composition rather than main's state. Filtering in the
    # query cannot fix it either, because the API takes one event and four of them vouch.
    listed: list[dict] = []
    runs: list[dict] = []
    total = None
    exhausted = False
    for page in range(1, MAIN_PAGES_EXAMINED + 1):
        # 🚨 THE LISTING IS READ INSIDE THE REFUSAL (Copilot review on MeshWeaver.SocialMedia#186).
        # Every per-run read below already turns a `ResolutionError` into a SKIP with a named
        # reason. This one did not — and it is the FIRST call the option makes, so a GitHub failure
        # here left `main()` as an uncaught traceback: no `::error`, no step summary, and no sentence
        # saying whether `main` had passed nothing or GitHub had simply not answered. Those are
        # opposite remedies, and the option whose entire purpose is to fail with a NAMED red verdict
        # failed with a stack trace instead.
        #
        # The page loop gives the two cases their own answers, and the distinction is the one this
        # function was just taught to make: page 1 failing means NOTHING was read, which is a
        # refusal naming the listing. A LATER page failing means the read stopped early with rows
        # already in hand — which `exhausted` stays False for, so it falls through to the
        # "not an exhaustion condition" note rather than being reported as main's state.
        try:
            data = fetch(f"/repos/{repo}/actions/workflows/{SATELLITE_CD_WORKFLOW}/runs"
                         f"?branch=main&status=success&per_page={MAIN_PAGE_SIZE}&page={page}")
        except ResolutionError as error:
            if page == 1:
                notes.append(f"the run listing for {repo} main could not be read: {error}")
                return Ceiling(None, notes, (
                    f"❌ WHICH SET {repo}'s `main` HAS PASSED COULD NOT BE READ — the run LISTING "
                    f"itself did not answer.\n"
                    f"    GitHub said: {error}\n"
                    f"    This is NOT evidence about main. Nothing was read, so nothing is known "
                    f"about what main\n"
                    f"    has passed — and taking the newest sealed set on a failed read is "
                    f"precisely what this rule\n"
                    f"    exists to prevent. The run is RED instead.\n"
                    f"    REMEDY: re-run this run. A listing read that failed is transient far more "
                    f"often than not;\n"
                    f"    re-run the WHOLE run, not just the failed jobs (MeshWeaver#4303). If it "
                    f"fails again with\n"
                    f"    the same status, check https://www.githubstatus.com/ before looking at "
                    f"this repository.\n"
                    f"    TO PROCEED WITHOUT EITHER: set the repository VARIABLE MW_PLATFORM_REF "
                    f"to one set (`X.Y.Z-ci.N`)."))
            notes.append(f"page {page} of the {repo} main listing could not be read ({error}) — "
                         f"the read stopped there with {len(runs)} vouching run(s) already in hand")
            break
        if total is None:
            total = data.get("total_count")
        got = list(data.get("workflow_runs") or [])
        listed.extend(got)
        runs.extend(run for run in got
                    if str(run.get("event") or "push") in MAIN_EVENTS_THAT_VOUCH)
        # Enough have SURVIVED the filter, or the listing is genuinely exhausted — a short page, or
        # every row `total_count` promised already read.
        if len(runs) >= limit or len(got) < MAIN_PAGE_SIZE or (
                isinstance(total, int) and len(listed) >= total):
            exhausted = True
            break
    runs = runs[:limit]
    # 🚨 THE BOUND IS NOT AN EXHAUSTION CONDITION, and saying otherwise would re-make the very bug
    # this function was just fixed for. If the page budget ran out while the listing still had rows,
    # "not enough vouching runs were FOUND" and "there are none" are different facts: the first is a
    # read that stopped early, the second is a statement about main. Reporting the first as the
    # second is how a bounded read becomes a false RED.
    if not exhausted and len(runs) < limit:
        notes.append(
            f"read {len(listed)} run(s) over {MAIN_PAGES_EXAMINED} page(s) of "
            f"{SATELLITE_CD_WORKFLOW} on {repo} main and found {len(runs)} that vouch "
            f"({'/'.join(MAIN_EVENTS_THAT_VOUCH)}) — the listing was NOT exhausted, so this is a "
            "read that stopped early, not evidence that main has passed on nothing. Raise "
            "MAIN_PAGES_EXAMINED or MAIN_PAGE_SIZE if this recurs.")

    def evidence() -> CeilingEvidence:
        """The refusal's inputs — computed from what was ALREADY read, never from a second call."""
        stamped = [(stamp, run) for run in runs for stamp in [_created(run)] if stamp is not None]
        newest = max(stamped, key=lambda pair: pair[0])[1] if stamped else None
        created = str(newest.get("created_at") or "") if newest else ""
        run_id = int(newest["id"]) if newest and newest.get("id") is not None else None
        return CeilingEvidence(
            repo=repo, listed=len(listed),
            total=int(total) if isinstance(total, int) else None,
            examined=len(runs), newest_created=created, newest_id=run_id,
            newest_url=(str(newest.get("html_url") or "") if newest else "")
            or (f"https://github.com/{repo}/actions/runs/{run_id}" if run_id else ""),
            age_hours=((now() - _created(newest)) / 3600
                       if newest and _created(newest) is not None else None),
            predating=predating, unreadable=unreadable_runs, silent=silent,
            ambiguous=ambiguous, unparseable=unparseable)

    if not runs:
        notes.append(f"no successful run of {SATELLITE_CD_WORKFLOW} on {repo} main "
                     f"({'/'.join(MAIN_EVENTS_THAT_VOUCH)}) in the newest {limit} — nothing to "
                     "read a passed set from")
        return Ceiling(None, notes, ceiling_refusal(evidence()))
    # 🚨 THE BOUND COUNTS RUNS, NOT NOTES (Copilot review, #4493). It used to read
    # `len(notes) >= limit`, which was a proxy for "runs examined" only while every run emitted
    # exactly one note. The attempt fallback below emits a SECOND note for the run it rescues, so
    # the proxy would stop the walk after as few as six runs of twelve — dropping up to half the
    # evidence and, because `best` is a max over the runs examined, answering with a LOWER ceiling
    # or a false RED. That is the very failure this function exists to prevent, so the counter is
    # now the thing it claims to be.
    examined = 0
    for run in runs:
        run_id = int(run["id"])
        examined += 1
        # 🚨 NEWEST ATTEMPT FIRST, then older ones (#4491). `/runs/{id}/jobs` serves the latest
        # attempt, and after a partial re-run every job of that attempt has a FRESH record — the
        # carried-over `Resolve the released platform` among them — with none of the annotations
        # the attempt that actually ran it published. Reading only the latest attempt therefore
        # loses the run's contribution to the ceiling while the run still reads `success`, and the
        # satellite then holds every pull request on an older set and says `main` has not passed on
        # the newer one, which is FALSE. Measured 2026-09-16 on MeshWeaver.Plugins: run 35073843357
        # resolved 3.0.0-ci.8721, died on an artifact-service 403, was re-run to success — attempt
        # 1's job carried the annotation, attempt 2's record for the same job carried zero.
        #
        # The NEWEST attempt that carries one wins, so a genuine re-resolution (a full re-run, or a
        # re-run OF this job) still decides; an older attempt is consulted only where the newer
        # record is silent, which is exactly the carried-over case.
        #
        # 🚨 AN UNREADABLE ATTEMPT IS NOT A SILENT ONE (Copilot review, #4493). Only a response
        # that came back and carried no matching annotation licenses the walk to an older attempt.
        # A read that FAILED proves nothing about what that attempt published — and the newer
        # attempt is exactly the one that may hold a genuine RE-RESOLUTION — so falling back on it
        # would publish an older attempt's stale verdict under a note asserting the newer one
        # carried none. That note would be false in the same way #4491's "main has not passed on
        # it yet" was false. So any unreadable attempt STOPS the walk and SKIPS the run, which is
        # this module's standing discipline: a run whose annotation cannot be read is skipped and
        # said so, never guessed at. `best` is a max over the other runs, so one unreadable run
        # costs a data point, never a wrong ceiling.
        attempt_rows: list[dict] | None = None
        searched = 0
        unreadable: str | None = None
        latest_attempt = max(1, int(run.get("run_attempt") or 1))
        floor_attempt = max(1, latest_attempt - CEILING_ATTEMPTS_WALKED + 1)
        for attempt in range(latest_attempt, floor_attempt - 1, -1):
            try:
                jobs_of = (run_jobs_of(fetch, repo, run_id) if attempt == latest_attempt
                           else run_jobs_of_attempt(fetch, repo, run_id, attempt))
            except ResolutionError as error:
                unreadable = f"attempt {attempt} jobs unreadable ({error})"
                break
            jobs = [j for j in jobs_of if j.get("name") == PLATFORM_REF_JOB]
            if not jobs:
                # 🚨 THE FALLBACK IS LICENSED BY A LOST ANNOTATION, NOT BY A MISSING JOB (Copilot
                # review, #4493). #4491's case is narrow and specific: the job IS present in the
                # latest attempt's records and its annotation list is EMPTY, because a partial
                # re-run re-created the record without it. A latest attempt that does not carry
                # the job at all is a different thing entirely — nothing establishes that this run
                # resolved a platform set, and an older attempt's annotation would be asserted on
                # its behalf. Skipping the run is what this reader did before #4491 and is still
                # right; only the empty-annotation case may walk backwards.
                if attempt == latest_attempt:
                    break
                continue
            searched += 1
            try:
                annotations = fetch(f"/repos/{repo}/check-runs/{int(jobs[0]['id'])}/annotations")
            except ResolutionError as error:
                unreadable = f"attempt {attempt} annotations unreadable ({error})"
                break
            candidate = (annotations if isinstance(annotations, list)
                         else annotations.get("annotations") or [])
            # 🚨 ANY NON-EMPTY LIST IS *THIS* ATTEMPT'S ANSWER (Copilot review on
            # MeshWeaver.Education#352, MeshWeaver.Crm#128, MeshWeaver.Reinsurance#221 — one
            # finding against three vendored copies of this file). #4491's licence is a LOST
            # annotation: the record came back EMPTY because a partial re-run re-created it
            # carrying none. This guard asked instead whether any row matched our TITLE, which is
            # strictly broader — a latest attempt that published something ELSE (a `::warning`
            # from a step in the same job lands on the same check run) walked back too, and an
            # older attempt's set was published under a note asserting the newer attempt carried
            # none. That note is false in exactly the way #4491's own sentence was false, and the
            # ceiling it yields is STALE — the one outcome `--passed-on-main` exists to prevent.
            #
            # So the walk stops on ANY answer. Whether that answer NAMES a set is the next
            # branch's question, and it already has both sentences for it: present-but-unparseable
            # and absent (#4493). Only an EMPTY list is a lost record, and only it may look back.
            if candidate:
                attempt_rows = candidate
                if attempt != latest_attempt and any(
                        NOTICE_TITLE in str(row.get("title") or "") for row in candidate):
                    notes.append(
                        f"main run {run_id}: attempt {latest_attempt} carries no "
                        f"`{NOTICE_TITLE}` annotation (a partial re-run re-creates the record "
                        f"without it) — read from attempt {attempt}, which published one (#4491)")
                break
            if attempt == floor_attempt and floor_attempt > 1:
                # The truncation is SAID. A bounded search reported as an exhausted one is a
                # gate that passes having checked less than it claims, and `best` is a max over
                # the runs examined — so this run costs a data point, never a wrong ceiling.
                # 🚨 IT COUNTS WHAT IT READ, NOT THE RANGE IT WALKED (Copilot review on this PR).
                # An intermediate attempt carrying no matching job `continue`s WITHOUT touching
                # `searched`, so "attempts N..M all carry an EMPTY annotation list" could assert
                # emptiness for attempts that were never looked into. Saying more than was read is
                # the whole defect class this change exists to fix.
                notes.append(
                    f"main run {run_id}: the {searched} attempt(s) between {latest_attempt} and "
                    f"{floor_attempt} that carried a `{PLATFORM_REF_JOB}` job each returned an "
                    f"EMPTY annotation list — the walk back stopped after "
                    f"{CEILING_ATTEMPTS_WALKED} attempt(s) and this run is SKIPPED rather than "
                    f"read from an attempt {CEILING_ATTEMPTS_WALKED} re-runs old")
        # The unreadable branch is FIRST: with the walk stopping on the error, an unreadable latest
        # attempt also leaves `searched == 0`, and "no `Resolve the released platform` job" would
        # then be the wrong sentence for it — the job may well be there, we could not look.
        if unreadable is not None:
            unreadable_runs += 1
            notes.append(f"main run {run_id}: {unreadable} — skipped. An unreadable attempt is not "
                         "a silent one, so no older attempt is consulted for this run")
            continue
        if attempt_rows is None and searched == 0:
            predating += 1
            notes.append(f"main run {run_id}: no `{PLATFORM_REF_JOB}` job — skipped")
            continue
        rows = attempt_rows or []
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
            ambiguous += 1
            notes.append(
                f"main run {run_id}: {len(distinct)} DIFFERENT sets published under "
                f"`{NOTICE_TITLE}` ({', '.join(f'#{n}' for n in distinct)}) — a run's own verdict "
                "cannot be told from another annotation on the same job, so this run is SKIPPED "
                "rather than guessed at (#1826). A self-test or probe that emits under the "
                "production title is the usual cause; it must use a title of its own.")
            continue
        found = distinct[0] if distinct else None
        if found is None:
            # 🚨 "PRESENT BUT UNPARSEABLE" IS NOT "ABSENT" (Copilot review, #4493). `named` filters
            # on the title AND the set pattern, so an annotation carrying the production title
            # whose message names no set lands here too — and reported as "no annotation" it hides
            # exactly the case worth seeing: a malformed notice, or a schema change that moved the
            # set out of the message. `rows` is non-empty only when some attempt DID carry a
            # title-matching annotation, so the two branches separate cleanly.
            titled = [row for row in rows if NOTICE_TITLE in str(row.get("title") or "")]
            if titled:
                unparseable += 1
                first = str(titled[0].get("message") or "")
                notes.append(
                    f"main run {run_id}: {len(titled)} `{NOTICE_TITLE}` annotation(s) are PRESENT "
                    f"but none names a set matching `{NOTICE_SET.pattern}` (first message: "
                    f"{first[:120]!r}) — skipped. An annotation that does not PARSE is not an "
                    "absent one, and only this sentence tells them apart")
            else:
                silent += 1
                notes.append(
                    f"main run {run_id}: no `{NOTICE_TITLE}` annotation on any of its "
                    f"{searched} attempt(s) carrying a `{PLATFORM_REF_JOB}` job — skipped")
            continue
        notes.append(f"main run {run_id} passed on core CD #{found}")
        best = found if best is None else max(best, found)
        if examined >= limit:
            break
    if best is None:
        # 🚨 THE REFUSAL NAMES WHAT IT READ, AND ORDERS ITS REMEDIES BY IT (#4664). Three times now
        # GitHub's `status=success` listing has served a page weeks old for ONE call — measured
        # 2026-09-14 (MeshWeaver.Plugins run 34822109263, a page from 08-19) and 2026-09-17
        # (Plugins PR #2038, a page from 08-13 while four sibling pull requests resolved a set in
        # the same minutes). Every row was correctly skipped ("no `Resolve the released platform`
        # job" — they predate the job), the refusal was right, and its wording sent two of the
        # three readers at `main`, which was fine: the old text closed on "Fix main, or set …" and
        # buried the stale-listing sentence mid-paragraph behind twelve skip notes.
        #
        # This is still NOT a retry, and deliberately so: a resolver that decides its own input
        # must be wrong and asks again is a gate testing its own inputs. What changed is that the
        # reader is handed the FALSIFIABLE TEST first, with the evidence it rests on.
        notes.append(f"none of the {len(runs)} successful main run(s) named the set it resolved — "
                     "refusing; the ordered remedies are in the error below")
        return Ceiling(None, notes, ceiling_refusal(evidence()))
    return Ceiling(best, notes, "")


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


# A registry answers these while it is perfectly healthy and the credential is perfectly good:
# 429 under load, 5xx from a node rolling, 408 on a slow upstream. They are answers about THIS
# MOMENT, not about the token — so they are retried, bounded, exactly as a dropped connection is.
# Everything else is a verdict on the first answer and is not retried.
REGISTRY_TRANSIENT = (408, 429, 500, 502, 503, 504)


def registry_blame(code: int, phase: str) -> str:
    """What an HTTP status from the registry actually implicates.

    🚨 One sentence for every status was a CONFIDENTLY WRONG diagnosis (Copilot review on
    MeshWeaver.SocialMedia#181): this read `A 401/403 is the registry credential (ACR_USERNAME /
    ACR_PASSWORD) — not a missing release.` whatever came back, so a reader handed a 503 went and
    rotated a credential that was never implicated. During an incident that is the most expensive
    kind of wrong an error message can be, because it is acted on. A message that names the cause
    must be told by the status; where the status does not tell, it says that instead of guessing.
    """
    if code in (401, 403):
        return ("A 401/403 is the registry CREDENTIAL (ACR_USERNAME / ACR_PASSWORD) — not a "
                "missing release.")
    if code == 404 and phase == "token endpoint":
        return ("A 404 from the TOKEN endpoint is the endpoint, not the tag: this registry does "
                "not serve the OAuth2 token path this resolver asked for.")
    # 🚨 THE COUNT AND THE CAUSE ARE BOTH TOLD BY THE STATUS (Copilot review on this PR). The loop
    # is `range(3)` retrying while `attempt < 2`, so a final refusal was retried TWICE across three
    # attempts — "3 times" overstated it, and in a message whose purpose is to stop a reader chasing
    # the wrong cause an inflated count invites the opposite error. And a 408 is the request not
    # completing, not the registry declining to serve it: one sentence over statuses that do not
    # share a cause is a smaller version of the fault this function was written to fix.
    if code == 408:
        return ("HTTP 408 is a TIMEOUT — the request did not complete in time. It was retried twice "
                "(3 attempts) before this; the credential is not implicated. Re-run.")
    if code == 429:
        return ("HTTP 429 is RATE LIMITING — the registry is throttling this caller, not refusing "
                "it. Retried twice (3 attempts) before this; the credential is not implicated. "
                "Re-run.")
    if code in REGISTRY_TRANSIENT:
        return (f"HTTP {code} is a SERVER ERROR from the registry, retried twice (3 attempts) "
                "before this. The registry answered badly just now; the credential is not "
                "implicated. Re-run.")
    return (f"HTTP {code} at the {phase} is not a status this resolver can attribute — read it "
            "against the registry's own docs before assuming either the credential or the tag.")


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
                # 🚨 A TRANSIENT STATUS IS RETRIED; A VERDICT IS NOT (Copilot review on
                # MeshWeaver.Manufacturing#82). This block wrapped EVERY status and raised on the
                # first answer, so a dropped connection was retried three times while a 429 or 503
                # from the same registry — the transient answer a registry actually gives under
                # load — took the CI critical path RED immediately. The retry structure was
                # already here; only the HTTP path did not use it.
                if error.code in REGISTRY_TRANSIENT and attempt < 2:
                    last = error
                    time.sleep(2 * (attempt + 1))
                    continue
                raise ResolutionError(
                    f"{image}:{tag} {phase} → HTTP {error.code}. "
                    + registry_blame(error.code, phase)) from error
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
        # one set and must keep working in an incident, so it is neither re-read nor refused here.
        why_stale, rereads, empties = None, 0, 0
        if page == 1 and not freeze_kind:
            runs, why_stale, rereads, empties = settle_page_one(
                fetch, runs, passed_ceiling, now=now, sleep=sleep, log=log)
        if why_stale:
            raise ResolutionError(
                f"GitHub served a STALE run listing (MeshWeaver#4433): page 1 of {CORE_CD_WORKFLOW} "
                f"runs on {CORE_REPO} {CORE_BRANCH} cannot be the newest — {why_stale}. Resolving "
                "from it would take an old set and report it as the newest (measured 2026-09-15: "
                "page 1 began ~260 runs behind, twice, and a re-read minutes later was correct). "
                "Re-run this job; the resolver refuses rather than re-reading, because this red is "
                "the harmless answer and a silently old platform is not."
                + reread_note(rereads, empties))
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
            # 🚨 …and under `--verify-source` the sha the freeze is tested against is the RECEIPT's,
            # not `head_sha` (#4780). `head_sha` was the only thing known here, so a run whose
            # receipt names the frozen sha while its head does not was judged NOT to be the frozen
            # run — by the definition of the very option that was passed. The consequence was
            # silent: at `plugins_pending` below, a frozen run whose platform trio is sealed while
            # its Plugins seal is still running was passed over as an ordinary candidate, its
            # receipt never read, and resolution fell through to an OLDER set. A freeze is an
            # instruction for an incident, and that is the path most likely to be used during one
            # and least likely to be noticed. So the attribution moves ahead of the decision for
            # exactly that case; `head_sha` matching still counts, so the ordinary run where the
            # two agree behaves as before, and a receipt that cannot be read leaves the honest "no
            # evidence that this is the frozen run" rather than inventing one.
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
            # The receipt, read at most once per run and only where the answer can CHANGE a decision
            # — a sha freeze under `--verify-source`. Every other path asks for no extra read and
            # behaves byte-identically. `attributed`/`attribution_error` are the memo the
            # `verify_source` block below reuses, so the receipt is never fetched twice.
            attributed: PublicationSource | None = None
            attribution_error: ProvenanceUnavailable | None = None
            if verify_source and freeze_kind == "sha":
                attributed, attribution_error = attribution_of(fetch, jobs, number, receipts)
            if not freeze_kind:
                freeze_names_this_run = False
            elif freeze_kind == "set" or not verify_source:
                # A set freeze is already down to one run number and an unverified sha freeze is
                # already down to one head sha — both filters ran above, so the scan is on that run.
                freeze_names_this_run = True
            elif attributed is not None:
                # 🚨 A RECEIPT THAT EXISTS IS THE ANSWER, and `head_sha` is NOT a second chance
                # (Copilot's review of #4920). Accepting either would resurrect #4242 from the other
                # side: a run whose HEAD matches the freeze while its receipt names a different
                # source — an ordinary re-bake — would be judged the frozen run, and if it is
                # unsealed the escalation below aborts the whole scan before the run whose receipt
                # actually matches is ever reached. Under `--verify-source` the set's sha IS the
                # receipt's, and that is the option's entire definition.
                freeze_names_this_run = attributed[0] == freeze_value
            else:
                # No receipt could be read, so `head_sha` is the only evidence there is. An
                # escalation here is still right: the ProvenanceUnavailable arm below says
                # "unverified" rather than claiming the set is something it could not read.
                freeze_names_this_run = sha == freeze_value
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
                if attributed is None and attribution_error is None:
                    attributed, attribution_error = attribution_of(fetch, jobs, number, receipts)
                try:
                    if attribution_error is not None:
                        raise attribution_error
                    sha, version, set_name = attributed
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


def code_blames_token(text: str) -> bool:
    """The 4xx verdict's wording — which a 5xx must never carry."""
    return "revoked token" in text


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

    # ── a transient GitHub 5xx is retried and named; a 4xx is a verdict on the first answer ──
    import io
    real_urlopen, real_sleep = urllib.request.urlopen, time.sleep

    def http_error(code: int) -> urllib.error.HTTPError:
        return urllib.error.HTTPError("https://api.github.com/x", code, "err", {}, io.BytesIO(b""))

    def scripted(codes: list[int]):
        calls = {"n": 0}

        def urlopen(request, timeout=0):
            calls["n"] += 1
            code = codes[calls["n"] - 1] if calls["n"] <= len(codes) else 200
            if code != 200:
                raise http_error(code)
            return io.BytesIO(b'{"ok": true}')
        return urlopen, calls

    try:
        time.sleep = lambda _seconds: None                      # type: ignore[assignment]
        for label, codes, want_ok, want_calls, says in (
            ("a 502 then a 200 is retried and answers", [502], True, 2, None),
            ("three 5xx then a 200 is still answered (bounded, 4 attempts)", [502, 503, 500], True, 4, None),
            ("four 5xx is RED naming the SERVER, never the token", [502, 502, 502, 502], False, 4,
             "GitHub server error"),
            ("a 404 is a verdict on the FIRST answer — not retried", [404], False, 1, "revoked token"),
        ):
            total += 1
            opener, calls = scripted(codes)
            urllib.request.urlopen = opener                     # type: ignore[assignment]
            try:
                answer = github_fetch_with("t")("/repos/x/y")
                ok, text = answer == {"ok": True}, ""
            except ResolutionError as error:
                ok, text = False, str(error)
            if ok != want_ok or calls["n"] != want_calls or (says and says not in text) \
                    or (not want_ok and code_blames_token(text) and says != "revoked token"):
                failures.append(f"{label}: ok={ok} calls={calls['n']} message={text[:160]!r}")
    finally:
        urllib.request.urlopen, time.sleep = real_urlopen, real_sleep

    # ── 🚨 THE REGISTRY GETS THE SAME TREATMENT GITHUB DOES (Copilot review on
    #    MeshWeaver.Manufacturing#82 and MeshWeaver.SocialMedia#181) ─────────────────────────────
    # Two findings against one block, and they are the same block for the same reason: this
    # resolver retried a TRANSPORT error (a dropped connection) three times while a 429 or a 503
    # from the very same registry — the transient answer a registry actually gives under load —
    # was wrapped and raised on the first try. So the CI critical path went RED on a condition the
    # retry structure sitting around it was built for. And whatever the status, the message said
    # "A 401/403 is the registry credential (ACR_USERNAME / ACR_PASSWORD)", sending every reader of
    # a 503 to check a credential that was never the problem. An error message that names the wrong
    # cause with confidence is worse than one that names none.
    class _Resp(io.BytesIO):
        """Enough of an HTTPResponse for both calls: a JSON body and a headers mapping."""
        def __init__(self, body: bytes = b"{}", headers: dict | None = None):
            super().__init__(body)
            self.headers = headers or {}

        def __enter__(self):
            return self

        def __exit__(self, *_exc):
            return False

    DIGEST = "sha256:" + "d" * 64

    def registry_scripted(codes: list[int | None]):
        """`codes[i]` is the status of call i+1; None means answer normally. Calls alternate
        token endpoint → manifest HEAD, so a pair per attempt."""
        calls = {"n": 0}

        def urlopen(request, timeout=0):
            calls["n"] += 1
            code = codes[calls["n"] - 1] if calls["n"] <= len(codes) else None
            if code is not None:
                raise urllib.error.HTTPError(
                    request.full_url, code, "err", {}, io.BytesIO(b""))
            if "/v2/" in request.full_url:            # the manifest HEAD
                return _Resp(headers={"Docker-Content-Digest": DIGEST})
            return _Resp(b'{"access_token": "tok"}')  # the token endpoint
        return urlopen, calls

    try:
        time.sleep = lambda _seconds: None                      # type: ignore[assignment]
        for label, codes, want, want_calls, says, forbid in (
            ("a 503 from the TOKEN endpoint is transient — retried, then answered",
             [503], DIGEST, 3, None, None),
            ("a 429 from the token endpoint is retried too (a registry under load, not a verdict)",
             [429], DIGEST, 3, None, None),
            ("a 503 from the MANIFEST head is retried — the second call of the pair, same rule",
             [None, 503], DIGEST, 4, None, None),
            ("transient codes are BOUNDED — 3 attempts, then RED naming the registry, not the "
             "token (the script offers 6 refusals; the BOUND is what stops it)",
             [503, 503, 503, 503, 503, 503], None, 3, "HTTP 503", "ACR_USERNAME"),
            ("a 401 is a VERDICT — not retried, and it DOES name the credential",
             [401], None, 1, "ACR_USERNAME", None),
            ("a 403 is a verdict too", [403], None, 1, "ACR_USERNAME", None),
            ("a manifest 404 is ABSENT, not an error — the tag is simply not there",
             [None, 404], "absent", 2, None, None),
            ("a 500 is RED naming HTTP 500, and does NOT blame a credential it cannot implicate",
             [500, 500, 500, 500, 500, 500], None, 3, "HTTP 500", "ACR_USERNAME"),
            # 🚨 The retry COUNT is what the loop does — two retries over three attempts, not three
            # (Copilot review on this PR). An inflated count invites the opposite wrong conclusion.
            ("…and states the retries the loop actually made",
             [500, 500, 500, 500, 500, 500], None, 3, "retried twice (3 attempts)", "retried 3 times"),
            # 🚨 A 408 is the request not completing, not the registry declining to serve it.
            ("a 408 is retried like the rest but described as a TIMEOUT, not as a refusal",
             [408, 408, 408, 408, 408, 408], None, 3, "TIMEOUT", "refusing or rate-limiting"),
            ("…and a 429 says rate limiting, which is a third distinct cause",
             [429, 429, 429, 429, 429, 429], None, 3, "RATE LIMITING", "SERVER ERROR"),
        ):
            total += 1
            opener, calls = registry_scripted(codes)
            urllib.request.urlopen = opener                     # type: ignore[assignment]
            try:
                answer = registry_resolver("u", "p")("reg.example.com/repo", "3.0.0-ci.1")
                got, text = ("absent" if answer is None else answer), ""
            except ResolutionError as error:
                got, text = None, str(error)
            if got != want or calls["n"] != want_calls \
                    or (says and says not in text) or (forbid and forbid in text):
                failures.append(f"{label}: got={got!r} calls={calls['n']} "
                                f"(expected {want!r} in {want_calls}) message={text[:200]!r}")
    finally:
        urllib.request.urlopen, time.sleep = real_urlopen, real_sleep

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
    # `sleep` is a no-op here and in every ceiling case below: a ceiling shortfall now re-reads
    # page 1 first (#4750), and the real `time.sleep` would spend the budget on the wall clock.
    case("a page FRESH by age whose newest run is below main's passed ceiling is RED", False,
         lambda: choose(_fetch_for(aged, sealed_two), _registry(full), tester, portal,
                        log=logs.append, now=lambda: made_at + 3600, passed_ceiling=8676,
                        sleep=lambda _: None),
         lambda message: "STALE" in message and "#8676" in message and "#8207" in message)
    case("…a ceiling AT the page's newest run is not staleness", True,
         lambda: choose(_fetch_for(aged, sealed_two), _registry(full), tester, portal,
                        log=logs.append, now=lambda: made_at + 3600, passed_ceiling=8207),
         lambda c: c.set_name == "3.0.0-ci.8207")
    case("the AGE is read from the newest-created row, whatever the page order", False,
         lambda: choose(_fetch_for(list(reversed(aged)), sealed_two), _registry(full), tester,
                        portal, log=logs.append, now=three_days),
         lambda message: "STALE" in message and made in message)

    # ── #4750: a PROVABLY stale page 1 is RE-READ before it is refused ──────────────────────────
    # 🚨 The CONTROL for the keyed refusal text. An INDEPENDENT literal copy on purpose: asserting
    # a module constant against itself would pass however the refusal were re-worded, and a
    # transient-retry steward keys its signature on this exact sentence
    # (MeshWeaver.Plugins#2077/#2123). If this literal stops matching, the steward stopped
    # recognising the refusal — that is the failure this case exists to make loud.
    KEYED_4433 = ("Re-run this job; the resolver refuses rather than re-reading, because this red "
                  "is the harmless answer and a silently old platform is not.")
    settled = [_run(8676, C, created_at=made)] + aged
    sealed_three = {**sealed_two, 1000 + 8676: _jobs()}
    full3 = {**full, ("mw-plugin-test", "3.0.0-ci.8676"): D1,
             ("memex-portal-ai", "3.0.0-ci.8676"): D2,
             ("mw-plugin-test", C[:7]): D1, ("memex-portal-ai", C[:7]): D2}

    def _flipping_page_one(pages: list[list[dict]]) -> tuple[Fetch, dict]:
        """A fetch whose page 1 answers `pages[0]`, then `pages[1]`, …, repeating the last. Every
        other path is served from the union, so a run chosen off ANY of the pages is readable.
        `state["page1"]` counts the reads — the number this issue is about."""
        state: dict = {"page1": 0, "slept": []}
        base = _fetch_for([row for page in pages for row in page], sealed_three)

        def fetch(path: str):
            if "/runs?" in path and re.search(r"[?&]page=1(?:&|$)", path):
                index = min(state["page1"], len(pages) - 1)
                state["page1"] += 1
                return {"workflow_runs": pages[index]}
            return base(path)
        return fetch, state

    fetch_settles, settles = _flipping_page_one([aged, settled])
    case("#4750: a page 1 stale by the CEILING is RE-READ, and a settled re-read resolves", True,
         lambda: choose(fetch_settles, _registry(full3), tester, portal, log=logs.append,
                        now=lambda: made_at + 3600, passed_ceiling=8676,
                        sleep=settles["slept"].append),
         lambda c: c.set_name == "3.0.0-ci.8676" and settles["page1"] == 2
         and settles["slept"] == [20.0]
         and any(l.strip().startswith("re-read 1 of 3:") and "#8676" in l and "SETTLED" in l
                 for l in logs))

    fetch_stuck, stuck = _flipping_page_one([aged])
    case("#4750: …a page still stale after every re-read is REFUSED, keyed text VERBATIM", False,
         lambda: choose(fetch_stuck, _registry(full), tester, portal, log=logs.append,
                        now=lambda: made_at + 3600, passed_ceiling=8676,
                        sleep=stuck["slept"].append),
         lambda message: KEYED_4433 in message and "#4750" in message
         and f"re-read {STALE_REREADS} time(s)" in message
         and stuck["page1"] == 1 + STALE_REREADS and stuck["slept"] == [20.0, 40.0, 60.0]
         # 🚨 It PROVES it re-read: one line per re-read, naming what page 1 held each time.
         and sum(1 for l in logs if l.strip().startswith("re-read ")) == STALE_REREADS
         and all(f"re-read {n} of {STALE_REREADS}:" in "".join(logs)
                 for n in range(1, STALE_REREADS + 1)))

    fetch_aged, aged_state = _flipping_page_one([aged])
    case("#4750: a page stale by AGE alone is NOT re-read — an inference has nothing to settle",
         False,
         lambda: choose(fetch_aged, _registry(full), tester, portal, log=logs.append,
                        now=three_days, sleep=aged_state["slept"].append),
         lambda message: "STALE" in message and "#4750" not in message
         and aged_state["page1"] == 1 and aged_state["slept"] == [])

    fetch_frozen, frozen = _flipping_page_one([aged])
    case("#4750: a FREEZE reads page 1 ONCE — an incident must not wait on re-reads", True,
         lambda: choose(fetch_frozen, _registry(full), tester, portal, freeze="3.0.0-ci.8203",
                        log=logs.append, now=lambda: made_at + 3600, passed_ceiling=8676,
                        sleep=frozen["slept"].append),
         lambda c: c.set_name == "3.0.0-ci.8203" and frozen["page1"] == 1
         and frozen["slept"] == [])

    fetch_empty, empty = _flipping_page_one([aged, []])
    # 🚨 …and the refusal must SAY empty, never "still stale every time". An empty re-read is not
    # a staleness finding, and a summary stronger than the evidence under it is the very defect
    # this change exists to remove (found by the automatic review on this PR).
    case("#4750: an EMPTY re-read is not ADOPTED — a page with no rows proves no freshness", False,
         lambda: choose(fetch_empty, _registry(full), tester, portal, log=logs.append,
                        now=lambda: made_at + 3600, passed_ceiling=8676,
                        sleep=empty["slept"].append),
         lambda message: "STALE" in message and "#8676" in message and "#8207" in message
         and empty["page1"] == 1 + STALE_REREADS
         and any("came back EMPTY" in l for l in logs)
         and "came back EMPTY" in message and "still stale every time" not in message)
    # …and the MIXED case words BOTH counts, rather than rounding to whichever came last.
    fetch_mixed, mixed = _flipping_page_one([aged, [], aged])
    case("#4750: a MIXED run of re-reads reports the stale and the empty counts separately", False,
         lambda: choose(fetch_mixed, _registry(full), tester, portal, log=logs.append,
                        now=lambda: made_at + 3600, passed_ceiling=8676,
                        sleep=mixed["slept"].append),
         lambda message: "2 came back still stale and 1 came back EMPTY" in message
         and "still stale every time" not in message and mixed["page1"] == 1 + STALE_REREADS)

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

    def _ceiling_case(name: str, fetch: Fetch, expect: int | None, says: str = "",
                      absent: str = "") -> None:
        """`says` is looked for in the per-run notes AND in the refusal — one is the log a reader
        scrolls, the other the sentence they act on, and a case may pin either. `absent` pins the
        opposite: a sentence that must NOT be spoken, for the cases where the wrong behaviour is
        not a wrong NUMBER but a true-sounding note about how a right one was reached."""
        nonlocal total
        total += 1
        result = main_passed_ceiling(fetch, SATELLITE, log=logs.append)
        spoken = result.notes + ([result.refusal] if result.refusal else [])
        if result.number != expect:
            failures.append(f"{name}: ceiling {result.number}, expected {expect}")
        elif says and not any(says in line for line in spoken):
            failures.append(f"{name}: nothing said {says!r} — notes={result.notes}, "
                            f"refusal={result.refusal!r}")
        elif absent and any(absent in line for line in spoken):
            failures.append(f"{name}: said {absent!r}, which must not be said here — "
                            f"notes={result.notes}")

    def _refusal_of(fetch: Fetch) -> str:
        return main_passed_ceiling(fetch, SATELLITE, log=logs.append).refusal

    def _freeze_case(name: str, freeze: str | None, fetch: Fetch, expect_fatal: bool,
                     says: str = "") -> None:
        nonlocal total
        total += 1
        result = ceiling_for(fetch, SATELLITE, freeze, log=logs.append)
        fatal = bool(result.refusal)
        spoken = result.notes + ([result.refusal] if result.refusal else [])
        if fatal != expect_fatal:
            failures.append(f"{name}: fatal={fatal}, expected {expect_fatal}")
        elif says and not any(says in line for line in spoken):
            failures.append(f"{name}: nothing said {says!r} — {spoken}")

    def _refuses(_path: str) -> dict:
        raise AssertionError("a freeze must not consult main at all")

    _freeze_case("a freeze skips the ceiling ENTIRELY — main is never consulted",
                 "3.0.0-ci.8207", _refuses, False, "overrides the main ceiling")
    _freeze_case("…so an unreadable ceiling under a freeze is NOT fatal",
                 "3.0.0-ci.8207", _fetch_main(None, has_run=False), False)
    _freeze_case("…while without a freeze it IS fatal",
                 None, _fetch_main(None, has_run=False), True, "no successful run")

    _ceiling_case("main's newest passed set is read back from its own run notice",
                  _fetch_main("3.0.0-ci.8203"), 8203, "8203")
    _ceiling_case("a prerelease set name in the notice is read too",
                  _fetch_main("3.0.0-rc.1-ci.8203"), 8203, "8203")
    _ceiling_case("no successful main run ⇒ no ceiling (the caller must go RED)",
                  _fetch_main(None, has_run=False), None, "no successful run")
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
                  _fetch_main("3.0.0-ci.8203", job=False), None, "stale page")

    # ── 🚨 THE REFUSAL MUST NOT SEND A READER AT `main` WHEN THE LISTING IS WHAT FAILED (#4664) ──
    # Measured 2026-09-17 on MeshWeaver.Plugins PR #2038 (job 105367690432): GitHub served a page
    # from 2026-08-13 — every row predating this very job — and the message closed on "Fix main, or
    # set the repo VARIABLE MW_PLATFORM_REF". The reader concluded main was broken and that NO pull
    # request in the repository could resolve a platform set, while four were resolving one in the
    # same minutes; a second agent then spent an investigation overturning it. The stale-listing
    # sentence was present and buried: mid-paragraph, behind twelve identical skip notes, ahead of
    # the remedy that closed the message. Third occurrence, second reader misdirected.
    #
    # `MEASURED_MISDIRECTION` is that message, from the job log, and it is the CONTROL: the guard
    # must reject it, or every ordering case under it checks nothing.
    MEASURED_MISDIRECTION = (
        "this run asked to follow `main` (--passed-on-main), so it resolves the newest sealed set "
        "main has already passed, and none could be established. main run 31741597338: no "
        "`Resolve the released platform` job — skipped main run 31738246252: no `Resolve the "
        "released platform` job — skipped none of the 12 successful main run(s) named the set it "
        "resolved (newest run examined was created 2026-08-13T20:35:15Z). If a successful `ci.yml` "
        "push run on main exists that is NEWER than that, GitHub's run listing served a stale page "
        "for this call — re-run this job; the resolver does not retry on its own, because a run "
        "listing is its input and a gate never tests its own inputs. Fix main, or set the repo "
        "VARIABLE MW_PLATFORM_REF to select one set explicitly. Refusing to fall back to the "
        "newest sealed set: that is what reddened every open pull request at once.")
    total += 1
    if not misdirects_to_main(MEASURED_MISDIRECTION):
        failures.append("the ordering guard PASSES the message measured on 2026-09-17 (Plugins job "
                        "105367690432) — it checks nothing, and every case below it is decorative")
    total += 1
    if misdirects_to_main("  1 · TEST THE LISTING — re-run.\n  2 · ONLY IF … then fix main."):
        failures.append("the ordering guard rejects a CONDITIONAL fix-main remedy — it would "
                        "forbid the very wording it exists to require")
    total += 1
    if misdirects_to_main("  1 · nothing implicates anything here."):
        failures.append("a refusal that never mentions main must not be read as misdirection")
    total += 1
    if not misdirects_to_main("Fix main.\n  1 · TEST THE LISTING — re-run.\n  2 · ONLY IF … fix main."):
        failures.append("a `fix main` in the HEADER escapes the guard — the reader meets it before "
                        "any remedy, which is before any test that could falsify it")

    _STALE = _fetch_main("3.0.0-ci.8203", job=False)   # every row examined predates the job
    total += 1
    if misdirects_to_main(_refusal_of(_STALE)):
        failures.append("a listing whose every row predates the job still sends the reader at "
                        f"main:\n{_refusal_of(_STALE)}")
    total += 1
    _first = (remedy_blocks(_refusal_of(_STALE)) or [""])[0]
    if "TEST THE LISTING" not in _first or "Re-run all jobs" not in _first:
        failures.append("remedy 1 must be the falsifiable listing test and the re-run that fixes "
                        f"it — got {_first!r}")
    total += 1
    if "#4303" not in _refusal_of(_STALE):
        failures.append("the remedy must say why the WHOLE run and not the failed jobs alone "
                        "(a failed-jobs re-run can read the earlier attempt's artefacts, #4303)")
    total += 1
    if "MW_PLATFORM_REF" not in (remedy_blocks(_refusal_of(_STALE)) or [""])[-1]:
        failures.append("MW_PLATFORM_REF is the escape hatch and stays LAST — it is an incident "
                        "instruction, never the lead")
    # 🚨 Asserted on the REFUSAL, not on the notes: the old paragraph carried these ids too, in
    # twelve skip lines nobody read. The id has to stand in the sentence the reader acts on.
    total += 1
    if "run 556" not in _refusal_of(_STALE):
        failures.append("the refusal must name the newest run it examined, by id — the month-old "
                        "ids were the tell someone had to go digging for (#4664)")
    total += 1
    if "actions/runs/556" not in _refusal_of(_STALE):
        failures.append("the refusal must carry a link that falsifies the listing in one click")
    total += 1
    _pinned = main_passed_ceiling(_STALE, SATELLITE, log=logs.append,
                                  now=lambda: _created({"created_at": "2026-09-18T08:00:00Z"}))
    if "(4 days ago)" not in _pinned.refusal:
        failures.append("the refusal must say how OLD the newest run it examined is — the tell "
                        f"that unlocked #4664 was a month-old run id; got {_pinned.refusal!r}")

    # 🚨 THE OTHER BRANCH IS NOT THE SAME MESSAGE. Runs that DID carry the job and still named no
    # set are main answering for itself, so that refusal leads with main and puts the listing test
    # second — the ordering follows the evidence rather than a preference, and both orderings must
    # cite what implicates main before asking anyone to fix it.
    _SILENT = _fetch_main(None)                        # the job ran; it published no notice
    total += 1
    _lead = (remedy_blocks(_refusal_of(_SILENT)) or [""])[0]
    if "MAIN'S OWN ANSWER" not in _lead or "DID carry" not in _lead:
        failures.append("runs that carried the job and named no set must lead with MAIN, citing "
                        f"the runs that implicate it — got {_lead!r}")
    total += 1
    if misdirects_to_main(_refusal_of(_SILENT)):
        failures.append("the main-first refusal blames main without citing what implicates it")
    total += 1
    if "stale page" not in _refusal_of(_SILENT):
        failures.append("even the main-first refusal must keep the listing test — a stale page "
                        "can produce this shape too, and the reader must be able to falsify it")

    # 🚨 A MIXED page reads as no evidence at all, and must lead with the listing (Copilot, #4681).
    # One row predates the job, one row's annotations GitHub refuses: neither skip reason covers the
    # whole page, yet not one row was read for content. Counting the reasons separately put this on
    # the MAIN'S OWN ANSWER branch, naming ZERO runs that carried the job while saying "Fix main".
    def _fetch_main_mixed(path: str):
        core = _fetch_for(two, sealed_two)
        if f"/repos/{SATELLITE}/" not in path:
            return core(path)
        if "/actions/workflows/" in path:
            return {"total_count": 2, "workflow_runs": [
                {"id": 555, "created_at": "2026-08-19T06:00:00Z"},
                {"id": 556, "created_at": "2026-09-14T08:00:00Z"}]}
        if "/actions/runs/555/jobs" in path:
            return {"total_count": 0, "jobs": []}
        if "/actions/runs/556/jobs" in path:
            return {"total_count": 1, "jobs": [{"id": 778, "name": PLATFORM_REF_JOB}]}
        if "/check-runs/778/annotations" in path:
            raise ResolutionError("HTTP 500 (GitHub server error)")
        raise AssertionError(path)

    total += 1
    _mixed = _refusal_of(_fetch_main_mixed)
    if misdirects_to_main(_mixed) or "TEST THE LISTING" not in (remedy_blocks(_mixed) or [""])[0]:
        failures.append("a MIXED page — one row predating the job, one with unreadable "
                        "annotations, neither reason covering it alone — must still lead with the "
                        f"LISTING: no row that could implicate main was read\n{_mixed}")
    total += 1
    if not misdirects_to_main("  1 · MAIN'S OWN ANSWER — 0 of the 12 run(s) examined DID carry the "
                              "job and named no set. Fix main."):
        failures.append("a `DID carry` claim over ZERO runs is not evidence about main — the guard "
                        "must reject it, or the marker becomes a way to say `Fix main` for free")

    def _contradicting(path: str):
        if f"/repos/{SATELLITE}/" in path and "/actions/workflows/" in path:
            return {"total_count": 9, "workflow_runs": []}
        return _fetch_main(None, has_run=False)(path)

    _ceiling_case("a page returning NO rows while declaring total_count>0 is named a bad READ, "
                  "not an empty history",
                  _contradicting, None, "contradicts itself")

    # ── 🚨 AN ANNOTATION BELONGS TO AN ATTEMPT, NOT TO A RUN (#4491) ────────────────────────────
    # `/runs/{id}/jobs` serves the LATEST attempt. After `rerun-failed-jobs`, GitHub re-creates a
    # record for every job of the new attempt — including the ones it did not re-run — carrying
    # none of the earlier attempt's annotations. The run still reads `success`, so its contribution
    # to the ceiling vanishes silently and the satellite pins every pull request to an older set
    # while stating, falsely, that `main` has not passed on the newer one.
    #
    # Measured 2026-09-16 on MeshWeaver.Plugins: run 35073843357 resolved 3.0.0-ci.8721, died on an
    # artifact-service 403 (`FinalizeArtifact`, tests failed: 0), was re-run and concluded success.
    # Attempt 1's `Resolve the released platform` job carried the annotation; attempt 2's record
    # for the same, NOT-re-run job carried zero. Every open PR then resolved 8716 — including the
    # one adopting a core capability that only exists from 8721 on.
    def _fetch_attempts(latest: str | None, earlier: str | None, attempts: int = 2) -> Fetch:
        """A run re-run `attempts` times: the latest attempt's record and attempt 1's disagree."""
        core = _fetch_for(two, sealed_two)

        def fetch(path: str) -> dict:
            if f"/repos/{SATELLITE}/" not in path:
                return core(path)
            if "/actions/workflows/" in path:
                return {"workflow_runs": [
                    {"id": 556, "created_at": "2026-09-14T08:00:00Z", "run_attempt": attempts},
                ]}
            # The ATTEMPT-scoped endpoint must be asked for by path — a reader that keeps using
            # `/runs/{id}/jobs` never reaches this branch and sees only the latest attempt.
            if "/attempts/1/jobs" in path:
                return {"total_count": 1, "jobs": [{"id": 701, "name": PLATFORM_REF_JOB}]}
            if "/jobs" in path:
                return {"total_count": 1, "jobs": [{"id": 702, "name": PLATFORM_REF_JOB}]}
            for job_id, named in ((701, earlier), (702, latest)):
                if f"/check-runs/{job_id}/annotations" in path:
                    return {"annotations": [] if named is None else [
                        {"title": NOTICE_TITLE, "message": f"{named} — core {B[:9]}"}]}
            raise AssertionError(path)
        return fetch

    _ceiling_case("a partial re-run erases the annotation from the latest attempt — the earlier "
                  "attempt that published it is read instead (#4491)",
                  _fetch_attempts(latest=None, earlier="3.0.0-ci.8721"), 8721, "8721")
    _ceiling_case("…and the note SAYS it fell back, naming the attempt, so the log is not silent "
                  "about where the number came from",
                  _fetch_attempts(latest=None, earlier="3.0.0-ci.8721"), 8721,
                  "read from attempt 1")
    _ceiling_case("a genuine RE-RESOLUTION still decides — the NEWEST attempt carrying an "
                  "annotation wins, never the oldest",
                  _fetch_attempts(latest="3.0.0-ci.8730", earlier="3.0.0-ci.8721"), 8730, "8730")
    _ceiling_case("…and that case does NOT claim a fallback happened",
                  _fetch_attempts(latest="3.0.0-ci.8730", earlier="3.0.0-ci.8721"), 8730,
                  "main run 556 passed on core CD #8730")
    _ceiling_case("no attempt carrying the job published one ⇒ still skipped, and the note counts "
                  "the attempts searched rather than implying one was never looked at",
                  _fetch_attempts(latest=None, earlier=None), None, "2 attempt(s)")

    # ── 🚨 AN UNREADABLE ATTEMPT IS NOT A SILENT ONE (Copilot review, #4493) ────────────────────
    # The fallback above is licensed by a response that came back and carried no matching
    # annotation. A read that FAILED licenses nothing: the newer attempt is the one that may hold a
    # genuine re-resolution, so consulting an older one would publish a stale verdict under a note
    # asserting the newer attempt carried none — false in exactly the way #4491's own sentence was
    # false. The three cases below are the guard; the two #4491 cases above are the control that
    # shows the fallback itself is still live and was not simply disabled.
    def _fetch_attempt_unreadable(where: str, earlier: str = "3.0.0-ci.8203") -> Fetch:
        """The LATEST attempt cannot be read; attempt 1 carries an OLDER annotation."""
        core = _fetch_for(two, sealed_two)

        def fetch(path: str) -> dict:
            if f"/repos/{SATELLITE}/" not in path:
                return core(path)
            if "/actions/workflows/" in path:
                return {"workflow_runs": [
                    {"id": 557, "created_at": "2026-09-14T08:00:00Z", "run_attempt": 2}]}
            if "/attempts/1/jobs" in path:
                return {"total_count": 1, "jobs": [{"id": 711, "name": PLATFORM_REF_JOB}]}
            if "/jobs" in path:
                if where == "jobs":
                    raise ResolutionError("GET /jobs: HTTP 500 (GitHub server error) (a fixture)")
                return {"total_count": 1, "jobs": [{"id": 712, "name": PLATFORM_REF_JOB}]}
            if "/check-runs/712/annotations" in path:
                raise ResolutionError("GET /annotations: HTTP 500 (GitHub server error) (a fixture)")
            if "/check-runs/711/annotations" in path:
                return {"annotations": [{"title": NOTICE_TITLE,
                                         "message": f"{earlier} — core {B[:9]}"}]}
            raise AssertionError(path)
        return fetch

    _ceiling_case("the latest attempt's JOB LISTING is unreadable ⇒ the run is SKIPPED, never "
                  "answered from an older attempt's stale verdict",
                  _fetch_attempt_unreadable("jobs"), None, "jobs unreadable")
    _ceiling_case("the latest attempt's ANNOTATIONS are unreadable ⇒ the run is SKIPPED too",
                  _fetch_attempt_unreadable("annotations"), None, "annotations unreadable")
    _ceiling_case("…and the note says an unreadable attempt is not a silent one, so nobody reads "
                  "the skip as `this attempt published nothing`",
                  _fetch_attempt_unreadable("annotations"), None,
                  "An unreadable attempt is not a silent one")

    # ── 🚨 THE BOUND COUNTS RUNS, NOT NOTES (Copilot review, #4493) ─────────────────────────────
    # `len(notes) >= limit` was a proxy for "runs examined" only while every run emitted exactly
    # one note. The attempt fallback emits a SECOND note for each run it rescues, so twelve such
    # runs reach the bound after SIX — and `best` being a max over the runs examined, the answer is
    # the highest set among the first half. Here the newest set is on the LAST of twelve runs, so a
    # note-counting bound answers 8203 where the true ceiling is 8250.
    def _fetch_many_fallback_runs(count: int, base: int, last: int) -> Fetch:
        """`count` runs, each re-run once so its latest attempt lost the annotation."""
        core = _fetch_for(two, sealed_two)

        def fetch(path: str) -> dict:
            if f"/repos/{SATELLITE}/" not in path:
                return core(path)
            if "/actions/workflows/" in path:
                return {"workflow_runs": [
                    {"id": 600 + i, "created_at": "2026-09-14T08:00:00Z", "run_attempt": 2}
                    for i in range(count)]}
            for i in range(count):
                if f"/runs/{600 + i}/attempts/1/jobs" in path:
                    return {"total_count": 1,
                            "jobs": [{"id": 6000 + i * 10 + 1, "name": PLATFORM_REF_JOB}]}
                if f"/runs/{600 + i}/jobs" in path:
                    return {"total_count": 1,
                            "jobs": [{"id": 6000 + i * 10 + 2, "name": PLATFORM_REF_JOB}]}
                if f"/check-runs/{6000 + i * 10 + 1}/annotations" in path:
                    named = last if i == count - 1 else base
                    return {"annotations": [{"title": NOTICE_TITLE,
                                             "message": f"3.0.0-ci.{named} — core {B[:9]}"}]}
                if f"/check-runs/{6000 + i * 10 + 2}/annotations" in path:
                    return {"annotations": []}
            raise AssertionError(path)
        return fetch

    _ceiling_case("twelve runs that each fell back emit two notes apiece — every one is still "
                  "examined, so the newest set on the LAST of them is the ceiling",
                  _fetch_many_fallback_runs(MAIN_RUNS_EXAMINED, 8203, 8250), 8250, "8250")

    # ── 🚨 THE FALLBACK IS LICENSED BY A LOST ANNOTATION, NOT A MISSING JOB (review, #4493) ─────
    # #4491's case is the job being PRESENT with an EMPTY annotation list. A latest attempt that
    # does not carry the job at all establishes nothing about what this run resolved, and reviving
    # an older attempt's annotation would assert a set on its behalf. That run is skipped, as it
    # was before #4491 — the two cases must not share a branch.
    def _fetch_attempt_without_job(earlier: str = "3.0.0-ci.8203") -> Fetch:
        core = _fetch_for(two, sealed_two)

        def fetch(path: str) -> dict:
            if f"/repos/{SATELLITE}/" not in path:
                return core(path)
            if "/actions/workflows/" in path:
                return {"workflow_runs": [
                    {"id": 558, "created_at": "2026-09-14T08:00:00Z", "run_attempt": 2}]}
            if "/attempts/1/jobs" in path:
                return {"total_count": 1, "jobs": [{"id": 721, "name": PLATFORM_REF_JOB}]}
            if "/jobs" in path:                       # the LATEST attempt carries some other job
                return {"total_count": 1, "jobs": [{"id": 722, "name": "Something else"}]}
            if "/check-runs/721/annotations" in path:
                return {"annotations": [{"title": NOTICE_TITLE,
                                         "message": f"{earlier} — core {B[:9]}"}]}
            raise AssertionError(path)
        return fetch

    _ceiling_case("a latest attempt that does not carry the platform-ref job at all ⇒ the run is "
                  "SKIPPED, not answered from an older attempt that did",
                  _fetch_attempt_without_job(), None, f"no `{PLATFORM_REF_JOB}` job")

    # ── 🚨 AN ANSWER THAT IS NOT THE ONE WE WANTED IS STILL AN ANSWER (review on #352/#128/#221) ─
    # #4491's licence is a LOST annotation — an EMPTY list, because a partial re-run re-creates the
    # record carrying none. The guard implemented "no annotation matching our TITLE", which is
    # strictly broader: a latest attempt that published SOMETHING ELSE (a `::warning` from a step
    # in the same job lands on the same check run) took the fallback too, and an older attempt's
    # set was then asserted under a note saying the newer attempt carried none. That note is false
    # in exactly the way #4491's own sentence was false, and the ceiling it publishes is STALE —
    # which is the one outcome `--passed-on-main` exists to prevent.
    #
    # The distinction is the whole of it: EMPTY means the record lost what the attempt published;
    # NON-EMPTY means the attempt published this, and whether it names a set is the next branch's
    # question, never a reason to speak for an older attempt.
    def _fetch_attempt_untitled(earlier: str = "3.0.0-ci.8203") -> Fetch:
        """The LATEST attempt's list came back NON-EMPTY, carrying an unrelated annotation."""
        core = _fetch_for(two, sealed_two)

        def fetch(path: str) -> dict:
            if f"/repos/{SATELLITE}/" not in path:
                return core(path)
            if "/actions/workflows/" in path:
                return {"workflow_runs": [
                    {"id": 560, "created_at": "2026-09-14T08:00:00Z", "run_attempt": 2}]}
            if "/attempts/1/jobs" in path:
                return {"total_count": 1, "jobs": [{"id": 741, "name": PLATFORM_REF_JOB}]}
            if "/jobs" in path:
                return {"total_count": 1, "jobs": [{"id": 742, "name": PLATFORM_REF_JOB}]}
            if "/check-runs/741/annotations" in path:
                return {"annotations": [{"title": NOTICE_TITLE,
                                         "message": f"{earlier} — core {B[:9]}"}]}
            if "/check-runs/742/annotations" in path:
                return {"annotations": [{"title": "Some other check",
                                         "message": "an unrelated annotation on the same job"}]}
            raise AssertionError(path)
        return fetch

    _ceiling_case("a latest attempt whose annotation list is NON-EMPTY but carries no platform "
                  "notice ⇒ the run is SKIPPED — an older attempt's set is NOT resurrected",
                  _fetch_attempt_untitled(), None, "no `Platform for this run` annotation")
    _ceiling_case("…and no note claims a fallback happened, because none did",
                  _fetch_attempt_untitled(), None, absent="read from attempt 1")

    # ── 🚨 THE WALK BACK IS BOUNDED (review on #206) ─────────────────────────────────────────────
    # Each attempt consulted costs a jobs read AND an annotations read, and `run_attempt` has no
    # ceiling of its own. Twelve runs each re-run a dozen times is 288 extra calls on the CI
    # critical path — spent, by construction, during the incident that made someone re-run them.
    # The module bounds every other walk it makes (MAIN_RUNS_EXAMINED, PLUGINS_LOOKBACK); this one
    # is bounded too, and SAYS so when it stops, so a truncated search is never read as an answer.
    def _fetch_attempt_deep(attempts: int, earliest_named: str = "3.0.0-ci.8203") -> Fetch:
        """Every attempt but the FIRST lost its annotations; attempt 1 published a set."""
        core = _fetch_for(two, sealed_two)

        def fetch(path: str) -> dict:
            if f"/repos/{SATELLITE}/" not in path:
                return core(path)
            if "/actions/workflows/" in path:
                return {"workflow_runs": [
                    {"id": 561, "created_at": "2026-09-14T08:00:00Z", "run_attempt": attempts}]}
            if "/attempts/1/jobs" in path:
                return {"total_count": 1, "jobs": [{"id": 751, "name": PLATFORM_REF_JOB}]}
            if "/jobs" in path or "/attempts/" in path:
                return {"total_count": 1, "jobs": [{"id": 752, "name": PLATFORM_REF_JOB}]}
            if "/check-runs/751/annotations" in path:
                return {"annotations": [{"title": NOTICE_TITLE,
                                         "message": f"{earliest_named} — core {B[:9]}"}]}
            if "/check-runs/752/annotations" in path:
                return {"annotations": []}
            raise AssertionError(path)
        return fetch

    _ceiling_case("a run re-run more times than the walk is allowed to look back ⇒ the walk STOPS "
                  "and says it was bounded, rather than spending a call per attempt",
                  _fetch_attempt_deep(CEILING_ATTEMPTS_WALKED + 6), None,
                  f"stopped after {CEILING_ATTEMPTS_WALKED} attempt(s)")
    # 🚨 …counting the attempts it READ, not the range it walked (Copilot review on this PR): an
    # intermediate attempt with no matching job is skipped without being looked into, so a note
    # asserting every attempt in the range was EMPTY would say more than was read.
    _ceiling_case("…and the truncation note counts the attempts that RETURNED empty annotations",
                  _fetch_attempt_deep(CEILING_ATTEMPTS_WALKED + 6), None,
                  f"that carried a `{PLATFORM_REF_JOB}` job each returned an EMPTY annotation list")
    _ceiling_case("…never asserting emptiness over attempts it never looked into",
                  _fetch_attempt_deep(CEILING_ATTEMPTS_WALKED + 6), None,
                  absent="all carry an EMPTY annotation list")
    _ceiling_case("…and a run within the bound still reaches the attempt that published a set",
                  _fetch_attempt_deep(CEILING_ATTEMPTS_WALKED), 8203, "8203")

    # ── 🚨 THE RUN LISTING IS READ INSIDE THE REFUSAL, NOT OUTSIDE IT (review on #186) ───────────
    # Every per-run read in this function already turns a `ResolutionError` into a SKIP with a
    # named reason. The LISTING call did not: a GitHub failure there escaped `main()` as an
    # uncaught traceback, so the one option whose whole purpose is to fail with a named RED
    # verdict failed with a stack trace instead — no `::error`, no step summary, and nothing
    # telling the reader whether main had passed anything or GitHub had simply not answered.
    def _listing_unreadable(path: str):
        if "/actions/workflows/" in path:
            raise ResolutionError("HTTP 503 (GitHub server error) after 4 attempts")
        raise AssertionError(path)

    total += 1
    try:
        _unread = main_passed_ceiling(_listing_unreadable, SATELLITE, log=logs.append)
    except Exception as error:                       # noqa: BLE001 — an escape here IS the failure
        failures.append("an unreadable run LISTING must be a named refusal, not an escaping "
                        f"{type(error).__name__}: {error}")
    else:
        if _unread.number is not None:
            failures.append("an unreadable run listing must never yield a ceiling — got "
                            f"{_unread.number}")
        elif not _unread.refusal or "listing" not in _unread.refusal.lower():
            failures.append("the refusal for an unreadable run listing must name the LISTING as "
                            f"what could not be read — got {(_unread.refusal or '')[:160]!r}")

    # ── 🚨 PRESENT-BUT-UNPARSEABLE IS NOT ABSENT (review, #4493) ────────────────────────────────
    # An annotation carrying the production title whose message names no set reaches the same dead
    # end as no annotation at all. Reported as "no annotation" it hides a malformed notice or a
    # schema change — the one case where the reader most needs to know something WAS published.
    def _fetch_unparseable(message: str) -> Fetch:
        core = _fetch_for(two, sealed_two)

        def fetch(path: str) -> dict:
            if f"/repos/{SATELLITE}/" not in path:
                return core(path)
            if "/actions/workflows/" in path:
                return {"workflow_runs": [{"id": 559, "created_at": "2026-09-14T08:00:00Z"}]}
            if "/jobs" in path:
                return {"total_count": 1, "jobs": [{"id": 731, "name": PLATFORM_REF_JOB}]}
            if "/check-runs/731/annotations" in path:
                return {"annotations": [{"title": NOTICE_TITLE, "message": message}]}
            raise AssertionError(path)
        return fetch

    _ceiling_case("a `Platform for this run` annotation whose message names no set ⇒ still no "
                  "ceiling, but the note says PRESENT and unparseable, never absent",
                  _fetch_unparseable("the platform set is now reported elsewhere"), None,
                  "are PRESENT but none names a set")
    _ceiling_case("…and it quotes the message, so a schema change is diagnosable from the log "
                  "alone",
                  _fetch_unparseable("the platform set is now reported elsewhere"), None,
                  "the platform set is now reported elsewhere")
    # 🚨 …AND THE SUMMARY SEPARATES THEM TOO (Copilot review on MeshWeaver.Crm#128). #4493 split
    # the two per-run NOTES and left the aggregate counting both as `silent`, under a sentence
    # saying those runs "published no notice" — which is the opposite of true for the unparseable
    # half, and points the reader at "main published nothing" when the actual fault is a malformed
    # notice or a schema change. The refusal is the sentence an operator acts on; it must not
    # contradict the note three lines above it.
    _ceiling_case("the REFUSAL counts a present-but-unparseable notice apart from a silent run, "
                  "rather than reporting it as a run that published nothing",
                  _fetch_unparseable("the platform set is now reported elsewhere"), None,
                  "names no set this reader can parse")
    _ceiling_case("…and does not claim that run published no notice, because it published one",
                  _fetch_unparseable("the platform set is now reported elsewhere"), None,
                  absent=f"1 DID carry that job and published no `{NOTICE_TITLE}` notice")

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

    # ── 🚨 EVERY FULL MAIN RUN VOUCHES, NOT `push` ALONE ───────────────────────────────────────
    # The reader used to ask the API for `event=push`, so the ceiling was only as fresh as the last
    # MERGE. A repository whose main is quiet still runs the daily poll, which resolves the newest
    # sealed set and rebuilds everything against it — passing evidence that was thrown away. The
    # last case is the load-bearing one: it fails if the QUERY ever pins an event again, which the
    # reader's own filter would otherwise hide.
    def _fetch_main_events(*events: str, seen: list[str] | None = None) -> Fetch:
        core = _fetch_for(two, sealed_two)

        def fetch(path: str) -> dict:
            if f"/repos/{SATELLITE}/" not in path:
                return core(path)
            if "/actions/workflows/" in path:
                if seen is not None:
                    seen.append(path)
                # Paging-aware, so a case can put the vouching run beyond the first slice and the
                # reader has to keep reading rather than give up on what one page happened to hold.
                size = int(re.search(r"per_page=(\d+)", path).group(1)) if "per_page=" in path else 100
                number = int(re.search(r"[?&]page=(\d+)", path).group(1)) if "page=" in path else 1
                window = list(enumerate(events))[(number - 1) * size: number * size]
                return {"workflow_runs": [
                    {"id": 900 + index, "created_at": "2026-09-16T08:00:00Z", "event": event}
                    for index, event in window]}
            if "/jobs" in path:
                return {"total_count": 1, "jobs": [{"id": 777, "name": PLATFORM_REF_JOB}]}
            if "/check-runs/777/annotations" in path:
                return {"annotations": [{"title": NOTICE_TITLE,
                                         "message": f"3.0.0-ci.8203 — core {B[:9]}"}]}
            raise AssertionError(path)
        return fetch

    _ceiling_case("the daily poll vouches — a schedule run on main is a FULL run",
                  _fetch_main_events("schedule"), 8203, "8203")
    _ceiling_case("…so does the release dispatch, which fires minutes after a seal",
                  _fetch_main_events("repository_dispatch"), 8203, "8203")
    _ceiling_case("…and a manual dispatch, which narrows nothing either",
                  _fetch_main_events("workflow_dispatch"), 8203, "8203")
    _ceiling_case("a pull-request run never vouches, however it came to be listed on main",
                  _fetch_main_events("pull_request"), None, "no successful run")
    # 🚨 THE UNDER-READ. Non-vouching runs on the page must not consume the examined budget. With a
    # page pinned to the limit and the filter applied after, twenty `workflow_run` rows ahead of the
    # real evidence meant the reader saw NONE of it and reported "main has published no passing run
    # to follow" — a RED on every satellite pull request, describing the page's composition rather
    # than main's state. Both arms matter: the ceiling resolves, and it resolves to the RIGHT set.
    _ceiling_case("non-vouching runs ahead of the evidence do not starve the read",
                  _fetch_main_events(*(["workflow_run"] * 20), "push"), 8203, "8203")
    _ceiling_case("…and a listing that is ALL non-vouching still refuses, naming the events",
                  _fetch_main_events(*(["workflow_run"] * 5)), None, "no successful run")

    # 🚨 …AND THE READ MUST ACTUALLY KEEP READING (#4783). The case above holds 21 rows, which fit
    # inside one `MAIN_PAGE_SIZE` slice — so it pins *filter-after-read* and says nothing about the
    # page loop. The only case that DID page exercised the refusal path (budget exhausted). A
    # regression that broke "keep reading until enough vouch" while leaving the single-page path
    # intact would therefore have been caught by nothing.
    #
    # Both cases ASSERT THE PAGE PARAMETER as well as the number, so neither can pass on a widened
    # `MAIN_PAGE_SIZE` — which would put the evidence back on page 1 and make the assertion about
    # the ceiling vacuous while reading exactly as little as before.
    _page_2: list[str] = []
    _ceiling_case("the evidence on page 2 is found — the read does not stop at page 1",
                  _fetch_main_events(*(["workflow_run"] * (MAIN_PAGE_SIZE + 10)), "push",
                                     seen=_page_2), 8203, "8203")
    total += 1
    if not any("&page=2" in path for path in _page_2):
        failures.append("the ceiling read resolved without ever REQUESTING page 2 — the evidence "
                        f"was placed beyond MAIN_PAGE_SIZE ({MAIN_PAGE_SIZE}) on purpose, so this "
                        "case would otherwise prove nothing about the page loop")

    _last_page: list[str] = []
    _ceiling_case("…and evidence on the LAST page inside the budget still resolves",
                  _fetch_main_events(
                      *(["workflow_run"] * (MAIN_PAGE_SIZE * (MAIN_PAGES_EXAMINED - 1) + 10)),
                      "push", seen=_last_page), 8203, "8203")
    total += 1
    if not any(f"&page={MAIN_PAGES_EXAMINED}" in path for path in _last_page):
        failures.append(f"the ceiling read resolved without requesting page {MAIN_PAGES_EXAMINED}, "
                        "the last one inside the budget — an off-by-one there would silently stop "
                        "one page early and report that main had passed nothing")

    # 🚨 A BOUNDED read that stopped early must not read as "main passed nothing" (Copilot on
    # #4773). Page budget exhausted with the listing still going is a DIFFERENT fact from an
    # exhausted listing, and only the second is evidence about main.
    _ceiling_case("a read that ran out of pages says so, rather than claiming main passed nothing",
                  _fetch_main_events(*(["workflow_run"] * 400)), None, "was NOT exhausted")
    _ceiling_case("…while a listing that genuinely ends still refuses on main's own terms",
                  _fetch_main_events(*(["workflow_run"] * 5)), None, "no successful run")

    # 🚨 …AND A PAGE THAT DID NOT ANSWER IS THE SAME KIND OF FACT (this change, over the review on
    # MeshWeaver.SocialMedia#186). The two failures are not one: page 1 failing means NOTHING was
    # read, so the refusal names the LISTING. A LATER page failing means the read stopped early with
    # rows in hand — indistinguishable, from the outside, from the page budget running out, and it
    # must reach the same "not exhausted" sentence rather than a claim about main.
    def _fetch_pages_then_error(fails_at: int, events: tuple[str, ...]) -> Fetch:
        core = _fetch_for(two, sealed_two)

        def fetch(path: str):
            if f"/repos/{SATELLITE}/" not in path:
                return core(path)
            if "/actions/workflows/" in path:
                page = int(path.rsplit("page=", 1)[1]) if "page=" in path else 1
                if page >= fails_at:
                    raise ResolutionError("HTTP 503 (GitHub server error) after 4 attempts")
                return {"total_count": 10_000, "workflow_runs": [
                    {"id": 900 + i, "created_at": "2026-09-14T08:00:00Z",
                     "event": events[i % len(events)]}
                    for i in range(MAIN_PAGE_SIZE)]}
            raise AssertionError(path)
        return fetch

    _ceiling_case("page 1 failing is a refusal naming the LISTING — nothing about main was read",
                  _fetch_pages_then_error(1, ("workflow_run",)), None, "run LISTING")
    _ceiling_case("a LATER page failing is a read that stopped early, not a verdict on main",
                  _fetch_pages_then_error(2, ("workflow_run",)), None, "the read stopped there")
    _ceiling_case("…and it does NOT claim the listing itself could not be read, because page 1 did",
                  _fetch_pages_then_error(2, ("workflow_run",)), None,
                  absent="the run LISTING itself did not answer")

    _seen_paths: list[str] = []
    _ceiling_case("…and a push still vouches, listed beside a poll",
                  _fetch_main_events("schedule", "push", seen=_seen_paths), 8203, "8203")
    total += 1
    if any("event=" in path for path in _seen_paths):
        failures.append("the ceiling query pins an `event=` again — the reader's filter would hide "
                        "that, and every poll and dispatch run would vanish from the listing")

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
        got = main_passed_ceiling(_bare_array, SATELLITE, log=logs.append).number
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

    # 🚨 #4780 — …AND IT MUST BE THE RECEIPT'S SHA *BEFORE* THE DECISIONS THE FREEZE GOVERNS, not
    # only after them. `freeze_names_this_run` was computed from `head_sha` alone, so a frozen run
    # whose receipt names the frozen sha while its head does not was judged NOT frozen — by the
    # definition of the very option that was passed. The case above does not reach it: that run is
    # fully sealed, so nothing consults `freeze_names_this_run` before the attribution happens.
    #
    # The one that does is the bounded `plugins_pending` exception. Fixture: #8207's platform trio
    # is sealed, its Plugins seal is still running, its receipt names the frozen sha and its head
    # (A) does not — and #8203 is a fully sealed re-bake of the SAME source at an older release.
    # On the pre-fix code #8207 is "not the frozen run", so it is passed over as an ordinary
    # candidate and #8203 is taken: a GREEN resolution at an OLDER set than the one the freeze
    # named, with no warning. A freeze is an instruction for an incident, and this is the path most
    # likely to be used during one and least likely to be noticed.
    pending_plugins_jobs = {1000 + 8207: _jobs_with_bake_id(id_8207, plugins="in progress"),
                            1000 + 8203: _jobs_with_bake_id(id_8203)}
    frozen_pending = _fetch_with_logs(
        {id_8207: _receipt(), id_8203: _receipt(RECEIPT_SHA, "3.0.0-ci.8203")},
        jobs=pending_plugins_jobs)
    case("a sha freeze the RECEIPT names is honoured even while its plugins seal is pending", True,
         lambda: choose(frozen_pending, _registry(full), tester, portal, freeze=RECEIPT_SHA,
                        log=logs.append, verify_source=True),
         lambda c: c.sha == RECEIPT_SHA and c.set_name == "3.0.0-ci.8207")
    total += 1
    if not any("Frozen set is still sealing its plugins" in line for line in logs):
        failures.append("#4780: the frozen set was taken while its plugins seal was pending, but "
                        "the `Frozen set is still sealing its plugins` warning was not spoken — a "
                        "reader has to be told the upstream fetch may not find it yet")

    # 🚨 …AND A RECEIPT THAT EXISTS IS THE ANSWER, so `head_sha` is not a second chance (Copilot's
    # review of #4920). Accepting either resurrects #4242 from the other side: a run whose HEAD
    # matches the freeze while its receipt names a different source — an ordinary re-bake — is judged
    # the frozen run, and if it is UNSEALED the escalation aborts the whole scan before the run whose
    # receipt actually matches is reached.
    #
    # Fixture: #8530 is the newest, its head IS the frozen sha, it has a successful platform bake (so
    # a receipt exists) naming a DIFFERENT source, and its promote leg failed — unsealed. #8506 is
    # fully sealed and its receipt names the frozen sha. The freeze must resolve to #8506.
    head_only = "f" * 40
    decoy = [_run(8530, head_only), _run(8506, C)]
    id_decoy, id_real = 70011, 70012
    decoy_jobs = {
        1000 + 8530: _jobs_with_bake_id(id_decoy, promote="failure"),   # unsealed, receipt exists
        1000 + 8506: _jobs_with_bake_id(id_real),                       # sealed, the frozen one
    }
    decoy_logs = {
        id_decoy: _receipt("d" * 40, "3.0.0-ci.8530"),   # a re-bake: head != what it published
        id_real: _receipt(head_only, "3.0.0-ci.8506"),   # the run the freeze actually names
    }
    decoy_full = dict(full)
    decoy_full[("mw-plugin-test", "3.0.0-ci.8506")] = D1
    decoy_full[("memex-portal-ai", "3.0.0-ci.8506")] = D2
    case("a run whose HEAD matches the freeze but whose RECEIPT does not must not abort the scan", True,
         lambda: choose(_fetch_with_logs(decoy_logs, runs=decoy, jobs=decoy_jobs),
                        _registry(decoy_full), tester, portal, freeze=head_only,
                        log=logs.append, verify_source=True),
         lambda c: c.sha == head_only and c.set_name == "3.0.0-ci.8506")
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
          "otherwise, the OPTIONAL main ceiling counts EVERY full main run and not `push` "
          "alone, keeps READING until enough runs vouch (proved on page 2 and on the last "
          "page inside the budget, by the page it requested), changes nothing unless asked for and refuses "
          "rather than falling back when main has passed nothing — naming what it read and leading "
          "with the remedy the evidence points at, never at `main` when the LISTING is what failed, "
          "the OPTIONAL source verification "
          "reads no job log unless asked for and passes over a set it cannot attribute, and a sha "
          "freeze is tested against the RECEIPT before the decisions the freeze governs, a main run "
          "whose annotations name two DIFFERENT sets is SKIPPED rather than guessed at, a set below "
          "this repository's declared FLOOR is refused on every path including under a freeze, a transient "
          "GitHub 5xx is retried (bounded) and named as a server error, a run "
          "listing whose page 1 is provably STALE (its newest run over 12 h old, or older than the "
          "set main has passed) is refused rather than resolved from — after a bounded RE-READ, "
          "which it proves line by line, where the staleness is the PROVABLE kind (the ceiling) "
          "and never where it is an inference (the age) or a freeze, and whose refusal keeps the "
          "#4433 sentence verbatim, and every dead end is RED "
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
        followed = ceiling_for(fetch, arguments.passed_on_main, arguments.freeze or None)
        ceiling = followed.number
        for note in followed.notes:
            print(f"  {note}")
        if followed.refusal:
            # 🚨 The per-run notes stay in the LOG (printed just above) and OUT of the annotation.
            # Jamming them in put twelve identical skip lines between the reader and the remedy,
            # and the remedy that mattered came last (#4664). The plain copy is printed first so
            # the raw log carries a readable form whatever the annotation renderer does with %0A.
            print(followed.refusal)
            print("::error title=Which set this repo's main has passed could not be read::"
                  + followed.refusal.replace("\n", "%0A"))
            summary = os.environ.get("GITHUB_STEP_SUMMARY")
            if summary:
                with open(summary, "a", encoding="utf-8") as handle:
                    handle.write("### ❌ Which set this repo's `main` has passed could not be "
                                 f"read\n\n```\n{followed.refusal}\n```\n")
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
