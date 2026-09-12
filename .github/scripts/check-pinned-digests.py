#!/usr/bin/env python3
"""check-pinned-digests.py — does every image digest the fleet PINS still exist in ACR, and is it
PROTECTED from the next purge?

(The name on this first line is load-bearing in the same way compile-check.py's and
check-workflow-timeouts.py's are: a lane that fetches this file at the platform ref can refuse a
body whose first 400 bytes do not name it.)

WHY THIS EXISTS (MeshWeaver#3438, 2026-09-05)
---------------------------------------------
The ACR retention task `purge-old-images` deleted the tester manifest that MeshWeaver.Education's
`main` pinned. Every run of that repo's `Disposable-mesh gate` then died at `manifest unknown`
before a single content gate executed — including `main`'s OWN NIGHTLY, so the repo lost its green
baseline at the same instant it lost its gate. Education, Manufacturing and SocialMedia went down
together at 2026-09-05T15:29Z. Nothing said why; the failure is a registry 404 four layers below
anything a CI log calls out.

🚨 THE HAZARD IS NOT THE ONE MEMEX#122 NAMED. That incident's rule is "if this tag were deleted
tonight, would anything recreate it?" — and `mw-plugin-test` PASSES it. It is republished many
times a day. It broke anyway, because the danger is not "nothing rebuilds this repo", it is

    something PINS a specific digest of it — and `--keep 10` is counted in NEWER BUILDS, so
    frequent republishing is what DESTROYS the pin rather than what protects it.

A pin is *designed* to sit still for weeks (Doc/Architecture/ModuleVersioning — pins move as one
set, deliberately and rarely). `--ago 7d --keep 10` guarantees any pin older than about a week is
bytes that no longer exist. The retention window and the intended pin lifetime are in direct
contradiction and nothing reconciles them.

WHAT THIS SCRIPT IS, AND WHAT IT IS NOT
---------------------------------------
Its EXISTENCE arm is the cheap half and is deliberately not the fix: it does not stop the deletion.
It converts "an entire repository's CI is inexplicably dead, including its nightly" into one named
red saying which repo, which variable, and which digest is gone.

The retention half — locking the pinned manifests so `acr purge` skips them, and collapsing the two
divergent tasks into one — is written up in Doc/Architecture/PinnedImageRetention and is implemented
by lock-pinned-digests.py, running at 01:00 UTC, two hours before the 03:00 purge.

🚨 THE PROTECTION ARM (#3438, added 2026-09-08) — EXISTENCE IS A LAGGING INDICATOR
---------------------------------------------------------------------------------
A manifest that is GONE is a past incident: the wedge has already happened and the repository is
already dead. The leading indicator is one field away and the same `az` call already fetches it:

    a pinned manifest whose `deleteEnabled` is still TRUE is not protected, and `--ago 7d --keep 10`
    guarantees it dies — the only open question is which night.

So this sweep now reads `changeableAttributes.deleteEnabled` off every pin it resolves and reports
PROTECTED / UNPROTECTED / PENDING-LOCK. That closes the one hole nothing covered: **the lock job
could stop protecting and nothing would say so.** A deleted `lock-pinned-digests.yml`, a removed
`schedule:`, a revoked `metadata/write` grant, a manifest unlocked by hand — every one of those
leaves a green wall until a satellite's CI dies at `manifest unknown` days later, which is exactly
the failure mode of the incident this whole file exists for, one level up.

Two structural assertions come with it, and they need NO credential, so they run on every pull
request inside `--self-test` rather than only at 05:20:

  * `lock-pinned-digests.yml` EXISTS and carries a `schedule:` — the protection is not a comment.
  * the lock fires BEFORE the purge. Both are read from committed files (the lock workflow's own
    `cron:`, and the purge task's `schedule` in .github/acr-retention/tasks.json). Moving the lock
    to 04:00 would make it useless and, until this arm existed, nothing anywhere would have noticed.

PENDING-LOCK, and why it is not a skip. A pin that moved AFTER the last scheduled lock has not yet
had an opportunity to be protected, so calling it a failure would red on the fleet's ordinary
working day. It is reported and EXEMPT — and the exemption is COUNTED in the denominator, so a
sweep that exempted everything cannot read as a sweep that found everything protected. The window
is narrow where it matters: this sweep runs at 05:20 UTC and the lock at 01:00, so the exempting
window is 4h20m, not a day.

DESIGN CONSTRAINTS, each one a thing this fleet has already been bitten by
-------------------------------------------------------------------------
* NO SKIP-TRAPDOOR. The caller's `preflight` asserts every input and fails RED naming what to
  provision; this script is invoked unconditionally. It never asks "is a credential set?" in order
  to decide whether to check (AGENTS.md — GitHub paints a skipped job the same colour as a passed
  one).

* NO PLACEHOLDER VACUITY. `check-platform-pins.py` matches `sha256:[0-9a-f]+`, so a NON-HEX
  placeholder digest reads to it as "no literal pin present" and passes having checked nothing.
  Here, a declaration whose NAME says digest and whose VALUE is neither a well-formed
  `sha256:<64 hex>` nor a `${{ … }}` forward is a FAILURE (MALFORMED), never an absence.

* A ZERO IS MEASURED, NOT ASSUMED. Education pins under `MW_PORTAL_DIGEST` / `MW_MIGRATION_DIGEST`
  / `MW_TEST_DIGEST`, SocialMedia and Crm pin inside a `run:` step output, Plugins and Reinsurance
  use `MW_IMAGE_DIGEST` / `MW_PORTAL_IMAGE_DIGEST`. A check keyed on the two common names reports
  four of six repos clean while verifying none of their pins. So extraction is shape-driven, not
  name-driven, and the report PRINTS THE DENOMINATOR — repos scanned, repos that pin, repos that do
  not, pins found, pins resolved — because a sweep whose zero has two causes must measure both.

* CANNOT-REACH IS NEVER "GONE". `az` exits non-zero both when a manifest is absent and when the
  registry is unreachable or the credential is refused. Only the registry's own `manifest unknown`
  is read as absent; every other stderr is INDETERMINATE and fails the run naming the az output. A
  control probe (`az acr repository list`) runs FIRST, so "every pin in the fleet is gone" can never
  be produced by a broken login.

USAGE
-----
  check-pinned-digests.py --discover                 enumerate the fleet from the App installation
  check-pinned-digests.py --repos Systemorph/A,...   scan exactly these repositories
  check-pinned-digests.py --self-test                prove the extractor fires and stays silent

Exit 0 only when every pin found resolves AND every pin the lock job has had an opportunity to
protect is locked. Every failure is a `::error` annotation naming the repository, the declaration
and the digest.
"""

from __future__ import annotations

import argparse
import base64
import datetime as dt
import json
import os
import re
import subprocess
import sys
import textwrap as _textwrap
from dataclasses import dataclass, field
from pathlib import Path

REGISTRY_DEFAULT = "meshweaver"
REGISTRY_HOST_RE = r"(?P<host>[a-z0-9]+)\.azurecr\.io"

# A digest literal, in the only form a registry accepts: sha256 and exactly 64 lowercase hex.
DIGEST_RE = re.compile(r"sha256:[0-9a-f]{64}")

# ── The three shapes a pin is written in across this fleet, measured 2026-09-06 ────────────────
#
#   1. a workflow-level env assignment       MW_TEST_DIGEST: sha256:df19f10a…
#   2. a step OUTPUT written by shell        echo "image-digest=sha256:d91d333f…" >> "$GITHUB_OUTPUT"
#   3. an inline image reference             meshweaver.azurecr.io/mw-plugin-test@sha256:…
#
# 1 and 2 give a NAME (which the report quotes, so the fix is one grep away); 3 gives the ACR
# repository directly. Neither alone is enough, so all three are extracted and then joined.

# Shape 1 — `NAME: value` at any indent, value optionally quoted. The name is captured whatever it
# is; whether it is pin-shaped is decided below, by the name test, not by this regex.
ASSIGN_RE = re.compile(
    r"^[ \t]*(?:-[ \t]+)?(?P<name>[A-Za-z_][A-Za-z0-9_.-]*)[ \t]*:[ \t]*"
    r"(?P<quote>['\"]?)(?P<value>[^\r\n]*?)(?P=quote)[ \t]*(?:#[^\r\n]*)?$",
    re.MULTILINE,
)

# Shape 2 — a shell assignment whose key is pin-shaped, e.g. `image-digest=sha256:…`.
#
# 🚨 IT MATCHES AN ATTEMPTED DIGEST, NOT A WELL-FORMED ONE. Requiring `[0-9a-f]{64}` here made the
# vacuity guard below a promise this shape did not keep: `image-digest=sha256:PLACEHOLDER` matched
# NOTHING and read as "no pin present" — in the exact shape MeshWeaver.SocialMedia and
# MeshWeaver.Crm write their tester pin, so two of the six pinning repositories were exempt from
# the guard while the docstring said none were. Measured 2026-09-06 while building
# check-pin-set-consistency.py (MeshWeaver#3454), whose first draft reproduced the same hole.
# Classification into pin / MALFORMED happens once, below, for both shapes.
OUTPUT_RE = re.compile(
    r"(?P<name>[A-Za-z_][A-Za-z0-9_.-]*)=(?P<quote>['\"]?)(?P<value>sha256:[^\s'\"]*)(?P=quote)"
)

