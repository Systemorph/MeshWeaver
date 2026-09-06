#!/usr/bin/env python3
"""check-pinned-digests.py — does every image digest the fleet PINS still exist in ACR?

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
It is the CHEAP half and it is deliberately not the fix: it does not stop the deletion. It converts
"an entire repository's CI is inexplicably dead, including its nightly" into one named red saying
which repo, which variable, and which digest is gone. That is worth having whichever retention
change lands, because it also catches the next variant.

The retention half — locking the pinned manifests so `acr purge` skips them, and collapsing the two
divergent tasks into one — is written up in Doc/Architecture/PinnedImageRetention.

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

Exit 0 only when every pin found resolves. Every failure is a `::error` annotation naming the
repository, the declaration and the digest.
"""

from __future__ import annotations

import argparse
import base64
import json
import os
import re
import subprocess
import sys
from dataclasses import dataclass, field

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
OUTPUT_RE = re.compile(
    r"(?P<name>[A-Za-z_][A-Za-z0-9_.-]*)=(?P<quote>['\"]?)(?P<value>sha256:[0-9a-f]{64})(?P=quote)"
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


def gh_api(path: str, paginate: bool = False) -> tuple[int, str, str]:
    cmd = ["gh", "api", "-H", "X-GitHub-Api-Version: 2022-11-28", path]
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
    rc, out, err = gh_api("/installation/repositories", paginate=True)
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
    names: list[str] = []
    for chunk in re.findall(r"\{.*?\n\}|\{.*\}", out, re.DOTALL) or [out]:
        try:
            doc = json.loads(chunk)
        except json.JSONDecodeError:
            continue
        for repo in doc.get("repositories") or []:
            full = repo.get("full_name")
            if full and full not in names:
                names.append(full)
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
            # No workflows directory at all. A measured zero, reported as such — not an error.
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
        name = match.group("name")
        if not PIN_NAME_RE.search(name):
            continue
        add(f"{filename}:{name}", match.group("value"), candidates(name))

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


def resolve(registry: str, acr_repo: str, digest: str, cache: dict) -> tuple[bool, str]:
    key = (acr_repo, digest)
    if key in cache:
        return cache[key]
    rc, _, err = az(
        ["acr", "repository", "show", "--name", registry, "--image", f"{acr_repo}@{digest}", "-o", "none"]
    )
    if rc == 0:
        result = (True, "")
    else:
        blob = err.lower()
        if any(marker in blob for marker in ABSENT_MARKERS):
            result = (False, "absent")
        else:
            # Neither present nor provably absent. Surfaced, never smoothed into "gone".
            result = (False, "INDETERMINATE: " + " ".join(err.split())[:300])
    cache[key] = result
    return result


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


def run(repos: list[str], registry: str) -> int:
    registry_control_probe(registry)

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
                ok, detail = resolve(registry, acr_repo, pin.digest, cache)
                if ok:
                    pin.verdict, pin.resolved_in = "RESOLVES", acr_repo
                    break
                if detail.startswith("INDETERMINATE"):
                    indeterminate = detail
            else:
                pin.verdict = "INDETERMINATE" if indeterminate else "GONE"
                pin.detail = indeterminate or "tried: " + ", ".join(pin.acr_repos)

    all_pins = [pin for scan in scans for pin in scan.pins]
    pinning = [scan for scan in scans if scan.pins]
    silent = [scan for scan in scans if not scan.pins and not scan.unreadable]
    unreadable = [scan for scan in scans if scan.unreadable]
    malformed = [bad for scan in scans for bad in scan.malformed]
    gone = [pin for pin in all_pins if pin.verdict == "GONE"]
    indeterminate = [pin for pin in all_pins if pin.verdict == "INDETERMINATE"]
    resolved = [pin for pin in all_pins if pin.verdict == "RESOLVES"]

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
    emit(f"    distinct pins found                   {len(all_pins)}")
    emit(f"    …that RESOLVE                         {len(resolved)}")
    emit(f"    …that are GONE                        {len(gone)}")
    emit(f"    …INDETERMINATE (registry not reached) {len(indeterminate)}")
    emit(f"    pin-shaped declarations MALFORMED     {len(malformed)}")
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
            emit(f"      {pin.verdict:14s} {target}@{pin.digest[:19]}…  {where}"
                 + (f"  [{pin.detail}]" if pin.detail else ""))
    emit("")

    failed = False

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

    # A sweep that found nothing has not proven the fleet is clean; it has proven the extractor is
    # broken (a workflow syntax the shapes above do not cover, or a token that reads no repository).
    if not all_pins and not unreadable:
        print("::error::not one digest pin was found anywhere in the fleet.")
        print("  Six repositories pinned at least one image on 2026-09-06, so zero means the")
        print("  extractor stopped matching, not that pinning stopped. Run --self-test.")
        failed = True

    if failed:
        return 1
    emit(f"All {len(resolved)} pinned digest(s) across {len(pinning)} repository(ies) resolve.")
    return 0


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
          echo "digest: $SOME_SHELL_VARIABLE"
      - env:
          BAKE_IMAGE: meshweaver.azurecr.io/mw-plugin-test@${{ env.MW_TEST_DIGEST }}
          MW_PORTAL_IMAGE: ${{ vars.MW_PORTAL_IMAGE || format('meshweaver.azurecr.io/memex-portal-ai@{0}', env.MW_PORTAL_DIGEST) }}
          MW_FORWARD_DIGEST: ${{ env.MW_TEST_DIGEST }}
          LITERAL: meshweaver.azurecr.io/memex-migration@sha256:d81df6cc79ad68fcfd9fa3d4cf40588e97c7adfe8c2516e1626231c0412b1f3f
        run: echo hi
"""


def self_test() -> int:
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
    if any(not m.value for m in malformed):
        failures.append("FALSE POSITIVE: a key whose value is the nested block beneath it")

    for line in failures:
        print(f"::error::self-test: {line}")
    if failures:
        return 1
    print(f"self-test: {len(pins)} pin(s) extracted, {len(malformed)} malformed, no false positive.")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repos", help="comma-separated owner/name list to scan")
    parser.add_argument("--discover", action="store_true",
                        help="enumerate the fleet from the App installation this token belongs to")
    parser.add_argument("--registry", default=REGISTRY_DEFAULT)
    parser.add_argument("--self-test", action="store_true",
                        help="prove the extractor fires and stays silent; no network")
    args = parser.parse_args()

    if args.self_test:
        return self_test()
    if bool(args.repos) == bool(args.discover):
        parser.error("give exactly one of --repos or --discover")

    repos = discover_repos() if args.discover else [
        r.strip() for r in args.repos.split(",") if r.strip()
    ]
    print(f"scanning {len(repos)} repository(ies): {', '.join(repos)}")
    return run(repos, args.registry)


if __name__ == "__main__":
    sys.exit(main())
