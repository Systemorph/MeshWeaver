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

Fetch = Callable[[str], dict]                      # GitHub REST: path → JSON
Resolve = Callable[[str, str], str | None]         # registry: (repo, tag) → digest or None (absent)


class ResolutionError(RuntimeError):
    """A RED verdict: what was looked for and what stood in the way. Never a silent pass."""


# ───────────────────────────── GitHub REST (public core repo) ─────────────────────────────

def github_fetch_with(token: str) -> Fetch:
    def fetch(path: str) -> dict:
        last: Exception | None = None
        for attempt in range(4):
            request = urllib.request.Request(
                f"{GITHUB_API}{path}",
                headers={
                    "Authorization": f"Bearer {token}",
                    "Accept": "application/vnd.github+json",
                    "X-GitHub-Api-Version": "2022-11-28",
                    "User-Agent": "meshweaver-lane-resolve-platform",
                })
            try:
                with urllib.request.urlopen(request, timeout=30) as response:
                    return json.load(response)
            except urllib.error.HTTPError as error:
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


def run_jobs(fetch: Fetch, run_id: int) -> list[dict]:
    jobs: list[dict] = []
    page = 1
    while True:
        data = fetch(f"/repos/{CORE_REPO}/actions/runs/{run_id}/jobs"
                     f"?filter=latest&per_page=100&page={page}")
        rows = list(data.get("jobs") or [])
        jobs += rows
        if len(rows) < 100 or len(jobs) >= int(data.get("total_count") or 0):
            return jobs
        page += 1


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
           now: Callable[[], float] = time.time, migration: str | None = None) -> Chosen:
    freeze_kind = freeze_value = None
    if freeze:
        freeze_kind, freeze_value = parse_freeze(freeze)
        log(f"freeze requested: {freeze_kind} {freeze_value} (repo VARIABLE MW_PLATFORM_REF)")

    deadline = now() + wait_for_seal
    examined = 0
    skipped: list[str] = []
    chosen: Chosen | None = None
    publication: PluginsPublication | None = None
    lookback = 0
    for page in range(1, MAX_RUN_PAGES + 1):
        if chosen is not None and (publication is not None or lookback >= PLUGINS_LOOKBACK):
            break
        runs = cd_runs(fetch, page)
        if not runs:
            break
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

            if freeze_kind == "sha" and sha != freeze_value:
                continue
            if freeze_kind == "set" and number != int(SET_NAME.fullmatch(freeze_value).group(2)):
                continue
            examined += 1

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
                if freeze_kind:
                    raise ResolutionError(
                        f"the freeze names {label}, which is not a sealed set ({why}). A freeze is "
                        "an instruction, not a preference: refusing to substitute another set.")
                continue
            # 🚨 The ONE bounded exception: platform sealed, its own Plugins seal still running.
            # The publication for this identity does not exist yet (minutes); taking the set now
            # is a certain red on the upstream fetch. A freeze is an instruction and goes through.
            if v.plugins_pending and not freeze_kind:
                skipped.append(f"{label}: platform sealed, but its Plugins publication is still "
                               f"sealing (`{PLUGINS_SEAL_JOB[1]}` {v.plugins}) — the newest set "
                               "with a sealed publication is taken; the next run follows this one")
                log(f"  skip {skipped[-1]}")
                continue
            if v.plugins_pending and freeze_kind:
                log(f"  ::warning title=Frozen set is still sealing its plugins::{label}: the freeze "
                    "names a set whose Plugins publication is still sealing; taken as instructed — "
                    "the upstream fetch says whether the registry holds it yet")

            if version is ...:
                version = platform_version(fetch, sha, log=log)
            set_name = f"{version}-ci.{number}" if version else f"ci.{number} (line unknown)"
            if freeze_kind == "set" and set_name != freeze_value:
                raise ResolutionError(
                    f"the freeze names {freeze_value}, but {label} resolves to {set_name}. "
                    "Refusing a different or unverifiable release version.")
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
                      else "the newest sealed platform set")
            if skipped and not freeze:
                source += f" — {len(skipped)} newer run(s) passed over, see the log"
            chosen = Chosen(sha, number, str(run.get("html_url", "")), set_name, digests,
                            v.plugins, source)
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
            if rows.get("override") == "true":
                handle.write(f"| **refreshed** | this job overrode the run's baseline "
                             f"`{rows.get('baseline-set', '')}` — a re-run must not test a stale set | |\n")
            handle.write("\nThe caller pinned no digest, so the lane took the newest platform-sealed "
                         "set itself — and a re-run re-resolves (maintainer rule 2026-09-12: for "
                         "compile always find latest package of platform and plugins; "
                         "MeshWeaver#3842, MeshWeaver.Education#320).\n")


def write_outputs(chosen: Chosen, tester: str, portal: str, migration: str | None = None) -> None:
    emit(output_rows(chosen, tester, portal, migration))


# ───────────────────────────────── self-test ──────────────────────────────────────────────

def _run(number: int, sha: str, status: str = "completed", conclusion: str = "success",
         branch: str = "main") -> dict:
    return {"id": 1000 + number, "run_number": number, "head_sha": sha, "status": status,
            "conclusion": conclusion, "head_branch": branch,
            "html_url": f"https://github.com/{CORE_REPO}/actions/runs/{1000 + number}"}


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
          "otherwise, and every dead end is RED naming why.")
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
    choose_fn = lambda: choose(fetch, resolve, tester, portal,  # noqa: E731 — one call, two callers
                               wait_for_seal=arguments.wait_for_seal, freeze=arguments.freeze or None,
                               migration=migration or None)
    try:
        baseline = load_baseline(os.environ.get(BASELINE_ENV))
        if baseline is not None:
            # A consumer job checking the run's baseline for freshness (Education#320): never RED
            # on the re-resolution itself — the baseline is a valid answer.
            rows, _ = refresh(baseline, choose_fn,
                              rows_of=lambda c: output_rows(c, tester, portal, migration or None))
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
    write_outputs(chosen, tester, portal, migration or None)
    return 0


if __name__ == "__main__":
    sys.exit(main())