# Shape 3 — an image reference that binds an ACR repository to a digest, either literally or
# through a variable. `format('…/{repo}@{0}', env.NAME)` is Education's shape and it is not
# optional to support: all three of that repo's pins are written that way or as a direct `@${{ }}`.
INLINE_LITERAL_RE = re.compile(
    REGISTRY_HOST_RE + r"/(?P<repo>[A-Za-z0-9][A-Za-z0-9._/-]*)@(?P<digest>sha256:[0-9a-f]{64})"
)
INLINE_VAR_RE = re.compile(
    REGISTRY_HOST_RE + r"/(?P<repo>[A-Za-z0-9][A-Za-z0-9._/-]*)@\$\{\{\s*"
    r"(?:env|steps\.[A-Za-z0-9_-]+\.outputs)\.(?P<name>[A-Za-z0-9_.-]+)\s*\}\}"
)
FORMAT_VAR_RE = re.compile(
    r"format\(\s*'" + REGISTRY_HOST_RE + r"/(?P<repo>[A-Za-z0-9][A-Za-z0-9._/-]*)@\{0\}'\s*,\s*"
    r"(?:env|steps\.[A-Za-z0-9_-]+\.outputs)\.(?P<name>[A-Za-z0-9_.-]+)\s*\)"
)

# Any ACR repository this GitHub repo mentions at all — the candidate set for a digest whose
# declaration is not wired to one image reference (Reinsurance declares MW_TESTER_IMAGE and
# MW_IMAGE_DIGEST as two separate env keys and joins them inside a reusable workflow's inputs).
MENTIONED_REPO_RE = re.compile(
    REGISTRY_HOST_RE + r"/(?P<repo>[A-Za-z0-9][A-Za-z0-9._-]*(?:/[A-Za-z0-9][A-Za-z0-9._-]*)*)"
)

# A declaration is PIN-SHAPED when its name says so. This is the vacuity guard's subject: a
# pin-shaped name whose value is an ATTEMPTED digest that is not a digest is a failure, not an
# absence.
PIN_NAME_RE = re.compile(r"(?i)(?:^|[^a-z])digest(?:[^a-z]|$)|digest$|^digest")

# 🚨 The MALFORMED predicate is deliberately narrow: the value ATTEMPTS to be a digest (it starts
# with `sha256`) and is not one. That is exactly the `check-platform-pins.py` hole — a loose
# `sha256:[0-9a-f]+` matcher reads `sha256:PLACEHOLDER` as "no pin present" and passes having
# checked nothing — and it is the whole of it.
#
# It is NOT "any pin-shaped name whose value is not a digest", and the difference is what keeps
# this gate from crying wolf on a schedule. The text scan reads `run:` shell and nested mappings
# too, where `digest: $D` (a shell variable) and a bare `digest:` (a key with a nested block under
# it, e.g. a composite action's `outputs:`) are both perfectly ordinary. Flagging those would put a
# false red on a nightly sweep, and a nightly red that is usually wrong is how a real one stops
# being read.
ATTEMPTED_DIGEST_RE = re.compile(r"(?i)^sha256")

# A `${{ … }}` value forwards another declaration; the thing it forwards is itself extracted
# wherever it is declared, so a forward is neither a pin nor a defect.
EXPRESSION_RE = re.compile(r"\$\{\{.*\}\}")

# The registry's own words for "that manifest is not here". Anything else az says is
# INDETERMINATE — a credential, a network or a service fault, never evidence of absence.
ABSENT_MARKERS = ("manifest unknown", "manifestunknown", "not found", "notfound")


@dataclass
class Pin:
    """One digest a repository pins, and where it is written."""

    gh_repo: str
    where: str          # "<workflow file>:<declaration name>", or ":inline" for a literal image ref
    digest: str
    acr_repos: list[str] = field(default_factory=list)   # candidates, most specific first
    resolved_in: str | None = None
    verdict: str = "UNCHECKED"    # RESOLVES | GONE | INDETERMINATE
    detail: str = ""
    # 🚨 A SECOND, INDEPENDENT VERDICT — never folded into `verdict` above. Existence and
    # protection are different questions about the same manifest and a pin can pass either while
    # failing the other; collapsing them into one field is how "it resolves" would come to mean
    # "it is safe", which is the reading that made #3438 invisible until the manifest was gone.
    #   PROTECTED     deleteEnabled == false — `acr purge` skips it (neither purge step passes
    #                 --include-locked; asserted against the committed record on every PR)
    #   UNPROTECTED   deleteEnabled == true, and the lock job HAS had an opportunity → a failure
    #   PENDING-LOCK  deleteEnabled == true, but this pin moved after the last scheduled lock run
    #   INDETERMINATE the registry did not say — never read as protected
    protection: str = "UNCHECKED"
    protection_detail: str = ""
    delete_enabled: bool | None = None

    @property
    def workflow_file(self) -> str:
        """The workflow file this pin is declared in — `where` is '<file>:<name>'."""
        return self.where.split(":", 1)[0]


@dataclass
class Malformed:
    gh_repo: str
    where: str
    value: str


@dataclass
class RepoScan:
    gh_repo: str
    workflows: int = 0
    pins: list[Pin] = field(default_factory=list)
    malformed: list[Malformed] = field(default_factory=list)
    unreadable: str | None = None


# ── GitHub reads. REST only (AGENTS.md): GraphQL exhausts first and the limit that bites is the
#    SECONDARY one, which takes GitHub access away from every concurrent session. ───────────────


def gh_api(path: str, paginate: bool = False, jq: str | None = None) -> tuple[int, str, str]:
    cmd = ["gh", "api", "-H", "X-GitHub-Api-Version: 2022-11-28", path]
    if jq:
        cmd += ["--jq", jq]
    if paginate:
        cmd.insert(2, "--paginate")
    proc = subprocess.run(cmd, capture_output=True, text=True)
    return proc.returncode, proc.stdout, proc.stderr


def discover_repos() -> list[str]:
    """Every repository the App installation reaches.

    Discovery rather than a committed list is the whole point: MeshWeaver.Crm pins the same tester
    digest as Plugins, Manufacturing and SocialMedia and is absent from `.github/shared-rules.json`'s
    `repos` array — so a check keyed on that list would have reported a fleet of six while a seventh
    sat unswept. A repository that pins nothing costs one API call and shows up in the denominator
    as a measured zero.
    """
    # 🚨 LET `gh` DO THE EXTRACTION. `gh api --paginate` emits one JSON DOCUMENT PER PAGE,
    # concatenated — not one merged array — so recovering the pages by splitting the text on
    # braces is guesswork, and guesswork that loses a page loses a REPOSITORY. That repository
    # then never appears in the report at all: not as a failure, not as a measured zero, simply
    # absent, while the run goes green. That is this gate's own defect class turned inward, and
    # swallowing the `JSONDecodeError` from a chunk that split badly is what would hide it.
    # `--jq` makes gh apply the filter to each page and print one name per line, so pagination
    # stops being this script's problem. It is also character-for-character what the caller's
    # preflight asserts with, so the two can never disagree about who is in the fleet.
    rc, out, err = gh_api("/installation/repositories", paginate=True,
                          jq=".repositories[].full_name")
    if rc != 0:
        die(
            "could not enumerate the App installation's repositories.",
            [
                "This needs an INSTALLATION token (GH_TOKEN from actions/create-github-app-token),",
                "not a user token: /installation/repositories is the endpoint an installation token",
                "answers about itself. Locally, pass --repos instead.",
                f"gh said: {err.strip()[:400]}",
            ],
        )
    names = sorted({line.strip() for line in out.splitlines() if line.strip()})
    if not names:
        die(
            "the App installation reaches ZERO repositories.",
            [
                "A sweep over an empty fleet reports 'every pin resolves' having checked nothing —",
                "the exact failure this gate exists to prevent. Add the fleet repositories to the",
                "meshweaver-cloud installation, then re-run.",
            ],
        )
    return sorted(names)


def fetch_workflows(gh_repo: str) -> tuple[list[tuple[str, str]], str | None]:
    """(filename, text) for every workflow file on the repository's default branch."""
    rc, out, err = gh_api(f"repos/{gh_repo}/contents/.github/workflows")
    if rc != 0:
        blob = (err + out).lower()
        if "404" in blob or "not found" in blob:
            # 🚨 A 404 HERE HAS TWO CAUSES AND THEY ARE OPPOSITE VERDICTS:
            #   * the repository is fine and simply has no `.github/workflows` — a MEASURED ZERO;
            #   * the repository is not there, renamed, or not readable by this token — NOT CHECKED.
            # Both arrive as the same status, and reading the second as the first is precisely the
            # failure this gate exists to name: it would print "no digest pin declared" for a
            # repository nobody looked at, and the sweep would go green. Measured 2026-09-06:
            # `--repos Systemorph/ThisRepoDoesNotExist12345` reported exactly that, and exit 0.
            # So ask about the REPOSITORY before believing the absence.
            rc_repo, _, err_repo = gh_api(f"repos/{gh_repo}")
            if rc_repo != 0:
                return [], (
                    "the repository itself could not be read, so its pins were never looked for "
                    f"(not 'it has no workflows'). gh said: {err_repo.strip()[:200]}"
                )
            return [], None
        return [], err.strip()[:400] or "unknown error listing .github/workflows"
    try:
        entries = json.loads(out)
    except json.JSONDecodeError as exc:
        return [], f".github/workflows listing is not JSON: {exc}"
    files: list[tuple[str, str]] = []
    for entry in entries:
        name = entry.get("name", "")
        if entry.get("type") != "file" or not name.endswith((".yml", ".yaml")):
            continue
        rc, body, err = gh_api(f"repos/{gh_repo}/contents/{entry['path']}")
        if rc != 0:
            return [], f"could not read {entry['path']}: {err.strip()[:200]}"
        try:
            doc = json.loads(body)
            text = base64.b64decode(doc["content"]).decode("utf-8", "replace")
        except (json.JSONDecodeError, KeyError, ValueError) as exc:
            return [], f"could not decode {entry['path']}: {exc}"
        files.append((name, text))
    return files, None


# ── Extraction ─────────────────────────────────────────────────────────────────────────────────


def extract(gh_repo: str, filename: str, text: str) -> tuple[list[Pin], list[Malformed]]:
    pins: list[Pin] = []
    malformed: list[Malformed] = []

    # name -> the ACR repository it is used with, wherever the file wires the two together.
    bindings: dict[str, list[str]] = {}
    for pattern in (INLINE_VAR_RE, FORMAT_VAR_RE):
        for match in pattern.finditer(text):
            bindings.setdefault(match.group("name"), [])
            if match.group("repo") not in bindings[match.group("name")]:
                bindings[match.group("name")].append(match.group("repo"))

    mentioned = sorted({m.group("repo") for m in MENTIONED_REPO_RE.finditer(text)})

    def candidates(name: str) -> list[str]:
        bound = bindings.get(name)
        if bound:
            return bound
        # Unbound: every ACR repository this file names. The report says which one it resolved in,
        # so the ambiguity is visible rather than hidden.
        return mentioned

    seen: set[tuple[str, str]] = set()

    def add(where: str, digest: str, acr_repos: list[str]) -> None:
        key = (where, digest)
        if key in seen:
            return
        seen.add(key)
        pins.append(Pin(gh_repo=gh_repo, where=where, digest=digest, acr_repos=list(acr_repos)))

    # Shape 3 first: a literal `…/repo@sha256:…` binds its own ACR repository with no inference.
    for match in INLINE_LITERAL_RE.finditer(text):
        add(f"{filename}:inline", match.group("digest"), [match.group("repo")])

    # Shape 1 — and the vacuity guard, which lives here because this is the only shape in which a
    # placeholder can masquerade as an absence.
    for match in ASSIGN_RE.finditer(text):
        name, value = match.group("name"), match.group("value").strip()
        if not PIN_NAME_RE.search(name):
            continue
        if EXPRESSION_RE.search(value):
            continue                      # forwards another declaration; that one is checked
        if DIGEST_RE.fullmatch(value):
            add(f"{filename}:{name}", value, candidates(name))
        elif ATTEMPTED_DIGEST_RE.match(value):
            # Pin-shaped name, and a value that ATTEMPTS to be a digest without being one. This is
            # the `check-platform-pins.py` hole: a loose `sha256:[0-9a-f]+` match reads
            # `sha256:PLACEHOLDER` as "no pin here" and passes. Here it is a failure.
            malformed.append(Malformed(gh_repo, f"{filename}:{name}", value))

    # Shape 2 — a step output. `>> "$GITHUB_OUTPUT"` makes it an env value for later jobs, so it is
    # a pin in every sense that matters; it is simply not visible to a scan of `env:` blocks.
    for match in OUTPUT_RE.finditer(text):
        name, value = match.group("name"), match.group("value")
        if not PIN_NAME_RE.search(name):
            continue
        if DIGEST_RE.fullmatch(value):
            add(f"{filename}:{name}", value, candidates(name))
        else:
            # Same verdict as the `env:` shape above, for the same reason: a pin-shaped name whose
            # value ATTEMPTS to be a digest and is not one is a defect, never an absence.
            malformed.append(Malformed(gh_repo, f"{filename}:{name}", value))

    return pins, malformed


# ── ACR resolution ─────────────────────────────────────────────────────────────────────────────


def az(args: list[str]) -> tuple[int, str, str]:
    proc = subprocess.run(["az", *args], capture_output=True, text=True)
    return proc.returncode, proc.stdout, proc.stderr


def registry_control_probe(registry: str) -> None:
    """Prove the registry ANSWERS before reading any absence as evidence.

    Without this, one refused credential turns the whole fleet's pins into `manifest unknown` and
    the gate reports a catastrophe that is not happening — the mirror image of the failure it is
    built to catch.
    """
    rc, out, err = az(["acr", "repository", "list", "--name", registry, "-o", "json"])
    if rc != 0:
        die(
            f"cannot reach the container registry '{registry}'.",
            [
                "Nothing was checked. This is NOT 'the pins are gone' — a gate that cannot reach",
                "its subject has not checked it, so it fails closed instead of reporting absence.",
                "Log in (azure/login OIDC in CI, `az login` locally) and re-run.",
                f"az said: {err.strip()[:400]}",
            ],
        )
    try:
        repos = json.loads(out)
    except json.JSONDecodeError as exc:
        die(f"'az acr repository list' did not return JSON: {exc}", [])
    if not repos:
        die(
            f"registry '{registry}' lists ZERO repositories.",
            [
                "Every digest would read as absent against an empty registry, so this is refused as",
                "an input fault rather than reported as a fleet-wide outage.",
            ],
        )
    print(f"registry {registry}: reachable, {len(repos)} repositories.")


def resolve(registry: str, acr_repo: str, digest: str, cache: dict) -> tuple[bool, str, bool | None]:
    """(exists, detail, delete_enabled).

    🚨 `-o json`, not `-o none`. The protection arm needs `changeableAttributes.deleteEnabled` and
    this is the SAME registry round trip that answers existence — reading it costs nothing and
    adds no call. `delete_enabled` is None when the manifest is absent, and also when it is present
    but the attribute could not be read; the caller treats the second as INDETERMINATE, never as
    protected. "The registry did not say" is not a confirmation.
    """
    key = (acr_repo, digest)
    if key in cache:
        return cache[key]
    rc, out, err = az(
        ["acr", "repository", "show", "--name", registry, "--image", f"{acr_repo}@{digest}", "-o", "json"]
    )
    if rc == 0:
        delete_enabled: bool | None = None
        try:
            attrs = (json.loads(out) or {}).get("changeableAttributes") or {}
            value = attrs.get("deleteEnabled")
            if isinstance(value, bool):
                delete_enabled = value
        except (json.JSONDecodeError, AttributeError):
            delete_enabled = None
        result = (True, "", delete_enabled)
    else:
        blob = err.lower()
        if any(marker in blob for marker in ABSENT_MARKERS):
            result = (False, "absent", None)
        else:
            # Neither present nor provably absent. Surfaced, never smoothed into "gone".
            result = (False, "INDETERMINATE: " + " ".join(err.split())[:300], None)
    cache[key] = result
    return result


# ── The protection schedule: read from committed files, never from a constant here ─────────────
#
# 🚨 THE ORDER OF TWO CLOCKS IS THE WHOLE PROTECTION. `acr purge` skips locked manifests; the lock
# job is what makes a pinned manifest locked. If the lock ever fires AFTER the purge, every pin
# moved since the previous lock is unprotected for a full day — and nothing anywhere would say so,
# because both jobs would report success about their own work. So the relation between the two
# schedules is asserted, from the two places they are actually written down.

LOCK_WORKFLOW = ".github/workflows/lock-pinned-digests.yml"
RETENTION_RECORD = ".github/acr-retention/tasks.json"

CRON_LINE_RE = re.compile(r"^\s*-\s*cron:\s*(?P<quote>['\"]?)(?P<expr>[^'\"#\r\n]+)(?P=quote)", re.MULTILINE)


def _cron_field_matches(spec: str, value: int, low: int, high: int) -> bool:
    """One crontab field against one value. Supports `*`, lists, ranges and `/steps`."""
    for part in spec.split(","):
        part = part.strip()
        if not part:
            return False
        step = 1
        if "/" in part:
            part, _, raw_step = part.partition("/")
            if not raw_step.isdigit() or int(raw_step) == 0:
                return False
            step = int(raw_step)
        if part in ("*", "?"):
            start, end = low, high
        elif "-" in part.lstrip("-"):
            start_s, _, end_s = part.partition("-")
            if not (start_s.isdigit() and end_s.isdigit()):
                return False
            start, end = int(start_s), int(end_s)
        elif part.isdigit():
            start = end = int(part)
            if step != 1:
                end = high
        else:
            return False
        if start < low or end > high or start > end:
            return False
        if start <= value <= end and (value - start) % step == 0:
            return True
    return False


def cron_matches(expr: str, when: dt.datetime) -> bool:
    """Does this 5-field crontab expression fire at `when` (UTC, minute resolution)?"""
    fields = expr.split()
    if len(fields) != 5:
        return False
    minute, hour, dom, month, dow = fields
    # GitHub/POSIX: when BOTH day-of-month and day-of-week are restricted the match is their union.
    dom_restricted = dom.strip() not in ("*", "?")
    dow_restricted = dow.strip() not in ("*", "?")
    day_ok_dom = _cron_field_matches(dom, when.day, 1, 31)
    day_ok_dow = _cron_field_matches(dow, when.isoweekday() % 7, 0, 6)
    if dom_restricted and dow_restricted:
        day_ok = day_ok_dom or day_ok_dow
    elif dom_restricted:
        day_ok = day_ok_dom
    elif dow_restricted:
        day_ok = day_ok_dow
    else:
        day_ok = True
    return (
        _cron_field_matches(minute, when.minute, 0, 59)
        and _cron_field_matches(hour, when.hour, 0, 23)
        and _cron_field_matches(month, when.month, 1, 12)
        and day_ok
    )


def last_cron_occurrence(exprs: list[str], now: dt.datetime, horizon_days: int = 8) -> dt.datetime | None:
    """The most recent minute at or before `now` at which any of these crons fires.

    Walks back minute by minute rather than solving the expression: the fleet's crons are daily and
    the horizon is a week, so the loop is ~11k cheap comparisons and there is no clever arithmetic
    to get subtly wrong. None means "no occurrence within the horizon" — for a job whose whole job
    is to run before a NIGHTLY purge, that is a defect, not an answer.
    """
    cursor = now.replace(second=0, microsecond=0)
    for _ in range(horizon_days * 24 * 60 + 1):
        if any(cron_matches(expr, cursor) for expr in exprs):
            return cursor
        cursor -= dt.timedelta(minutes=1)
    return None


def read_lock_schedule(repo_root: Path) -> tuple[list[str], str | None]:
    """The `cron:` expressions the lock workflow declares — from the file, never remembered here."""
    path = repo_root / LOCK_WORKFLOW
    if not path.is_file():
        return [], (
            f"{LOCK_WORKFLOW} does not exist. That workflow IS the protection: it locks every "
            "manifest the fleet pins so `acr purge` skips it. Without it every pin ages out."
        )
    text = path.read_text(encoding="utf-8", errors="replace")
    exprs = [m.group("expr").strip() for m in CRON_LINE_RE.finditer(text)]
    if not exprs:
        return [], (
            f"{LOCK_WORKFLOW} declares no `schedule:` cron. A protection that only runs when "
            "someone remembers to dispatch it is not a protection."
        )
    return exprs, None


def read_purge_schedules(repo_root: Path) -> tuple[list[tuple[str, str]], str | None]:
    """(task name, cron) for every ENABLED purge task in the committed retention record."""
    path = repo_root / RETENTION_RECORD
    if not path.is_file():
        return [], f"{RETENTION_RECORD} does not exist, so the purge schedule is unknown."
    try:
        doc = json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as exc:
        return [], f"{RETENTION_RECORD} is not JSON: {exc}"
    tasks = []
    for task in doc.get("tasks", []):
        if str(task.get("status", "")).lower() != "enabled":
            continue          # a Disabled task deletes nothing; only a live clock can beat the lock
        schedule = task.get("schedule")
        if schedule:
            tasks.append((task.get("name", "?"), str(schedule)))
    if not tasks:
        # 🚨 AN ABSENCE IS ACCEPTABLE ONLY WHEN IT IS DECLARED — the same rule the instance roster
        # follows. "No enabled purge" has three causes and they are not the same verdict: the
        # record went stale, retention silently stopped, or a person deliberately paused it. The
        # first two must stay RED; the third is a fact, and on 2026-09-12 it became the true one
        # (Roland paused `purge-old-images` while protection was incomplete, Memex#219). Without
        # this branch, recording that truth would redden every pull request in the repository —
        # which is how a record gets left saying `Enabled` about a task that is not.
        pause = doc.get("pause") or {}
        missing = [k for k in ("inForce", "since", "reason", "reEnableWhen") if not pause.get(k)]
        if pause.get("inForce") is True and not missing:
            return [], None
        if pause:
            return [], (
                f"{RETENTION_RECORD} records no ENABLED purge task AND its `pause` declaration is "
                f"incomplete (missing or empty: {', '.join(missing) or 'inForce is not true'}). A "
                "pause that does not say why, since when, and what re-enables it is indistinguishable "
                "from a record nobody updated."
            )
        return [], (
            f"{RETENTION_RECORD} records no ENABLED purge task with a schedule and declares no "
            "`pause`. Either the record is stale or retention stopped; both are worth a red rather "
            "than a silent pass. A DELIBERATE pause is declared in the record's `pause` block."
        )
    return tasks, None


def ordering_problems(lock_crons: list[str], purges: list[tuple[str, str]]) -> list[str]:
    """Pure comparison of two sets of daily clocks — the lock's, and every enabled purge's.

    Separated from the file reads so --self-test can drive it with SABOTAGED schedules and watch it
    go red. A gate that has only ever been shown to pass has not been shown to be a gate.
    """
    problems: list[str] = []
    # Anchor on a fixed, ordinary UTC day rather than "now": the question is about the relation
    # between two daily clocks, and it must answer the same on every day this runs.
    end_of_day = dt.datetime(2026, 1, 7, 23, 59, tzinfo=dt.timezone.utc)   # a Wednesday, mid-month
    any_lock = last_cron_occurrence(lock_crons, end_of_day, horizon_days=1)
    if any_lock is None:
        return [
            f"{LOCK_WORKFLOW}'s schedule ({', '.join(lock_crons)}) does not fire on an ordinary "
            "day. The purge runs nightly, so a lock that does not is not protection."
        ]
    for name, expr in purges:
        purge_at = last_cron_occurrence([expr], end_of_day, horizon_days=1)
        if purge_at is None:
            problems.append(
                f"purge task '{name}' has schedule '{expr}', which does not fire on an ordinary "
                "day — the record cannot be related to the lock's clock."
            )
            continue
        # 🚨 "SOME lock fires earlier the SAME night", not "the last lock of the day is earlier".
        # A schedule that fires more than once (`0 1,5 * * *`) satisfies the protection through its
        # 01:00 run; comparing only the day's last occurrence would red on it for no reason, and a
        # gate that is usually wrong stops being read. Requiring the same DAY is what keeps the
        # 04:00 sabotage red — yesterday's 04:00 lock does technically precede today's 03:00 purge,
        # 23 hours earlier, which is the whole defect rather than a satisfaction of it.
        lock_before = last_cron_occurrence(
            lock_crons, purge_at - dt.timedelta(minutes=1), horizon_days=1
        )
        if lock_before is None or lock_before.date() != purge_at.date():
            problems.append(
                f"no lock fires before purge task '{name}' on the same night: the lock schedule is "
                f"{', '.join(lock_crons)} and the purge runs at {purge_at:%H:%M} UTC. Every pin "
                "moved since the previous lock run would face that purge unprotected, and both "
                "jobs would still report success about their own work."
            )
    return problems


def check_schedule_ordering(repo_root: Path) -> list[str]:
    """Is the lock still scheduled to fire BEFORE the purge? Offline, credential-free.

    Returns a list of problems; empty means the ordering holds. This runs in --self-test, so a pull
    request that moves either clock the wrong way reds on that pull request rather than on the night
    a satellite's pins quietly stop being protected.
    """
    lock_crons, lock_error = read_lock_schedule(repo_root)
    if lock_error:
        return [lock_error]
    purges, purge_error = read_purge_schedules(repo_root)
    if purge_error:
        return [purge_error]
    return ordering_problems(lock_crons, purges)


def classify_protection(
    delete_enabled: bool | None,
    moved_at: dt.datetime | None,
    last_lock: dt.datetime | None,
    move_error: str = "",
) -> tuple[str, str]:
    """(verdict, detail) for one resolving pin. Pure, so --self-test can drive every branch.

    🚨 The default is never PROTECTED. Only `deleteEnabled is False` — the registry saying the
    manifest is locked, in its own words — earns that; every other answer, including "the field was
    not in the response", is INDETERMINATE. That asymmetry is the whole point: the failure this
    guards against is a protection that quietly stopped, and a protection that cannot be read has
    not been shown to be working.
    """
    if delete_enabled is False:
        return "PROTECTED", ""
    if delete_enabled is None:
        return "INDETERMINATE", (
            "the manifest exists but the registry did not report "
            "changeableAttributes.deleteEnabled"
        )
    # delete_enabled is True — unlocked. Has the lock job had an opportunity?
    if last_lock is None:
        # No schedule to compare against. The missing schedule is its own red (see
        # check_schedule_ordering); the pin stays UNPROTECTED rather than being exempted by it,
        # because "the clock is gone" is the worst possible reason to relax a protection check.
        return "UNPROTECTED", "no lock schedule could be read, so no opportunity can be established"
    if moved_at is None:
        return "INDETERMINATE", (
            "this manifest is UNLOCKED and it could not be established whether the lock job has "
            f"had an opportunity since the pin moved — {move_error or 'no commit date'}"
        )
    if moved_at > last_lock:
        return "PENDING-LOCK", (
            f"the declaring workflow last changed {moved_at:%Y-%m-%dT%H:%M:%SZ}, after the last "
            f"scheduled lock at {last_lock:%Y-%m-%dT%H:%M:%SZ}"
        )
    return "UNPROTECTED", (
        f"the declaring workflow last changed {moved_at:%Y-%m-%dT%H:%M:%SZ}; the lock was scheduled "
        f"at {last_lock:%Y-%m-%dT%H:%M:%SZ} and did not protect it"
    )


def last_touched(gh_repo: str, path: str) -> tuple[dt.datetime | None, str]:
    """When the default branch last changed this file. One REST call, Contents: Read.

    🚨 This is a PROXY and it errs in the SAFE direction only. It answers "could this pin have
    moved since the last lock run", never "did it". A file touched for an unrelated reason exempts
    its pins from the protection failure — softening, never a false alarm — and the exemption is
    COUNTED in the denominator so a sweep that exempted the whole fleet cannot read as a clean one.
    """
    rc, out, err = gh_api(f"repos/{gh_repo}/commits?path={path}&per_page=1")
    if rc != 0:
        return None, err.strip()[:200] or "commits listing failed"
    try:
        commits = json.loads(out)
    except json.JSONDecodeError as exc:
        return None, f"commits listing is not JSON: {exc}"
    if not commits:
        return None, "no commit touches this path"
    stamp = (((commits[0] or {}).get("commit") or {}).get("committer") or {}).get("date")
    if not stamp:
        return None, "commit carries no committer date"
    try:
        return dt.datetime.fromisoformat(stamp.replace("Z", "+00:00")), ""
    except ValueError as exc:
        return None, f"unparseable commit date {stamp!r}: {exc}"


# ── Reporting ──────────────────────────────────────────────────────────────────────────────────


def die(headline: str, lines: list[str]) -> None:
    print(f"::error::{headline}")
    for line in lines:
        print(f"  {line}")
    sys.exit(1)


def emit(text: str) -> None:
    print(text)
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as handle:
            handle.write(text + "\n")


def run(repos: list[str], registry: str, repo_root: Path) -> int:
    registry_control_probe(registry)

    # 🚨 FIRST, and with no credential: is the protection still SCHEDULED, and still scheduled
    # BEFORE the purge? Everything below assumes the lock job ran; this is what makes that an
    # assertion rather than an assumption.
    ordering_problems = check_schedule_ordering(repo_root)
    lock_crons, _ = read_lock_schedule(repo_root)
    last_lock = last_cron_occurrence(lock_crons, dt.datetime.now(dt.timezone.utc)) if lock_crons else None

    scans: list[RepoScan] = []
    for gh_repo in repos:
        scan = RepoScan(gh_repo=gh_repo)
        files, error = fetch_workflows(gh_repo)
        if error:
            scan.unreadable = error
            scans.append(scan)
            continue
        scan.workflows = len(files)
        for filename, text in files:
            pins, malformed = extract(gh_repo, filename, text)
            scan.pins.extend(pins)
            scan.malformed.extend(malformed)
        scans.append(scan)

    cache: dict = {}
    for scan in scans:
        for pin in scan.pins:
            if not pin.acr_repos:
                pin.verdict = "GONE"
                pin.detail = "no ACR repository is named anywhere in this repo's workflows"
                continue
            indeterminate = ""
            for acr_repo in pin.acr_repos:
                ok, detail, delete_enabled = resolve(registry, acr_repo, pin.digest, cache)
                if ok:
                    pin.verdict, pin.resolved_in = "RESOLVES", acr_repo
                    # Protection is decided in two passes: the registry answer now, and — only for
                    # the unlocked ones — the pin's own age, below, which costs a REST call each.
                    if delete_enabled is False:
                        pin.protection = "PROTECTED"
                    elif delete_enabled is True:
                        pin.protection = "UNPROTECTED"
                    else:
                        pin.protection, pin.protection_detail = classify_protection(None, None, None)
                    pin.delete_enabled = delete_enabled
                    break
                if detail.startswith("INDETERMINATE"):
                    indeterminate = detail
            else:
                pin.verdict = "INDETERMINATE" if indeterminate else "GONE"
                pin.detail = indeterminate or "tried: " + ", ".join(pin.acr_repos)

    # PENDING-LOCK: an UNPROTECTED pin the lock job has not yet had a scheduled opportunity to see.
    # One commits call per (repository, workflow file) that actually holds an unprotected pin — so
    # a fleet that is fully protected costs nothing extra.
    touched_cache: dict[tuple[str, str], tuple[dt.datetime | None, str]] = {}
    for scan in scans:
        for pin in scan.pins:
            if pin.protection != "UNPROTECTED":
                continue
            moved_at, error = None, ""
            if last_lock is not None:
                key = (pin.gh_repo, pin.workflow_file)
                if key not in touched_cache:
                    touched_cache[key] = last_touched(
                        pin.gh_repo, f".github/workflows/{pin.workflow_file}"
                    )
                moved_at, error = touched_cache[key]
            pin.protection, pin.protection_detail = classify_protection(
                pin.delete_enabled, moved_at, last_lock, error
            )

    all_pins = [pin for scan in scans for pin in scan.pins]
    pinning = [scan for scan in scans if scan.pins]
    silent = [scan for scan in scans if not scan.pins and not scan.unreadable]
    unreadable = [scan for scan in scans if scan.unreadable]
    malformed = [bad for scan in scans for bad in scan.malformed]
    gone = [pin for pin in all_pins if pin.verdict == "GONE"]
    indeterminate = [pin for pin in all_pins if pin.verdict == "INDETERMINATE"]
    resolved = [pin for pin in all_pins if pin.verdict == "RESOLVES"]
    distinct_digests = {pin.digest for pin in all_pins}
    gone_digests = {pin.digest for pin in gone}
    protected = [pin for pin in resolved if pin.protection == "PROTECTED"]
    unprotected = [pin for pin in resolved if pin.protection == "UNPROTECTED"]
    pending_lock = [pin for pin in resolved if pin.protection == "PENDING-LOCK"]
    protection_unknown = [pin for pin in resolved if pin.protection == "INDETERMINATE"]

    # 🚨 THE DENOMINATOR, PRINTED. A bare "0 unresolved" has two causes — every pin is fine, or
    # nothing was extracted — and they are indistinguishable from the verdict alone.
    emit("")
    emit(f"### Pinned image digests — {registry}.azurecr.io")
    emit("")
    emit(f"    repositories scanned                  {len(scans)}")
    emit(f"    …declaring at least one digest pin    {len(pinning)}")
    emit(f"    …declaring NO digest pin              {len(silent)}"
         + (f"  ({', '.join(s.gh_repo for s in silent)})" if silent else ""))
    emit(f"    …whose workflows could not be read    {len(unreadable)}")
    # 🚨 DECLARATIONS and DIGESTS are different numbers and the label must say which. One digest
    # is commonly pinned by four repositories at once (the fleet moves its pins as ONE set), so
    # "12 pins, 9 resolve" invites the reading "nine different images are fine" when it means nine
    # declaration SITES. Both are worth having: declarations are how much editing a pin move costs,
    # distinct digests are how many manifests retention actually has to keep alive.
    emit(f"    pin DECLARATIONS found                {len(all_pins)}")
    emit(f"    …over how many DISTINCT digests       {len(distinct_digests)}")
    emit(f"    …declarations that RESOLVE            {len(resolved)}")
    emit(f"    …declarations that are GONE           {len(gone)}")
    emit(f"    …INDETERMINATE (registry not reached) {len(indeterminate)}")
    emit(f"    pin-shaped declarations MALFORMED     {len(malformed)}")
    emit("")
    # 🚨 THE PROTECTION DENOMINATOR — the leading indicator beside the lagging one. Existence says
    # whether the wedge already happened; protection says whether it is coming. The EXEMPT line is
    # printed even at zero: it is the one number that could quietly make this whole arm vacuous.
    emit("    of the declarations that RESOLVE —")
    emit(f"      …PROTECTED (deleteEnabled false)    {len(protected)}")
    emit(f"      …UNPROTECTED — purge can delete     {len(unprotected)}")
    emit(f"      …PENDING-LOCK (exempt: moved since  {len(pending_lock)}")
    emit("        the last scheduled lock)")
    emit(f"      …protection INDETERMINATE           {len(protection_unknown)}")
    if last_lock is not None:
        emit(f"      last scheduled lock run             {last_lock:%Y-%m-%dT%H:%M:%SZ}"
             f"  ({', '.join(lock_crons)})")
    emit("")
    for scan in scans:
        if scan.unreadable:
            emit(f"  {scan.gh_repo}: UNREADABLE — {scan.unreadable}")
            continue
        if not scan.pins:
            emit(f"  {scan.gh_repo}: {scan.workflows} workflow file(s), no digest pin declared")
            continue
        emit(f"  {scan.gh_repo}: {scan.workflows} workflow file(s), {len(scan.pins)} pin(s)")
        for pin in sorted(scan.pins, key=lambda p: (p.verdict, p.where)):
            where = f"{pin.where}"
            target = pin.resolved_in or "/".join(pin.acr_repos) or "(no ACR repo named)"
            state = pin.verdict if pin.verdict != "RESOLVES" else f"RESOLVES/{pin.protection}"
            emit(f"      {state:26s} {target}@{pin.digest[:19]}…  {where}"
                 + (f"  [{pin.detail}]" if pin.detail else ""))
    emit("")

    failed = False

    # 🚨 THE BUCKETS MUST ACCOUNT FOR EVERY RESOLVING PIN. A pin that fell through every branch
    # would leave the protection block reporting fewer manifests than it examined, with no line
    # saying so — the "0 at risk over an unstated denominator" shape this whole file refuses.
    accounted = len(protected) + len(unprotected) + len(pending_lock) + len(protection_unknown)
    if accounted != len(resolved):
        print(f"::error::{len(resolved)} pin declaration(s) resolve but only {accounted} carry a "
              "protection verdict.")
        print("  The protection buckets must partition the resolving pins. An unaccounted pin is")
        print("  invisible in the report, which is the failure this arm exists to prevent.")
        failed = True

    # The protection's own schedule — asserted before anything that depends on it having run.
    for problem in ordering_problems:
        print("::error::the pinned-manifest protection is not correctly scheduled.")
        print(f"  {problem}")
        print("  `acr purge` skips locked manifests and lock-pinned-digests.yml is what locks them;")
        print("  the ordering of those two clocks IS the protection. See")
        print("  Doc/Architecture/PinnedImageRetention.")
        failed = True

    for scan in unreadable:
        print(f"::error::{scan.gh_repo}: its workflows could not be read, so its pins were NOT checked.")
        print(f"  {scan.unreadable}")
        print("  Failing closed: an unswept repository must never read as a swept one.")
        failed = True

    for bad in malformed:
        print(f"::error::{bad.gh_repo} — {bad.where} is pin-shaped but its value is not a digest: "
              f"{bad.value!r}")
        print("  A registry accepts exactly `sha256:` + 64 lowercase hex. A looser matcher reads a")
        print("  placeholder as 'no pin present' and passes having checked nothing — that hole is")
        print("  why this is a failure and not a warning. Write the real digest, or a `${{ … }}`")
        print("  forward to the declaration that carries it.")
        failed = True

    for pin in indeterminate:
        print(f"::error::{pin.gh_repo} — {pin.where}: could not determine whether "
              f"{pin.digest} exists.")
        print(f"  {pin.detail}")
        print("  Indeterminate is not a pass and it is not 'gone' either. Fix the registry access")
        print("  and re-run.")
        failed = True

    if gone:
        # Declarations vs manifests again: N red declarations can be ONE deleted manifest pinned
        # from N places, which is a single retention event and a single fix — or N separate ones,
        # which is a policy failure of a different size. Say which before listing them.
        print(f"::error::{len(gone)} pin declaration(s) name {len(gone_digests)} manifest(s) that "
              f"no longer exist in {registry}.azurecr.io.")

    for pin in gone:
        print(f"::error::{pin.gh_repo} — {pin.where} pins {pin.digest}, which no longer exists in "
              f"{registry}.azurecr.io.")
        print(f"  tried: {', '.join(pin.acr_repos) or '(no ACR repository named in its workflows)'}")
        print("  CONSEQUENCE — every job that pulls this image dies at `manifest unknown` before it")
        print("  runs anything, INCLUDING that repository's nightly, so the repo loses its gate and")
        print("  its green baseline together (MeshWeaver#3438).")
        print("  🚨 The purge run and the breakage are NOT adjacent in any log: runners and pods")
        print("  serve from cache long after the delete, so the failure surfaces at the next fresh")
        print("  pull. Do not look for an `ImagePullBackOff` near the purge timestamp.")
        print("  TO FIX — move the pin to a manifest that exists, as one set:")
        print(f"      az acr repository show-tags --name {registry} --repository <repo> \\")
        print("        --orderby time_desc --detail --query \"[].{tag:name,updated:lastUpdateTime}\" -o tsv")
        print("  and then protect the new one — see Doc/Architecture/PinnedImageRetention.")
        failed = True

    # 🚨 THE LEADING INDICATOR. Everything above this point fires after the manifest is already
    # gone — which is to say, after the repository is already wedged. This fires while the bytes
    # are still there and the fix is one `az` command.
    if protection_unknown:
        print(f"::error::{len(protection_unknown)} pin declaration(s) exist but their protection "
              "state could not be read, so it is NOT known whether the purge can delete them.")
    for pin in protection_unknown:
        print(f"::error::{pin.gh_repo} — {pin.where}: {pin.protection_detail}")
        print("  Indeterminate is not a confirmation. A manifest whose lock cannot be read has not")
        print("  been shown to be locked.")
        failed = True

    if unprotected:
        unprotected_digests = {pin.digest for pin in unprotected}
        print(f"::error::{len(unprotected)} pin declaration(s) over {len(unprotected_digests)} "
              "manifest(s) are pinned and NOT protected from the purge.")
    for pin in unprotected:
        print(f"::error::{pin.gh_repo} — {pin.where} pins "
              f"{pin.resolved_in}@{pin.digest}, which is UNLOCKED (deleteEnabled=true).")
        if pin.protection_detail:
            print(f"  {pin.protection_detail}")
        print("  CONSEQUENCE — `--ago 7d --keep 10` is measured in AGE and in NEWER BUILDS, so an")
        print("  unlocked pinned manifest is not at some hypothetical risk: it will be deleted, and")
        print("  the only open question is which night. When it goes, every job that pulls it dies")
        print("  at `manifest unknown` before it runs anything, INCLUDING that repository's own")
        print("  nightly (MeshWeaver#3438 — three satellites down at once, Education's baseline")
        print("  among them).")
        print("  🚨 This is the LEADING half. `GONE` above is the same defect after the fact.")
        print("  WHY IT IS UNLOCKED — the nightly lock job derives its set from the fleet's pins")
        print("  and locks them (lock-pinned-digests.yml, 01:00 UTC). A pin it has had an")
        print("  opportunity to see and did not protect means that job is not doing its work:")
        print("  check its last run, and check that the AZURE_CLIENT_ID app still holds")
        print("  'Container Registry Repository Writer' on the registry.")
        print("  TO FIX NOW — one idempotent command, then fix the job:")
        print(f"      az acr repository update --name {registry} \\")
        print(f"        --image {pin.resolved_in}@{pin.digest} --delete-enabled false")
        print("  Do NOT raise --ago/--keep instead: that moves the cliff rather than removing it")
        print("  (Doc/Architecture/PinnedImageRetention).")
        failed = True

    # 🚨 A SWEEP THAT FOUND NOTHING HAS NOT PROVEN THE FLEET IS CLEAN — but "zero pins" stopped
    # being the evidence for that on 2026-09-12. This assertion read "six repositories pinned at
    # least one image on 2026-09-06, so zero means the extractor stopped matching"; #3842 then moved
    # every satellite to RESOLVING the platform set at run time (`platform-ref` /
    # `resolve-platform.py`), and measured with this very extractor the fleet declares ZERO digest
    # pins and SIX repositories that name a platform image without pinning one. Pinning did stop.
    # The same premise failed the LOCK lane the same morning
    # (run 34664099031, two hours before the purge — Doc/Architecture/ArtifactRetentionInterlock).
    #
    # An assertion about the FLEET expires like that; one about the INSTRUMENT does not. So:
    #   * the extractor is exercised against a fixture carrying known digest pins on every run,
    #     which fires even when the fleet happens to declare pins anyway;
    #   * the surviving fleet-shaped denominator is the one still impossible — a fleet reaching for
    #     NO platform image at all.
    for problem in extractor_control():
        print(f"::error::{problem}")
        failed = True

    if failed:
        return 1
    emit(verdict_line(len(scans), len(all_pins), len(resolved), len(distinct_digests),
                      len(pinning), len(protected), len(pending_lock)))
    return 0


def verdict_line(scanned: int, pins: int, resolved: int, distinct: int,
                 pinning: int, protected: int, pending_lock: int) -> str:
    """The sweep's one-line verdict — pure, so the empty-subject case is falsifiable offline.

    🚨 GREEN OVER AN EMPTY SUBJECT MUST SAY SO. Zero digest pins is a true answer now: #3842 moved
    the satellites to RESOLVING the platform set at run time. But "all 0 declaration(s) resolve"
    reads like a sweep that checked something, and that sentence over an empty subject is how a
    lane goes vacuous without anybody noticing. The evidence that this zero is measured is the
    extractor control, not the absence itself."""
    if pins == 0:
        return (f"NOTHING TO SWEEP: {scanned} repository(ies) scanned and not one declares a digest "
                "pin. Under #3842 the satellites RESOLVE the platform set at run time, so this is a "
                "measured zero and not a broken extractor — the extractor control passed against a "
                "fixture carrying known pins. What each installation is RUNNING is protected by "
                "`lock-pinned-digests.py` AXIS 3, and the tag references by its tag lock "
                "(Doc/Architecture/ArtifactRetentionInterlock).")
    return (f"All {resolved} pin declaration(s) — {distinct} distinct digest(s) — "
            f"across {pinning} repository(ies) resolve; {protected} are locked against the "
            f"purge and {pending_lock} await the next scheduled lock.")


def extractor_control() -> list[str]:
    """🚨 THE INSTRUMENT, MEASURED ON EVERY RUN — the assertion that replaced the zero-pin one.

    Known digest pins in a fixture, through the SAME `extract()` the sweep uses. It fires when the
    matcher breaks even if the fleet happens to declare pins anyway, which is precisely what the
    fleet-shaped assertion could not do."""
    pins, malformed = extract("Systemorph/Control", "ci.yml", SELF_TEST_YAML)
    found = {pin.digest for pin in pins}
    expected = {
        "sha256:df19f10afc1f807441b403eb0dfa4b8645d5187b830f77ad080def7fc65b391f",
        "sha256:dab2a7b3a7e1523d7d728d6a9a397abef5f98b0e1e0ff8f02395a433bdedf97d",
        "sha256:d81df6cc79ad68fcfd9fa3d4cf40588e97c7adfe8c2516e1626231c0412b1f3f",
    }
    problems: list[str] = []
    if not expected <= found:
        problems.append(
            "THE DIGEST EXTRACTOR STOPPED MATCHING. Its control fixture declares "
            f"{len(expected)} digest pins and `extract()` returned {len(found)}. Every "
            "'no pins found' answer in this run is therefore worthless — including the one that "
            "would otherwise read as a fleet that stopped pinning. Run --self-test.")
    if not malformed:
        problems.append(
            "the extractor control fixture declares a PLACEHOLDER pin and the extractor reported "
            "none malformed — the vacuity arm of the same instrument has stopped working.")
    return problems


# ── Self-test: the extractor's own both-ways falsification, offline ────────────────────────────

SELF_TEST_YAML = """
name: fixture
env:
  MW_PLATFORM_REF: bbcb22f256240c7132fea1e56c7df3f97564e648
  MW_PORTAL_DIGEST: sha256:dab2a7b3a7e1523d7d728d6a9a397abef5f98b0e1e0ff8f02395a433bdedf97d
  MW_TEST_DIGEST: sha256:df19f10afc1f807441b403eb0dfa4b8645d5187b830f77ad080def7fc65b391f
  MW_BROKEN_DIGEST: sha256:PLACEHOLDER
  MW_SHORT_DIGEST: sha256:df19f10a
  MW_TESTER_IMAGE: meshweaver.azurecr.io/mw-plugin-test
jobs:
  a:
    outputs:
      digest:
        description: a composite-style key whose value is the nested block below it
    steps:
      - id: pin
        run: |
          echo "image-digest=sha256:d91d333f19f5b42d59fa76004b6904d5ff46a0d30952e6b854f9505e2c5cdd0d" >> "$GITHUB_OUTPUT"
          echo "bake-image-digest=sha256:PLACEHOLDER" >> "$GITHUB_OUTPUT"
          echo "image-digest=$RESOLVED" >> "$GITHUB_OUTPUT"
          echo "digest: $SOME_SHELL_VARIABLE"
      - env:
          BAKE_IMAGE: meshweaver.azurecr.io/mw-plugin-test@${{ env.MW_TEST_DIGEST }}
          MW_PORTAL_IMAGE: ${{ vars.MW_PORTAL_IMAGE || format('meshweaver.azurecr.io/memex-portal-ai@{0}', env.MW_PORTAL_DIGEST) }}
          MW_FORWARD_DIGEST: ${{ env.MW_TEST_DIGEST }}
          LITERAL: meshweaver.azurecr.io/memex-migration@sha256:d81df6cc79ad68fcfd9fa3d4cf40588e97c7adfe8c2516e1626231c0412b1f3f
        run: echo hi
"""


def self_test_schedule(repo_root: Path) -> list[str]:
    """The protection-schedule arms: the cron evaluator, then the REAL committed schedules.

    🚨 The second half is deliberately not a fixture. The whole point of the ordering assertion is
    that it is about the two clocks this repository actually ships, and a fixture would agree with
    itself forever while the shipped ones drifted apart — the vacuity this file's own history is
    made of (a placeholder digest read as an absence; a release arm asserted against a fixture with
    nothing to release).
    """
    failures: list[str] = []

    def expect(expr: str, when: str, want: bool, why: str) -> None:
        moment = dt.datetime.fromisoformat(when).replace(tzinfo=dt.timezone.utc)
        if cron_matches(expr, moment) != want:
            failures.append(f"cron {expr!r} at {when}: expected {want} — {why}")

    expect("0 1 * * *", "2026-01-07T01:00", True, "a daily 01:00 job fires at 01:00")
    expect("0 1 * * *", "2026-01-07T01:01", False, "…and not a minute later")
    expect("0 1 * * *", "2026-01-07T03:00", False, "…and not at the purge's hour")
    expect("20 5 * * *", "2026-01-07T05:20", True, "this sweep's own schedule")
    expect("*/15 * * * *", "2026-01-07T04:45", True, "a step field")
    expect("*/15 * * * *", "2026-01-07T04:50", False, "…which does not fire off-step")
    expect("0 1 * * 1-5", "2026-01-07T01:00", True, "a weekday range, on a Wednesday")
    expect("0 1 * * 0", "2026-01-07T01:00", False, "…and not on a Sunday-only cron")
    expect("0 1 * *", "2026-01-07T01:00", False, "a 4-field expression is not a cron")
    expect("0 99 * * *", "2026-01-07T01:00", False, "an out-of-range hour matches nothing")

    # last_cron_occurrence walks BACK; it must never answer with a future minute.
    now = dt.datetime(2026, 1, 7, 2, 30, tzinfo=dt.timezone.utc)
    last = last_cron_occurrence(["0 1 * * *"], now)
    if last != dt.datetime(2026, 1, 7, 1, 0, tzinfo=dt.timezone.utc):
        failures.append(f"last_cron_occurrence returned {last}, expected 2026-01-07T01:00Z")
    if last_cron_occurrence(["0 0 30 2 *"], now, horizon_days=8) is not None:
        failures.append("a cron that cannot fire within the horizon must answer None, not a time")

    # …and the real thing: the shipped lock workflow and the shipped purge record.
    for problem in check_schedule_ordering(repo_root):
        failures.append(f"the shipped schedules do not hold the ordering: {problem}")

    # 🚨 POSITIVE CONTROL — the assertion must be able to FAIL. Every arm above could pass with the
    # comparison deleted; these two cannot.
    lock_crons, error = read_lock_schedule(repo_root)
    purges, purge_error = read_purge_schedules(repo_root)
    if error or purge_error:
        failures.append(f"falsification arm has no input: {error or purge_error}")
    else:
        # 🚨 THE COMPARATOR IS DRIVEN ON A FIXTURE, NOT ON THE LIVE RECORD. It used to be driven on
        # `purges`, and on 2026-09-12 that silently went VACUOUS: Roland paused `purge-old-images`
        # (Memex#219), the record now declares that pause, `purges` is legitimately EMPTY, and
        # `ordering_problems(anything, [])` compares nothing and returns no problem — so all four
        # arms below would have passed while proving nothing. The self-test caught it, which is the
        # only reason it is not shipped that way. A comparator must stay proven on a day when its
        # subject is switched off.
        fixture = [("fixture-purge", "0 3 * * *")]
        if ordering_problems(["0 23 * * *"], fixture) == []:
            failures.append(
                "SABOTAGE UNDETECTED: a lock scheduled at 23:00 — after every purge — was accepted"
            )
        if ordering_problems(["0 0 30 2 *"], fixture) == []:
            failures.append(
                "SABOTAGE UNDETECTED: a lock schedule that never fires was accepted"
            )
        if ordering_problems(lock_crons, fixture) != []:
            failures.append("the shipped pair was rejected by the very check that accepted it above")
        # …and the multi-occurrence case is ACCEPTED, so the gate does not cry wolf on a schedule
        # that fires twice and satisfies the protection through its earlier run.
        if ordering_problems(["0 1,23 * * *"], fixture) != []:
            failures.append(
                "FALSE POSITIVE: a lock firing at 01:00 AND 23:00 was rejected, though its 01:00 "
                "run precedes every purge"
            )
        # …and THEN the shipped record, whichever of its two states it is in. Both are asserted, so
        # neither can become the silent one.
        if purges:
            if ordering_problems(lock_crons, purges) != []:
                failures.append(
                    "the SHIPPED schedules no longer order: the lock does not fire before every "
                    f"enabled purge ({', '.join(n for n, _ in purges)})")
        else:
            record = json.loads((repo_root / RETENTION_RECORD).read_text(encoding="utf-8"))
            pause = record.get("pause") or {}
            if pause.get("inForce") is not True:
                failures.append(
                    "the record lists no enabled purge and `read_purge_schedules` accepted it "
                    "anyway without an in-force `pause` — an undeclared absence must stay RED")
            for key in ("since", "reason", "reEnableWhen"):
                if not pause.get(key):
                    failures.append(
                        f"the purge pause is in force and declares no `{key}`. A pause that does "
                        "not say why, since when and what re-enables it is a record nobody updated")

    # 🚨 AND THE REFUSAL ITSELF, driven against fixtures rather than against the shipped record.
    # The arms above assert what the RECORD says; they cannot see whether `read_purge_schedules`
    # would still refuse an UNDECLARED absence, because the shipped record declares one. Making the
    # function accept anything left this self-test green until this arm existed.
    import tempfile as _tempfile
    with _tempfile.TemporaryDirectory() as _scratch:
        _root = Path(_scratch)
        (_root / RETENTION_RECORD).parent.mkdir(parents=True, exist_ok=True)

        def _purges_for(document: dict):
            (_root / RETENTION_RECORD).write_text(json.dumps(document), encoding="utf-8")
            return read_purge_schedules(_root)

        _no_pause = {"tasks": [{"name": "p", "status": "Disabled", "schedule": "0 3 * * *"}]}
        if not _purges_for(_no_pause)[1]:
            failures.append(
                "SABOTAGE UNDETECTED: no enabled purge and NO `pause` declaration was accepted — "
                "a stale record and a silently stopped retention would both read as fine")
        _partial = dict(_no_pause, pause={"inForce": True, "since": "2026-09-12"})
        if not _purges_for(_partial)[1]:
            failures.append(
                "SABOTAGE UNDETECTED: a `pause` with no `reason`/`reEnableWhen` was accepted — a "
                "pause that explains nothing is a record nobody updated")
        _complete = dict(_no_pause, pause={"inForce": True, "since": "2026-09-12",
                                           "reason": "r", "reEnableWhen": "w"})
        _tasks, _err = _purges_for(_complete)
        if _err or _tasks:
            failures.append(
                f"a COMPLETE pause declaration was not accepted as a stated absence: {_err}")
        _enabled = {"tasks": [{"name": "p", "status": "Enabled", "schedule": "0 3 * * *"}]}
        if _purges_for(_enabled)[0] != [("p", "0 3 * * *")]:
            failures.append("an ENABLED purge task was not read back from the record")

    # A missing lock workflow is a DELETED protection, and must not read as an absent subject.
    missing_root = Path(__file__).resolve().parent / "__no_such_repo_root__"
    _, missing_error = read_lock_schedule(missing_root)
    if not missing_error:
        failures.append("SABOTAGE UNDETECTED: a missing lock-pinned-digests.yml read as fine")

    # ── The protection classifier, every branch, offline ────────────────────────────────────────
    # 🚨 This is the arm that cannot be exercised against the live registry without UNLOCKING a
    # manifest, which is a mutation of production retention state and is not something a test may
    # do. So the decision is a pure function and it is driven here instead.
    lock_at = dt.datetime(2026, 9, 8, 1, 0, tzinfo=dt.timezone.utc)
    before = dt.datetime(2026, 9, 7, 21, 28, tzinfo=dt.timezone.utc)   # Crm's real shape
    after = dt.datetime(2026, 9, 8, 10, 46, tzinfo=dt.timezone.utc)    # Plugins' real shape

    def expect_class(delete_enabled, moved_at, last, want, why, move_error=""):
        got, _ = classify_protection(delete_enabled, moved_at, last, move_error)
        if got != want:
            failures.append(f"classify_protection({delete_enabled}, {moved_at}) = {got}, "
                            f"expected {want} — {why}")

    expect_class(False, before, lock_at, "PROTECTED",
                 "deleteEnabled=false is the registry saying the purge will skip it")
    expect_class(False, after, lock_at, "PROTECTED",
                 "a locked manifest is locked whenever the pin moved")
    expect_class(True, before, lock_at, "UNPROTECTED",
                 "the lock ran after this pin was written and did not protect it — THE red")
    expect_class(True, after, lock_at, "PENDING-LOCK",
                 "the pin moved after the last scheduled lock, so no opportunity has passed")
    expect_class(True, None, lock_at, "INDETERMINATE",
                 "unlocked, and no commit date — never silently exempted")
    expect_class(True, before, None, "UNPROTECTED",
                 "🚨 a MISSING lock schedule must not exempt an unlocked pin")
    expect_class(None, before, lock_at, "INDETERMINATE",
                 "the registry did not report deleteEnabled — not a confirmation")
    # The one that would make the whole arm vacuous: an unlocked pin reading as protected.
    if classify_protection(True, before, lock_at)[0] == "PROTECTED":
        failures.append("SABOTAGE UNDETECTED: an UNLOCKED pinned manifest classified as PROTECTED")

    return failures


def self_test(repo_root: Path) -> int:
    pins, malformed = extract("Systemorph/Fixture", "ci.yml", SELF_TEST_YAML)
    found = {(p.where, p.digest, tuple(p.acr_repos)) for p in pins}
    failures: list[str] = []

    def want(where: str, digest: str, acr_repo: str) -> None:
        for w, d, repos in found:
            if w == where and d == digest and acr_repo in repos:
                return
        failures.append(f"MISSING pin {where} -> {acr_repo}@{digest[:19]}…")

    # Shape 1 with a `format(...)` binding — Education's portal pin.
    want("ci.yml:MW_PORTAL_DIGEST",
         "sha256:dab2a7b3a7e1523d7d728d6a9a397abef5f98b0e1e0ff8f02395a433bdedf97d",
         "memex-portal-ai")
    # Shape 1 with an `@${{ env.X }}` binding — Education's tester pin.
    want("ci.yml:MW_TEST_DIGEST",
         "sha256:df19f10afc1f807441b403eb0dfa4b8645d5187b830f77ad080def7fc65b391f",
         "mw-plugin-test")
    # Shape 2 — SocialMedia's and Crm's step output, invisible to any scan of `env:` blocks.
    want("ci.yml:image-digest",
         "sha256:d91d333f19f5b42d59fa76004b6904d5ff46a0d30952e6b854f9505e2c5cdd0d",
         "mw-plugin-test")
    # Shape 3 — a bare literal image reference.
    want("ci.yml:inline",
         "sha256:d81df6cc79ad68fcfd9fa3d4cf40588e97c7adfe8c2516e1626231c0412b1f3f",
         "memex-migration")

    # The vacuity guard fires on an ATTEMPTED digest that is not one — both the non-hex placeholder
    # (which a `sha256:[0-9a-f]+` matcher reads as "no pin here") and the too-short hex (which such
    # a matcher reads as a perfectly good pin).
    if not any(m.where == "ci.yml:MW_BROKEN_DIGEST" for m in malformed):
        failures.append("MISSING malformed: sha256:PLACEHOLDER read as an absence, not a defect")
    if not any(m.where == "ci.yml:MW_SHORT_DIGEST" for m in malformed):
        failures.append("MISSING malformed: a truncated digest accepted as well-formed")
    # …and in the $GITHUB_OUTPUT shape too. Covering only the `env:` shape exempted SocialMedia and
    # Crm — the two repositories that write their tester pin as a step output — from the guard
    # entirely, while this self-test reported it proven (MeshWeaver#3454, 2026-09-06).
    if not any(m.where == "ci.yml:bake-image-digest" for m in malformed):
        failures.append("MISSING malformed: a placeholder in a $GITHUB_OUTPUT read as an absence")

    # …and stays silent on the four things that legitimately are not pins. The last two are what
    # keep a NIGHTLY sweep from crying wolf: the text scan reads `run:` shell and nested mappings
    # too, where a shell variable and a key-with-a-block-under-it are entirely ordinary.
    if any("MW_PLATFORM_REF" in p.where for p in pins) or any(
        "MW_PLATFORM_REF" in m.where for m in malformed
    ):
        failures.append("FALSE POSITIVE: MW_PLATFORM_REF is a git sha, not an image digest")
    if any("MW_FORWARD_DIGEST" in m.where for m in malformed):
        failures.append("FALSE POSITIVE: a `${{ … }}` forward is neither a pin nor a defect")
    if any(m.value.startswith("$") for m in malformed):
        failures.append("FALSE POSITIVE: `digest: $SHELL_VARIABLE` inside a run: block")
    if any(m.value.startswith("$") or m.value == "" for m in malformed):
        failures.append("FALSE POSITIVE: a $GITHUB_OUTPUT carrying a resolved shell variable")
    if any(not m.value for m in malformed):
        failures.append("FALSE POSITIVE: a key whose value is the nested block beneath it")

    # The protection arms — the cron evaluator, the SHIPPED lock/purge ordering, and the sabotages
    # that prove the ordering check can go red. No credential, so a pull request that moves either
    # clock the wrong way is caught on that pull request.
    schedule_failures = self_test_schedule(repo_root)
    failures.extend(schedule_failures)

    # ── The INSTRUMENT control, which replaced the zero-pin assertion (2026-09-12) ─────────────
    # The old rule inferred the extractor's health from the fleet's behaviour and expired the day
    # #3842 moved the satellites off digest pins — reddening the LOCK lane two hours before the
    # purge. This arm proves the replacement both ways.
    if extractor_control():
        failures.append("CONTROL: the extractor control reports a problem against its own fixture, "
                        f"which declares well-formed pins: {extractor_control()}")
    original_extract = globals()["extract"]
    try:
        globals()["extract"] = lambda gh_repo, filename, text: ([], [])
        problems = extractor_control()
    finally:
        globals()["extract"] = original_extract
    if not any("STOPPED MATCHING" in problem for problem in problems):
        failures.append("CONTROL: the digest extractor was broken and the control did not notice — "
                        "which would leave a fleet-wide 'no pins found' reading as clean")
    if not any("vacuity arm" in problem for problem in problems):
        failures.append("CONTROL: the control's malformed half did not fire on an extractor that "
                        "reports nothing, so half of it is decoration")

    # 🚨 AND THE WIRING, WHICH IS THE HALF THAT STAYED GREEN UNDER FALSIFICATION. Every arm above
    # calls `extractor_control()` itself, so deleting the call from `run()` — or deleting the
    # branch that refuses to report a plain green over an empty subject — left this self-test
    # passing about code the nightly sweep no longer executes.
    import ast as _ast
    import inspect as _inspect

    run_source = _inspect.getsource(run)
    run_tree = _ast.parse(_textwrap.dedent(run_source))
    run_calls = {node.func.id for node in _ast.walk(run_tree)
                 if isinstance(node, _ast.Call) and isinstance(node.func, _ast.Name)}
    if "extractor_control" not in run_calls:
        failures.append("WIRING: run() no longer calls extractor_control(), so a broken extractor "
                        "would leave a fleet-wide 'no pins found' sweep reporting success")
    if "verdict_line" not in run_calls:
        failures.append("WIRING: run() no longer reports through verdict_line(), so the "
                        "empty-subject distinction below is unfalsified")
    if "NOTHING TO SWEEP" not in verdict_line(8, 0, 0, 0, 0, 0, 0):
        failures.append("VERDICT: a sweep that found NOTHING reported a plain green. "
                        "'All 0 declaration(s) resolve' reads like a sweep that checked something")
    if "NOTHING TO SWEEP" in verdict_line(8, 4, 4, 3, 2, 4, 0):
        failures.append("VERDICT: a sweep that DID find pins reported that it found none")

    for line in failures:
        print(f"::error::self-test: {line}")
    if failures:
        return 1
    print(f"self-test: {len(pins)} pin(s) extracted, {len(malformed)} malformed, no false positive; "
          "the lock is scheduled before every enabled purge and the ordering check goes red when "
          "it is not; and the digest extractor is controlled against a fixture on every run rather "
          "than inferred from what the fleet happens to declare.")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repos", help="comma-separated owner/name list to scan")
    parser.add_argument("--discover", action="store_true",
                        help="enumerate the fleet from the App installation this token belongs to")
    parser.add_argument("--registry", default=REGISTRY_DEFAULT)
    parser.add_argument("--self-test", action="store_true",
                        help="prove the extractor fires and stays silent; no network")
    # The lock workflow and the retention record are read from here — both are committed files in
    # THIS repository, so the default is this script's own repo root and not a guess.
    parser.add_argument("--repo-root", default=str(Path(__file__).resolve().parents[2]),
                        help="root of the MeshWeaver checkout holding the lock workflow and the "
                             "ACR retention record")
    args = parser.parse_args()

    repo_root = Path(args.repo_root).resolve()

    if args.self_test:
        return self_test(repo_root)
    if bool(args.repos) == bool(args.discover):
        parser.error("give exactly one of --repos or --discover")

    repos = discover_repos() if args.discover else [
        r.strip() for r in args.repos.split(",") if r.strip()
    ]
    print(f"scanning {len(repos)} repository(ies): {', '.join(repos)}")
    return run(repos, args.registry, repo_root)


if __name__ == "__main__":
    sys.exit(main())
