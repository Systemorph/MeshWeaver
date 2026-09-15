#!/usr/bin/env python3
"""lock-pinned-digests.py — protect every manifest the fleet PINS, by locking it in the registry.

(The name on this first line is load-bearing in the same way compile-check.py's,
check-pinned-digests.py's and check-pin-set-consistency.py's are: a lane that fetches this file at
the platform ref can refuse a body whose first 400 bytes do not name it.)

WHY THIS EXISTS (MeshWeaver#3438 requirement 1, 2026-09-07)
-----------------------------------------------------------
ACR's nightly `purge-old-images` task deleted the manifests MeshWeaver.Education's `main` pinned.
Every run of that repo's `Disposable-mesh gate` then died at `manifest unknown` before a single
content gate executed — INCLUDING `main`'s own nightly, so the repository lost its gate and its
green baseline in the same instant. Education, Manufacturing and SocialMedia went down together at
2026-09-05T15:29Z.

`check-pinned-digests.py` (#3462) is the GUARD half: it asks, after the fact, whether each pinned
digest still exists, and turns a mute fleet-wide wedge into one named red. It does not stop the
deletion. THIS is the other half: it stops the deletion, by deriving protection from the live pin
set instead of from a human remembering.

    `acr purge` SKIPS LOCKED MANIFESTS BY DEFAULT. Deleting one needs the explicit
    `--include-locked`, and neither step of `purge-old-images` passes it (verified against the live
    task definition, 2026-09-07 — the definition is recorded in .github/acr-retention/). So
    `deleteEnabled: false` on a manifest is a HARD protection against this exact configuration,
    not a hint.

Five manifests were locked BY HAND on 2026-09-06 to stop the bleeding. A hand list is the defect,
not the fix: it is only ever correct on the day it is written. This job re-derives the set nightly,
at 01:00 UTC — two hours before the 03:00 purge.

🚨 IT LOCKS. IT DOES NOT UNLOCK.
--------------------------------
Releasing a lock is the only direction that can DESTROY data, and it is unsafe in exactly the
situation where the extractor is wrong: a pin this scan cannot see is spelled identically to a pin
that is not there, so "nothing pins this any more" and "I failed to read the thing that pins it"
produce the same answer. The release arm is therefore:

  * always COMPUTED and PRINTED, so the stale-lock debt is visible and cannot grow silently;
  * never acted on unless `MW_ACR_RELEASE_UNPINNED=true` (or `--release-unpinned`) is set by a
    person, AND the run is free of every blocker below.

The asymmetry is deliberate and it is the whole safety argument: a stale lock costs one manifest's
unshared layers; a wrong release costs an outage. Memex#122 — a purged pinned tag — served the
public brand site 503 for ~11 hours and nothing alerted.

LOCKS ARE APPLIED ON A DEGRADED RUN; RELEASES ARE NOT. A lock cannot destroy anything, so when the
sweep is partial the safe move is to protect what WAS found and still fail red. A release on a
partial sweep is precisely the outage. Both facts are printed.

WHY `--delete-enabled false` AND NOT ALSO `--write-enabled false`
-----------------------------------------------------------------
`deleteEnabled: false` is exactly and only what `acr purge` skips on. `writeEnabled: false`
additionally refuses a manifest PUT for that digest — and the release pipeline PROMOTES by
retagging in ACR (`release.yml`: `docker buildx imagetools create --tag $ACR/$repo:$VERSION
$ACR/$repo:$SHORT`), which is a manifest PUT of an already-present digest under a new tag. So a
write-lock buys NO extra purge protection and may cost a promotion. (Reasoned from the mechanism,
not measured: confirming it needs an actual retag against a write-locked manifest.) This job
therefore locks DELETE only — the strictly smaller grant for the strictly sufficient effect — and it
never relaxes an attribute somebody else set: a manifest already carrying `writeEnabled: false` is
reported and left exactly as it is.

THE PIN SET IS THE UNION OF TWO AXES, AND ONE OF THEM IS INVISIBLE TO THE OTHER
-------------------------------------------------------------------------------
  AXIS 1  digests pinned in the satellites' `.github/workflows` — what #3462 sweeps.
  AXIS 2  image TAGS pinned in the deployment overlays (Memex#141). `check-pinned-digests.py`
          reads `.github/workflows` and enumerates NONE of these, so a lock set built from axis 1
          alone leaves every overlay-pinned manifest unprotected. Memex#122's victim was pinned
          exactly that way.

Measured 2026-09-07: axis 2 is `meshweaver.azurecr.io/memex-portal-ai:3.0.0-ci.7926` and its
migration twin (memex + memex-cloud), `memex-portal-next:3.0.0-next.43`, `hosting-operator:1.0.0`,
the pearl overlay's `3.0.0-rc9.ci.7601` pair, and core's `whisper-swiss-german:1.7.4`.

🚨 NO SECOND EXTRACTOR FOR AXIS 1. This script IMPORTS `check-pin-set-consistency.py` and uses its
`extract()`, so there is exactly one definition in this repository of what a digest pin IS.
`check-pin-set-consistency.py` and `check-pinned-digests.py` already share their shapes on purpose;
the self-test here asserts that the two AGREE on one fixture, so a future edit that diverges them
reddens rather than silently splitting the fleet's idea of a pin in two.

Axis 2 is a different subject with a different shape (a TAG, in a helm overlay, not a digest in a
workflow) and it necessarily has its own reader. Its scope is deliberately narrow, and the boundary
is measured rather than guessed: `values*.yml` / `values*.yaml` under a `deploy/` or `deployments/`
path segment. Widening it to every YAML/JSON under `deploy/` was tried and rejected — core's
`deploy/aks/operator/test/fixtures/**` carry image references to tags that never existed
(`memex-portal-ai:3.0.0-rc8.ci.5000`), which would red this job nightly over test fixtures, and
`deploy/aks/manifests/observability/log-watcher.yaml` names the FLOATING tag `:latest`, which has
no fixed manifest to protect.

WHAT MAKES IT RED (fail closed — an unswept repository must never read as a swept one)
---------------------------------------------------------------------------------------
  * a repository whose workflows or whose git tree could not be read;
  * a truncated git tree (GitHub's `truncated: true`) — an unknown number of unread files;
  * a pin-shaped declaration whose value is not a digest (I4 — placeholder vacuity);
  * a digest whose ACR repository cannot be determined (I5 — an unclassified pin has NOT been
    checked, and for LOCKING it is worse than unchecked: there is no manifest to lock);
  * an overlay tag that does not resolve, or a pinned digest that is already gone;
  * an INDETERMINATE registry answer — az failing for any reason other than the registry's own
    `manifest unknown`;
  * ZERO pins on either axis. Both were non-zero when this was written, so a zero means the
    extractor stopped matching, not that pinning stopped;
  * a lock that was requested and did not take.

USAGE
-----
  lock-pinned-digests.py --discover                  report what it WOULD lock (no mutation)
  lock-pinned-digests.py --discover --apply          …and actually lock it
  lock-pinned-digests.py --repos Systemorph/A,…      scan exactly these repositories
  lock-pinned-digests.py --check-retention-record .  assert the committed retention record still
                                                     describes a purge a lock can protect against
  lock-pinned-digests.py --self-test                 prove both arms fire, offline

Exit 0 only when every pin found is protected and nothing was blocked.
"""

from __future__ import annotations

import argparse
import base64
import contextlib
import importlib.util
import io
import json
import os
import shlex
import re
import sys
import tempfile
import textwrap
import urllib.request
from dataclasses import dataclass, field
from pathlib import Path

REGISTRY_DEFAULT = "meshweaver"
HERE = Path(__file__).resolve().parent


# ── The ONE definition of a digest pin, imported rather than rewritten ──────────────────────────
#
# 🚨 A third extractor would break the property `check-pin-set-consistency.py` was built with —
# "the two scripts deliberately share their extraction SHAPES … so they can never disagree about
# what a pin IS". Importing is how that stays true by construction rather than by review.


def _load(name: str, filename: str):
    spec = importlib.util.spec_from_file_location(name, HERE / filename)
    if spec is None or spec.loader is None:      # pragma: no cover - packaging accident
        raise ImportError(f"cannot load {filename} from {HERE}")
    module = importlib.util.module_from_spec(spec)
    # 🚨 REGISTER BEFORE EXECUTING. `@dataclass` resolves its own annotations through
    # `sys.modules[cls.__module__]`, so a module executed while absent from `sys.modules` dies on
    # the first dataclass with `AttributeError: 'NoneType' object has no attribute '__dict__'`.
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


consistency = _load("mw_pin_set_consistency", "check-pin-set-consistency.py")
existence = _load("mw_pinned_digests", "check-pinned-digests.py")


# ── Axis 2: an image TAG pinned in a deployment overlay ────────────────────────────────────────

# Shape A — the whole reference on one line: `image: "meshweaver.azurecr.io/memex-portal-ai:3.0.0"`.
OVERLAY_INLINE_RE = re.compile(
    consistency.REGISTRY_HOST_RE
    + r"/(?P<repo>[A-Za-z0-9][A-Za-z0-9._/-]*):(?P<tag>[A-Za-z0-9_][A-Za-z0-9._-]*)"
)

# Shape B — helm's split convention, `repository:` on one line and `tag:` in the same mapping:
#
#     image:
#       repository: meshweaver.azurecr.io/whisper-swiss-german
#       tag: "1.7.4"
#
# It is not hypothetical and it is not rare: it is how BOTH this repository's `deploy/whisper/helm`
# and Memex's `deployments/aks/memex-cloud/whisper/helm` write their pin. A reader that saw only
# shape A would report those overlays as pinning NOTHING — the exact "not looked at, spelled the
# same as clean" confusion this whole family of gates exists to remove.
OVERLAY_REPOSITORY_RE = re.compile(
    r"^(?P<indent>[ \t]*)repository[ \t]*:[ \t]*(?P<quote>['\"]?)"
    + consistency.REGISTRY_HOST_RE
    + r"/(?P<repo>[A-Za-z0-9][A-Za-z0-9._/-]*?)(?P=quote)[ \t]*(?:#[^\r\n]*)?$"
)
OVERLAY_TAG_RE = re.compile(
    r"^(?P<indent>[ \t]*)tag[ \t]*:[ \t]*(?P<quote>['\"]?)"
    r"(?P<tag>[A-Za-z0-9_][A-Za-z0-9._-]*)(?P=quote)[ \t]*(?:#[^\r\n]*)?$"
)

# 🚨 SHAPE C — AN IMAGE IN A REGISTRY THIS LANE DOES NOT PROTECT.
#
# `REGISTRY_HOST_RE` matches `*.azurecr.io` and nothing else, which is correct — this lane locks
# manifests in ONE ACR. But an overlay that pins its images somewhere else then extracts as pinning
# NOTHING, and AXIS 3 said so in as many words: *"installation `build` runs core c84c6c0 and its
# overlay pins no image at all"*. That sentence was FALSE on 2026-09-13 — the overlay pins
# `cr.meshweaver.cloud/memex-portal-ai:3.0.0-ci.8411` and its migration twin — and the fleet's own
# registry is where new installations are provisioned (`pearl` pins there too). An instrument that
# answers "pins nothing" about an overlay with two pins in it is the confidently-wrong shape this
# whole family of gates exists to remove, and it reads as a broken extractor rather than as an
# installation outside this registry.
#
# So foreign references are EXTRACTED and NAMED. They are never locked — nothing here can write to
# another registry — but "this installation's images are in <host>, which this run does not
# protect" and "this overlay pins nothing" are different facts with different fixes.
FOREIGN_INLINE_RE = re.compile(
    r"(?<![A-Za-z0-9._/-])(?P<host>[a-z0-9][a-z0-9-]*(?:\.[a-z0-9][a-z0-9-]*)+)"
    r"/(?P<repo>[A-Za-z0-9][A-Za-z0-9._/-]*):(?P<tag>[A-Za-z0-9_][A-Za-z0-9._-]*)"
)

# Shape B for a foreign registry — helm's split `repository:` + sibling `tag:`, the same convention
# the ACR path already handles. A foreign-only overlay written that way would otherwise extract as
# NOTHING and fall straight back into `pins no image at all`: this bug, in its second shape.
FOREIGN_REPOSITORY_RE = re.compile(
    r"^(?P<indent>[ \t]*)repository[ \t]*:[ \t]*(?P<quote>['\"]?)"
    r"(?P<host>[a-z0-9][a-z0-9-]*(?:\.[a-z0-9][a-z0-9-]*)+)"
    r"/(?P<repo>[A-Za-z0-9][A-Za-z0-9._/-]*?)(?P=quote)[ \t]*(?:#[^\r\n]*)?$"
)

# 🚨 A COMMENT IS PROSE, NOT A PIN — and this issue already paid for the lesson once: eleven of the
# thirty-one `sha256:` tokens in the 2026-09-08 hand count were comments, several of them narrating
# THIS incident inside the very files it broke, so a counter that does not exclude them over-reports
# by more than a third. Measured 2026-09-13 on the fleet's twelve overlays: of two `ghcr.io`
# references, ONE is a line of prose in `ci-runners` explaining what the runner image is built from.
# With an undeclared registry now a BLOCKER, a match inside a comment would red the lane over a
# sentence.
COMMENT_LINE_RE = re.compile(r"^[ \t]*#")


def without_comments(text: str) -> str:
    """The overlay with whole-line comments removed. Trailing `# …` is left alone — the shapes above
    already tolerate it, and cutting at a `#` would truncate a value that legitimately contains one."""
    return "\n".join("" if COMMENT_LINE_RE.match(line) else line for line in text.splitlines())

# A `${{ … }}` / `{{ … }}` value is a template, not a pin.
TEMPLATED_RE = re.compile(r"\{\{")

# A tag that MOVES has no fixed manifest to protect, and locking whatever it happens to point at
# today would pin the wrong bytes forever. These are reported as "floating", never locked.
FLOATING_TAGS = {"latest", "main", "master", "edge", "stable", "nightly"}

# Where a deployment overlay lives. Narrow ON PURPOSE — see the module docstring: widening this to
# every YAML/JSON under `deploy/` drags in test fixtures whose tags never existed.
OVERLAY_DIR_SEGMENTS = ("deploy", "deployments")
OVERLAY_FILE_RE = re.compile(r"^values[A-Za-z0-9._-]*\.ya?ml$")


def is_overlay_path(path: str) -> bool:
    parts = path.split("/")
    if not any(segment in OVERLAY_DIR_SEGMENTS for segment in parts[:-1]):
        return False
    return bool(OVERLAY_FILE_RE.match(parts[-1]))


def extract_foreign_pins(text: str, registry: str) -> list[tuple[str, str, str]]:
    """(registry host, repo, tag) for every image reference in a registry this lane does NOT lock.

    Deliberately a SUPERSET minus THIS registry's own matches, rather than a list of known hosts: a
    host nobody thought of must show up as foreign, never as nothing.

    🚨 `registry` IS THE ONE THIS RUN LOCKS, NOT "ANY ACR". `--registry meshweaver` locks
    `meshweaver.azurecr.io` and nothing else, so a pin in a DIFFERENT `*.azurecr.io` is foreign too
    — treating every ACR as in-scope would extract its repository and then look it up in the wrong
    registry, which is this bug reintroduced one registry along."""
    mine = f"{registry}.azurecr.io"
    body = without_comments(text)
    foreign: list[tuple[str, str, str]] = []

    def add(host: str, repo: str, tag: str, matched: str) -> None:
        if host == mine or TEMPLATED_RE.search(matched):
            return
        triple = (host, repo, tag)
        if triple not in foreign:
            foreign.append(triple)

    for match in FOREIGN_INLINE_RE.finditer(body):
        add(match.group("host"), match.group("repo"), match.group("tag"), match.group(0))

    lines = body.splitlines()
    for index, line in enumerate(lines):
        repo_match = FOREIGN_REPOSITORY_RE.match(line)
        if not repo_match or TEMPLATED_RE.search(line):
            continue
        indent = len(repo_match.group("indent").replace("\t", "    "))
        for follower in lines[index + 1:]:
            if not follower.strip():
                continue
            follower_indent = len(follower[: len(follower) - len(follower.lstrip())]
                                  .replace("\t", "    "))
            if follower_indent < indent:
                break
            if follower_indent > indent:
                continue
            tag_match = OVERLAY_TAG_RE.match(follower)
            if tag_match and not TEMPLATED_RE.search(follower):
                add(repo_match.group("host"), repo_match.group("repo"), tag_match.group("tag"),
                    line + follower)
            break
    return foreign


def extract_overlay_pins(text: str, registry: str = "") -> tuple[list[tuple[str, str]],
                                                                 list[tuple[str, str]]]:
    """(pins, floating) as (acr_repo, tag) pairs, from one overlay file's text.

    🚨 `registry`, when given, is the ONE this run locks: a `*.azurecr.io` host that is not it is
    NOT this lane's repository and is left to `extract_foreign_pins`. Empty keeps the historical
    any-ACR behaviour for callers that have no registry to compare against."""
    pins: list[tuple[str, str]] = []
    floating: list[tuple[str, str]] = []
    mine = f"{registry}.azurecr.io" if registry else ""
    text = without_comments(text)

    def add(repo: str, tag: str) -> None:
        pair = (repo, tag)
        if tag.lower() in FLOATING_TAGS:
            if pair not in floating:
                floating.append(pair)
        elif pair not in pins:
            pins.append(pair)

    for match in OVERLAY_INLINE_RE.finditer(text):
        if mine and match.group("host") + ".azurecr.io" != mine:
            continue
        add(match.group("repo"), match.group("tag"))

    lines = text.splitlines()
    for index, line in enumerate(lines):
        repo_match = OVERLAY_REPOSITORY_RE.match(line)
        if not repo_match or TEMPLATED_RE.search(line):
            continue
        if mine and repo_match.group("host") + ".azurecr.io" != mine:
            continue
        indent = len(repo_match.group("indent").replace("\t", "    "))
        # The sibling `tag:` is in the SAME mapping block: same indent, before the block ends.
        for follower in lines[index + 1:]:
            if not follower.strip() or follower.lstrip().startswith("#"):
                continue
            follower_indent = len(follower[: len(follower) - len(follower.lstrip())]
                                  .replace("\t", "    "))
            if follower_indent < indent:
                break
            if follower_indent > indent:
                continue
            tag_match = OVERLAY_TAG_RE.match(follower)
            if tag_match and not TEMPLATED_RE.search(follower):
                add(repo_match.group("repo"), tag_match.group("tag"))
            break
    return pins, floating


# ── AXIS 3: the installations the overlays declare ─────────────────────────────────────────────
#
# An overlay does not only pin images; it NAMES the installation it configures. Both keys are
# ordinary scalars in every overlay in the fleet (measured 2026-09-12 across Memex's three):
#
#     config:
#       memex_portal:
#         Hosting__Deployment: "memex-cloud"     ← the id, equal to its Hosting/Deployment record's
#     ingress:
#       host: "memex.meshweaver.cloud"           ← where to ask it what it is running
#
# That pair is what turns a committed INTENT into a live MEASUREMENT, and the difference between
# the two is the whole of this axis: the overlay says what the instance should run, `/api/version`
# says what it does. Memex#219's third shape is exactly the gap — memex-cloud was rolled to
# `3.0.0-ci.8399` at 06:15Z on 2026-09-12 while its committed pin still read `8372`, so the nightly
# lock protected the manifest it was NOT running.
INSTANCE_ID_RE = re.compile(
    r"^[ \t]*Hosting__Deployment[ \t]*:[ \t]*(?P<quote>['\"]?)"
    r"(?P<id>[A-Za-z0-9][A-Za-z0-9._-]*)(?P=quote)[ \t]*(?:#[^\r\n]*)?$",
    re.MULTILINE,
)
INGRESS_HOST_RE = re.compile(
    r"^(?P<indent>[ \t]+)host[ \t]*:[ \t]*(?P<quote>['\"]?)"
    r"(?P<host>[A-Za-z0-9][A-Za-z0-9.-]*\.[A-Za-z]{2,})(?P=quote)[ \t]*(?:#[^\r\n]*)?$"
)


def extract_overlay_instances(text: str) -> list[tuple[str, str | None]]:
    """(deployment id, ingress host) for every installation one overlay file declares."""
    ids = [match.group("id") for match in INSTANCE_ID_RE.finditer(text)
           if not TEMPLATED_RE.search(match.group(0))]
    if not ids:
        return []
    host: str | None = None
    in_ingress = False
    for line in text.splitlines():
        stripped = line.strip()
        if not stripped or stripped.startswith("#"):
            continue
        if not line[:1].isspace():
            # A top-level key: we are inside `ingress:` only while it is the one we just saw.
            in_ingress = stripped.startswith("ingress:")
            continue
        if in_ingress and host is None:
            match = INGRESS_HOST_RE.match(line)
            if match and not TEMPLATED_RE.search(line):
                host = match.group("host")
    # One overlay configures ONE installation; a second id in the same file would make the host
    # ambiguous, so it is reported without one and the run refuses to guess.
    return [(ids[0], host)] if len(ids) == 1 else [(i, None) for i in ids]


@dataclass
class OverlayScan:
    gh_repo: str
    files: int = 0
    pins: list[tuple[str, str, str]] = field(default_factory=list)      # (repo, tag, where)
    floating: list[tuple[str, str, str]] = field(default_factory=list)
    # (deployment id, ingress host, where) — one per overlay that names an installation.
    instances: list[tuple[str, str | None, str]] = field(default_factory=list)
    # (registry host, repo, tag, where) — images pinned in a registry this lane does NOT lock.
    # Never locked; carried so "pins nothing" and "pins elsewhere" stay different answers.
    foreign: list[tuple[str, str, str, str]] = field(default_factory=list)
    unreadable: str | None = None


def scan_overlays_remote(gh_repo: str, registry: str) -> OverlayScan:
    scan = OverlayScan(gh_repo=gh_repo)
    rc, out, err = consistency.gh_api(f"repos/{gh_repo}/git/trees/HEAD?recursive=1")
    if rc != 0:
        blob = (err + out).lower()
        if "404" in blob or "not found" in blob or "empty" in blob:
            # 🚨 SAME TWO-CAUSES-ONE-STATUS TRAP as `.github/workflows` (#3462): a 404 here means
            # either an empty repository — a MEASURED ZERO — or a repository this token cannot
            # read, which means NOBODY LOOKED. Ask about the repository before believing it.
            rc_repo, _, err_repo = consistency.gh_api(f"repos/{gh_repo}")
            if rc_repo != 0:
                scan.unreadable = (
                    "the repository itself could not be read, so its overlays were never looked "
                    f"for. gh said: {err_repo.strip()[:200]}"
                )
            return scan
        scan.unreadable = err.strip()[:300] or "unknown error reading the git tree"
        return scan
    try:
        tree = json.loads(out)
    except json.JSONDecodeError as exc:
        scan.unreadable = f"the git tree is not JSON: {exc}"
        return scan
    if tree.get("truncated"):
        # An unknown number of paths were not returned, so an absence here proves nothing.
        scan.unreadable = ("GitHub truncated the recursive tree listing, so an unknown number of "
                           "paths were never seen. An absence read off a truncated listing is not "
                           "a measured zero.")
        return scan
    paths = [entry["path"] for entry in tree.get("tree", [])
             if entry.get("type") == "blob" and is_overlay_path(entry.get("path", ""))]
    for path in sorted(paths):
        rc, body, err = consistency.gh_api(f"repos/{gh_repo}/contents/{path}")
        if rc != 0:
            scan.unreadable = f"could not read {path}: {err.strip()[:200]}"
            return scan
        try:
            doc = json.loads(body)
            text = base64.b64decode(doc["content"]).decode("utf-8", "replace")
        except (json.JSONDecodeError, KeyError, ValueError) as exc:
            scan.unreadable = f"could not decode {path}: {exc}"
            return scan
        scan.files += 1
        pins, floating = extract_overlay_pins(text, registry)
        scan.pins.extend((repo, tag, path) for repo, tag in pins)
        scan.floating.extend((repo, tag, path) for repo, tag in floating)
        scan.instances.extend((ident, host, path)
                              for ident, host in extract_overlay_instances(text))
        scan.foreign.extend((host, repo, tag, path)
                            for host, repo, tag in extract_foreign_pins(text, registry))
    return scan


def scan_overlays_local(root: str, gh_repo: str, registry: str) -> OverlayScan:
    scan = OverlayScan(gh_repo=gh_repo)
    base = Path(root)
    if not base.is_dir():
        scan.unreadable = f"{root} is not a directory, so its overlays were never looked for."
        return scan
    for path in sorted(base.rglob("values*.y*ml")):
        rel = path.relative_to(base).as_posix()
        if not is_overlay_path(rel):
            continue
        scan.files += 1
        text = path.read_text(encoding="utf-8", errors="replace")
        pins, floating = extract_overlay_pins(text, registry)
        scan.pins.extend((repo, tag, rel) for repo, tag in pins)
        scan.floating.extend((repo, tag, rel) for repo, tag in floating)
        scan.instances.extend((ident, host, rel) for ident, host in extract_overlay_instances(text))
        scan.foreign.extend((host, repo, tag, rel)
                            for host, repo, tag in extract_foreign_pins(text, registry))
    return scan


# ── The registry, behind one seam so the self-test can drive it offline ─────────────────────────


class Registry:
    """Every registry interaction this job makes. Read methods first, then the one write."""

    def __init__(self, name: str) -> None:
        self.name = name
        self._tag_cache: dict[tuple[str, str], tuple[str | None, str]] = {}
        self._tag_detail_cache: dict[str, tuple[list["Tag"] | None, str]] = {}

    # -- reads ----------------------------------------------------------------------------------

    def control_probe(self) -> None:
        consistency.registry_control_probe(self.name)

    def manifests(self, acr_repo: str) -> tuple[list["Manifest"] | None, str]:
        rc, out, err = consistency.az(
            ["acr", "manifest", "list-metadata", "--registry", self.name,
             "--name", acr_repo, "-o", "json"]
        )
        if rc != 0:
            return None, " ".join(err.split())[:300]
        try:
            raw_list = json.loads(out)
        except json.JSONDecodeError as exc:
            return None, f"list-metadata did not return JSON: {exc}"
        parsed: list[Manifest] = []
        for raw in raw_list:
            # ACR nests the lock flags under `changeableAttributes`; some API versions also carry
            # them flat. Read both, and default to UNLOCKED — the safe direction, because it makes
            # the job try to lock rather than assume protection it has not seen.
            attributes = raw.get("changeableAttributes") or {}
            parsed.append(Manifest(
                acr_repo=acr_repo,
                digest=raw.get("digest", ""),
                media_type=raw.get("mediaType", "") or "",
                delete_enabled=attributes.get("deleteEnabled", raw.get("deleteEnabled", True)),
                write_enabled=attributes.get("writeEnabled", raw.get("writeEnabled", True)),
                tags=raw.get("tags") or [],
            ))
        return parsed, ""

    def repositories(self) -> tuple[list[str] | None, str]:
        rc, out, err = consistency.az(["acr", "repository", "list", "--name", self.name, "-o", "json"])
        if rc != 0:
            return None, " ".join(err.split())[:300]
        try:
            return json.loads(out), ""
        except json.JSONDecodeError as exc:
            return None, f"repository list did not return JSON: {exc}"

    def resolve_tag(self, acr_repo: str, tag: str) -> tuple[str | None, str]:
        """A tag's digest. (None, 'absent') when the registry says so; (None, 'INDETERMINATE: …')
        for every other failure — a credential or a network fault is NEVER evidence of absence."""
        key = (acr_repo, tag)
        if key in self._tag_cache:
            return self._tag_cache[key]
        rc, out, err = consistency.az(
            ["acr", "repository", "show", "--name", self.name,
             "--image", f"{acr_repo}:{tag}", "--query", "digest", "-o", "tsv"]
        )
        if rc == 0 and out.strip().startswith("sha256:"):
            result: tuple[str | None, str] = (out.strip(), "")
        elif rc == 0:
            result = (None, f"INDETERMINATE: az answered 0 but no digest: {out.strip()[:120]!r}")
        else:
            blob = err.lower()
            if any(marker in blob for marker in consistency.ABSENT_MARKERS):
                result = (None, "absent")
            else:
                result = (None, "INDETERMINATE: " + " ".join(err.split())[:300])
        self._tag_cache[key] = result
        return result

    def index_children(self, acr_repo: str, digest: str) -> tuple[list[str] | None, str]:
        """The platform manifests one image index references. `None` is INDETERMINATE."""
        rc, out, err = consistency.az(
            ["acr", "manifest", "show", "--registry", self.name,
             "--name", f"{acr_repo}@{digest}", "-o", "json"]
        )
        if rc != 0:
            return None, " ".join(err.split())[:300]
        try:
            document = json.loads(out)
        except json.JSONDecodeError as exc:
            return None, f"manifest show did not return JSON: {exc}"
        children = [child.get("digest", "") for child in document.get("manifests") or []]
        if any(not re.fullmatch(r"sha256:[0-9a-f]{64}", child) for child in children):
            return None, "the index lists a child with no usable digest"
        return children, ""

    def read_delete_enabled(self, acr_repo: str, digest: str) -> tuple[bool | None, str]:
        """The manifest's CURRENT `deleteEnabled`, read back. `None` is INDETERMINATE — the
        registry did not answer, which is never evidence that a lock took.

        🚨 This exists because the only thing this whole job does is set that one attribute, and
        until #3438's follow-up the job believed `az`'s EXIT CODE that it had. An exit code is a
        statement about the request, not about the manifest: a `-o none` write that returns 0
        without the attribute changing (a subscription-level policy, an API version whose
        `--delete-enabled` is inert, a proxy that accepted and dropped it) would leave the job
        reporting `Protected N manifest(s)` over manifests the 03:00 purge can still delete. That is
        AGENTS.md's "a verification step that cannot fail is not a verification step", applied to
        the job's own postcondition."""
        rc, out, err = consistency.az(
            ["acr", "repository", "show", "--name", self.name,
             "--image", f"{acr_repo}@{digest}",
             "--query", "changeableAttributes.deleteEnabled", "-o", "tsv"]
        )
        if rc != 0:
            return None, "INDETERMINATE: " + " ".join(err.split())[:300]
        answer = out.strip().lower()
        if answer in ("true", "false"):
            return answer == "true", ""
        return None, f"INDETERMINATE: az answered 0 but not a boolean: {out.strip()[:120]!r}"

    def tags(self, acr_repo: str) -> tuple[list["Tag"] | None, str]:
        """Every TAG of one repository, with its OWN lock attributes.

        🚨 A TAG HAS ITS OWN `deleteEnabled`, AND IT IS THE ONE THE PURGE READS WHEN IT DELETES A
        TAG. Measured 2026-09-12 on meshweaver.azurecr.io: `memex-portal-ai:3.0.0-ci.8372` — the tag
        both production overlays pin — read `deleteEnabled: true` while the manifest it resolves to
        read `false`. Azure/acr-cli decides tag deletion on the TAG:

            if includeLocked || (*(*tag.ChangeableAttributes).DeleteEnabled &&
                                 *(*tag.ChangeableAttributes).WriteEnabled) { … eligible … }

        So a manifest lock saves the BYTES and not the REFERENCE: the purge deletes the tag, the
        manifest survives locked and untagged, and `…/memex-portal-ai:3.0.0-ci.8372` answers
        `manifest unknown` to the next pull — the same wedge, from a fully protected manifest. The
        same registry read says 1,396 tags in that repository and ZERO of them locked."""
        if acr_repo in self._tag_detail_cache:
            return self._tag_detail_cache[acr_repo]
        rc, out, err = consistency.az(
            ["acr", "repository", "show-tags", "--name", self.name,
             "--repository", acr_repo, "--detail", "-o", "json"]
        )
        if rc != 0:
            result: tuple[list[Tag] | None, str] = (None, " ".join(err.split())[:300])
        else:
            try:
                raw_list = json.loads(out)
            except json.JSONDecodeError as exc:
                result = (None, f"show-tags did not return JSON: {exc}")
            else:
                result = ([Tag(acr_repo=acr_repo,
                               name=raw.get("name", ""),
                               digest=raw.get("digest", ""),
                               # UNLOCKED is the safe default: it makes the job try to lock rather
                               # than assume a protection it has not read.
                               delete_enabled=(raw.get("changeableAttributes") or {}).get(
                                   "deleteEnabled", True))
                           for raw in raw_list], "")
        self._tag_detail_cache[acr_repo] = result
        return result

    def read_tag_delete_enabled(self, acr_repo: str, tag: str) -> tuple[bool | None, str]:
        """The TAG's current `deleteEnabled`, read back after a write. `None` is INDETERMINATE."""
        rc, out, err = consistency.az(
            ["acr", "repository", "show", "--name", self.name,
             "--image", f"{acr_repo}:{tag}",
             "--query", "changeableAttributes.deleteEnabled", "-o", "tsv"]
        )
        if rc != 0:
            return None, "INDETERMINATE: " + " ".join(err.split())[:300]
        answer = out.strip().lower()
        if answer in ("true", "false"):
            return answer == "true", ""
        return None, f"INDETERMINATE: az answered 0 but not a boolean: {out.strip()[:120]!r}"

    # -- the two writes -------------------------------------------------------------------------

    def set_tag_delete_enabled(self, acr_repo: str, tag: str, enabled: bool) -> tuple[bool, str]:
        """Lock (or release) the TAG itself. Delete only — never `--write-enabled`, which would
        refuse the retag `release.yml` promotes with and buys no purge protection (acr-cli requires
        BOTH attributes true to delete a tag, so `deleteEnabled: false` alone already refuses it)."""
        rc, _, err = consistency.az(
            ["acr", "repository", "update", "--name", self.name,
             "--image", f"{acr_repo}:{tag}",
             "--delete-enabled", "true" if enabled else "false",
             "-o", "none"]
        )
        return (rc == 0, " ".join(err.split())[:300])

    def set_delete_enabled(self, acr_repo: str, digest: str, enabled: bool) -> tuple[bool, str]:
        rc, _, err = consistency.az(
            ["acr", "repository", "update", "--name", self.name,
             "--image", f"{acr_repo}@{digest}",
             "--delete-enabled", "true" if enabled else "false",
             "-o", "none"]
        )
        return (rc == 0, " ".join(err.split())[:300])


@dataclass
class Manifest:
    acr_repo: str
    digest: str
    delete_enabled: bool
    write_enabled: bool
    tags: list[str]
    media_type: str = ""


@dataclass
class Tag:
    """One TAG of one repository, carrying its OWN lock attribute — see `Registry.tags`."""

    acr_repo: str
    name: str
    digest: str
    delete_enabled: bool


@dataclass
class WantedTag:
    """One tag REFERENCE the fleet depends on resolving, and every place that depends on it.

    A digest pin needs only the manifest. A TAG pin — every deployment overlay, and every image an
    instance is running — needs the tag to keep resolving, which is a separate attribute on a
    separate object."""

    acr_repo: str
    tag: str
    sources: list[str] = field(default_factory=list)
    problem: str = ""


@dataclass
class Instance:
    """One installation the fleet is expected to have, and what it turned out to be running."""

    id: str
    host: str | None = None
    source: str = ""
    state: str = "live"          # live | not-installed | retired
    reason: str = ""             # why it is not live, from the roster
    commit: str | None = None    # what /api/version answered
    version: str | None = None
    error: str = ""              # why it could not be asked, or could not be believed
    manifests: list[str] = field(default_factory=list)   # the closure this instance pins alive
    # Declared out of this lane's scope: live, answering, and served by a fleet-unlockable registry.
    out_of_scope: bool = False
    # Registry hosts THIS installation's overlay pins images in, other than the ACR this lane locks.
    foreign_registries: list[str] = field(default_factory=list)
    # The same pins as (host, repository) — because a HOST is the wrong unit for `ghcr.io`, which
    # serves `systemorph/*` (ours, published by our own CD) beside `distribution/*` (the registry
    # service's own image, which cannot come from the registry it boots). #4323.
    foreign_references: list[tuple[str, str]] = field(default_factory=list)
    # Those of them declared `fleet-unlockable` — the ones that make it out of scope.
    unlockable_registries: list[str] = field(default_factory=list)


@dataclass
class Wanted:
    """One manifest the fleet pins, and every place that pins it."""

    acr_repo: str
    digest: str | None                 # None while an axis-2 tag is unresolved
    sources: list[str] = field(default_factory=list)
    tag: str | None = None             # axis 2 only
    problem: str = ""                  # non-empty ⇒ a blocker, and nothing to lock


@dataclass
class Plan:
    wanted: list[Wanted] = field(default_factory=list)
    already_protected: list[Wanted] = field(default_factory=list)
    to_lock: list[Wanted] = field(default_factory=list)
    release_candidates: list[Manifest] = field(default_factory=list)
    blockers: list[str] = field(default_factory=list)
    locked_now: int = 0
    released_now: int = 0
    # The denominator, carried rather than recomputed, so the report and the verdict read the
    # same numbers by construction.
    axis1_sites: int = 0
    axis1_unclassified: int = 0
    axis1_image_repos: int = 0        # repositories that NAME a platform image without pinning one
    axis2_sites: int = 0
    axis2_pairs: dict[tuple[str, str], list[str]] = field(default_factory=dict)
    # The TAG half: a reference that must keep RESOLVING, which is a different object from the
    # manifest it resolves to and carries its own lock attribute.
    wanted_tags: list[WantedTag] = field(default_factory=list)
    tags_already_protected: list[WantedTag] = field(default_factory=list)
    tags_to_lock: list[WantedTag] = field(default_factory=list)
    tags_locked_now: int = 0
    platform_manifests: int = 0       # children pulled in by an index in the wanted set
    # AXIS 3: the installations, and whether the inventory of them is COMPLETE.
    instances: list[Instance] = field(default_factory=list)
    inventory_complete: bool = False
    # 🚨 THE SECOND REGISTRY'S PROTECTED SET (#4230). Every reference the fleet's COMMITTED overlays
    # make to a host this lane cannot lock — (registry host, repository, tag, where). Nothing here
    # is ever locked; it is collected so the nightly run can PRINT what a cleanup on that registry
    # would have to keep. That printout IS the dry run: a registry with no lock has no object to
    # write a protected set into, so the only artifact available is the derivation itself.
    # Doc/Architecture/FleetRegistryRetention.
    foreign_references: list[tuple[str, str, str, str]] = field(default_factory=list)


# ── Planning: pure over its inputs, so the self-test can falsify it with no network ─────────────


def build_plan(axis1: list, axis2: list[OverlayScan]) -> Plan:
    """Everything decidable WITHOUT the registry: what is pinned, and what was not looked at."""
    plan = Plan()

    # ---- blockers that mean "a pin was not looked at" -----------------------------------------
    for scan in axis1:
        if scan.unreadable:
            plan.blockers.append(
                f"{scan.gh_repo}: its workflows could not be read, so its digest pins were NOT "
                f"looked for — {scan.unreadable}"
            )
        for bad in scan.malformed:
            plan.blockers.append(
                f"{scan.gh_repo} — {bad.filename}:{bad.line} `{bad.name}` is pin-shaped and its "
                f"value {bad.value!r} is not a digest (I4). A placeholder reads as an absence to a "
                f"looser matcher, so it is a failure here, never a silence."
            )
    for scan in axis2:
        if scan.unreadable:
            plan.blockers.append(
                f"{scan.gh_repo}: its deployment overlays could not be read, so its TAG pins were "
                f"NOT looked for — {scan.unreadable}"
            )
        # The committed half of the OTHER registry's protected set, collected here rather than
        # re-derived at report time so the number the report prints and the set it lists are the
        # same object (#4230).
        plan.foreign_references.extend(
            (host, repo, tag, f"{scan.gh_repo} {where}") for host, repo, tag, where in scan.foreign)

    # ---- axis 1: a classified digest pin ------------------------------------------------------
    by_key: dict[tuple[str, str], Wanted] = {}
    axis1_sites = 0
    axis1_unclassified = 0
    for scan in axis1:
        for site in scan.sites:
            if site.kind != "digest":
                continue
            axis1_sites += 1
            where = f"{scan.gh_repo} {site.filename}:{site.line} `{site.name}`"
            if not site.role:
                axis1_unclassified += 1
                plan.blockers.append(
                    f"{where} pins {site.value} and NO ACR repository could be determined for it "
                    "(I5). An unclassified pin has not been checked — and for locking it is worse: "
                    "there is no manifest to protect. Bind it to its image in the file, or add it "
                    "to ROLE_ALIASES in check-pin-set-consistency.py."
                )
                continue
            key = (site.role, site.value)
            entry = by_key.setdefault(key, Wanted(acr_repo=site.role, digest=site.value))
            entry.sources.append(where)

    # ---- axis 2: a tag pin. Its digest needs the registry, so it is only COLLECTED here. -------
    axis2_sites = 0
    for scan in axis2:
        for acr_repo, tag, where in scan.pins:
            axis2_sites += 1
            plan.axis2_pairs.setdefault((acr_repo, tag), []).append(f"{scan.gh_repo} {where}")

    plan.wanted = list(by_key.values())
    plan.axis1_sites = axis1_sites
    plan.axis1_unclassified = axis1_unclassified
    plan.axis1_image_repos = sum(
        1 for scan in axis1
        if any(site.kind in ("digest", "image-name") for site in scan.sites))
    plan.axis2_sites = axis2_sites

    # ---- the denominator rule, per axis -------------------------------------------------------
    unreadable_axis1 = any(scan.unreadable for scan in axis1)
    unreadable_axis2 = any(scan.unreadable for scan in axis2)

    # 🚨 THE ZERO-PIN ABSOLUTE EXPIRED, AND ON 2026-09-12 IT FAILED THE PROTECTION LANE FOR IT.
    #
    # It used to read: "six repositories pinned a digest on 2026-09-06, so zero means the extractor
    # stopped matching". Run 34664099031 (01:12Z, 2026-09-12) died on exactly that line — two hours
    # before the purge — and the premise was simply no longer true. #3842 moved every satellite to
    # RESOLVING the platform set at run time (`platform-ref` / `resolve-platform.py`); measured the
    # same day with this repository's own extractor, the fleet declares ZERO digest pins and SIX
    # repositories that name a platform image without pinning one. Pinning did stop.
    #
    # An assertion about the FLEET can expire like that. An assertion about the INSTRUMENT cannot,
    # so that is what replaced it, and it is strictly stronger than what it replaces — it fails when
    # the digest matcher breaks even if the fleet happens to declare pins anyway:
    #
    #   * `extractor_control()` runs `extract()` over a fixture with two known digest pins on every
    #     run and reds if they stop being found. That is the "positive completeness evidence"
    #     Doc/Architecture/ReleasedArtifactRetention asks for in place of the zero assertion.
    #   * the denominator below stays, because a fleet that names NO platform image anywhere is
    #     still impossible — the satellites pull the tester and the portal on every run.
    if plan.axis1_image_repos == 0 and axis1_sites == 0 and not unreadable_axis1:
        plan.blockers.append(
            "AXIS 1 found ZERO digest pins AND ZERO repositories naming a platform image across "
            "the whole fleet. Zero pins is expected under #3842 (the satellites resolve the "
            "platform set at run time); zero repositories reaching for the images at all is not — "
            "six named one on 2026-09-12. Run --self-test."
        )
    if axis2_sites == 0 and not unreadable_axis2:
        plan.blockers.append(
            "AXIS 2 found ZERO deployment-overlay tag pins across the whole fleet. Seven were "
            "measured on 2026-09-07 (Memex's three overlays and this repository's whisper chart), "
            "so zero means the overlay reader stopped matching, not that the overlays stopped "
            "pinning. Run --self-test."
        )
    return plan


# These are the ACR/GHCR image repositories promoted by release.yml. Numeric tags
# in cached third-party images or independently versioned helpers are not MW releases.
# --check-retention-record checks this set against the publisher's explicit repo loops.
ROSTER_PATH = "instances.json"
ROSTER_STATES = {"live", "not-installed", "retired"}
INSTANCE_PROBE_TIMEOUT = 25
VERSION_ROUTE = "/api/version"


REGISTRY_DISPOSITIONS = {"fleet-unlockable", "third-party"}


def read_registry_dispositions(root: str) -> tuple[dict[str, tuple[str, str]], list[str]]:
    """What every registry the fleet's overlays name is, and what protects it — or that nothing does.

    🚨 THE UNIT IS THE REGISTRY, NOT THE INSTANCE, and that is what makes the declaration COMPLETE.
    A per-installation field answers "is this one out of scope"; it cannot answer "is every registry
    the fleet pins in accounted for". An installation pinning its declared registry AND a second
    undeclared one would pass a per-instance check while half its running set went unnamed.

    Two dispositions, and the difference is load-bearing:

      fleet-unlockable  a registry that serves MESHWEAVER'S OWN images and that this lane cannot
                        lock (`cr.meshweaver.cloud`). An installation running from one is out of
                        THIS lane's scope; whether anything retains it is a separate question, and
                        the `reason` is where that is stated rather than assumed.
      third-party       a registry whose images are somebody else's and were never this lane's to
                        protect (`ghcr.io/distribution/distribution` — the registry service's own
                        image). Reported, never a blocker.

    An UNDECLARED host is a blocker wherever it appears. Measured 2026-09-13 across the fleet's
    twelve overlays: exactly two hosts, `cr.meshweaver.cloud` (4 references) and `ghcr.io` (2, one
    of which was a line of prose) — so the table is small, and an addition to it is a reviewed
    sentence about a registry rather than a silent widening."""
    path = Path(root) / ".github" / "acr-retention" / ROSTER_PATH
    if not path.is_file():
        return {}, []          # the roster reader already reds on a missing file
    try:
        document = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return {}, []          # …and on an unreadable one
    table: dict[str, tuple[str, str]] = {}
    problems: list[str] = []
    registries = document.get("registries")
    if registries is None:
        return {}, [f"{ROSTER_PATH}: no `registries` table. Every registry the fleet's overlays "
                    "name has to be accounted for, and an absent table accounts for none of them."]
    if not isinstance(registries, dict):
        return {}, [f"{ROSTER_PATH}: `registries` is not an object."]
    for host, entry in registries.items():
        if not isinstance(entry, dict):
            problems.append(f"{ROSTER_PATH}: `registries.{host}` is not an object.")
            continue
        disposition = str(entry.get("disposition", "")).strip()
        reason = str(entry.get("reason", "")).strip()
        if disposition not in REGISTRY_DISPOSITIONS:
            problems.append(f"{ROSTER_PATH}: `registries.{host}.disposition` is "
                            f"{disposition!r}; expected one of "
                            + ", ".join(sorted(REGISTRY_DISPOSITIONS)) + ".")
            continue
        if not reason:
            problems.append(
                f"{ROSTER_PATH}: `registries.{host}` has no `reason`. Declaring a registry out of "
                "this lane's reach without saying what — if anything — protects it instead is "
                "exactly where the next gap goes unnoticed.")
            continue
        table[host] = (disposition, reason)
    return table, problems


def read_instance_roster(root: str) -> tuple[dict[str, tuple[str, str, str]], list[str]]:
    """The DECLARED state of any installation that is not live, from `.github/acr-retention/`.

    🚨 THE OVERLAYS ARE THE DENOMINATOR; THIS FILE ONLY EXPLAINS AN ABSENCE. An installation the
    overlays declare and this file does not is LIVE — so forgetting to write an entry makes the run
    stricter, never looser, which is the only direction a hand-maintained file may fail in. #3858
    asks for the other half in as many words: *retirement is explicit and auditable; a temporarily
    unavailable portal is not treated as retired*. Silence is unavailability. Only a line here,
    committed and reviewable, is retirement."""
    path = Path(root) / ".github" / "acr-retention" / ROSTER_PATH
    if not path.is_file():
        return {}, [f"the instance roster {path} is missing, so no installation can be declared "
                    "retired or not-yet-installed and every one of them is expected to answer."]
    try:
        document = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        return {}, [f"the instance roster {path} could not be read: {exc}. An unreadable roster is "
                    "not an empty one."]
    roster: dict[str, tuple[str, str, str]] = {}
    problems: list[str] = []
    for entry in document.get("instances", []):
        ident = str(entry.get("id", "")).strip()
        state = str(entry.get("state", "")).strip()
        reason = str(entry.get("reason", "")).strip()
        registry = str(entry.get("registry", "")).strip()
        if not ident:
            problems.append(f"{ROSTER_PATH}: an entry has no `id`.")
            continue
        if state not in ROSTER_STATES:
            problems.append(f"{ROSTER_PATH}: `{ident}` has state {state!r}; expected one of "
                            + ", ".join(sorted(ROSTER_STATES)) + ".")
            continue
        # 🚨 A PER-INSTANCE `registry` CANNOT BE COMPLETE, so it is refused rather than supported.
        # It answers "is THIS one out of scope" and never "is every registry the fleet pins in
        # accounted for" — an installation pinning its declared registry AND a second undeclared
        # one would pass it with half its running set unnamed. The `registries` table one level up
        # is the unit that can be complete; see `read_registry_dispositions`.
        if registry:
            problems.append(
                f"{ROSTER_PATH}: `{ident}` carries a per-instance `registry`. Declare the registry "
                "in the top-level `registries` table instead — per instance it can never be shown "
                "to cover every registry the fleet pins in.")
            continue
        if state != "live" and not reason:
            problems.append(f"{ROSTER_PATH}: `{ident}` is {state} with no `reason`. A declaration "
                            "that explains nothing is the hand list this job exists to replace.")
            continue
        if ident in roster:
            # 🚨 LAST-WINS ON A FILE THAT GRANTS EXEMPTIONS IS A SILENT EXEMPTION. Two entries for
            # one installation let the order of a JSON array decide whether it must answer, so a
            # `live` line added above an old `retired` one changes nothing and reads as if it did.
            problems.append(
                f"{ROSTER_PATH}: `{ident}` is declared TWICE ({roster[ident][0]} and {state}). "
                "Which one is in force would be decided by the order of the array, so neither is. "
                "Delete the line that no longer applies.")
            continue
        roster[ident] = (state, reason, "")
    return roster, problems


def probe_running_commit(host: str) -> tuple[str | None, str | None, str]:
    """(commit, version, error) from the installation's own `/api/version`.

    Unauthenticated by design (`MapVersionEndpoint` … `.AllowAnonymous()`), and deliberately the
    instance's own answer rather than the cluster's or the record's: the overlay is what an
    instance SHOULD run and this is what it DOES. A non-answer is an error, never a zero."""
    url = f"https://{host}{VERSION_ROUTE}"
    request = urllib.request.Request(url, headers={"User-Agent": "lock-pinned-digests"})
    try:
        with urllib.request.urlopen(request, timeout=INSTANCE_PROBE_TIMEOUT) as response:
            if response.status != 200:
                return None, None, f"{url} answered HTTP {response.status}"
            payload = json.loads(response.read().decode("utf-8", "replace"))
    except Exception as exc:                                  # noqa: BLE001 — every failure is one
        return None, None, f"{url} could not be read: {type(exc).__name__}: {exc}"
    commit = str(payload.get("commit", "")).strip()
    version = str(payload.get("version", "")).strip() or None
    if not re.fullmatch(r"[0-9a-f]{40}", commit):
        return None, version, (f"{url} answered without a usable `commit` ({commit!r}). The route "
                               "is the contract this axis reads; a changed shape is a refusal.")
    return commit, version, ""


def running_tag_patterns(commit: str) -> list[re.Pattern[str]]:
    """Every tag shape `main-cd.yml` puts on an image set built from one core commit.

    🚨 THE BARE SHORT SHA IS A MOVING TAG AND CANNOT IDENTIFY A BUILD. Measured 2026-09-12: core
    `4c99ec26` produced TWO image sets ninety minutes apart — `3.0.0-ci.8399` / `4c99ec2-p38ebf08`
    and `3.0.0-ci.8401` / `4c99ec2` — because the plugins half moved underneath it, and the bare
    `4c99ec2` tag followed the newer one. `/api/version` answers the CORE commit and nothing finer,
    so which of the two an instance runs cannot be decided from outside it. This axis therefore
    protects the CLOSURE of that commit rather than guessing a member of it: locking a superset is
    safe (a lock destroys nothing), guessing is not."""
    short = re.escape(commit[:7])
    return [re.compile(rf"^{short}$"),
            re.compile(rf"^{short}-p[0-9a-f]{{7,}}$"),
            re.compile(rf"^staging-{short}-[0-9]+$")]


def build_instances(axis2: list[OverlayScan], roster: dict[str, tuple[str, str]],
                    probe=probe_running_commit) -> tuple[list[Instance], list[str]]:
    """The expected installations, each asked what it is running. Discovery first, roster second."""
    instances: dict[str, Instance] = {}
    blockers: list[str] = []
    for scan in axis2:
        for ident, host, where in scan.instances:
            source = f"{scan.gh_repo} {where}"
            existing = instances.get(ident)
            if existing is not None:
                blockers.append(
                    f"installation `{ident}` is declared by two overlays ({existing.source} and "
                    f"{source}), so which host answers for it is ambiguous.")
                continue
            state, reason, _ = roster.get(ident, ("live", "", ""))
            instances[ident] = Instance(id=ident, host=host, source=source, state=state,
                                        reason=reason,
                                        foreign_registries=sorted({
                                            foreign_host for foreign_host, _, _, foreign_where
                                            in scan.foreign if foreign_where == where}),
                                        foreign_references=sorted({
                                            (foreign_host, foreign_repo)
                                            for foreign_host, foreign_repo, _, foreign_where
                                            in scan.foreign if foreign_where == where}))
    for ident in sorted(set(roster) - set(instances)):
        blockers.append(
            f"{ROSTER_PATH} declares `{ident}` ({roster[ident][0]}) and no overlay in the fleet "
            "names it. A roster entry for an installation that no longer exists exempts nothing "
            "and hides the next one — delete the line.")

    for instance in instances.values():
        if instance.state != "live":
            continue
        if not instance.host:
            instance.error = ("its overlay declares the installation but no ingress host, so it "
                              "cannot be asked what it is running.")
            continue
        instance.commit, instance.version, instance.error = probe(instance.host)
    return list(instances.values()), blockers


def classify_foreign_registries(plan: Plan, dispositions: dict[str, tuple[str, str]],
                                publications: dict[str, set[str]] | None = None) -> None:
    """Hold EVERY registry the fleet's overlays name to the declaration table, before anything
    decides an installation is covered.

    🚨 THIS RUNS OVER EVERY LIVE INSTALLATION, NOT ONLY THE ONES WITH NOTHING IN THIS ACR. An
    installation that pins here AND somewhere else is the case a per-instance scope field cannot
    see: its ACR half gets locked, the run reports success, and the other half — which is also what
    it is RUNNING — is protected by nothing and named by nobody. Half covered reported as covered
    is precisely the shape this whole mechanism exists to refuse."""
    for instance in sorted(plan.instances, key=lambda i: i.id):
        if instance.state != "live" or not instance.foreign_registries:
            continue
        undeclared = [host for host in instance.foreign_registries if host not in dispositions]
        if undeclared:
            plan.blockers.append(
                f"AXIS 3 — installation `{instance.id}` pins images in "
                f"{', '.join(undeclared)}, which `.github/acr-retention/{ROSTER_PATH}` does not "
                "account for. An unknown registry is not a registry with nothing in it: declare it "
                "in the `registries` table as `fleet-unlockable` (it serves our images and this "
                "lane cannot lock them — say what retains it, or that nothing does) or "
                "`third-party` (the images are somebody else's and were never ours to protect).")
            continue
        instance.unlockable_registries = [
            host for host in instance.foreign_registries
            if dispositions[host][0] == "fleet-unlockable"]
        # 🚨 THE ARM THAT MAKES `publishes` LOAD-BEARING RATHER THAN PROSE (#4323). A host may be
        # declared `third-party` about what the fleet PULLS and still hold repositories the fleet
        # PUBLISHES — `ghcr.io` is exactly that. An overlay pinning one of THOSE is not a
        # third-party pin: it is an installation running OUR images from a store nothing of ours
        # retains and this lane cannot lock, and the `third-party` disposition would wave it
        # through in the one branch (pins here AND there) that prints success.
        #
        # It fires on nobody today — measured 2026-09-15, no overlay in the fleet names a
        # `ghcr.io/systemorph/*` image — which is precisely the claim `publishes.runFrom` makes and
        # therefore precisely the claim that has to red when it stops being true.
        published = publications or {}
        pinned = sorted({f"{host}/{repo}" for host, repo in instance.foreign_references
                         if repo in published.get(host, set())})
        if pinned:
            plan.blockers.append(
                f"AXIS 3 — installation `{instance.id}` pins {', '.join(pinned)}, which "
                f"`.github/acr-retention/{ROSTER_PATH}` declares this fleet PUBLISHES rather than "
                "retains. Those are OUR images in a store this lane cannot lock and nothing of "
                "ours keeps — its operator's retention is the whole of their protection — so an "
                "installation RUNNING from them is running an unprotected set that this run would "
                "otherwise report as covered. Point the overlay at a registry the fleet retains "
                "(`cr.meshweaver.cloud`, or this ACR), or move the repository out of `publishes` "
                "and declare what protects it. Doc/Architecture/FleetRegistryRetention.")


def resolve_running_sets(plan: Plan, inventory: dict[str, list[Manifest]],
                         repositories_of: dict[str, list[str]]) -> None:
    """Protect the closure of every image set each live installation is running.

    The manifests come from the inventory already read — this axis costs no extra registry call."""
    for instance in sorted(plan.instances, key=lambda i: i.id):
        if instance.state != "live":
            continue
        if instance.error or not instance.commit:
            plan.blockers.append(
                f"AXIS 3 — installation `{instance.id}` could not be accounted for: "
                f"{instance.error or 'it answered no commit'}. An installation that did not answer "
                "is NOT an installation running nothing: whatever it is serving is unprotected and "
                "unnameable. Either it answers, or it is declared in "
                f"`.github/acr-retention/{ROSTER_PATH}` with a reason.")
            continue
        patterns = running_tag_patterns(instance.commit)
        repositories = repositories_of.get(instance.id, [])
        # 🚨 HALF COVERED IS NOT COVERED. An installation whose overlay pins BOTH here and in a
        # fleet-unlockable registry has part of its running set protected and part of it not, and
        # locking the half that is here while reporting success is how a partially-protected
        # installation reads as a protected one. The declaration accounts for the registry; it does
        # not make the unprotected half disappear. Nothing in the fleet is in this state today
        # (measured 2026-09-13), so it costs nothing now and fires exactly when the migration
        # reaches an installation that still pins here.
        if repositories and instance.unlockable_registries:
            plan.blockers.append(
                f"AXIS 3 — installation `{instance.id}` pins {len(repositories)} repository(ies) "
                f"in the registry this run locks ({', '.join(repositories)}) AND images in "
                f"{', '.join(instance.unlockable_registries)}, which it cannot. Half its running "
                "set would be protected and half would not, and the run would report success. "
                "Finish the move, or move it back.")
            continue
        if not repositories:
            # 🚨 "PINS NO IMAGE AT ALL" WAS FALSE ON 2026-09-13 AND THAT IS WHY THIS BRANCH SPLIT.
            # `build` — the fleet's build server, live since that week — pins
            # `cr.meshweaver.cloud/memex-portal-ai:3.0.0-ci.8411` and its migration twin, in the
            # fleet's OWN registry. The ACR-scoped extractor saw zero and the run said the overlay
            # pinned nothing, which reads as a broken matcher and sent the reader to the wrong
            # place. The images are real; they are simply not in the registry this lane locks.
            if instance.foreign_registries:
                # Every foreign host is declared by now (classify_foreign_registries blocks
                # otherwise), so this is out of scope exactly when a fleet-unlockable one is why
                # there is nothing here to protect.
                if instance.unlockable_registries:
                    instance.out_of_scope = True
                    continue
                plan.blockers.append(
                    f"AXIS 3 — installation `{instance.id}` runs core {instance.commit[:7]} and "
                    f"its overlay ({instance.source}) pins images ONLY in "
                    f"{', '.join(instance.foreign_registries)}, every one of them declared "
                    "`third-party` — so nothing it runs is accounted for by any registry that "
                    "holds our images. Either its portal images are missing from the overlay, or "
                    "one of those hosts is really `fleet-unlockable`.")
                continue
            plan.blockers.append(
                f"AXIS 3 — installation `{instance.id}` runs core {instance.commit[:7]} and its "
                f"overlay ({instance.source}) pins no image at all, in ANY registry, so there is "
                "no repository in which to protect what it runs.")
            continue
        for acr_repo in repositories:
            manifests = inventory.get(acr_repo)
            if manifests is None:
                continue          # already a blocker from the inventory read
            for manifest in manifests:
                matched = [tag for tag in manifest.tags
                           if any(pattern.match(tag) for pattern in patterns)]
                if not matched:
                    continue
                instance.manifests.append(f"{acr_repo}@{manifest.digest[:19]}…")
                entry = next((w for w in plan.wanted
                              if w.acr_repo == acr_repo and w.digest == manifest.digest), None)
                if entry is None:
                    entry = Wanted(acr_repo=acr_repo, digest=manifest.digest)
                    plan.wanted.append(entry)
                entry.sources.append(
                    f"installation `{instance.id}` is RUNNING core {instance.commit[:7]} "
                    f"(tag {', '.join(sorted(matched))})")
                # 🚨 EVERY TAG ON A RUNNING MANIFEST IS A REFERENCE THAT MAY BE THE ONE ITS POD
                # SPEC NAMES, and from outside the cluster there is no way to tell which. The
                # helm value pins `memex-portal-ai:3.0.0-ci.N`; `/api/version` answers the core
                # commit; the short-sha tags are what this axis matched on. When the instance is
                # AHEAD of its committed pin, that `3.0.0-ci.N` tag is in NO committed file, so
                # axis 2 never sees it — and a purge that deletes it breaks the next restart while
                # the manifest sits locked. Protect the manifest's whole set of names.
                for tag in sorted(manifest.tags):
                    want_tag(plan, acr_repo, tag,
                             f"installation `{instance.id}` runs core {instance.commit[:7]}"
                             + ("" if tag in matched else " (a name of that same manifest)"))
        if not instance.manifests:
            plan.blockers.append(
                f"AXIS 3 — installation `{instance.id}` reports core {instance.commit[:7]} and NO "
                f"manifest in {', '.join(repositories)} carries a tag for it. Either the image set "
                "it is running has already been purged — which is this issue happening again — or "
                "the tag scheme moved and this axis stopped matching. Neither is a pass.")


def want_tag(plan: Plan, acr_repo: str, tag: str, source: str) -> None:
    """Record that a TAG must keep resolving, merging sources for a tag wanted twice."""
    entry = next((t for t in plan.wanted_tags if t.acr_repo == acr_repo and t.tag == tag), None)
    if entry is None:
        entry = WantedTag(acr_repo=acr_repo, tag=tag)
        plan.wanted_tags.append(entry)
    if source not in entry.sources:
        entry.sources.append(source)


def classify_tags(plan: Plan, registry: Registry) -> None:
    """Split the wanted tags into already-protected and to-lock, reading each tag's OWN attribute."""
    for acr_repo in sorted({t.acr_repo for t in plan.wanted_tags}):
        detail, error = registry.tags(acr_repo)
        if detail is None:
            plan.blockers.append(
                f"could not read the TAGS of `{acr_repo}` — {error}. Their lock state is the "
                "attribute the purge reads when it deletes a tag, so this run cannot say whether "
                "any reference in that repository is protected.")
            continue
        state = {tag.name: tag.delete_enabled for tag in detail}
        for entry in (t for t in plan.wanted_tags if t.acr_repo == acr_repo):
            if entry.tag not in state:
                entry.problem = (
                    f"{acr_repo}:{entry.tag} is not in the registry's tag list, so the reference "
                    "that is pinned does not resolve.")
                plan.blockers.append(f"{', '.join(entry.sources[:3])}: {entry.problem}")
                continue
            if state[entry.tag] is False:
                plan.tags_already_protected.append(entry)
            else:
                plan.tags_to_lock.append(entry)


OFFICIAL_RELEASE_REPOSITORIES = frozenset({"memex-migration", "mw-plugin-test", "memex-portal-ai"})


INDEX_MEDIA_TYPES = frozenset({
    "application/vnd.oci.image.index.v1+json",
    "application/vnd.docker.distribution.manifest.list.v2+json",
})


def expand_platform_closure(plan: Plan, registry: Registry,
                            inventory: dict[str, list[Manifest]],
                            inventory_errors: dict[str, str]) -> None:
    """🚨 LOCKING AN INDEX DOES NOT PROTECT ITS PLATFORM MANIFESTS — IT REMOVES THE PROTECTION THEY
    HAD. Read from `Azure/acr-cli`'s `GetUntaggedManifests`, in statement order:

        if _, ok := ignoreList.Load(*manifest.Digest); ok { continue }
        if !includeLocked && manifest.ChangeableAttributes != nil {
            if …DeleteEnabled != nil && !(*…DeleteEnabled) { continue }      // ← a LOCKED index exits HERE
            …
        }
        …
        if isProtectedByTags || isProtectedByAge {
            if *manifest.MediaType != v1.MediaTypeImageIndex && … { continue }
            group.SubmitErr(func() error { … addDependentManifestsToIgnoreList(…) })   // ← the walk
            continue
        }

    The lock `continue` comes BEFORE the walk that adds an index's children to the ignore list. So a
    locked index is never walked, its children are never ignore-listed, and each untagged child is
    then judged on its own: no tags, older than `--ago` ⇒ deleted. The locked index is left pointing
    at manifests that no longer exist, and the pull fails exactly as if the image had been deleted.

    And the perverse corollary, which is why this is not a nice-to-have: an index that is TAGGED and
    UNLOCKED reaches `isProtectedByTags`, IS walked, and its children ARE ignore-listed. Locking it
    short-circuits that. Protection applied to the index alone makes its children strictly LESS safe
    than leaving it unprotected.

    Measured 2026-09-12 on meshweaver.azurecr.io: `memex-portal-ai@sha256:0217fd11…` — the set
    `memex` is RUNNING — is locked, and its two children (`sha256:3296b0ba…` linux/amd64,
    `sha256:322de2ff…` linux/arm64) both read `deleteEnabled: true`. They survive today only
    because they still carry `staging-74d4c85-…-linux-x64` / `-linux-arm64` tags, which the SAME
    purge step deletes once they pass `--ago 7d`.

    A closure that could not be enumerated is a blocker, never an empty closure."""
    by_digest = {(m.acr_repo, m.digest): m for group in inventory.values() for m in group}
    pending = [entry for entry in plan.wanted if entry.digest and not entry.problem]
    seen: set[tuple[str, str]] = set()
    while pending:
        entry = pending.pop()
        key = (entry.acr_repo, entry.digest or "")
        if key in seen:
            continue
        seen.add(key)
        manifest = by_digest.get(key)
        if manifest is None:
            continue          # absent, or an unreadable repository — already handled downstream
        if manifest.media_type not in INDEX_MEDIA_TYPES:
            continue
        children, error = registry.index_children(entry.acr_repo, entry.digest or "")
        if children is None:
            plan.blockers.append(
                f"could not enumerate the platform manifests of {entry.acr_repo}@{entry.digest} — "
                f"{error}. A locked index does NOT protect its children (acr-cli skips a locked "
                "manifest BEFORE it walks the index), so a closure that could not be read is not an "
                "empty closure.")
            continue
        if not children:
            plan.blockers.append(
                f"{entry.acr_repo}@{entry.digest} is an image index and lists NO platform "
                "manifests. An index with nothing under it is a malformed reference set, not a "
                "single-architecture image.")
            continue
        for child in children:
            existing = next((w for w in plan.wanted
                             if w.acr_repo == entry.acr_repo and w.digest == child), None)
            if existing is None:
                existing = Wanted(acr_repo=entry.acr_repo, digest=child)
                plan.wanted.append(existing)
                plan.platform_manifests += 1
                pending.append(existing)
            existing.sources.append(
                f"platform manifest of {entry.acr_repo}@{(entry.digest or '')[:19]}…, which a "
                "LOCKED index does not protect")


def resolve_and_classify(plan: Plan, registry: Registry,
                         inventory: dict[str, list[Manifest]],
                         inventory_errors: dict[str, str]) -> None:
    """Turn axis-2 tags into digests, then split the union into already-protected / to-lock."""
    for (acr_repo, tag), wheres in sorted(plan.axis2_pairs.items()):
        digest, detail = registry.resolve_tag(acr_repo, tag)
        if digest is None:
            entry = Wanted(acr_repo=acr_repo, digest=None, sources=wheres, tag=tag)
            if detail.startswith("INDETERMINATE"):
                entry.problem = (f"could not determine whether {acr_repo}:{tag} exists — {detail}. "
                                 "Indeterminate is not a pass and it is not 'gone' either.")
            else:
                entry.problem = (
                    f"{acr_repo}:{tag} does not resolve in the registry, so there is nothing to "
                    "protect. This is Memex#141's shape: a committed overlay pin whose image is "
                    "gone is inert until the one deploy that reads it — a first install."
                )
            plan.wanted.append(entry)
            plan.blockers.append(f"{', '.join(wheres)}: {entry.problem}")
            continue
        merged = next((w for w in plan.wanted if w.acr_repo == acr_repo and w.digest == digest),
                      None)
        if merged is None:
            merged = Wanted(acr_repo=acr_repo, digest=digest, tag=tag)
            plan.wanted.append(merged)
        merged.sources.extend(f"{where} (tag {tag})" for where in wheres)
        # The manifest is the bytes; the TAG is the reference that is actually pinned, and the
        # purge deletes it on the tag's own attribute. Both or neither.
        for where in wheres:
            want_tag(plan, acr_repo, tag, where)

    # release.yml publishes clean major.minor.patch image tags. Support follows the
    # underlying .NET lifecycle, not whether a deployment currently pins the release.
    # Until an authoritative end-of-support mapping is available, absence of a pin
    # cannot authorize removing it. Preserve every clean release (including legacy v
    # prefixes) and keep it out of the optional unlock arm as well.
    for acr_repo, manifests in sorted(inventory.items()):
        if acr_repo not in OFFICIAL_RELEASE_REPOSITORIES:
            continue
        for manifest in manifests:
            release_tags = sorted(tag for tag in manifest.tags
                                  if re.fullmatch(r"v?[0-9]+\.[0-9]+\.[0-9]+", tag))
            if not release_tags:
                continue
            if not re.fullmatch(r"sha256:[0-9a-f]{64}", manifest.digest):
                plan.blockers.append(
                    f"official release {acr_repo}:{', '.join(release_tags)} has no valid "
                    "manifest digest, so its protection could not be established")
                continue
            entry = next((w for w in plan.wanted
                          if w.acr_repo == acr_repo and w.digest == manifest.digest), None)
            if entry is None:
                entry = Wanted(acr_repo=acr_repo, digest=manifest.digest)
                plan.wanted.append(entry)
            entry.sources.append(
                "official release tag(s) " + ", ".join(release_tags)
                + " — support has not been established as ended")
            for release_tag in release_tags:
                want_tag(plan, acr_repo, release_tag,
                         f"official release {acr_repo}:{release_tag}")

    # 🚨 THE CLOSURE, BEFORE ANYTHING IS CLASSIFIED. A protected index whose platform manifests are
    # not also protected is a reference set with a hole in it that opens a week later.
    expand_platform_closure(plan, registry, inventory, inventory_errors)

    # Which of them are already protected?
    known: dict[tuple[str, str], Manifest] = {}
    for acr_repo, manifests in inventory.items():
        for manifest in manifests:
            known[(acr_repo, manifest.digest)] = manifest

    for acr_repo, error in sorted(inventory_errors.items()):
        if acr_repo == "*":
            plan.blockers.append(
                f"could not enumerate the registry's repositories — {error}. NOTHING was checked "
                "and nothing can be protected. This is not 'no manifest is locked': a gate that "
                "cannot reach its subject has not checked it."
            )
        else:
            plan.blockers.append(
                f"could not enumerate the manifests of `{acr_repo}` — {error}. Nothing in that "
                "repository could be checked or protected, so this run is partial."
            )

    for entry in plan.wanted:
        if entry.problem or entry.digest is None:
            continue
        manifest = known.get((entry.acr_repo, entry.digest))
        if manifest is None:
            # 🚨 ABSENCE FROM AN INVENTORY NOBODY COULD READ IS NOT ABSENCE. Without this, one
            # refused `az acr repository list` turns every pin in the fleet into "the manifest is
            # gone" — the catastrophe-that-is-not-happening this whole family of gates refuses to
            # report (check-pinned-digests.py's control probe, for the same reason).
            if "*" in inventory_errors or entry.acr_repo in inventory_errors:
                continue        # already a blocker, and its absence proves nothing
            entry.problem = (
                f"{entry.acr_repo}@{entry.digest} does not exist in the registry, so it CANNOT be "
                "protected. Every job that pulls it dies at `manifest unknown` before it runs "
                "anything (MeshWeaver#3438). Move the pin to a manifest that exists — as one set."
            )
            plan.blockers.append(f"{', '.join(entry.sources[:3])}: {entry.problem}")
            continue
        if manifest.delete_enabled is False:
            plan.already_protected.append(entry)
        else:
            plan.to_lock.append(entry)

    # Release candidates: locked, and pinned by nothing this run could see. REPORTED, not acted on
    # unless a person has turned the arm on AND the run is clean.
    pinned_keys = {(w.acr_repo, w.digest) for w in plan.wanted if w.digest and not w.problem}
    for acr_repo, manifests in sorted(inventory.items()):
        for manifest in manifests:
            if manifest.delete_enabled is False and (acr_repo, manifest.digest) not in pinned_keys:
                plan.release_candidates.append(manifest)


# ── Reporting ──────────────────────────────────────────────────────────────────────────────────


def emit(text: str) -> None:
    print(text)
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as handle:
            handle.write(text + "\n")


def report(plan: Plan, axis1, axis2: list[OverlayScan], registry_name: str,
           inventory: dict[str, list[Manifest]], apply: bool, release_enabled: bool,
           root: str = ".") -> None:
    def pins_a_digest(scan) -> bool:
        return any(site.kind == "digest" for site in scan.sites)

    # 🚨 `x not in list` on a DATACLASS compares by VALUE, and two repositories that both scanned
    # clean with zero pins are equal by value — so a membership test would classify one of them by
    # the other's identity. Ask the predicate, not the list.
    pinning1 = [s for s in axis1 if pins_a_digest(s)]
    silent1 = [s for s in axis1 if not s.unreadable and not pins_a_digest(s)]
    unreadable1 = [s for s in axis1 if s.unreadable]
    pinning2 = [s for s in axis2 if s.pins]
    silent2 = [s for s in axis2 if not s.unreadable and not s.pins]
    unreadable2 = [s for s in axis2 if s.unreadable]
    floating = [(s.gh_repo, repo, tag, where) for s in axis2 for repo, tag, where in s.floating]
    total_manifests = sum(len(v) for v in inventory.values())
    locked_now = sum(1 for v in inventory.values() for m in v if m.delete_enabled is False)

    emit("")
    emit(f"### Pinned-manifest protection — {registry_name}.azurecr.io")
    emit("")
    emit(f"    mode                                       "
         f"{'APPLY (locks will be written)' if apply else 'REPORT ONLY (no registry mutation)'}")
    emit("")
    emit("    AXIS 1 — digest pins in .github/workflows")
    emit(f"      repositories scanned                     {len(axis1)}")
    emit(f"      …declaring at least one digest pin       {len(pinning1)}"
         + (f"  ({', '.join(s.gh_repo for s in pinning1)})" if pinning1 else ""))
    emit(f"      …declaring NO digest pin                 {len(silent1)}")
    emit(f"      …whose workflows could not be read       {len(unreadable1)}")
    emit(f"      digest pin SITES found                   {getattr(plan, 'axis1_sites', 0)}")
    emit(f"      …UNCLASSIFIED (I5 — not checked)         {getattr(plan, 'axis1_unclassified', 0)}")
    emit("")
    emit("    AXIS 2 — image TAG pins in deployment overlays")
    emit(f"      repositories scanned                     {len(axis2)}")
    emit(f"      …carrying an overlay that pins           {len(pinning2)}"
         + (f"  ({', '.join(s.gh_repo for s in pinning2)})" if pinning2 else ""))
    emit(f"      …carrying none                           {len(silent2)}")
    emit(f"      …whose tree could not be read            {len(unreadable2)}")
    emit(f"      overlay files considered                 {sum(s.files for s in axis2)}")
    emit(f"      tag pin SITES found                      {getattr(plan, 'axis2_sites', 0)}")
    emit(f"      …FLOATING tags (no fixed manifest)       {len(floating)}")
    live = [i for i in plan.instances if i.state == "live"]
    answered = [i for i in live if i.commit and not i.error]
    declared = [i for i in plan.instances if i.state != "live"]
    emit("")
    emit("    AXIS 3 — what the installations are RUNNING (/api/version, live)")
    emit(f"      installations the overlays declare       {len(plan.instances)}")
    emit(f"      …expected to answer                      {len(live)}")
    emit(f"      …that ANSWERED                           {len(answered)}")
    emit(f"      …that did NOT                            {len(live) - len(answered)}"
         + ("   🚨 INVENTORY INCOMPLETE" if len(live) != len(answered) else ""))
    emit(f"      …declared not-live in instances.json     {len(declared)}"
         + (f"  ({', '.join(f'{i.id}: {i.state}' for i in declared)})" if declared else ""))
    # 🚨 ITS OWN LINE, ALWAYS PRINTED. An installation whose images this lane cannot reach is
    # neither protected nor a failure, and those two already have lines — so without a third it
    # would be counted among the answered and read as covered. It is live, it answered, and
    # NOTHING here protects what it runs; that has to be legible without opening the roster.
    out_of_scope = [i for i in plan.instances if i.out_of_scope]
    # 🚨 IT SAYS UNVERIFIED, NOT PROTECTED. The declaration states only that this lane cannot lock
    # those images; `instances.json` and Doc/Architecture/ArtifactRetentionInterlock both record
    # that what retains that registry is NOT established. A summary line claiming the other
    # registry protects them would make a green run read as covered and support re-enabling
    # cleanup on a premise nobody has checked — the false green this mechanism exists to refuse,
    # committed by its own report.
    emit(f"      …OUT OF THIS REGISTRY'S SCOPE (declared)  {len(out_of_scope)}"
         + (f"  ({', '.join(f'{i.id} → {i.unlockable_registries[0]}' for i in out_of_scope)}"
            + " — NOT locked here, and whether anything retains that registry is UNVERIFIED)"
            if out_of_scope else ""))
    for instance in sorted(plan.instances, key=lambda i: i.id):
        if instance.state != "live":
            emit(f"        {instance.id:<18} {instance.state} — {instance.reason}")
        elif instance.out_of_scope:
            emit(f"        {instance.id:<18} core "
                 f"{(instance.commit or '???????')[:7]} — images in "
                 f"{', '.join(instance.unlockable_registries)}; NOT locked by this run, and "
                 "protection there is UNVERIFIED")
        elif instance.commit and not instance.error:
            emit(f"        {instance.id:<18} core {instance.commit[:7]} "
                 f"({instance.version or 'no version'}) → {len(instance.manifests)} manifest(s) "
                 "in its closure")
        else:
            emit(f"        {instance.id:<18} 🚨 NOT ACCOUNTED FOR — {instance.error}")
    # 🚨 THE OTHER REGISTRY'S PROTECTED SET, PRINTED — the dry run, and the only artifact a
    # lock-less registry can have (#4230). `distribution` has no `deleteEnabled`, so nothing can be
    # written INTO that registry ahead of a deleter; the derivation itself is the whole of the
    # protection, and a derivation nobody can read is not one. This deletes nothing, locks nothing
    # and needs no credential it does not already hold — it says what a cleanup there would have to
    # KEEP. Doc/Architecture/FleetRegistryRetention.
    if plan.foreign_references:
        emit("")
        emit("    PROTECTED SET — registries this lane cannot lock (dry run; nothing is written)")
        by_host: dict[str, list[tuple[str, str, str]]] = {}
        for host, repo, tag, where in plan.foreign_references:
            by_host.setdefault(host, []).append((repo, tag, where))
        # Read off the same record the planner classified from, so the label the reader sees and the
        # verdict the run reached cannot disagree.
        dispositions, _ = read_registry_dispositions(root)
        # 🚨 THE DISPOSITION IS PRINTED BESIDE EACH HOST, AND THE SENTENCE DIFFERS (#4323). The
        # first live run of this section said "a cleanup must KEEP" over `ghcr.io` — a host declared
        # `third-party`, where this fleet runs no cleanup at all. A report that asserts the same
        # thing about a store we retain and a store somebody else retains is wrong about one of them
        # whichever way the declaration reads.
        #
        # 🚨 And LISTING is what caught the deeper one, so it is stated as the reason: the same run
        # showed two of ghcr.io's five references are `systemorph/*` — OUR images, on a host whose
        # declaration says "never published by this fleet", while release.yml mirrors three
        # repositories there on every official release (#4323). A count alone would have hidden it.
        publications = read_registry_publications(root)
        for host in sorted(by_host):
            references = sorted(set(by_host[host]))
            disposition = dispositions.get(host, ("undeclared", ""))[0]
            # 🚨 A MIXED HOST GETS ITS REFERENCES SPLIT, NOT ONE SENTENCE OVER BOTH HALVES (#4323).
            # `ghcr.io` serves `systemorph/*` — ours, mirrored there by our own CD — beside
            # `distribution/*` and `oras-project/*`, which are not. Printing "declared NOT ours to
            # retain" over our own two chart-default references is the report asserting the very
            # thing that was false in the record, in the one artifact a reader checks it against.
            ours = sorted({(repo, tag, where) for repo, tag, where in references
                           if repo in publications.get(host, set())})
            if ours:
                emit(f"      {host} (declared {disposition}, and this fleet PUBLISHES here): "
                     f"{len(ours)} of {len(references)} committed reference(s) are OUR OWN "
                     "repositories — a MIRROR this fleet pushes and does not retain; the store's "
                     "operator keeps them, no cleanup of ours protects them, and no installation "
                     "may run from them (`publishes.runFrom`)")
                for repo, tag, where in ours:
                    emit(f"        {repo}:{tag}".ljust(52) + f" published by us — {where}")
                references = [reference for reference in references if reference not in ours]
                if not references:
                    continue
            if disposition == "fleet-unlockable":
                emit(f"      {host} (declared fleet-unlockable): {len(references)} committed "
                     "reference(s) a cleanup must KEEP")
            elif disposition == "third-party":
                emit(f"      {host} (declared third-party): {len(references)} committed "
                     "reference(s) — declared NOT ours to retain, so no cleanup of ours protects "
                     "them. Listed so the declaration can be checked against what is really pinned")
            else:
                # 🚨 THE FALLBACK IS NOT A KEEP SENTENCE (#4324 review). An UNDECLARED, malformed or
                # unreadable entry has already BLOCKED this run (`classify_foreign_registries`), so
                # the one thing the report must not do is print a verdict over it — an unknown that
                # reads as actionable is the whole failure mode of this family, and it would be the
                # shape a reader is most likely to act on: a list of references under "must KEEP",
                # produced by a run that refused.
                emit(f"      {host} (UNCLASSIFIED — {disposition}): {len(references)} committed "
                     "reference(s), and NO verdict about them. This host is not declared in "
                     f"`{ROSTER_PATH}`'s `registries` table, so the run is BLOCKED and nothing here "
                     "says whether anything retains these — declare it before reading this list as "
                     "anything")
            for repo, tag, where in references:
                # 🚨 The floating set, not the string `latest` (#4324 review). `extract_foreign_pins`
                # PRESERVES a moving tag, and `main`/`master`/`edge`/`stable`/`nightly` move exactly
                # as `latest` does — checking one spelling reports the other five as ordinary pins.
                # One set, shared with the ACR path, so the two cannot drift apart.
                moving = tag.strip().lower() in FLOATING_TAGS
                emit(f"        {repo}:{tag}"
                     + ("   🚨 a MOVING tag — outside this model (#3438)" if moving else ""))
                emit(f"          pinned by {where}")
        # 🚨 ITS OWN INCOMPLETENESS, ON ITS OWN LINE. This is the COMMITTED axis only. The set an
        # installation is RUNNING is derivable the same way it is here (the mirror pushes the
        # identical manifest under the identical tag and proves it by read-back), and the PLUGIN
        # BUNDLE family — plugins/<source>/<package> — is not derivable at all today: nothing
        # enumerates what that registry holds as data, and nothing records a last-pulled signal.
        # A protected set complete for one artifact family and empty for the other, reported as one
        # number, is this whole mechanism's failure mode committed by its own report (#4066).
        emit("      🚨 COMMITTED PINS ONLY. The running set is derivable and not derived here; the")
        emit("         PLUGIN BUNDLE family (plugins/<source>/<package>) is NOT derivable today —")
        emit("         nothing enumerates what that registry HOLDS as data (#4066). So this is a")
        emit("         floor, never a complete protected set, and no cleanup may run against it.")

    emit("")
    emit("    UNION — fleet pins and official release manifests")
    emit(f"      distinct manifests wanted                {len(plan.wanted)}")
    emit(f"      …already protected (deleteEnabled false) {len(plan.already_protected)}")
    emit(f"      …TO LOCK                                 {len(plan.to_lock)}")
    emit(f"      …platform manifests of an index in it     {plan.platform_manifests}")
    emit(f"      …that could NOT be protected             "
         f"{len([w for w in plan.wanted if w.problem])}")
    emit("")
    emit("")
    emit("    TAG REFERENCES — the OTHER lock, and the one the purge reads to delete a tag")
    emit(f"      tag references depended on               {len(plan.wanted_tags)}")
    emit(f"      …already protected (deleteEnabled false) {len(plan.tags_already_protected)}")
    emit(f"      …TO LOCK                                 {len(plan.tags_to_lock)}")
    emit(f"      …that do NOT resolve                     "
         f"{len([t for t in plan.wanted_tags if t.problem])}")
    emit("")
    emit("    THE WINDOW THIS IS RACING — the enabled purge steps, as recorded")
    windows, window_problem = retention_windows(root)
    for line in windows:
        emit(f"      {line}")
    if window_problem:
        emit(f"      🚨 {window_problem} (the verdict on the record is the "
             "--check-retention-record step)")
    emit("")
    emit("    REGISTRY")
    emit(f"      manifests in the registry                {total_manifests}")
    emit(f"      …currently locked (deleteEnabled false)  {locked_now}")
    emit(f"      …locked and pinned by NOTHING            {len(plan.release_candidates)}")
    emit("")

    for scan in axis1:
        if scan.unreadable:
            emit(f"  A1 {scan.gh_repo}: UNREADABLE — {scan.unreadable}")
    for scan in axis2:
        if scan.unreadable:
            emit(f"  A2 {scan.gh_repo}: UNREADABLE — {scan.unreadable}")

    for entry in sorted(plan.already_protected, key=lambda w: (w.acr_repo, w.digest or "")):
        emit(f"  PROTECTED  {entry.acr_repo}@{(entry.digest or '')[:26]}…")
        for source in entry.sources:
            emit(f"               ← {source}")
    for entry in sorted(plan.to_lock, key=lambda w: (w.acr_repo, w.digest or "")):
        emit(f"  {'LOCK      ' if apply else 'WOULD LOCK'} "
             f"{entry.acr_repo}@{(entry.digest or '')[:26]}…")
        for source in entry.sources:
            emit(f"               ← {source}")
    for entry in sorted([w for w in plan.wanted if w.problem], key=lambda w: w.acr_repo):
        emit(f"  UNPROTECTABLE {entry.acr_repo}"
             + (f":{entry.tag}" if entry.tag else f"@{(entry.digest or '')[:26]}…"))
        emit(f"               {entry.problem}")
        for source in entry.sources:
            emit(f"               ← {source}")
    for gh_repo, repo, tag, where in floating:
        emit(f"  FLOATING   {repo}:{tag}  ← {gh_repo} {where}")
        emit("               a moving tag has no fixed manifest to protect; not locked.")

    emit("")
    if release_enabled:
        emit(f"  release arm: ENABLED by MW_ACR_RELEASE_UNPINNED / --release-unpinned; "
             f"{plan.released_now} released this run.")
    else:
        value = os.environ.get("MW_ACR_RELEASE_UNPINNED")
        emit("  release arm: DISABLED — MW_ACR_RELEASE_UNPINNED is "
             + (f"{value!r}, not 'true'." if value is not None else "unset."))
    emit("  Unlocking is the only direction that can DESTROY data, and it is unsafe in exactly the")
    emit("  case where this extractor is wrong: a pin it cannot see is spelled identically to a pin")
    emit("  that is not there. The candidates below are REPORTED so the stale-lock debt is visible.")
    for manifest in plan.release_candidates:
        emit(f"  RELEASE CANDIDATE  {manifest.acr_repo}@{manifest.digest[:26]}…  "
             f"tags={manifest.tags or '(none)'}")
        emit("               🚨 a lock a PERSON placed for a reason no pin scan can see — a pin "
             "that is about to exist — looks exactly like this. Review before releasing.")
    if not plan.release_candidates:
        emit("  (none — every locked manifest is pinned by something this run could see.)")
    emit("")


# ── Applying ───────────────────────────────────────────────────────────────────────────────────


# The registry's words for "this credential may not write manifest attributes". Distinguished from
# every other failure because the REMEDY is completely different — a role assignment, not a retry —
# and because it fails identically for every manifest, so repeating it N times buries the one line
# that says what to do.
DENIED_MARKERS = ("authorizationfailed", "authorization failed", "denied", "forbidden",
                  "403", "not authorized", "insufficient", "unauthorized")

GRANT_HELP = [
    "LOCKING IS A WRITE. `az acr repository update` needs the data action",
    "`Microsoft.ContainerRegistry/registries/repositories/metadata/write`, which is in the",
    "`Container Registry Repository Writer` role (and in Contributor/Owner). Measured 2026-09-07:",
    "neither AcrPull nor AcrPush contains it — AcrPush is pull/read + push/write and nothing else —",
    "so the OIDC identity the read-only pinned-digest sweep uses CANNOT lock. This is a",
    "PROVISIONING task, not a workflow defect:",
    "",
    "    az role assignment create --assignee <the AZURE_CLIENT_ID app's object id> \\",
    "      --role 'Container Registry Repository Writer' \\",
    "      --scope /subscriptions/<sub>/resourceGroups/meshweaver-shared/providers/\\",
    "Microsoft.ContainerRegistry/registries/meshweaver",
    "",
    "Until it is granted, retention can still delete every manifest listed above.",
]


def apply_locks(plan: Plan, registry: Registry) -> list[str]:
    """Lock every manifest that needs it. Locks are applied even on a degraded run — a lock cannot
    destroy anything, and protecting what WAS found is strictly better than protecting nothing."""
    failures: list[str] = []
    for index, entry in enumerate(plan.to_lock):
        assert entry.digest is not None
        ok, detail = registry.set_delete_enabled(entry.acr_repo, entry.digest, False)
        if ok:
            # 🚨 THE WRITE'S EXIT CODE IS NOT THE POSTCONDITION. Read the attribute back and require
            # it to actually be `false`; an INDETERMINATE read (`None`) is not a confirmation
            # either. Without this the job's central claim — "N manifests are protected" — rested
            # on `az` having accepted a request, which is the one failure mode that would leave
            # every lock decoration while the report stayed green.
            # `delete_enabled`, named for what it HOLDS rather than for what it implies: PROTECTED
            # is `delete_enabled is False`, and a local called `protected` holding the opposite
            # polarity is how a future reader talks themselves into `== True` being success.
            # `None` is INDETERMINATE and is neither.
            delete_enabled, why = registry.read_delete_enabled(entry.acr_repo, entry.digest)
            if delete_enabled is False:
                plan.locked_now += 1
                print(f"locked {entry.acr_repo}@{entry.digest}")
                continue
            failures.append(
                f"{entry.acr_repo}@{entry.digest}: the lock WRITE succeeded and the manifest still "
                + ("reads deleteEnabled=true" if delete_enabled is True
                   else f"cannot be confirmed locked ({why})")
                + ". It is pinned by "
                + f"{', '.join(entry.sources[:3])} and the 03:00 purge can still delete it. A write "
                "that returns 0 without taking is exactly what this read-back exists to catch."
            )
            continue
        if any(marker in detail.lower() for marker in DENIED_MARKERS):
            # 🚨 STOP ON THE FIRST REFUSAL. Every remaining lock fails the same way for the same
            # reason, and N identical errors bury the ONE line that says which role to grant.
            remaining = plan.to_lock[index:]
            failures.append(
                "the credential is not allowed to lock a manifest. "
                f"{plan.locked_now} of {len(plan.to_lock)} were locked before the refusal. "
                f"az said: {detail}"
            )
            failures.extend(GRANT_HELP)
            failures.append(
                f"{len(remaining)} manifest(s) left unprotected: "
                + ", ".join(f"{w.acr_repo}@{(w.digest or '')[:19]}…" for w in remaining)
            )
            break
        failures.append(
            f"could not lock {entry.acr_repo}@{entry.digest}: {detail}. It is pinned by "
            f"{', '.join(entry.sources[:3])} and the 03:00 purge can still delete it."
        )
    return failures


def apply_tag_locks(plan: Plan, registry: Registry) -> list[str]:
    """Lock every tag reference the fleet depends on, with the same read-back as the manifests.

    Same asymmetry as `apply_locks`: applied on a degraded run, because a lock destroys nothing and
    protecting what WAS found beats protecting nothing."""
    failures: list[str] = []
    for index, entry in enumerate(plan.tags_to_lock):
        ok, detail = registry.set_tag_delete_enabled(entry.acr_repo, entry.tag, False)
        if ok:
            delete_enabled, why = registry.read_tag_delete_enabled(entry.acr_repo, entry.tag)
            if delete_enabled is False:
                plan.tags_locked_now += 1
                print(f"locked tag {entry.acr_repo}:{entry.tag}")
                continue
            failures.append(
                f"{entry.acr_repo}:{entry.tag}: the tag lock WRITE succeeded and the tag still "
                + ("reads deleteEnabled=true" if delete_enabled is True
                   else f"cannot be confirmed locked ({why})")
                + ". It is depended on by " + f"{', '.join(entry.sources[:3])}, and the 03:00 purge "
                "can still delete the REFERENCE even where the manifest is locked.")
            continue
        if any(marker in detail.lower() for marker in DENIED_MARKERS):
            remaining = plan.tags_to_lock[index:]
            failures.append(
                "the credential is not allowed to lock a TAG. "
                f"{plan.tags_locked_now} of {len(plan.tags_to_lock)} were locked before the "
                f"refusal. az said: {detail}")
            failures.extend(GRANT_HELP)
            failures.append(f"{len(remaining)} tag reference(s) left unprotected: "
                            + ", ".join(f"{t.acr_repo}:{t.tag}" for t in remaining))
            break
        failures.append(f"could not lock tag {entry.acr_repo}:{entry.tag}: {detail}. It is "
                        f"depended on by {', '.join(entry.sources[:3])}.")
    return failures


def extractor_control() -> list[str]:
    """🚨 THE INSTRUMENT, MEASURED ON EVERY RUN — not inferred from what the fleet happens to say.

    Two known digest pins in a fixture, through the SAME `extract()` the sweep uses. A fleet-shaped
    assertion ("six repositories pin, so zero is impossible") expires the day the fleet changes, and
    this one did, reddening the protection lane on 2026-09-12 two hours before the purge. This one
    cannot: it fails exactly when the digest matcher stops matching, whatever the fleet is doing."""
    sites, _lanes, malformed, _set, _refs = consistency.extract("ci.yml", FIXTURE_WORKFLOW)
    found = {(site.role, site.value) for site in sites if site.kind == "digest"}
    expected = {("mw-plugin-test", TESTER), ("memex-portal-ai", PORTAL)}
    problems: list[str] = []
    if not expected <= found:
        problems.append(
            "THE DIGEST EXTRACTOR STOPPED MATCHING. Its control fixture declares "
            f"{len(expected)} digest pins and `extract()` returned {sorted(found)}. Every "
            "'no pins found' answer this run makes is therefore worthless — including the ones "
            "that would otherwise read as a fleet that stopped pinning. Run --self-test.")
    if malformed:
        problems.append(f"the extractor control fixture reported {len(malformed)} malformed "
                        "declaration(s); it declares none.")
    return problems


def apply_releases(plan: Plan, registry: Registry) -> list[str]:
    failures: list[str] = []
    for manifest in plan.release_candidates:
        ok, detail = registry.set_delete_enabled(manifest.acr_repo, manifest.digest, True)
        if ok:
            plan.released_now += 1
            print(f"released {manifest.acr_repo}@{manifest.digest}")
        else:
            failures.append(f"could not release {manifest.acr_repo}@{manifest.digest}: {detail}")
    return failures


# ── Inventory ──────────────────────────────────────────────────────────────────────────────────


def read_inventory(registry: Registry) -> tuple[dict[str, list[Manifest]], dict[str, str]]:
    inventory: dict[str, list[Manifest]] = {}
    errors: dict[str, str] = {}
    repos, error = registry.repositories()
    if repos is None:
        errors["*"] = error
        return inventory, errors
    for acr_repo in sorted(repos):
        manifests, error = registry.manifests(acr_repo)
        if manifests is None:
            errors[acr_repo] = error
            continue
        inventory[acr_repo] = manifests
    return inventory, errors


# ── Entry point ────────────────────────────────────────────────────────────────────────────────


def run(repos: list[str], registry_name: str, apply: bool, release_enabled: bool,
        local_root: str | None) -> int:
    registry = Registry(registry_name)
    registry.control_probe()          # dies red if the registry does not answer

    axis1 = []
    axis2: list[OverlayScan] = []
    for gh_repo in repos:
        if local_root:
            axis1.append(consistency.scan_local(local_root, gh_repo))
            axis2.append(scan_overlays_local(local_root, gh_repo, registry_name))
        else:
            axis1.append(consistency.scan_remote(gh_repo))
            axis2.append(scan_overlays_remote(gh_repo, registry_name))

    inventory, inventory_errors = read_inventory(registry)
    plan = build_plan(axis1, axis2)

    # 🚨 THE INSTRUMENT BEFORE THE MEASUREMENT. Every "no pins here" answer below is only worth the
    # extractor that produced it, so the extractor is exercised against known input first.
    plan.blockers.extend(extractor_control())

    # AXIS 3 — the installations, asked what they are RUNNING rather than what they should run.
    roster, roster_problems = read_instance_roster(local_root or ".")
    plan.blockers.extend(roster_problems)
    dispositions, disposition_problems = read_registry_dispositions(local_root or ".")
    plan.blockers.extend(disposition_problems)
    plan.instances, instance_blockers = build_instances(axis2, roster)
    plan.blockers.extend(instance_blockers)
    if not plan.instances and not any(scan.unreadable for scan in axis2):
        plan.blockers.append(
            "AXIS 3 found ZERO installations across the whole fleet. Three overlays declared one "
            "on 2026-09-12 (memex, memex-cloud, pearl), so zero means the overlay reader stopped "
            "finding `Hosting__Deployment` — never that the fleet has no installations. "
            "Run --self-test.")
    repositories_of = {
        instance.id: sorted({repo for scan in axis2 for repo, _tag, where in scan.pins
                             if f"{scan.gh_repo} {where}" == instance.source})
        for instance in plan.instances
    }

    # 🚨 AXIS 3 FIRST. `resolve_and_classify` is what splits `plan.wanted` into already-protected
    # and to-lock, so anything added to `wanted` after it runs is wanted by nobody who locks.
    classify_foreign_registries(plan, dispositions,
                                read_registry_publications(local_root or "."))
    resolve_running_sets(plan, inventory, repositories_of)
    resolve_and_classify(plan, registry, inventory, inventory_errors)
    classify_tags(plan, registry)
    plan.inventory_complete = not plan.blockers

    failures: list[str] = []
    if apply:
        failures.extend(apply_locks(plan, registry))
        failures.extend(apply_tag_locks(plan, registry))

    # 🚨 RELEASING NEEDS A COMPLETE INVENTORY, NOT MERELY A TIDY RUN. `plan.blockers` already
    # carries every installation that did not answer, so an incomplete consumer inventory blocks
    # the unlock arm by construction — and that is the interlock #3859 asks for, stated where it is
    # decided rather than left to be inferred from an error list.
    release_blocked = bool(plan.blockers) or bool(failures) or not plan.inventory_complete
    if release_enabled and apply and not release_blocked:
        failures.extend(apply_releases(plan, registry))

    report(plan, axis1, axis2, registry_name, inventory, apply, release_enabled,
           local_root or ".")

    if release_enabled and release_blocked:
        print("::error::the release arm is enabled but this run is DEGRADED, so nothing was "
              "released.")
        print("  A release on a partial sweep is the outage this job exists to prevent: a pin that "
              "was not read is indistinguishable from a pin that is not there.")

    for blocker in plan.blockers:
        print(f"::error::{blocker}")
    for failure in failures:
        print(f"::error::{failure}")

    if plan.blockers or failures:
        return 1
    if apply:
        emit(f"Protected {len(plan.already_protected) + plan.locked_now} manifest(s) "
             f"({plan.locked_now} newly locked this run) and "
             f"{len(plan.tags_already_protected) + plan.tags_locked_now} tag reference(s) "
             f"({plan.tags_locked_now} newly locked); {plan.released_now} released.")
    else:
        emit(f"{len(plan.already_protected)} manifest(s) and "
             f"{len(plan.tags_already_protected)} tag reference(s) already protected; "
             f"{len(plan.to_lock)} manifest(s) and {len(plan.tags_to_lock)} tag(s) would be "
             "locked. No registry mutation was made.")
    return 0


# ── The retention record: a credential-free assertion that runs on every pull request ──────────

# 🚨 THE DECIDED WINDOW FOR CONTINUOUS ARTIFACTS, and it is not a preference of this script.
# The platform owner's rule (#3842, quoted verbatim in #3438's body and in #3859's acceptance
# list): *"Retain unreferenced continuous artifacts for at least 30 days by age, without a
# build-count quota."* Two code paths already CLAMP to it rather than default to it —
# `PrebuiltBundleRetention` (src/MeshWeaver.Hosting/PrebuiltBundleRetention.cs, `MinimumAge` and
# the `< 30 days ⇒ 30 days` clamp in its planner) and `AssemblyCacheRetention`
# (src/MeshWeaver.Compiler.Pipeline/AssemblyCacheRetention.cs, the same clamp) — and both carry
# `KeepNewestPerSource` as a property that explicitly NO LONGER DRIVES DELETION.
#
# The registry lane was the third store and the one nobody asserted. #3843's own body says so:
# *"The ACR task record and live cloud cleanup are unchanged."* So the recorded purge kept
# `--ago 7d --keep 10` over five continuously-republished repositories — `memex-portal-ai`
# included, which is the image BOTH production portals run — while the rule that governs the
# other two stores said 30 days and no quota. Nothing compared them.
#
# 🚨 `--keep` IS THE BUILD-COUNT QUOTA, AND IT IS THE HALF THE AGE WINDOW CANNOT REPLACE. `--keep N`
# counts NEWER BUILDS, so the more often a repository is republished the faster its older manifests
# become eligible — which is why frequent republishing DESTROYS a pin rather than protecting it
# (#3438's own root cause). A 30-day `--ago` beside a `--keep 10` still collects a manifest ten
# builds old on the day it is written. Both halves, or neither is the rule.
MINIMUM_PURGE_AGE_DAYS = 30
AGO_UNIT_DAYS = {"d": 1.0, "h": 1.0 / 24, "m": 1.0 / 1440, "s": 1.0 / 86400}
AGO_TOKEN_RE = re.compile(r"(\d+)([dhms])")


def parse_ago_days(value: str) -> float | None:
    """`--ago` as DAYS, or None when it cannot be read.

    🚨 UNPARSEABLE IS None, NEVER ZERO AND NEVER A DEFAULT. The caller reds on None. A duration
    this function cannot read is one nobody has checked, and folding it into a number would spell
    "not measured" exactly like "measured and fine" — the confusion #3438 is made of."""
    text = (value or "").strip()
    if not text:
        return None
    consumed = 0
    days = 0.0
    for amount, unit in AGO_TOKEN_RE.findall(text):
        consumed += len(amount) + len(unit)
        days += int(amount) * AGO_UNIT_DAYS[unit]
    # Every character must have been part of a token, or something was silently ignored.
    return days if consumed == len(text) and days > 0 else None


# 🚨 TOKENS, NOT TEXT — and the reason is that both halves of this check are defeated by quoting.
#
#   * `acr purge --include-"locked" …` EXECUTES with the option `--include-locked` (the shell joins
#     the quoted fragment) and matches no grep for that string. That flag deletes every manifest the
#     lock protects, so a text search is the wrong instrument for the one invariant this whole lane
#     rests on.
#   * `echo "acr purge --filter 'a:.*' --ago 30d"` contains the text `acr purge` and deletes
#     nothing, so a text search reports a compliant purge where no purge exists.
#
# A `cmd:` is a SHELL line: it may hold several commands, and a quoted fragment is DATA. So it is
# tokenized the way a shell would, split on the operators, and only a command whose first two
# tokens are literally `acr` and `purge` is a purge. Every one of them is then checked — a second
# invocation carrying `--ago 7d` must not ride in behind a compliant first.
SHELL_OPERATORS = {";", "&&", "||", "|", "&", "(", ")", "\n"}


def purge_invocations(step: str) -> tuple[list[list[str]], str]:
    """Every real `acr purge` command in one `cmd:` line, as token lists; plus a parse error."""
    command = step.split("cmd:", 1)[1] if "cmd:" in step else step
    lexer = shlex.shlex(command, posix=True, punctuation_chars=True)
    lexer.whitespace_split = True
    try:
        tokens = list(lexer)
    except ValueError as exc:
        # Unbalanced quotes. NOT "no purge here": the step could not be read at all.
        return [], f"the step could not be tokenized ({exc}), so nothing in it was checked"
    commands: list[list[str]] = []
    current: list[str] = []
    for token in tokens:
        if token in SHELL_OPERATORS:
            commands.append(current)
            current = []
        else:
            current.append(token)
    commands.append(current)
    return [c for c in commands if len(c) >= 2 and c[0] == "acr" and c[1] == "purge"], ""


def option_value(tokens: list[str], name: str) -> tuple[bool, str | None]:
    """(present, value) for `--name value` and `--name=value`; value is None for a bare flag."""
    for index, token in enumerate(tokens):
        if token == name:
            following = tokens[index + 1] if index + 1 < len(tokens) else None
            return True, (None if following is None or following.startswith("-") else following)
        if token.startswith(name + "="):
            return True, token.split("=", 1)[1] or None
    return False, None


def check_window_policy(where: str, step: str) -> list[str]:
    """Does ONE recorded `acr purge` step obey the decided continuous-artifact window?"""
    invocations, parse_error = purge_invocations(step)
    if parse_error:
        return [f"{where}: {parse_error}:\n      {step.strip()[:160]}"]
    if not invocations:
        # The caller already reds on a `cmd:` that is not an `acr purge`; this is the belt, so a
        # reshaped line cannot silently skip the window check while passing that one.
        return [f"{where}: no `acr purge` command could be read from this step, so its window was "
                f"NOT checked:\n      {step.strip()[:160]}"]
    problems: list[str] = []
    for tokens in invocations:
        problems.extend(check_one_invocation(where, step, tokens))
    return problems


def check_one_invocation(where: str, step: str, tokens: list[str]) -> list[str]:
    problems: list[str] = []
    has_ago, ago_value = option_value(tokens, "--ago")
    if has_ago and not ago_value:
        return [f"{where}: a purge step carries a bare `--ago` with no duration, so the window was "
                f"NOT checked:\n      {step.strip()[:160]}"]
    if not has_ago:
        problems.append(
            f"{where}: a purge step declares NO `--ago` window:\n      {step.strip()[:160]}\n"
            "    acr purge then has no age floor at all, so a manifest is eligible the moment it "
            "is superseded. The decided rule is at least "
            f"{MINIMUM_PURGE_AGE_DAYS} days by age (#3842).")
    else:
        days = parse_ago_days(ago_value)
        if days is None:
            problems.append(
                f"{where}: `--ago {ago_value}` could not be read as a duration, so the window was "
                "NOT checked. An unreadable window is not a satisfied one.")
        elif days < MINIMUM_PURGE_AGE_DAYS:
            problems.append(
                f"{where}: a purge step retains for `--ago {ago_value}` "
                f"({days:g} day(s)), under the decided floor of {MINIMUM_PURGE_AGE_DAYS} days:\n"
                f"      {step.strip()[:160]}\n"
                "    #3842: *retain unreferenced continuous artifacts for at least 30 days by "
                "age*. PrebuiltBundleRetention and AssemblyCacheRetention already CLAMP to that "
                "floor; this record is the third store and the only one nobody asserted.")
    has_keep, keep_value = option_value(tokens, "--keep")
    if has_keep:
        problems.append(
            f"{where}: a purge step carries a BUILD-COUNT QUOTA "
            f"`--keep{('=' + keep_value) if keep_value else ' (bare)'}`:\n"
            f"      {step.strip()[:160]}\n"
            "    #3842 rules one out in as many words — *without a build-count quota* — and it is "
            "the half an age window cannot replace: `--keep` counts NEWER BUILDS, so the more "
            "often a repository is republished the FASTER its older manifests become eligible. "
            "That is #3438's own root cause, not a second-order concern.")
    return problems


def check_in_force_is_boolean(manifest: dict, block: str) -> list[str]:
    """🚨 A DECLARATION WHOSE SWITCH IS NOT A BOOLEAN IS FAIL-OPEN, AND SILENTLY.

    Every reader of these blocks asks `inForce is True`, which is correct for a JSON boolean and
    catastrophic for `"true"`: a string is not `True`, so a record that LOOKS like an in-force pause
    reads as no pause at all — `apply` proceeds past the interlock, `record` skips the overwrite
    guard, and this very coherence check passes an `Enabled` task sitting under an apparent pause.
    A typed quote mark would disarm three guards at once and every one of them would report success.

    So the SHAPE is asserted here, on every pull request, before any of them reads the value. The
    shell halves fail closed on the same condition rather than trusting this to have run."""
    # 🚨 ABSENT AND NULL ARE DIFFERENT FACTS. `manifest.get(block)` answers None for both, so a
    # written `"pause": null` — a present block declaring nothing — would read exactly like a record
    # that never had one, and every reader below it would walk past. Only a MISSING key means "no
    # declaration"; a present null is a malformed one.
    if block not in manifest:
        return []
    block_value = manifest[block]
    if block_value is None:
        return [f"tasks.json: `{block}` is present and null. A block written as null declares "
                "nothing while looking like a declaration; delete the key or fill it in."]
    if not isinstance(block_value, dict):
        return [f"tasks.json: `{block}` is {type(block_value).__name__}, not an object. Every "
                "reader of it asks for fields it cannot have."]
    if "inForce" not in block_value:
        return [f"tasks.json: `{block}` has no `inForce`. Every reader treats its absence as NOT "
                "in force, so a declaration written without it silently declares nothing."]
    if not isinstance(block_value["inForce"], bool):
        return [f"tasks.json: `{block}.inForce` is {block_value['inForce']!r}, not a JSON boolean. "
                "`\"true\"` is not `true`: every reader asks `is True`, so a quoted value disarms "
                "the guard while reading, to a human, as if it were armed."]
    return []


def check_pause_coherence(manifest: dict) -> list[str]:
    """Is the record's pause DECLARATION consistent with the statuses it is recorded beside?

    🚨 A PAUSE IS A CLAIM ABOUT THE REGISTRY, AND THE RECORD CAN CONTRADICT IT SILENTLY.
    `retention_windows` prints "PAUSED" only when it finds no ENABLED step, so a task flipped to
    `Enabled` while `pause.inForce` is still true makes the report print windows and never mention
    the pause — a record asserting both that cleanup is stopped and that it runs at 03:00, with
    nothing red. The reverse is the shape that goes stale: every task Disabled and no declaration,
    which reads identically to a retention that silently stopped.
    """
    problems: list[str] = []
    problems.extend(check_in_force_is_boolean(manifest, "pause"))
    problems.extend(check_in_force_is_boolean(manifest, "recordAheadOfRegistry"))
    pause = manifest.get("pause") or {}
    in_force = pause.get("inForce") is True
    tasks = manifest.get("tasks") or []
    enabled = [str(task.get("name")) for task in tasks
               if str(task.get("status", "")).lower() == "enabled"]
    if in_force:
        for field_name in ("since", "reason", "reEnableWhen"):
            if not str(pause.get(field_name, "")).strip():
                problems.append(
                    f"tasks.json: `pause.inForce` is true with no `{field_name}`. A pause with no "
                    f"{field_name} is indistinguishable from a retention that silently stopped — "
                    "which is the state #3438 spent a week telling apart from a policy.")
        if enabled:
            problems.append(
                f"tasks.json: `pause.inForce` is true and {enabled} is recorded `Enabled`. The "
                "record then asserts both that cleanup is stopped and that it runs on its "
                "schedule, and the report prints the window without ever mentioning the pause. "
                "Lift the pause deliberately (delete the block, citing what satisfied "
                "`reEnableWhen`) or record the task as Disabled — never both.")
    elif not enabled:
        problems.append(
            "tasks.json: every recorded task is Disabled and no in-force `pause` explains it. A "
            "stopped retention and a stale record read identically, and only one of them is a "
            "decision. Declare the pause (`inForce`/`since`/`reason`/`reEnableWhen`) or record "
            "the enabled task.")
    return problems


def retention_windows(root: str) -> tuple[list[str], str]:
    """The ENABLED purge steps this protection is racing, as recorded — the "over what window" half
    of the denominator.

    A protection report that does not name what it protects against leaves the reader to go and
    look, and the two numbers that decide whether a lock was needed at all (`--ago`, `--keep`) live
    in a cloud-only task whose only committed copy is this record. The VERDICT on the record is the
    separate `--check-retention-record` step, which reds; this only reads it for the report, so an
    unreadable record prints as unreadable here rather than being quietly omitted."""
    record = Path(root) / ".github" / "acr-retention"
    manifest_path = record / "tasks.json"
    if not manifest_path.is_file():
        return [], f"{manifest_path} is not there — the window cannot be stated"
    try:
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        return [], f"{manifest_path} could not be read ({exc}) — the window cannot be stated"
    lines: list[str] = []
    for task in manifest.get("tasks") or []:
        if str(task.get("status", "")).lower() != "enabled":
            continue
        yaml_path = record / str(task.get("file", ""))
        if not yaml_path.is_file():
            lines.append(f"{task.get('name')}: its recorded definition is missing")
            continue
        for step in re.findall(r"^\s*-\s+cmd:\s*(.+)$", yaml_path.read_text(encoding="utf-8"),
                               re.MULTILINE):
            filters = " ".join(re.findall(r"--filter '([^']+)'", step)) or "?"
            ago = (re.search(r"--ago (\S+)", step) or [None, "?"])[1]
            keep = (re.search(r"--keep (\S+)", step) or [None, "none"])[1]
            lines.append(f"{task.get('schedule', '?')}  {filters}  --ago {ago} --keep {keep}")
    if not lines:
        # 🚨 "NO ENABLED PURGE" IS A FACT WHEN IT IS DECLARED AND A DEFECT WHEN IT IS NOT — the same
        # rule the instance roster follows. On 2026-09-12 the true cause became the third one: the
        # purge was deliberately PAUSED while protection was incomplete (Memex#219), so the window
        # this run is racing is currently none. That is the good news, and printing it as a problem
        # would teach the reader to ignore the line.
        pause = manifest.get("pause") or {}
        if pause.get("inForce") is True and pause.get("since") and pause.get("reEnableWhen"):
            return ([f"PAUSED since {pause['since']} — no purge step runs; this protection is not "
                     "racing a clock.",
                     f"re-enable when: {pause['reEnableWhen']}"], "")
        return [], ("no ENABLED purge step is recorded and the record declares no in-force `pause` "
                    "with a `since` and a `reEnableWhen` — so this is a stale record or a silently "
                    "stopped retention, not a stated one")
    return lines, ""


def check_retention_record(root: str) -> int:
    """Does the committed record still describe a retention policy the LOCK can protect against?

    🚨 THE LOCK'S ENTIRE VALUE RESTS ON ONE PROPERTY OF THE PURGE: `acr purge` skips locked
    manifests unless `--include-locked` is passed. Add that flag to a step and every lock this job
    places becomes decoration — silently, with this job still reporting that it protected N
    manifests. That is the failure mode worth a gate, and this one needs NO credential: it reads
    `.github/acr-retention/`, the committed record of the live task definitions.

    It does NOT compare against the live registry. That needs `registries/tasks/read`, a strictly
    larger grant than the lock itself uses, and the live comparison is a maintainer's manual
    instrument (`.github/scripts/acr-retention-tasks.sh verify`). What this covers is the half a
    pull request can actually break: the record.
    """
    record = Path(root) / ".github" / "acr-retention"
    problems: list[str] = []
    if not record.is_dir():
        print(f"::error::{record} does not exist — the retention record is the only copy of the "
              "purge tasks' reasoning; the tasks themselves are cloud-only (`contextPath: null`).")
        return 1
    release_workflow = Path(root) / ".github" / "workflows" / "release.yml"
    if not release_workflow.is_file():
        problems.append("release.yml is missing; official image protection scope could not be checked")
    else:
        published_repos = {
            repo for group in re.findall(r"for repo in ([^;\n]+); do",
                                         release_workflow.read_text(encoding="utf-8"))
            for repo in group.split()
        }
        if published_repos != OFFICIAL_RELEASE_REPOSITORIES:
            problems.append(
                "release.yml image repositories differ from official retention protection: "
                f"publisher={sorted(published_repos)}, protection={sorted(OFFICIAL_RELEASE_REPOSITORIES)}")
    manifest_path = record / "tasks.json"
    if not manifest_path.is_file():
        print(f"::error::{manifest_path} is missing.")
        return 1
    try:
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as exc:
        print(f"::error::{manifest_path} is not JSON: {exc}")
        return 1

    tasks = manifest.get("tasks") or []
    steps_seen = 0
    for task in tasks:
        yaml_path = record / task.get("file", "")
        if not yaml_path.is_file():
            problems.append(f"tasks.json names {task.get('file')!r} for task "
                            f"{task.get('name')!r}, and that file is not in the record.")
            continue
        text = yaml_path.read_text(encoding="utf-8")
        steps = [line for line in text.splitlines() if re.match(r"^\s*-\s+cmd:", line)]
        steps_seen += len(steps)
        if not steps:
            problems.append(f"{yaml_path.name} declares no `cmd:` step — a recorded task with no "
                            "step is a record of nothing.")
        for step in steps:
            # 🚨 THE `cmd:` LINES, NOT THE FILE — and TOKENS, not the text of the line. Both
            # recorded definitions EXPLAIN in a comment that they do not pass `--include-locked`,
            # and a whole-file grep fires on that prose and reds over the sentence saying the guard
            # is satisfied (measured 2026-09-07). A substring search on the line has the opposite
            # defect: `--include-"locked"` EXECUTES as that option and matches no such search. Both
            # are answered by reading the step the way a shell would.
            purges, parse_error = purge_invocations(step)
            if parse_error:
                problems.append(f"{yaml_path.name}: {parse_error}: {step.strip()[:120]}")
            if any("--include-locked" in tokens for tokens in purges):
                problems.append(
                    f"{yaml_path.name} has a purge step passing --include-locked:\n"
                    f"      {step.strip()[:160]}\n"
                    "    That flag DELETES LOCKED MANIFESTS, which is the entire protection "
                    "lock-pinned-digests.py provides. Every pin the fleet holds becomes purgeable "
                    "again, silently, while the lock job keeps reporting that it protected them.")
            if "acr purge" not in step:
                problems.append(f"{yaml_path.name} has a `cmd:` step that is not an `acr purge`: "
                                f"{step.strip()[:120]} — this record is for retention tasks.")
            # 🚨 EVERY RECORDED STEP, not only the enabled ones. The record is what
            # `acr-retention-tasks.sh apply` PUSHES, so a disabled task carrying a 7-day window is
            # a 7-day window one command away from running — which is exactly the state this
            # record was in while the pause held it off.
            problems.extend(check_window_policy(yaml_path.name, step))

    # 🚨 THE DENOMINATOR. Zero recorded tasks, or zero steps across them, reads exactly like a
    # clean record while having inspected nothing — the confusion #3438 is made of.
    print(f"retention record: {len(tasks)} task(s), {steps_seen} purge step(s) inspected.")
    if not tasks:
        problems.append("tasks.json records ZERO tasks. The registry had two on 2026-09-07, so "
                        "zero means the record was emptied, not that retention stopped.")
    if tasks and steps_seen == 0:
        problems.append("ZERO purge steps were found across every recorded task, so the "
                        "--include-locked assertion inspected nothing.")

    problems.extend(check_pause_coherence(manifest))

    for problem in problems:
        print(f"::error::{problem}")
    if problems:
        return 1
    print("  no recorded purge step passes --include-locked, so `acr purge` skips locked "
          "manifests — which is what makes a lock a protection.")
    print(f"  every recorded step retains for at least {MINIMUM_PURGE_AGE_DAYS} days by age with "
          "no --keep build-count quota (#3842), and the record's pause declaration agrees with "
          "the statuses it is recorded beside.")
    return 0


# ── The SECOND question about a registry: what DELETES from it (#4230) ─────────────────────────
#
# `disposition` answers "can THIS lane lock that registry". It says nothing whatever about whether
# anything deletes from it, and for `cr.meshweaver.cloud` — the fleet's OWN registry, the default
# for newly provisioned instances — the answer to the second question was *established by nothing*
# until 2026-09-14. The `registries` table is the unit that can be shown COMPLETE, so it is where
# the second question is asked too: every entry carries a `retention` block, and a registry joining
# the fleet cannot enter without answering it.
#
# 🚨 AND THE ANSWER IS STRUCTURALLY DIFFERENT FROM THE ACR'S, which is why this is a separate gate
# rather than the same one pointed at another host. `distribution` HAS NO LOCK: ACR's
# `changeableAttributes.deleteEnabled` is an ACR feature, so there is no object a protected set can
# be written INTO ahead of a deleter. On the ACR an incomplete nightly run merely writes fewer locks
# and last night's still hold; on a registry with no lock, a cleanup's derivation IS the entire
# safety margin and an incomplete one deletes what it could not see, in the same act.
# Design: Doc/Architecture/FleetRegistryRetention.

RETENTION_RULES = {"nothing-deletes", "derived-protected-set", "not-ours"}

# The third state of `deleters[].present`. "measured, and it is not there" and "nobody could look"
# are different facts, and only one of them is evidence (#4315 review).
UNVERIFIED = "unverified"

# Which rules a disposition may carry, and the pairing is checked BOTH ways. `third-party` means the
# images were never ours, so the only honest statement is that there is nothing of ours to keep;
# `fleet-unlockable` means our images live there and something has to say what keeps them — a
# `not-ours` on one of those would be a blanket exemption wearing a retention statement's clothes.
RULES_FOR_DISPOSITION = {
    "fleet-unlockable": {"nothing-deletes", "derived-protected-set"},
    "third-party": {"not-ours"},
}

# ── The THIRD question about a registry: what this fleet PUSHES to it (#4323) ─────────────────
#
# `disposition` answers "can THIS lane lock that host", `retention` answers "what deletes from it".
# Neither asks the question that turned out to be wrong about `ghcr.io` for a day: **does this
# fleet PUBLISH there at all**. The record said of that host *"never published by this fleet"* and
# *"Nothing this fleet produces is stored on ghcr.io"* while `main-cd.yml` pushed twelve tags across
# three of our own repositories to it on every promoting run — measured in the job log of run
# 34918214035 (2026-09-15T02:27Z): `mw-plugin-test`, `memex-migration` and `memex-portal-ai`, all
# under `ghcr.io/systemorph/`.
#
# 🚨 THE UNIT OF `disposition` IS WHAT THE FLEET PULLS, AND THAT IS NOT AN EVASION — it is what
# every axis of this script reads it FOR. `foreign_registries` comes off deployment overlays;
# `classify_foreign_registries` and `resolve_running_sets` ask whether an installation's RUNNING SET
# is accounted for. Nothing in a running set comes from a registry no overlay pins. `ghcr.io` is the
# one host in the fleet that is genuinely MIXED — `systemorph/*` is ours, `distribution/*`,
# `oras-project/*` and `actions/*` are not — and a single per-host disposition is false about one
# half whichever value it takes. Measured, both halves:
#
#   * `third-party` is false about `systemorph/*`. That was #4323.
#   * `fleet-unlockable` is false about `distribution/*` — AND IT REDS THE LANE. `memex-cloud` pins
#     `meshweaver.azurecr.io/memex-portal-ai:3.0.0-ci.8411` AND
#     `ghcr.io/distribution/distribution:3.1.1@sha256:…` (the registry service's own image, which
#     cannot come from the registry it boots). Flip the host and `resolve_running_sets` fires "half
#     its running set would be protected and half would not" over an installation whose every image
#     OF OURS is in this ACR and protected. `pause.reEnableWhen` is "lock-pinned-digests is green",
#     so that false red would stand between the fleet and re-enabling cleanup — the exact shape
#     ARM 32 exists for, manufactured by the fix.
#
# So the publication is declared as its OWN fact, on the same unit, and this is the derivation that
# holds the declaration to the workflows rather than re-reading it. A record that checks itself
# passes on the day it stops being true.

# The workflows in THIS repository that push an image anywhere. Both are asserted to exist: a
# publishing lane that was renamed and left underived would take the whole check with it silently.
PUBLISHING_WORKFLOWS = ("main-cd.yml", "release.yml")

# 🚨 ONE PUSH TARGET IS DELIBERATELY NOT IN THE `registries` TABLE, and the skip is named and
# printed rather than silent: `meshweaver.azurecr.io` is the registry this lane LOCKS — the subject
# of the entire script — not a foreign host it declares. Every OTHER derived target must be
# accounted for by the table.
LANE_REGISTRY = "meshweaver.azurecr.io"

# `NS` in both workflows is the repository owner, lowercased. Resolving it needs a literal, and the
# literal is worth nothing on its own — so the ASSIGNMENT is what is asserted: the derivation reds
# if a workflow ever computes `NS` from something that is not the owner, which is the only way this
# substitution could go quietly wrong.
_NS_FROM_OWNER = re.compile(r"NS=\$\(\s*(?:printf\s+'%s'|echo)\s+'?\"?"
                            r"(?:\$GITHUB_REPOSITORY_OWNER|\$\{\{\s*github\.repository_owner\s*\}\})")
REPOSITORY_OWNER = "systemorph"

# `--tag <ref>` in any of the three quotings a shell accepts.
_TAG_ARGUMENT = re.compile(r"--tag\s+(?:\"([^\"]+)\"|'([^']+)'|(\S+))")
# 🚨 `--tag` IS NOT THE ONLY PUSH, and a derivation that thinks it is misses an entire registry.
# Every consumer-visible tag main-cd writes on the ACR is written on `cr.meshweaver.cloud` too, and
# that half goes through `mirror-image-to-registry.sh <source> <destination>…` — no `--tag` on the
# line. A publication mechanism the derivation cannot see is a host the table never has to account
# for, which is this issue one register down.
_MIRROR_CALL = re.compile(r"mirror-image-to-registry\.sh\s+(.+?)\s*(?:\\|$)")
# …and its destinations are often an ARRAY built a line earlier (`mirror+=("…")`), so the appends
# are collected first and `"${mirror[@]}"` is expanded from them.
_ARRAY_APPEND = re.compile(r"(\w+)\+=\(\s*\"([^\"]+)\"\s*\)")
_ARRAY_EXPANSION = re.compile(r"^\"?\$\{(\w+)\[@\]\}\"?$")
# `for repo in a b c; do` — the loop that makes `$repo` on a tag line resolvable.
_REPO_LOOP = re.compile(r"for\s+repo\s+in\s+([^;\n]+);\s*do")
# `for pair in "memex-migration:$V_MIGRATION" "memex-portal-ai:$V_PORTAL"; do` — the same thing
# spelled as pairs, which is how main-cd's phase D writes it.
_PAIR_LOOP = re.compile(r"for\s+pair\s+in\s+([^;\n]+);\s*do")


def _workflow_env(text: str) -> dict[str, str]:
    """The top-level `env:` map of a workflow — enough to resolve `${{ env.ACR }}` in a tag."""
    values: dict[str, str] = {}
    inside = False
    for line in text.splitlines():
        if re.match(r"^env:\s*$", line):
            inside = True
            continue
        if inside:
            if line and not line[0].isspace():
                break
            entry = re.match(r"^\s{2}([A-Za-z_][A-Za-z0-9_]*):\s*(\S+)\s*$", line)
            if entry:
                values[entry.group(1)] = entry.group(2).strip("'\"")
    return values


# A YAML FOLDED scalar — `run: >` / `>-` / `>+` — whose body is joined into ONE shell line at
# runtime with no backslash anywhere.
_FOLDED_RUN = re.compile(r"^(\s*)-?\s*run:\s*>[-+]?\s*$")


def _continued_lines(text: str) -> list[tuple[int, str]]:
    """The file's lines as the SHELL will see them, each carrying the number it STARTS on.

    Two joins, because a workflow spells one command across several physical lines in two different
    ways and BOTH appear in these lanes:

      * a shell continuation — a trailing `\\`. `mirror-image-to-registry.sh`'s destinations
        routinely sit on the line after the source that way.
      * 🚨 a YAML FOLDED scalar — `run: >` — which has no backslash at all and is folded into one
        line by the YAML parser before any shell sees it. `main-cd.yml:1260` and `:1416` mirror the
        portal and migration staging tags to the fleet registry exactly so, and read line-by-line
        those calls publish to NOTHING (#4362 review).

    A host whose only publication is spelled across lines is a host the accounting never asks
    about — which is the very defect this derivation exists to make impossible."""
    joined: list[tuple[int, str]] = []
    buffer = ""
    start = 1
    lines = text.splitlines()
    index = 0
    while index < len(lines):
        line = lines[index]
        folded = _FOLDED_RUN.match(line)
        if folded and not buffer:
            # Everything more-indented than the `run:` key is one folded command.
            indent = len(folded.group(1))
            body: list[str] = []
            cursor = index + 1
            while cursor < len(lines):
                nxt = lines[cursor]
                if nxt.strip() and (len(nxt) - len(nxt.lstrip())) <= indent:
                    break
                body.append(nxt.strip())
                cursor += 1
            joined.append((index + 1, " ".join(part for part in body if part)))
            index = cursor
            continue
        if not buffer:
            start = index + 1
        stripped = line.rstrip()
        if stripped.endswith("\\"):
            buffer += stripped[:-1] + " "
            index += 1
            continue
        joined.append((start, buffer + stripped))
        buffer = ""
        index += 1
    if buffer:
        joined.append((start, buffer))
    return joined


def publish_targets(root: str) -> tuple[dict[str, set[str]], dict[str, set[str]], list[str], int]:
    """Every `(host, repository)` this repository's own workflows PUSH an image to.

    Returns the targets by host, the PRODUCERS by host (which lane actually emits each — so a
    record's `producedBy` is held to the derivation rather than to the presence of the host's name
    somewhere in a file, which a comment satisfies), any problems, and the number of image
    references read — the denominator, because "no publication found" and "nothing was parsed" are
    the same output without it, and that is the confusion this whole family is made of.

    🚨 DERIVED FROM THE WORKFLOWS, NEVER FROM THE RECORD. The record is what this checks."""
    base = Path(root) / ".github" / "workflows"
    targets: dict[str, set[str]] = {}
    producers: dict[str, set[str]] = {}
    problems: list[str] = []
    arguments = 0
    for name in PUBLISHING_WORKFLOWS:
        path = base / name
        if not path.is_file():
            problems.append(
                f".github/workflows/{name} is missing, and it is one of the two lanes this fleet "
                "publishes images from. A renamed publishing workflow takes the derivation with "
                "it — and a derivation that reads nothing reports exactly like a clean one.")
            continue
        text = path.read_text(encoding="utf-8")
        environment = _workflow_env(text)
        namespace = REPOSITORY_OWNER if _NS_FROM_OWNER.search(text) else None
        if "$NS" in text and namespace is None:
            problems.append(
                f".github/workflows/{name} uses `$NS` in an image reference and does not assign it "
                "from the repository owner. The substitution this check makes would be a guess, so "
                "it refuses rather than deriving a namespace that is not the one being pushed to.")
        loops = [repo for group in _REPO_LOOP.findall(text) for repo in group.split()]
        loops += [pair.strip("\"'").split(":")[0]
                  for group in _PAIR_LOOP.findall(text) for pair in group.split()]
        arrays: dict[str, list[str]] = {}
        for _, line in _continued_lines(text):
            if line.strip().startswith("#"):
                continue
            for variable, value in _ARRAY_APPEND.findall(line):
                arrays.setdefault(variable, []).append(value)
        for number, line in _continued_lines(text):
            if line.strip().startswith("#"):
                continue
            references = [next(group for group in match.groups() if group is not None)
                          for match in _TAG_ARGUMENT.finditer(line)]
            for call in _MIRROR_CALL.findall(line):
                try:
                    destinations = shlex.split(call)[1:]     # [0] is the SOURCE, not a publication
                except ValueError:
                    problems.append(
                        f".github/workflows/{name}:{number} calls mirror-image-to-registry.sh with "
                        "a command line this check could not tokenize, so its destinations — every "
                        "one of them a publication — were never read.")
                    continue
                for destination in destinations:
                    array = _ARRAY_EXPANSION.match(destination)
                    references.extend(arrays.get(array.group(1), []) if array else [destination])
            for reference in references:
                arguments += 1
                # Strip the tag: the LAST colon that is not inside a `${{ … }}` expansion.
                image = re.sub(r":[^:/]*$", "", reference)
                # 🚨 A WORKFLOW SPELLS ITS REGISTRY THREE WAYS AND ALL THREE APPEAR IN THESE LANES:
                # `${{ env.ACR }}` (main-cd's tag lines), a plain shell `$ACR` / `${ACR}` exported
                # from the SAME top-level `env:` map (release.yml:272, :275, :279), and a literal.
                # Expanding only the first left every release-lane target reading as the host
                # `$ACR` (#4362 review).
                for key, value in environment.items():
                    image = (image.replace("${{ env.%s }}" % key, value)
                                  .replace("${%s}" % key, value)
                                  .replace("$%s" % key, value))
                if "/" not in image:
                    continue
                host, _, repository = image.partition("/")
                # 🚨 THE UNRESOLVED CHECK COMES FIRST, AND THAT ORDER IS THE WHOLE POINT. A host
                # still carrying `$` has no dot, so the Docker-Hub short-name test below would
                # DISCARD it silently — a push target dropped with no error, by a derivation whose
                # entire job is to make a dropped push target impossible. Unresolved is a PROBLEM.
                if "${{" in host or "$" in host:
                    problems.append(
                        f".github/workflows/{name}:{number} pushes to a registry host this check "
                        f"could not resolve (`{host}`). An unresolved push target is not an absent "
                        "one — it is a publication nothing in the table has to account for.")
                    continue
                if "." not in host and host != "localhost":
                    continue          # a Docker Hub short name, not a registry this fleet runs
                if namespace:
                    repository = repository.replace("${NS}", namespace).replace("$NS", namespace)
                expansions = [repository]
                if "$repo" in repository or "${repo}" in repository:
                    expansions = [repository.replace("${repo}", name_).replace("$repo", name_)
                                  for name_ in sorted(set(loops))] or []
                    if not expansions:
                        problems.append(
                            f".github/workflows/{name}:{number} pushes `{repository}` and no "
                            "`for repo in …` loop in the file says what `$repo` ranges over, so "
                            "the repositories being published cannot be named.")
                for expanded in expansions:
                    if "$" in expanded or "${{" in expanded:
                        problems.append(
                            f".github/workflows/{name}:{number} pushes to `{host}/{expanded}`, a "
                            "repository this check could not resolve to a literal name.")
                        continue
                    targets.setdefault(host, set()).add(expanded)
                    producers.setdefault(host, set()).add(f".github/workflows/{name}")
    if not targets and not problems:
        problems.append(
            "ZERO publication targets were derived from "
            + ", ".join(PUBLISHING_WORKFLOWS)
            + ". Both lanes push images today, so zero means the derivation stopped matching, not "
              "that this fleet stopped publishing — and an empty derivation accounts for every "
              "registry equally well.")
    return targets, producers, problems, arguments


# A `publishes` block's retention may ONLY be this, and it is deliberately NOT in `RETENTION_RULES`.
# Allowing `operator-retained` as a HOST-level rule would be a trapdoor out of `nothing-deletes`:
# any registry could then answer the second question with "somebody else's problem". It is legible
# only about a publication into a store this fleet does not operate.
PUBLICATION_RETENTION_RULES = {"operator-retained"}


def read_registry_publications(root: str) -> dict[str, set[str]]:
    """What each registry entry DECLARES this fleet publishes there — `(host → repositories)`.

    Read leniently and used only to LABEL: `--check-registry-retention` is what holds the
    declaration to the workflows, and a malformed entry has already reddened there."""
    path = Path(root) / ".github" / "acr-retention" / ROSTER_PATH
    declared: dict[str, set[str]] = {}
    if not path.is_file():
        return declared
    try:
        document = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return declared
    registries = document.get("registries")
    # 🚨 A NON-MAPPING `registries` MUST NOT RAISE HERE. `read_registry_dispositions` already reds
    # on that shape, and this reader runs inside the LIVE locking run — an AttributeError would
    # replace a named blocker with a stack trace and take the whole sweep down (#4362 review).
    if not isinstance(registries, dict):
        return declared
    for host, entry in registries.items():
        if not isinstance(entry, dict):
            continue
        publishes = entry.get("publishes")
        if isinstance(publishes, dict) and isinstance(publishes.get("repositories"), list):
            # 🚨 STRIPPED, exactly as `check_publication_accounting` strips. A value written as
            # `" systemorph/memex-portal-ai "` would otherwise satisfy the accounting arm and match
            # NOTHING here — so an overlay could run a published repository with the ARM 34b
            # blocker silently inert, which is a declaration that passes while protecting nobody
            # (#4362 review).
            declared[host] = {str(repo).strip() for repo in publishes["repositories"]
                              if str(repo).strip()}
    return declared


def check_publication_accounting(root: str, registries: dict) -> tuple[list[str], str]:
    """Every registry THIS FLEET PUSHES TO is accounted for by the table, derived from the lanes.

    🚨 THIS IS THE ARM THAT WOULD HAVE CAUGHT #4323, and it is derived rather than re-read. The
    record said of `ghcr.io` *"never published by this fleet"* and *"Nothing this fleet produces is
    stored on ghcr.io"* on the very day `main-cd.yml` pushed twelve tags across three of our own
    repositories there. Both fields validated green, because every existing arm asks the record what
    it says and this one asks the WORKFLOWS what they do.

    A host we push to satisfies the table in one of two ways:

      * `disposition: fleet-unlockable` — the host-level declaration already says our images live
        there (`cr.meshweaver.cloud`), and the `retention` block already has to say what keeps them.
      * a `publishes` block — for a host whose disposition is about what the fleet PULLS, because
        the host is MIXED. `ghcr.io` is the fleet's only one: `systemorph/*` is ours and every other
        namespace on it is somebody else's, and a single per-host disposition is false about one
        half whichever value it takes.

    Returns the problems and the denominator line, which is PRINTED whatever the verdict."""
    targets, producers, problems, references = publish_targets(root)
    # 🚨 THE POPULATION IS THE UNION, NOT THE DERIVED SET (#4362 review). Looping over derived hosts
    # alone means that if BOTH lanes stop pushing to a host while its `publishes` block stays in the
    # record, the host is never visited — so the promised stale check, and every validation under
    # it, passes having inspected nothing. A whole-host stale entry is the same defect as a stale
    # repository, one level up, and it reads exactly like a clean record.
    declared_publishers = {host for host, entry in registries.items()
                           if isinstance(entry, dict) and entry.get("publishes") is not None}
    hosts = sorted((set(targets) | declared_publishers) - {LANE_REGISTRY})
    denominator = (
        f"publication accounting: {references} image reference(s) read across "
        + ", ".join(PUBLISHING_WORKFLOWS)
        + f"; {len(targets)} registry(ies) pushed to ({', '.join(sorted(targets))}); "
        + (f"`{LANE_REGISTRY}` skipped — it is the registry this lane LOCKS, the subject of this "
           "script rather than a foreign host it declares" if LANE_REGISTRY in targets
           else f"`{LANE_REGISTRY}` was NOT among them, which is itself a change worth reading")
        + f"; {len(hosts)} left for the table to account for.")

    for host in hosts:
        pushed = sorted(targets.get(host, set()))
        entry = registries.get(host)
        if not pushed and isinstance(entry, dict) and entry.get("publishes") is not None:
            problems.append(
                f"{ROSTER_PATH}: `registries.{host}` carries a `publishes` block and NOTHING in "
                + " / ".join(PUBLISHING_WORKFLOWS) + f" pushes to `{host}` any more. A publication "
                "record outliving its publication exempts nothing and hides the next one — delete "
                "the block, or restore the lane.")
            continue
        if not isinstance(entry, dict):
            problems.append(
                f"{ROSTER_PATH}: this fleet PUBLISHES {len(pushed)} repository(ies) to `{host}` "
                f"({', '.join(pushed)}) and the `registries` table does not declare that host at "
                "all. A registry we push our own images to is not a registry with nothing of ours "
                "in it.")
            continue
        disposition = str(entry.get("disposition", "")).strip()
        publishes = entry.get("publishes")
        if disposition == "fleet-unlockable" and publishes is None:
            continue          # the host-level declaration already says our images live there
        where = f"{ROSTER_PATH}: `registries.{host}`"
        if publishes is None:
            problems.append(
                f"{where} is `{disposition}` and declares no `publishes` block, while this fleet "
                f"PUSHES {len(pushed)} repository(ies) to it: {', '.join(pushed)} — derived from "
                + " and ".join(PUBLISHING_WORKFLOWS) + ", not from this record. `third-party` and "
                "its `not-ours` retention BOTH say nothing of ours is stored there, and that is "
                "the false sentence #4323 is. Either the host is really `fleet-unlockable`, or — "
                "if the host is MIXED, as `ghcr.io` is — declare `publishes` with the repositories, "
                "the lanes that produce them, what retains them, and whether any installation runs "
                "from them. Doc/Architecture/FleetRegistryRetention.")
            continue
        if not isinstance(publishes, dict):
            problems.append(f"{where}.publishes is not an object.")
            continue

        # 🚨 EQUALITY, NOT CONTAINMENT, IN BOTH DIRECTIONS. A repository we push that the record
        # does not name is the gap; a repository the record names and nothing pushes is a stale
        # exemption, and a stale one hides the next one — the same doctrine the roster is held to.
        declared = publishes.get("repositories")
        if not isinstance(declared, list) or not declared or not all(
                isinstance(repo, str) and repo.strip() for repo in declared):
            problems.append(
                f"{where}.publishes names no `repositories`. The claim IS the enumeration: a "
                "publication block that lists nothing accounts for nothing while reading exactly "
                "like a complete one.")
        else:
            named = {repo.strip() for repo in declared}
            missing = sorted(set(pushed) - named)
            stale = sorted(named - set(pushed))
            if missing:
                problems.append(
                    f"{where}.publishes does not name {', '.join(missing)}, which "
                    + " / ".join(PUBLISHING_WORKFLOWS) + f" push(es) to `{host}`. An unnamed "
                    "publication is one nothing in this record has to say anything about.")
            if stale:
                problems.append(
                    f"{where}.publishes names {', '.join(stale)}, which nothing in "
                    + " / ".join(PUBLISHING_WORKFLOWS) + f" pushes to `{host}` any more. A stale "
                    "entry exempts nothing and hides the next one — delete the line, or restore "
                    "the publication.")

        produced_by = publishes.get("producedBy")
        if not isinstance(produced_by, list) or not produced_by:
            problems.append(
                f"{where}.publishes names no `producedBy`. Without the committed lane the claim "
                "was derived from, the next reader cannot tell a publication that still happens "
                "from one that was removed.")
        else:
            named_lanes = {str(candidate).strip() for candidate in produced_by
                           if str(candidate).strip()}
            for relative in sorted(named_lanes):
                if not (Path(root) / relative).is_file():
                    problems.append(
                        f"{where}.publishes names `producedBy: {relative}`, which is not a file in "
                        "this repository. A record pointing at a moved lane checks nothing.")
            # 🚨 HELD TO THE DERIVATION, NOT TO THE HOST'S NAME APPEARING SOMEWHERE IN THE FILE
            # (#4362 review). A comment mentioning `ghcr.io`, or a lane that stopped emitting it,
            # satisfies a substring test — so a misattributed producer would read as evidence. The
            # derived set is which lanes actually EMIT a reference to this host.
            emitting = producers.get(host, set())
            misattributed = sorted(named_lanes - emitting
                                   - {lane for lane in named_lanes
                                      if not (Path(root) / lane).is_file()})
            unnamed = sorted(emitting - named_lanes)
            if misattributed:
                problems.append(
                    f"{where}.publishes names {', '.join(misattributed)} as producing `{host}`, and "
                    f"no image reference to that host is derived from "
                    f"{'it' if len(misattributed) == 1 else 'them'}. A lane that no longer emits "
                    "this host — or never did — reads as evidence while naming nothing.")
            if unnamed:
                problems.append(
                    f"{where}.publishes does not name {', '.join(unnamed)}, which DOES push to "
                    f"`{host}`. A producer the record has not seen is a publication nobody reviewed.")

        if not str(publishes.get("runFrom", "")).strip():
            problems.append(
                f"{where}.publishes states no `runFrom`. Whether any INSTALLATION pulls its running "
                "set from these repositories is the whole difference between a publication and a "
                "source this lane has to protect, and leaving it unsaid lets the next reader assume "
                "either one.")

        retention = publishes.get("retention")
        if not isinstance(retention, dict):
            problems.append(
                f"{where}.publishes has no `retention` block. Our artifacts are stored there, so "
                "something has to say what keeps them — the whole point of asking the second "
                "question of every registry (#4230) is that it may not go unasked about ours.")
            continue
        rule = str(retention.get("rule", "")).strip()
        if rule not in PUBLICATION_RETENTION_RULES:
            problems.append(
                f"{where}.publishes.retention.rule is {rule!r}; expected one of "
                + ", ".join(sorted(PUBLICATION_RETENTION_RULES))
                + ". A publication into a store this fleet does not operate can make exactly one "
                  "honest statement: that its operator retains it and we do not.")
            continue
        if not str(retention.get("operator", "")).strip():
            problems.append(
                f"{where}.publishes.retention is `{rule}` and names no `operator`. 'Somebody else "
                "keeps it' with nobody named is the sentence that reads as an answer and is not one.")
        if not str(retention.get("reason", "")).strip():
            problems.append(f"{where}.publishes.retention has no `reason`.")
        if retention.get("deleters") is not None:
            problems.append(
                f"{where}.publishes.retention enumerates `deleters` for a store this fleet does not "
                "operate. We cannot measure GitHub's deletion mechanisms from a committed file, so "
                "such a list would be a verdict about OUR artifacts resting on nothing — the same "
                "false reassurance `not-ours` refuses.")
        # 🚨 STRICTLY `false`, NOT MERELY "not the singleton True" (#4362 review). This file has
        # already paid for the other spelling twice — `inForce` and `present` both written as the
        # STRING "true", which every `is True` reader silently treats as absent. A truthy non-bool
        # here would pass as though a cleanup were forbidden while a later consumer reads it as
        # authorization.
        authorized = retention.get("cleanupAuthorized")
        if authorized is not False:
            problems.append(
                f"{where}.publishes.retention declares `cleanupAuthorized: {authorized!r}`, and the "
                "only value a publication into somebody else's store may carry is the boolean "
                "`false`. There is no cleanup of ours to authorize there — and a truthy non-boolean "
                "(the STRING \"true\", say) is read as absent by every `is True` reader in this "
                "file while a later consumer may read it as authorization.")
    return problems, denominator


# Every spelling of "delete something from a registry" this fleet could plausibly acquire.
# 🚨 `acr purge` is DELIBERATELY ABSENT: it is legitimately present in `.github/acr-retention/`, it
# addresses `meshweaver.azurecr.io`, and it cannot reach the fleet registry at all — the record
# declares it `present: false` with that reason, and `--check-retention-record` is the gate that
# reads it. Including it here would red on the other registry's own record.
#
# 🚨 THE SPELLINGS ARE THE COMMAND LINE'S, NOT THE README'S (#4315 review). `curl` takes `-XDELETE`
# with no space and `--request=DELETE` with an equals sign, and both execute identically to the
# spaced forms — a sweep that matches only `-X DELETE` is defeated by a keystroke, silently, while
# still printing a denominator and a clean verdict. Same lesson as `--include-"locked"` one gate
# along: read what the shell will RUN, never what it usually looks like.
DELETER_PATTERNS = (
    (r"-X\s*['\"]?DELETE\b", "an HTTP DELETE"),
    (r"--request[\s=]\s*['\"]?DELETE\b", "an HTTP DELETE"),
    (r"\bcrane\s+delete\b", "a crane deletion"),
    (r"\bskopeo\s+delete\b", "a skopeo deletion"),
    (r"\bregctl\s+manifest\s+delete\b", "a regctl manifest deletion"),
    (r"\boras\s+manifest\s+delete\b", "an oras manifest deletion"),
    (r"\bacr\s+repository\s+delete\b", "an az acr repository deletion"),
    (r"\bgarbage-collect\b", "a blob garbage collection"),
)

# What can actually RUN a command against a registry: workflow and chart YAML, shell, Python,
# Helm templates, bicep. `.md` is prose and `.json` is data — a record naming a deleter is not one.
DELETER_SWEEP_EXTENSIONS = {".yml", ".yaml", ".sh", ".py", ".tpl", ".bicep"}
DELETER_SWEEP_ROOTS = ("deploy", ".github")


def sweep_for_deleters(root: str) -> tuple[list[tuple[str, int, str, str]], int, int]:
    """Every deleter spelling that appears on an EXECUTABLE, non-comment line. Returns the hits and
    the denominator — files scanned and files deliberately skipped.

    🚨 TWO SKIPS, BOTH PRINCIPLED, BOTH NAMED. This script itself carries every pattern above as a
    literal, and `.github/acr-retention/` is the RECORD — a file whose job is to name deleters is
    not one, and its two purge YAMLs are the other gate's subject (`--check-retention-record`).
    Skipping either silently would be the trapdoor this repository keeps paying for, so both are
    counted and printed.

    🚨 WHOLE-LINE COMMENTS ARE STRIPPED, and that is load-bearing rather than tidy: the registry's
    own ConfigMap explains in a comment that blobs are removed only by `registry garbage-collect`.
    A sweep that fired on the sentence saying a thing does not happen is the shape this file already
    learned once, on 2026-09-07, over a trailing newline."""
    base = Path(root)
    compiled = [(re.compile(pattern), description) for pattern, description in DELETER_PATTERNS]
    myself = (base / ".github" / "scripts" / "lock-pinned-digests.py").resolve()
    record = (base / ".github" / "acr-retention").resolve()
    hits: list[tuple[str, int, str, str]] = []
    scanned = skipped = 0
    for sweep_root in DELETER_SWEEP_ROOTS:
        if not base.joinpath(sweep_root).is_dir():
            continue
        for path in sorted(base.joinpath(sweep_root).rglob("*")):
            if not path.is_file() or path.suffix not in DELETER_SWEEP_EXTENSIONS:
                continue
            resolved = path.resolve()
            if resolved == myself or record in resolved.parents:
                skipped += 1
                continue
            scanned += 1
            try:
                text = path.read_text(encoding="utf-8")
            except (OSError, UnicodeDecodeError):
                # 🚨 UNREADABLE IS NOT CLEAN. R3 in the design page: what the derivation could not
                # SEE is protected, not skipped — and here that means named as a hit.
                hits.append((str(path.relative_to(base)), 0, "unreadable",
                             "this file could not be read, so it was never swept"))
                continue
            for number, line in enumerate(text.splitlines(), 1):
                if line.strip().startswith("#"):
                    continue
                for pattern, description in compiled:
                    if pattern.search(line):
                        hits.append((str(path.relative_to(base)), number, description,
                                     line.strip()[:120]))
    return hits, scanned, skipped


def maintenance_stanza(chart_dir: Path) -> tuple[dict[str, str], list[str]]:
    """The `maintenance:` keys the registry's rendered config carries, and the `age:` under each.

    Read off the committed template rather than a live pod, because this gate runs on a pull request
    with no credential and no cluster — and the pull request is the half that can BREAK it. The
    stanza is plain YAML inside the `config.yml: |` block, so it is read by indentation rather than
    by a YAML parser that would choke on the Helm expressions elsewhere in the same file."""
    found: dict[str, str] = {}
    problems: list[str] = []
    configmaps = sorted(chart_dir.glob("configmap*.yaml")) + sorted(chart_dir.glob("configmap*.yml"))
    if not configmaps:
        return found, [f"{chart_dir}: no configmap template — the registry's own configuration is "
                       "what says whether anything in it deletes, and it could not be found."]
    for configmap in configmaps:
        lines = configmap.read_text(encoding="utf-8").splitlines()
        for index, line in enumerate(lines):
            opener = re.match(r"^(\s*)maintenance:\s*$", line)
            if not opener:
                continue
            depth = len(opener.group(1))
            child: str | None = None
            for following in lines[index + 1:]:
                if not following.strip() or following.strip().startswith("#"):
                    continue
                indent = len(following) - len(following.lstrip())
                if indent <= depth:
                    break
                key = re.match(r"^\s*([A-Za-z0-9_.-]+):", following)
                if indent == depth + 2 and key:
                    child = key.group(1)
                    found.setdefault(child, "")
                elif child is not None:
                    age = re.match(r"^\s*age:\s*(\S+)\s*$", following)
                    if age:
                        found[child] = age.group(1)
    return found, problems


def acl_delete_grants(chart_dir: Path) -> tuple[list[tuple[str, int, str]], int]:
    """Every ACL rule in the registry's chart that grants the `delete` ACTION, with the `match:` it
    is attached to — and how many rules were inspected.

    🚨 THE ACL IS THE ONLY EVIDENCE BEHIND ONE OF THE RECORD'S VERDICTS (#4315 review). *"an
    installation holding an instance key cannot delete"* is not a grep result — it is a property of
    `docker_auth`'s ACL, where exactly one rule carries `delete` and its `match:` names the publisher.
    Change that rule's match to `/.+/` and every authenticated account may delete, while the record
    still reads "nothing deletes" and every other arm of this gate stays green. So the gate re-derives
    it, the same way it re-derives the `maintenance:` stanza.

    Read by structure rather than by YAML parser: the file is a Helm template whose ACL matches carry
    `{{ … }}` expressions, which is exactly the shape a parser refuses. The rules are a list of
    `- match: {…}` / `actions: [...]` pairs, so each `actions:` line granting `delete` is attributed
    to the nearest `- match:` above it.

    🚨 The returned COUNT is the denominator: zero rules inspected and zero bad rules are the same
    answer without it, and the caller reds on the first."""
    grants: list[tuple[str, int, str]] = []
    rules = 0
    for template in sorted(chart_dir.rglob("*.yaml")) + sorted(chart_dir.rglob("*.yml")):
        match_line = ""
        for number, line in enumerate(template.read_text(encoding="utf-8").splitlines(), 1):
            if line.strip().startswith("#"):
                continue
            if re.match(r"^\s*-\s*match:", line):
                match_line = line.strip()
                continue
            action = re.match(r"^\s*actions:\s*\[(?P<actions>[^\]]*)\]", line)
            if not action or not match_line:
                continue
            rules += 1
            if re.search(r"['\"]delete['\"]", action.group("actions")):
                grants.append((str(template), number, match_line))
    return grants, rules


def check_registry_retention(root: str) -> int:
    """Does every registry the fleet's overlays name still say what — if anything — DELETES from it,
    and does the committed chart still agree with what the record says?

    No credential, no network, no registry call. What it covers is the half a pull request can
    break: the declaration, and the chart the declaration is a statement ABOUT.

    🚨 THE POINT IS THAT THE DECLARATION IS FALSIFIABLE. A `reason` field alone is a comment: it
    reads exactly the same on the day a `CronJob` running `registry garbage-collect` lands beside it.
    So a `nothing-deletes` rule names the chart it was derived from, and this re-derives the chart's
    own deleters and reds when the two disagree — naming what changed, and saying that the record
    has to be re-derived before the change lands rather than after."""
    path = Path(root) / ".github" / "acr-retention" / ROSTER_PATH
    if not path.is_file():
        print(f"::error::{path} does not exist — the `registries` table is where every registry the "
              "fleet pins in is accounted for, both for what locks it and for what deletes from it.")
        return 1
    try:
        document = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        print(f"::error::{path} is not readable JSON: {exc}")
        return 1
    registries = document.get("registries")
    if not isinstance(registries, dict) or not registries:
        print(f"::error::{ROSTER_PATH}: `registries` is missing or empty, so ZERO registries were "
              "asked what deletes from them. An empty table and a clean one read identically.")
        return 1

    problems: list[str] = []
    # 🚨 THE THIRD QUESTION, ASKED FIRST because it is the one that was never asked (#4323): what
    # does this fleet PUSH to each of these hosts? Derived from the publishing workflows, so it
    # cannot be satisfied by the record agreeing with itself.
    publication_problems, publication_denominator = check_publication_accounting(root, registries)
    problems.extend(publication_problems)
    charts_checked = 0
    acl_rules_checked = 0
    swept_files = swept_skipped = 0
    declaring_nothing_deletes: list[str] = []
    unverified_by_host: dict[str, list[str]] = {}
    for host, entry in sorted(registries.items()):
        where = f"{ROSTER_PATH}: `registries.{host}`"
        if not isinstance(entry, dict):
            problems.append(f"{where} is not an object.")
            continue
        disposition = str(entry.get("disposition", "")).strip()
        retention = entry.get("retention")
        if retention is None or not isinstance(retention, dict):
            problems.append(
                f"{where} has no `retention` block. `disposition` says whether THIS lane can lock "
                f"{host}; it says nothing about whether anything DELETES from it, and that second "
                "question went unasked about the fleet's own registry until #4230. Write "
                "`retention` with a `rule` (" + ", ".join(sorted(RETENTION_RULES)) + ") and a "
                "`reason`. Doc/Architecture/FleetRegistryRetention.")
            continue
        rule = str(retention.get("rule", "")).strip()
        if rule not in RETENTION_RULES:
            problems.append(f"{where}.retention.rule is {rule!r}; expected one of "
                            + ", ".join(sorted(RETENTION_RULES)) + ".")
            continue
        if not str(retention.get("reason", "")).strip():
            problems.append(f"{where}.retention has no `reason`. A rule with no reasoning is a "
                            "word, and the next reader cannot tell a decision from a default.")
        allowed = RULES_FOR_DISPOSITION.get(disposition)
        if allowed is not None and rule not in allowed:
            problems.append(
                f"{where} is `{disposition}` and declares retention `{rule}`, which that "
                f"disposition may not carry (allowed: {', '.join(sorted(allowed))}). A "
                "`third-party` registry holds nothing of ours to keep; a `fleet-unlockable` one "
                "holds our images, so `not-ours` there would be a blanket exemption wearing a "
                "retention statement's clothes.")

        if rule == "not-ours":
            if retention.get("deleters") is not None:
                problems.append(
                    f"{where}.retention is `not-ours` and still enumerates `deleters`. Nothing of "
                    "ours is stored there, so listing somebody else's deleters would read as a "
                    "verdict about OUR artifacts — which is the false reassurance this table "
                    "exists to refuse.")
            continue

        if rule == "derived-protected-set":
            axes = ((retention.get("protectedSet") or {}).get("axes")
                    if isinstance(retention.get("protectedSet"), dict) else None)
            if not isinstance(axes, list) or not axes:
                problems.append(
                    f"{where}.retention is `derived-protected-set` and names no "
                    "`protectedSet.axes`. A cleanup that deletes the complement of a set nobody "
                    "wrote down deletes the complement of nothing.")
                continue
            for position, axis in enumerate(axes):
                if not isinstance(axis, dict):
                    problems.append(f"{where}.retention.protectedSet.axes[{position}] is not an object.")
                    continue
                for required in ("axis", "derivedFrom"):
                    if not str(axis.get(required, "")).strip():
                        problems.append(f"{where}.retention.protectedSet.axes[{position}] has no "
                                        f"`{required}`.")
                if axis.get("onIncomplete") != "refuse":
                    problems.append(
                        f"{where}.retention.protectedSet.axes[{position}] declares "
                        f"`onIncomplete: {axis.get('onIncomplete')!r}`. On a registry with no lock "
                        "the ONLY safe answer is `refuse`: an incomplete derivation there does not "
                        "protect less, it DELETES what it could not see, in the same act.")
            continue

        # rule == "nothing-deletes" — the enumeration, and then the chart it was derived from.
        deleters = retention.get("deleters")
        if not isinstance(deleters, list) or not deleters:
            problems.append(
                f"{where}.retention is `nothing-deletes` and enumerates no `deleters`. The claim "
                "IS the enumeration: without one, 'nothing deletes' and 'nobody looked' are the "
                "same sentence.")
            continue
        present = 0
        unverified: list[str] = []
        for position, deleter in enumerate(deleters):
            if not isinstance(deleter, dict):
                problems.append(f"{where}.retention.deleters[{position}] is not an object.")
                continue
            for required in ("mechanism", "verdict"):
                if not str(deleter.get(required, "")).strip():
                    problems.append(f"{where}.retention.deleters[{position}] has no `{required}`.")
            state = deleter.get("present")
            # 🚨 THREE STATES, NOT TWO (#4315 review). `present: false` used to carry BOTH "measured,
            # and it is not there" and "nobody could look" — and the second is the one that matters:
            # `cr.meshweaver.cloud`'s storage account is provisioned outside this chart, so whether a
            # blob LIFECYCLE POLICY deletes referenced layers cannot be read from any committed file.
            # Filing that as `false` let the declaration read as fully measured over an open unknown,
            # which is precisely the "not-checked spelled as clean" confusion this family is made of.
            if state == UNVERIFIED:
                if not str(deleter.get("verifiedBy", "")).strip():
                    problems.append(
                        f"{where}.retention.deleters[{position}] is `{UNVERIFIED}` and names no "
                        "`verifiedBy`. An unknown with no stated way to answer it is indistinguishable "
                        "from one nobody intends to answer.")
                unverified.append(str(deleter.get("mechanism", f"[{position}]")))
            elif not isinstance(state, bool):
                problems.append(
                    f"{where}.retention.deleters[{position}].present is {state!r}; expected true, "
                    f"false or {UNVERIFIED!r}. Every `is True` reader in this file treats another "
                    "string or a null as absent, so a typed quote mark would silently move a "
                    "mechanism out of the enumeration.")
            elif state:
                present += 1
        if deleters and present == 0:
            problems.append(
                f"{where}.retention.deleters lists {len(deleters)} mechanism(s) and NOT ONE is "
                "`present: true`. An enumeration in which nothing is present is an enumeration "
                "that inspected nothing, and it reads exactly like a clean one — the confusion "
                "#3438 is made of. `cr.meshweaver.cloud` has one: `maintenance.uploadpurging`.")

        # 🚨 AND THE UNKNOWN HAS TO BLOCK SOMETHING, or naming it is decoration. It cannot usefully
        # block the GATE — a check that is red until somebody reads an Azure storage account is a
        # check nobody reads, which this file learned on 2026-09-07 over a trailing newline, and it
        # would sit on `pause.reEnableWhen` besides. What it blocks is the ACT: `cleanupAuthorized`
        # is the record's own statement that a cleanup here would be safe, and it may not be `true`
        # while any mechanism is unverified. The day somebody writes the cleanup, this is what
        # refuses — naming the row that is still open.
        authorized = retention.get("cleanupAuthorized")
        if not isinstance(authorized, bool):
            problems.append(
                f"{where}.retention is `nothing-deletes` and declares "
                f"`cleanupAuthorized: {authorized!r}`, which is not a boolean. It must state, "
                "explicitly, whether this enumeration is complete enough to authorize deleting "
                "anything — an absent field would default to whatever the next reader assumes.")
        elif authorized and unverified:
            problems.append(
                f"{where}.retention declares `cleanupAuthorized: true` while "
                f"{len(unverified)} mechanism(s) are `{UNVERIFIED}`: {', '.join(unverified)}. "
                "On a registry with NO LOCK the derivation is the whole of the protection, so a "
                "deleter nobody has ruled out is a deleter that may be running beside the cleanup. "
                "Answer it — each row names its `verifiedBy` — or leave the authorization false.")

        unverified_by_host[host] = unverified

        chart = str(retention.get("chart", "")).strip()
        if not chart:
            problems.append(
                f"{where}.retention is `nothing-deletes` and names no `chart`. Without the "
                "committed source the claim was derived from, this gate can only re-read the "
                "claim — and a claim that checks itself passes on the day it stops being true.")
            continue
        chart_dir = Path(root) / chart
        if not chart_dir.is_dir():
            problems.append(f"{where}.retention.chart names {chart!r}, which is not a directory in "
                            "this repository. A record pointing at a moved chart checks nothing.")
            continue
        charts_checked += 1

        # (a) no Job or CronJob — a blob GC would be one, and it is the deletion that never returns.
        for rendered in sorted(chart_dir.rglob("*.yaml")) + sorted(chart_dir.rglob("*.yml")):
            for number, line in enumerate(rendered.read_text(encoding="utf-8").splitlines(), 1):
                if line.strip().startswith("#"):
                    continue
                if re.match(r'^\s*kind:\s*"?(Job|CronJob)"?\s*$', line):
                    problems.append(
                        f"{rendered.relative_to(Path(root))}:{number} renders a "
                        f"{line.split(':', 1)[1].strip()} under {host}'s chart, and the record "
                        "says nothing deletes there. A scheduled job beside a registry is how a "
                        "`registry garbage-collect` arrives. Re-derive "
                        f"`{ROSTER_PATH}` → `registries.{host}.retention.deleters` in THIS diff.")

        # (b) the maintenance stanza still says what the record says it says.
        found, stanza_problems = maintenance_stanza(chart_dir)
        problems.extend(stanza_problems)
        declared = retention.get("maintenance")
        if not isinstance(declared, dict):
            problems.append(
                f"{where}.retention declares no `maintenance` map. `maintenance:` is the ONE place "
                "in a distribution configuration where something deletes on a timer, so an "
                "undeclared stanza is the arm of this gate that would matter most.")
        elif set(declared) != set(found):
            problems.append(
                f"{where}: the chart's `maintenance:` stanza carries {sorted(found) or 'nothing'} "
                f"and the record declares {sorted(declared)}. Every key there is a timer that can "
                "delete; one the record has not seen is exactly the deletion nobody can name.")
        else:
            for key, age in sorted(declared.items()):
                if str(age) != found.get(key, ""):
                    problems.append(
                        f"{where}: `maintenance.{key}` retains for {found.get(key) or '<no age>'} "
                        f"in the chart and {age!r} in the record. A window that moved without the "
                        "record moving is a deletion nobody decided.")

        # (c) the ACL — the ONLY evidence behind the "a non-publisher account cannot delete" verdict.
        # The record names the token its match must carry, so the check cannot silently inspect
        # nothing when the ACL moves or is templated differently.
        principal = str(retention.get("deleteGrantedTo", "")).strip()
        if not principal:
            problems.append(
                f"{where}.retention names no `deleteGrantedTo`. One row of this enumeration rests "
                "entirely on the registry's ACL — change the rule that carries the `delete` action "
                "to match every account and the verdict becomes false with every other arm still "
                "green — so the record has to say which principal that rule may name.")
        else:
            grants, rules_seen = acl_delete_grants(chart_dir)
            acl_rules_checked += rules_seen
            if rules_seen == 0:
                problems.append(
                    f"{where}.retention names `deleteGrantedTo: {principal!r}` and ZERO ACL rules "
                    f"were found under {chart}. An ACL that cannot be located is not an ACL that "
                    "grants nothing — the check inspected nothing while reporting success.")
            for template, number, match_line in grants:
                if principal not in match_line:
                    problems.append(
                        f"{Path(template).relative_to(Path(root))}:{number} grants the `delete` "
                        f"action to `{match_line}`, which does not name {principal!r}. "
                        f"`registries.{host}` records that only the publisher may delete, and that "
                        "verdict is this ACL and nothing else — an installation holding an instance "
                        "key would now be able to delete a manifest a live portal pins.")

        declaring_nothing_deletes.append(host)

    # (c) Nothing anywhere executes a deletion against a registry. ONE sweep, after the loop: it
    # reads the whole repository, so running it per host would re-read every file and — worse —
    # leave the printed denominator describing whichever host happened to be last.
    if declaring_nothing_deletes:
        hits, swept_files, swept_skipped = sweep_for_deleters(root)
        for hit_path, number, description, line in hits:
            problems.append(
                f"{hit_path}:{number} executes {description} — `{line}` — while "
                f"{', '.join(declaring_nothing_deletes)} record(s) that nothing deletes. Whichever "
                "registry that command addresses, the enumeration has to name it and say what "
                "protects what it can reach, in THIS diff. "
                "Doc/Architecture/FleetRegistryRetention.")

    # 🚨 THE DENOMINATOR, printed whatever the verdict. "No deleter found" and "nothing was swept"
    # are the same output without it, and that is the confusion this whole family is made of.
    print(publication_denominator)
    print(f"registry retention: {len(registries)} registry(ies) declared "
          f"({', '.join(sorted(registries))}); {charts_checked} chart(s) re-derived; "
          f"{acl_rules_checked} ACL rule(s) read; "
          f"{swept_files} executable file(s) swept for a deleter, {swept_skipped} skipped "
          "(this script, which carries every pattern as a literal, and the ACR record, whose job "
          "is to name deleters).")
    # 🚨 PRINTED WHATEVER THE VERDICT, and NOT only when the list is empty. A green run over an
    # enumeration with an open row must not read as a fully measured one — that is the same
    # not-checked-spelled-as-clean confusion one level down (#4315 review).
    for host in sorted(unverified_by_host):
        open_rows = unverified_by_host[host]
        print(f"  {host}: {len(open_rows)} mechanism(s) UNVERIFIED"
              + (f" — {', '.join(open_rows)}" if open_rows else "")
              + ("; no cleanup may be authorized here while that stands" if open_rows else ""))
    for problem in problems:
        print(f"::error::{problem}")
    if problems:
        return 1
    print("  every declared registry says what deletes from it, the pairing with its disposition "
          "holds, and a `nothing-deletes` enumeration names at least one mechanism that IS "
          "present — so it inspected something — while an UNVERIFIED one is counted apart from a "
          "measured absence and keeps `cleanupAuthorized` false.")
    print("  the committed chart still agrees: no Job or CronJob beside the registry, the "
          "`maintenance:` stanza carries exactly the declared keys and windows, every ACL rule "
          "granting `delete` still names the declared principal, and no executable line anywhere "
          "runs a deletion against a registry.")
    print("  and every registry this fleet PUSHES to is accounted for by the table — derived from "
          "the publishing lanes rather than re-read from the record, which is the half that was "
          "missing while `ghcr.io` read 'never published by this fleet' (#4323).")
    return 0


def describe_purge_file(path: str) -> int:
    """Print, as JSON, what the `cmd:` steps of ONE recorded-or-live task definition actually are.

    🚨 ONE PARSER FOR BOTH SIDES. `acr-retention-tasks.sh` used to grep the live YAML for
    `--include-locked` and for `--filter '…'`, and a grep is defeated by the shell's own quoting:
    `--include-"locked"` executes as `--include-locked` and matches no search for that string. The
    tokenizer above is the only thing in this repository that reads a purge step correctly, so the
    shell asks it rather than keeping a second, weaker copy."""
    text = Path(path).read_text(encoding="utf-8")
    steps = [line for line in text.splitlines() if re.match(r"^\s*-\s+cmd:", line)]
    described = []
    for step in steps:
        invocations, parse_error = purge_invocations(step)
        for tokens in invocations:
            described.append({
                "filters": sorted(value for name, value in (
                    (tokens[i], tokens[i + 1]) for i in range(len(tokens) - 1))
                    if name == "--filter"),
                "includeLocked": "--include-locked" in tokens,
                "ago": option_value(tokens, "--ago")[1],
                "keep": option_value(tokens, "--keep")[1],
            })
        if not invocations:
            described.append({"notAPurge": step.strip()[:160],
                              "parseError": parse_error or None})
    print(json.dumps({"steps": len(steps), "purges": described}))
    return 0


# ── Self-test: both arms, offline ──────────────────────────────────────────────────────────────

FIXTURE_WORKFLOW = """
name: fixture
env:
  MW_PLATFORM_REF: bbcb22f256240c7132fea1e56c7df3f97564e648
  MW_TEST_DIGEST: sha256:df19f10afc1f807441b403eb0dfa4b8645d5187b830f77ad080def7fc65b391f
  MW_PORTAL_DIGEST: sha256:dab2a7b3a7e1523d7d728d6a9a397abef5f98b0e1e0ff8f02395a433bdedf97d
jobs:
  a:
    steps:
      - env:
          BAKE_IMAGE: meshweaver.azurecr.io/mw-plugin-test@${{ env.MW_TEST_DIGEST }}
          MW_PORTAL_IMAGE: ${{ format('meshweaver.azurecr.io/memex-portal-ai@{0}', env.MW_PORTAL_DIGEST) }}
        run: echo hi
"""

FIXTURE_MALFORMED = """
env:
  MW_TEST_DIGEST: sha256:PLACEHOLDER
  TESTER_IMAGE: meshweaver.azurecr.io/mw-plugin-test
"""

FIXTURE_UNCLASSIFIED = """
env:
  SOMETHING_DIGEST: sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
"""

FIXTURE_OVERLAY = """
config:
  memex_portal:
    Hosting__Deployment: "memex-cloud"
ingress:
  enabled: true
  host: "memex.example.cloud"
portal:
  image: "meshweaver.azurecr.io/memex-portal-ai:3.0.0-ci.7926"
migration:
  image: meshweaver.azurecr.io/memex-migration:3.0.0-ci.7926
watcher:
  image: meshweaver.azurecr.io/memex-log-watcher:latest
templated:
  image: "meshweaver.azurecr.io/memex-portal-ai:{{ .Values.tag }}"
whisper:
  image:
    repository: meshweaver.azurecr.io/whisper-swiss-german
    tag: "1.7.4"
    pullPolicy: IfNotPresent
other:
  image:
    repository: meshweaver.azurecr.io/hosting-operator
  notATag: 9
"""


class FakeRegistry(Registry):
    """The registry, as a dict — so every arm can be falsified with no network and no credential."""

    def __init__(self, manifests: dict[str, list[Manifest]],
                 tags: dict[tuple[str, str], tuple[str | None, str]],
                 repo_error: str | None = None,
                 locked_tags: set[tuple[str, str]] | None = None,
                 tag_list_error: str | None = None,
                 children: dict[tuple[str, str], list[str]] | None = None,
                 children_error: str | None = None) -> None:
        super().__init__("fake")
        self._manifests = manifests
        self._tags = tags
        self._repo_error = repo_error
        self.locked_tags = set(locked_tags or set())
        self.tag_list_error = tag_list_error
        self.tag_writes: list[tuple[str, str, bool]] = []
        self._children = children if children is not None else dict(FAKE_CHILDREN)
        self.children_error = children_error
        self.writes: list[tuple[str, str, bool]] = []
        # Whether a write actually MOVES the attribute. Default true (a healthy registry); an arm
        # sets it false to drive the "exit 0, nothing changed" case the read-back must catch.
        self.writes_take = True
        # Set to a message to make the read-back INDETERMINATE — which must not confirm a lock.
        self.read_back_error: str | None = None

    def control_probe(self) -> None:
        return None

    def repositories(self):
        if self._repo_error:
            return None, self._repo_error
        return sorted(self._manifests), ""

    def manifests(self, acr_repo: str):
        return list(self._manifests.get(acr_repo, [])), ""

    def resolve_tag(self, acr_repo: str, tag: str):
        return self._tags.get((acr_repo, tag), (None, "absent"))

    def set_delete_enabled(self, acr_repo: str, digest: str, enabled: bool):
        self.writes.append((acr_repo, digest, enabled))
        if not self.writes_take:
            # The sabotage this fixture exists to express: `az` exits 0 and the attribute does not
            # move. Nothing about the WRITE distinguishes it from a real one.
            return True, ""
        for manifests in self._manifests.values():
            for manifest in manifests:
                if manifest.acr_repo == acr_repo and manifest.digest == digest:
                    manifest.delete_enabled = enabled
        return True, ""

    def read_delete_enabled(self, acr_repo: str, digest: str):
        if self.read_back_error:
            return None, self.read_back_error
        for manifests in self._manifests.values():
            for manifest in manifests:
                if manifest.acr_repo == acr_repo and manifest.digest == digest:
                    return manifest.delete_enabled, ""
        return None, "INDETERMINATE: not in the fixture inventory"

    # -- the TAG half, which carries its own lock attribute -------------------------------------

    def tags(self, acr_repo: str):
        if self.tag_list_error:
            return None, self.tag_list_error
        return [Tag(acr_repo, name, manifest.digest, (acr_repo, name) not in self.locked_tags)
                for manifest in self._manifests.get(acr_repo, [])
                for name in manifest.tags], ""

    def index_children(self, acr_repo: str, digest: str):
        if self.children_error:
            return None, self.children_error
        return list(self._children.get((acr_repo, digest), [])), ""

    def set_tag_delete_enabled(self, acr_repo: str, tag: str, enabled: bool):
        self.tag_writes.append((acr_repo, tag, enabled))
        if not self.writes_take:
            return True, ""
        if enabled:
            self.locked_tags.discard((acr_repo, tag))
        else:
            self.locked_tags.add((acr_repo, tag))
        return True, ""

    def read_tag_delete_enabled(self, acr_repo: str, tag: str):
        if self.read_back_error:
            return None, self.read_back_error
        if not any(name == tag for manifest in self._manifests.get(acr_repo, [])
                   for name in manifest.tags):
            return None, "INDETERMINATE: not in the fixture inventory"
        return (acr_repo, tag) not in self.locked_tags, ""


TESTER = "sha256:df19f10afc1f807441b403eb0dfa4b8645d5187b830f77ad080def7fc65b391f"
PORTAL = "sha256:dab2a7b3a7e1523d7d728d6a9a397abef5f98b0e1e0ff8f02395a433bdedf97d"
OVERLAY_PORTAL = "sha256:" + "c" * 64
OVERLAY_MIGRATION = "sha256:" + "d" * 64
WHISPER = "sha256:" + "e" * 64
STALE = "sha256:" + "f" * 64


def _scan1(gh_repo: str, text: str):
    scan = consistency.RepoScan(gh_repo=gh_repo)
    sites, lanes, malformed, platform_set, platform_refs = consistency.extract("ci.yml", text)
    scan.workflows = 1
    scan.sites.extend(sites)
    scan.malformed.extend(malformed)
    return scan


def _scan2(gh_repo: str, text: str, where: str = "deployments/aks/x/values.x.yaml",
           registry: str = "meshweaver") -> OverlayScan:
    scan = OverlayScan(gh_repo=gh_repo, files=1)
    pins, floating = extract_overlay_pins(text, registry)
    scan.pins = [(repo, tag, where) for repo, tag in pins]
    scan.floating = [(repo, tag, where) for repo, tag in floating]
    scan.instances = [(ident, host, where) for ident, host in extract_overlay_instances(text)]
    # Mirrors both production scanners — ARM 29 compares the call lists, and an out-of-scope arm
    # driven over a harness that never extracts a foreign pin would be about nothing.
    scan.foreign = [(host, repo, tag, where)
                    for host, repo, tag in extract_foreign_pins(text, registry)]
    return scan


# One overlay, in the shape every real one has: it pins its images AND names the installation it
# configures, with the ingress host that answers /api/version.
FIXTURE_OVERLAY_INSTANCE = """
config:
  memex_portal:
    Hosting__Deployment: "memex"
portal:
  image: "meshweaver.azurecr.io/memex-portal-ai:3.0.0-ci.7926"
memex_migration:
  image: "meshweaver.azurecr.io/memex-migration:3.0.0-ci.7926"
ingress:
  enabled: true
  host: "memex.example.cloud"
"""

# 🚨 THE REAL SHAPE, taken from Systemorph/Memex deployments/aks/build/values.build.public.yaml on
# 2026-09-13: a LIVE installation whose images are pinned in the fleet's own registry rather than
# in the ACR this lane locks. The overlay pins two images; the ACR-scoped extractor sees ZERO.
FIXTURE_OVERLAY_FOREIGN = """
config:
  memex_portal:
    Hosting__Deployment: "build"
portal:
  image: "cr.meshweaver.cloud/memex-portal-ai:3.0.0-ci.8411"
migration:
  image: "cr.meshweaver.cloud/memex-migration:3.0.0-ci.8411"
ingress:
  enabled: true
  host: "build.example.cloud"
"""

INDEX = "application/vnd.oci.image.index.v1+json"
RUNNING = "sha256:" + "a" * 64          # the set the installation is actually running
RUNNING_TWIN = "sha256:" + "b" * 64     # its migration twin, same core commit
RUNNING_COMMIT = "1234567" + "0" * 33
# The platform manifests under the running index — untagged, as buildx leaves them once the
# `staging-…-linux-x64` tags age out of the same purge step.
RUNNING_AMD64 = "sha256:" + "1a" * 32
RUNNING_ARM64 = "sha256:" + "2b" * 32
FAKE_CHILDREN = {("memex-portal-ai", RUNNING): [RUNNING_AMD64, RUNNING_ARM64]}


def _inventory(locked: set[tuple[str, str]] | None = None,
               extra: list[Manifest] | None = None) -> dict[str, list[Manifest]]:
    locked = locked or set()

    def make(acr_repo: str, digest: str, tags: list[str], media_type: str = "") -> Manifest:
        return Manifest(acr_repo, digest, (acr_repo, digest) not in locked, True, tags, media_type)

    short = RUNNING_COMMIT[:7]
    inventory = {
        "mw-plugin-test": [make("mw-plugin-test", TESTER, ["3.0.0-ci.7917"])],
        "memex-portal-ai": [make("memex-portal-ai", PORTAL, ["3.0.0-ci.7917"]),
                            make("memex-portal-ai", OVERLAY_PORTAL, ["3.0.0-ci.7926"]),
                            # What the installation is RUNNING: a set the overlay does NOT pin,
                            # reachable only from the instance's own answer. This is Memex#219's
                            # third shape as a fixture — memex-cloud on 3.0.0-ci.8399 while its
                            # committed pin still read 8372.
                            make("memex-portal-ai", RUNNING,
                                 ["3.0.0-ci.9999", short, f"staging-{short}-4242"], INDEX),
                            make("memex-portal-ai", RUNNING_AMD64, []),
                            make("memex-portal-ai", RUNNING_ARM64, [])],
        "memex-migration": [make("memex-migration", OVERLAY_MIGRATION, ["3.0.0-ci.7926"]),
                            make("memex-migration", RUNNING_TWIN,
                                 ["3.0.0-ci.9999", f"{short}-pabcdef1"])],
        "whisper-swiss-german": [make("whisper-swiss-german", WHISPER, ["1.7.4"])],
    }
    for manifest in extra or []:
        inventory.setdefault(manifest.acr_repo, []).append(manifest)
    return inventory


FAKE_TAGS = {
    ("memex-portal-ai", "3.0.0-ci.7926"): (OVERLAY_PORTAL, ""),
    ("memex-migration", "3.0.0-ci.7926"): (OVERLAY_MIGRATION, ""),
    ("whisper-swiss-german", "1.7.4"): (WHISPER, ""),
}


def _answers(commit: str | None = RUNNING_COMMIT, error: str = ""):
    """A stand-in for `/api/version`. `error` drives the installation that will not answer."""
    def probe(_host: str):
        return (commit, "3.0.0+" + (commit or ""), error)
    return probe


def _drive(axis1, axis2, registry: FakeRegistry, apply: bool = True,
           release_enabled: bool = False,
           roster: dict[str, tuple[str, str, str]] | None = None,
           probe=None,
           dispositions: dict[str, tuple[str, str]] | None = None,
           publications_root: str | None = None,
           ) -> tuple[Plan, list[str], FakeRegistry]:
    """The SAME sequence `run()` performs, minus the report — so the self-test falsifies the real
    decision path rather than a paraphrase of it. Its per-lock chatter is swallowed; the assertions
    read `registry.writes` / `registry.tag_writes`, which is what would actually reach the registry.

    🚨 THIS MUST STAY IN STEP WITH `run()`. A harness that drives a subset of the decision path is a
    guard whose subject moved and whose roots did not — the arms keep passing while the thing they
    describe is no longer what runs. `--self-test` asserts the two agree (ARM: harness parity)."""
    with contextlib.redirect_stdout(io.StringIO()):
        inventory, inventory_errors = read_inventory(registry)
        plan = build_plan(axis1, axis2)
        plan.blockers.extend(extractor_control())
        plan.instances, instance_blockers = build_instances(
            axis2, roster or {}, probe or _answers())
        plan.blockers.extend(instance_blockers)
        if not plan.instances and not any(scan.unreadable for scan in axis2):
            plan.blockers.append("AXIS 3 found ZERO installations across the whole fleet.")
        repositories_of = {
            instance.id: sorted({repo for scan in axis2 for repo, _tag, where in scan.pins
                                 if f"{scan.gh_repo} {where}" == instance.source})
            for instance in plan.instances
        }
        # Read the same way `run()` reads it — from a root — so ARM 29's parity check is answered
        # by the harness doing the step, not by the harness declaring it did.
        classify_foreign_registries(
            plan, dispositions or {},
            read_registry_publications(publications_root) if publications_root else {})
        resolve_running_sets(plan, inventory, repositories_of)
        resolve_and_classify(plan, registry, inventory, inventory_errors)
        classify_tags(plan, registry)
        plan.inventory_complete = not plan.blockers
        failures: list[str] = []
        if apply:
            failures.extend(apply_locks(plan, registry))
            failures.extend(apply_tag_locks(plan, registry))
        if (release_enabled and apply
                and not (plan.blockers or failures) and plan.inventory_complete):
            failures.extend(apply_releases(plan, registry))
    return plan, failures, registry


def self_test() -> int:
    failures: list[str] = []

    def check(condition: bool, message: str) -> None:
        if not condition:
            failures.append(message)

    # ── ARM 1: a pin present ⇒ a lock is REQUESTED, on both axes ────────────────────────────────
    axis1 = [_scan1("Systemorph/Fixture", FIXTURE_WORKFLOW)]
    axis2 = [_scan2("Systemorph/Memex", FIXTURE_OVERLAY)]
    plan, fails, registry = _drive(axis1, axis2, FakeRegistry(_inventory(), FAKE_TAGS))
    check(not plan.blockers, f"ARM 1: a clean fleet produced blockers: {plan.blockers}")
    check(not fails, f"ARM 1: a clean fleet produced failures: {fails}")
    locked = {(repo, digest) for repo, digest, enabled in registry.writes if enabled is False}
    check(("mw-plugin-test", TESTER) in locked,
          "ARM 1: an axis-1 `env:` digest pin did not produce a lock")
    check(("memex-portal-ai", PORTAL) in locked,
          "ARM 1: an axis-1 `format(...)`-bound digest pin did not produce a lock")
    check(("memex-portal-ai", OVERLAY_PORTAL) in locked,
          "ARM 1: an axis-2 inline overlay TAG pin did not produce a lock")
    check(("memex-migration", OVERLAY_MIGRATION) in locked,
          "ARM 1: an axis-2 unquoted overlay TAG pin did not produce a lock")
    check(("whisper-swiss-german", WHISPER) in locked,
          "ARM 1: helm's SPLIT repository:/tag: shape did not produce a lock — the shape both "
          "whisper charts in the fleet actually use")
    check(not any(enabled for _, _, enabled in registry.writes),
          "ARM 1: a lock run wrote an UNLOCK")
    # 5 from the two committed axes + 2 the INSTALLATION is running that no file pins (AXIS 3)
    # + the 2 platform manifests under the running index, which a lock on the index does not cover.
    check(len(plan.wanted) == 9, f"ARM 1: expected 9 wanted manifests, got {len(plan.wanted)}")

    # A floating tag is reported and NOT locked — locking `:latest` pins bytes that move.
    check(not any(repo == "memex-log-watcher" for repo, _, _ in registry.writes),
          "ARM 1: a FLOATING tag (`:latest`) was locked; it has no fixed manifest to protect")
    # A templated value is not a pin at all.
    check(all(digest != "{{" for _, digest, _ in registry.writes),
          "ARM 1: a `{{ … }}` template was read as a tag")

    # Official releases remain protected even when no current deployment pins them.
    official_digest = "sha256:" + "1" * 64
    official = Manifest("memex-portal-ai", official_digest, True, True, ["3.0.0"])
    plan, fails, registry = _drive(axis1, axis2,
                                  FakeRegistry(_inventory(extra=[official]), FAKE_TAGS))
    check(not fails, "OFFICIAL: protection failed")
    check(("memex-portal-ai", official_digest, False) in registry.writes,
          "OFFICIAL: an unpinned official release was left purgeable")
    plan, _, registry = _drive(axis1, axis2,
                              FakeRegistry(_inventory(extra=[Manifest("memex-portal-ai", official_digest,
                                  False, True, ["3.0.0"])]), FAKE_TAGS),
                              release_enabled=True)
    check(not any(m.digest == official_digest for m in plan.release_candidates),
          "OFFICIAL: a protected release became an unpinned unlock candidate")
    check(("memex-portal-ai", official_digest, True) not in registry.writes,
          "OFFICIAL: an enabled release arm unlocked an official release")

    unknown = Manifest("memex-portal-ai", "sha256:" + "2" * 64, True, True,
                       ["v999.0.0", "999.0.0"])
    ci_only = Manifest("memex-portal-ai", "sha256:" + "3" * 64, True, True,
                       ["3.0.0-ci.9000"])
    plan, _, registry = _drive(axis1, axis2,
                              FakeRegistry(_inventory(extra=[unknown, ci_only]), FAKE_TAGS))
    check((unknown.acr_repo, unknown.digest, False) in registry.writes,
          "OFFICIAL: an unknown support mapping was treated as end of support")
    check(sum(w.digest == unknown.digest for w in plan.wanted) == 1,
          "OFFICIAL: two release tags produced duplicate locks for one digest")
    check(not any(w.digest == ci_only.digest for w in plan.wanted),
          "OFFICIAL: an unpinned CI build was retained as an official release")

    malformed_release = Manifest("memex-portal-ai", "", True, True, ["3.0.0"])
    plan, _, _ = _drive(axis1, axis2,
                       FakeRegistry(_inventory(extra=[malformed_release]), FAKE_TAGS))
    check(any("official release" in b and "valid" in b for b in plan.blockers),
          "OFFICIAL: a release without a usable digest was reported as protectable")

    third_party = Manifest("grafana/loki", "sha256:" + "4" * 64, True, True, ["3.3.2"])
    helper = Manifest("memex-log-watcher", "sha256:" + "5" * 64, True, True, ["1.3.0"])
    plan, _, _ = _drive(axis1, axis2,
                       FakeRegistry(_inventory(extra=[third_party, helper]), FAKE_TAGS))
    check(not any(w.digest in {third_party.digest, helper.digest} for w in plan.wanted),
          "OFFICIAL: third-party or independently versioned helper images were classified as MW releases")

    # ── ARM 2: idempotence — an already-protected manifest is not re-locked ─────────────────────
    plan, _, registry = _drive(
        axis1, axis2,
        FakeRegistry(_inventory(locked={("mw-plugin-test", TESTER)}), FAKE_TAGS))
    check(any(w.acr_repo == "mw-plugin-test" for w in plan.already_protected),
          "ARM 2: an already-locked manifest was not recognised as protected")
    check(("mw-plugin-test", TESTER) not in
          {(r, d) for r, d, _ in registry.writes},
          "ARM 2: an already-locked manifest was locked again")

    # ── ARM 3: a repository that could not be READ ⇒ RED, and nothing released ──────────────────
    #
    # 🚨 EVERY "nothing was released" ASSERTION BELOW RUNS AGAINST `inventory_with_stale`, an
    # inventory that HOLDS a release candidate. The first draft used the clean inventory, where
    # there was nothing to release in the first place — so the assertion held no matter what the
    # guard did, and sabotaging the guard (`release_enabled and apply` with the blocker test
    # deleted) left the whole self-test GREEN. A check that cannot fail is not a check; it was
    # found by falsifying this file rather than by reading it, 2026-09-07.
    stale = Manifest("memex-portal-ai", STALE, False, True, ["3.0.0-ci.7574"])
    inventory_with_stale = _inventory(extra=[stale])

    broken = consistency.RepoScan(gh_repo="Systemorph/Unreadable")
    broken.unreadable = "the repository itself could not be read"
    plan, fails, registry = _drive([_scan1("Systemorph/Fixture", FIXTURE_WORKFLOW), broken],
                                   axis2, FakeRegistry(inventory_with_stale, FAKE_TAGS),
                                   release_enabled=True)
    check(any(m.digest == STALE for m in plan.release_candidates),
          "ARM 3: the fixture holds no release candidate, so 'nothing was released' would hold "
          "however broken the guard is — the assertion must be able to fail")
    check(bool(plan.blockers), "ARM 3: an unreadable repository did not produce a blocker")
    check(any("Unreadable" in b for b in plan.blockers),
          "ARM 3: the blocker does not name the repository nobody could read")
    check(not any(enabled for _, _, enabled in registry.writes),
          "ARM 3: something was RELEASED on a run with an unreadable repository — the exact "
          "outage shape this job refuses")
    # …and locking still happened, because a lock cannot destroy anything.
    check(any(enabled is False for _, _, enabled in registry.writes),
          "ARM 3: a degraded run locked NOTHING; locks are additive and must still be applied")

    broken2 = OverlayScan(gh_repo="Systemorph/Memex")
    broken2.unreadable = "GitHub truncated the recursive tree listing"
    plan, _, registry = _drive(axis1, [broken2], FakeRegistry(inventory_with_stale, FAKE_TAGS),
                               release_enabled=True)
    check(any("truncated" in b for b in plan.blockers),
          "ARM 3b: a TRUNCATED git tree was read as a measured zero")
    check(not any(enabled for _, _, enabled in registry.writes),
          "ARM 3b: something was released while an overlay tree was unread")

    # ── ARM 4: ZERO pins ⇒ RED, per axis ───────────────────────────────────────────────────────
    empty1 = consistency.RepoScan(gh_repo="Systemorph/NoPins")
    empty1.workflows = 3
    plan, _, _ = _drive([empty1], axis2, FakeRegistry(_inventory(), FAKE_TAGS))
    check(any("AXIS 1 found ZERO" in b for b in plan.blockers),
          "ARM 4: a fleet with zero digest pins passed — a zero denominator is not a pass")
    plan, _, _ = _drive(axis1, [OverlayScan(gh_repo="Systemorph/Memex", files=2)],
                        FakeRegistry(_inventory(), FAKE_TAGS))
    check(any("AXIS 2 found ZERO" in b for b in plan.blockers),
          "ARM 4b: a fleet with zero overlay tag pins passed")

    # ── ARM 5: an UNCLASSIFIED pin (I5) ⇒ RED, and nothing released ─────────────────────────────
    plan, _, registry = _drive([_scan1("Systemorph/Fixture", FIXTURE_WORKFLOW),
                                _scan1("Systemorph/Odd", FIXTURE_UNCLASSIFIED)],
                               axis2, FakeRegistry(inventory_with_stale, FAKE_TAGS),
                               release_enabled=True)
    check(any("I5" in b for b in plan.blockers),
          "ARM 5: a digest whose ACR repository cannot be determined passed — an unclassified pin "
          "has NOT been checked, and there is no manifest to lock")
    check(not any(enabled for _, _, enabled in registry.writes),
          "ARM 5: something was released while a pin was unclassified")

    # ── ARM 6: a MALFORMED pin (I4) ⇒ RED ──────────────────────────────────────────────────────
    plan, _, _ = _drive([_scan1("Systemorph/Fixture", FIXTURE_WORKFLOW),
                         _scan1("Systemorph/Bad", FIXTURE_MALFORMED)],
                        axis2, FakeRegistry(_inventory(), FAKE_TAGS))
    check(any("I4" in b for b in plan.blockers),
          "ARM 6: `sha256:PLACEHOLDER` read as an absence rather than a defect")

    # ── ARM 7: a pinned manifest that is GONE ⇒ RED, and no lock claimed for it ─────────────────
    thin = _inventory()
    thin["mw-plugin-test"] = []
    plan, fails, registry = _drive(axis1, axis2, FakeRegistry(thin, FAKE_TAGS))
    check(any("does not exist in the registry" in b for b in plan.blockers),
          "ARM 7: a pin naming a manifest that no longer exists passed")
    check(("mw-plugin-test", TESTER) not in {(r, d) for r, d, _ in registry.writes},
          "ARM 7: a lock was attempted for a manifest that is gone")

    # ── ARM 8: an overlay TAG that does not resolve ⇒ RED ───────────────────────────────────────
    plan, _, _ = _drive(axis1, axis2, FakeRegistry(_inventory(), {}))
    check(any("does not resolve" in b for b in plan.blockers),
          "ARM 8: an overlay tag that resolves to nothing passed")
    plan, _, _ = _drive(axis1, axis2, FakeRegistry(
        _inventory(), {("memex-portal-ai", "3.0.0-ci.7926"): (None, "INDETERMINATE: az died"),
                       ("memex-migration", "3.0.0-ci.7926"): (OVERLAY_MIGRATION, ""),
                       ("whisper-swiss-german", "1.7.4"): (WHISPER, "")}))
    check(any("Indeterminate is not a pass" in b for b in plan.blockers),
          "ARM 8b: an INDETERMINATE registry answer was smoothed into a verdict")

    # ── ARM 9: a registry that cannot be enumerated ⇒ RED, not 'everything is unpinned' ─────────
    plan, _, registry = _drive(axis1, axis2,
                               FakeRegistry(inventory_with_stale, FAKE_TAGS,
                                            repo_error="AcrPull denied"),
                               release_enabled=True)
    check(any("could not enumerate the registry's repositories" in b for b in plan.blockers),
          "ARM 9: a registry that refused enumeration produced no blocker")
    check(not any("does not exist in the registry" in b for b in plan.blockers),
          "ARM 9: an unreadable registry was reported as 'every pinned manifest is gone' — the "
          "catastrophe-that-is-not-happening, which is the mirror of what this job catches")
    check(not any(enabled for _, _, enabled in registry.writes),
          "ARM 9: a release was attempted against a registry that could not be enumerated")

    # ── ARM 10: the release arm — reported always, acted on only when clean AND enabled ─────────
    plan, _, registry = _drive(axis1, axis2, FakeRegistry(inventory_with_stale, FAKE_TAGS),
                               release_enabled=False)
    check([m.digest for m in plan.release_candidates] == [STALE],
          f"ARM 10: the stale lock was not REPORTED as a release candidate: "
          f"{[m.digest for m in plan.release_candidates]}")
    check(not any(enabled for _, _, enabled in registry.writes),
          "ARM 10: the release arm acted while DISABLED — it ships off by design")
    plan, _, registry = _drive(axis1, axis2, FakeRegistry(inventory_with_stale, FAKE_TAGS),
                               release_enabled=True)
    check(("memex-portal-ai", STALE, True) in registry.writes,
          "ARM 10b: the release arm was enabled on a clean run and released nothing — the arm is "
          "inert, which would make its safety argument untestable")

    # ── ARM 11: report-only makes NO registry write at all ──────────────────────────────────────
    plan, _, registry = _drive(axis1, axis2, FakeRegistry(inventory_with_stale, FAKE_TAGS),
                               apply=False, release_enabled=True)
    check(registry.writes == [],
          f"ARM 11: report-only wrote to the registry: {registry.writes}")

    # ── ARM 11b: a WRITE THAT RETURNS 0 AND DOES NOT TAKE ⇒ RED, and nothing counted ────────────
    #
    # 🚨 The arm the whole job rests on. `az acr repository update` exiting 0 is a statement about
    # the REQUEST; the postcondition is the manifest's attribute. Before the read-back this case
    # was indistinguishable from a successful lock — the job printed `locked …`, counted it, and
    # reported `Protected N manifest(s)` over manifests the 03:00 purge could still delete. Deleting
    # the read-back must make this arm go red, which is what makes it a real assertion.
    registry = FakeRegistry(_inventory(), FAKE_TAGS)
    registry.writes_take = False
    plan, fails, registry = _drive(axis1, axis2, registry)
    check(bool(fails),
          "ARM 11b: a lock write that exits 0 WITHOUT moving deleteEnabled produced no failure — "
          "the job would report protection it does not have")
    check(any("still reads deleteEnabled=true" in f for f in fails),
          f"ARM 11b: the failure does not say the manifest is still unlocked: {fails}")
    check(plan.locked_now == 0,
          f"ARM 11b: {plan.locked_now} lock(s) were COUNTED although none took")

    # ── ARM 11c: a read-back that cannot answer is NOT a confirmation ───────────────────────────
    #
    # Same rule one step further in: INDETERMINATE is not `false`. A registry that accepts the write
    # and then refuses to say what the attribute is has not shown the lock took, and reporting it as
    # protected would be the "not checked reads as clean" failure this job exists to remove.
    registry = FakeRegistry(_inventory(), FAKE_TAGS)
    registry.read_back_error = "INDETERMINATE: the registry did not answer"
    plan, fails, registry = _drive(axis1, axis2, registry)
    check(bool(fails),
          "ARM 11c: a lock whose read-back is INDETERMINATE was accepted as protected")
    check(any("cannot be confirmed locked" in f for f in fails),
          f"ARM 11c: the failure does not name the unconfirmed read-back: {fails}")
    check(plan.locked_now == 0,
          f"ARM 11c: {plan.locked_now} unconfirmed lock(s) were counted as protected")

    # ── ARM 12: the two EXISTING extractors still agree about what a pin IS ─────────────────────
    #
    # The property `check-pin-set-consistency.py` was built with — "the two scripts deliberately
    # share their extraction SHAPES … so they can never disagree about what a pin IS" — is asserted
    # here rather than left to review, because this file's correctness rests on it: it imports one
    # of the two and the fleet's nightly existence sweep uses the other.
    mine = {(site.name, site.value, site.role)
            for site in consistency.extract("ci.yml", FIXTURE_WORKFLOW)[0]
            if site.kind == "digest"}
    theirs = {(pin.where.split(":", 1)[1], pin.digest, pin.acr_repos[0] if pin.acr_repos else None)
              for pin in existence.extract("Systemorph/Fixture", "ci.yml", FIXTURE_WORKFLOW)[0]}
    check(mine == theirs,
          f"ARM 12: the two pin extractors DISAGREE about this fixture.\n"
          f"    check-pin-set-consistency.py: {sorted(mine)}\n"
          f"    check-pinned-digests.py:      {sorted(theirs)}\n"
          "  They share their shapes on purpose so the fleet has ONE definition of a pin. Fix the "
          "divergence rather than this assertion.")

    # ── ARM 13: the overlay path filter is narrow, and measured ────────────────────────────────
    check(is_overlay_path("deployments/aks/memex/values.memex.public.yaml"),
          "ARM 13: Memex's real overlay path was rejected")
    check(is_overlay_path("deploy/whisper/helm/values.yaml"),
          "ARM 13: this repository's whisper chart values file was rejected")
    check(not is_overlay_path("deploy/aks/operator/test/fixtures/audit/clean/manifest.json"),
          "ARM 13: a TEST FIXTURE was accepted as a deployment overlay — those name tags that "
          "never existed and would red this job nightly")
    check(not is_overlay_path("deploy/aks/manifests/observability/log-watcher.yaml"),
          "ARM 13: a raw manifest was accepted as an overlay")
    check(not is_overlay_path("src/values.yaml"),
          "ARM 13: a values file outside any deploy path was accepted")


    # ══ AXIS 3 and the TAG half — MeshWeaver#3438 / #3858 / #3859 / #3860 ═══════════════════════

    clean1 = [_scan1("Systemorph/Fixture", FIXTURE_WORKFLOW)]
    clean2 = [_scan2("Systemorph/Memex", FIXTURE_OVERLAY)]

    # ── ARM 20: the set an installation is RUNNING is protected even though no file pins it ─────
    # Memex#219's third shape, and the one produced by NORMAL operation rather than by a broken
    # run: memex-cloud was rolled to 3.0.0-ci.8399 at 06:15Z on 2026-09-12 while its committed pin
    # still read 8372, so the nightly lock protected the manifest it was not running. Measured the
    # same morning: 3.0.0-ci.8399 read `deleteEnabled: true` on a repository the purge filters at
    # `--ago 7d --keep 10`.
    plan, fails, registry = _drive(clean1, clean2, FakeRegistry(_inventory(), FAKE_TAGS))
    locked = {(repo, digest) for repo, digest, enabled in registry.writes if enabled is False}
    check(not plan.blockers and not fails,
          f"ARM 20: a fleet whose installation answers produced blockers {plan.blockers} {fails}")
    check(("memex-portal-ai", RUNNING) in locked,
          "ARM 20: the manifest the installation is RUNNING was not locked — the overlay pins a "
          "different one, and deriving protection from the committed pin alone is the whole defect")
    check(("memex-migration", RUNNING_TWIN) in locked,
          "ARM 20: the migration twin of the running set was not locked; helm derives it FROM the "
          "portal tag (Memex#2555), so half a set is a broken deploy")
    running = next(i for i in plan.instances if i.id == "memex-cloud")
    check(len(running.manifests) == 2,
          f"ARM 20: expected the running closure to be 2 manifests, got {running.manifests}")

    # ── ARM 21: a TAG is locked, not only the manifest it resolves to ───────────────────────────
    # Measured on meshweaver.azurecr.io, 2026-09-12: `memex-portal-ai:3.0.0-ci.8372` — the tag both
    # production overlays pin — read deleteEnabled TRUE while its manifest read FALSE, and all
    # 1,396 tags of that repository were unlocked. acr-cli deletes a tag on the TAG's attribute:
    #   if includeLocked || (*(*tag.ChangeableAttributes).DeleteEnabled && …WriteEnabled)
    # so a manifest lock saves the bytes and loses the reference — `manifest unknown` all the same.
    tag_locked = {(repo, tag) for repo, tag, enabled in registry.tag_writes if enabled is False}
    check(("memex-portal-ai", "3.0.0-ci.7926") in tag_locked,
          "ARM 21: an overlay TAG pin did not produce a TAG lock")
    check(("whisper-swiss-german", "1.7.4") in tag_locked,
          "ARM 21: helm's split repository:/tag: pin did not produce a TAG lock")
    short = RUNNING_COMMIT[:7]
    check(("memex-portal-ai", short) in tag_locked,
          "ARM 21: the tag naming the set the installation runs was not locked")
    # 🚨 …and the VERSION tag on that same manifest, which is what the pod spec names and which no
    # committed file carries while the installation is ahead of its pin.
    check(("memex-portal-ai", "3.0.0-ci.9999") in tag_locked,
          "ARM 21: the version tag of the manifest the installation is RUNNING was left deletable. "
          "The overlay is behind, so axis 2 never names it; the pod spec does, and a purged tag "
          "breaks the next restart over a perfectly locked manifest")
    check(not any(enabled for _, _, enabled in registry.tag_writes),
          "ARM 21: a lock run wrote a tag UNLOCK")

    # …and the exact production shape: manifest already locked, tag not. It must still lock the tag.
    already = _inventory(locked={("memex-portal-ai", OVERLAY_PORTAL)})
    plan, fails, registry = _drive(clean1, clean2, FakeRegistry(already, FAKE_TAGS))
    check(("memex-portal-ai", "3.0.0-ci.7926", False) in registry.tag_writes,
          "ARM 21: a manifest that is already locked left its TAG unlocked — that is the live "
          "state measured on 2026-09-12 and it is what breaks the pin")

    # ── ARM 22: an installation that will not answer is INCOMPLETE, never zero consumers ────────
    plan, fails, registry = _drive(clean1, clean2, FakeRegistry(_inventory(), FAKE_TAGS),
                                   probe=_answers(None, "connection timed out"))
    check(any("could not be accounted for" in b and "memex-cloud" in b for b in plan.blockers),
          f"ARM 22: an installation that did not answer did not red, naming it: {plan.blockers}")
    check(not plan.inventory_complete,
          "ARM 22: an installation that did not answer still reported a COMPLETE inventory")

    # ── ARM 23: an incomplete inventory REFUSES the unlock arm (the #3859 interlock) ────────────
    stale = _inventory(locked={("memex-portal-ai", PORTAL)})
    plan, fails, registry = _drive([_scan1("Systemorph/Fixture", "name: nothing\n")], clean2,
                                   FakeRegistry(stale, FAKE_TAGS), release_enabled=True,
                                   probe=_answers(None, "connection timed out"))
    check(plan.release_candidates,
          "ARM 23: the fixture produced no release candidate, so the arm cannot be shown to be held")
    check(not any(enabled for _, _, enabled in registry.writes),
          "ARM 23: a run whose consumer inventory was INCOMPLETE released a lock. That is the "
          "failure this whole family exists to prevent: an installation that did not answer is "
          "indistinguishable, in the references it contributes, from one that consumes nothing")

    # ── ARM 24: silence is unavailability; only a declaration is retirement ─────────────────────
    check(any("instances.json" in b for b in plan.blockers) or
          any("could not be accounted for" in b for b in plan.blockers),
          "ARM 24: a silent installation neither red nor pointed at the roster")
    plan, fails, registry = _drive(clean1, clean2, FakeRegistry(_inventory(), FAKE_TAGS),
                                   roster={"memex-cloud": ("not-installed", "never stood up", "")},
                                   probe=_answers(None, "no such host"))
    check(not plan.blockers,
          f"ARM 24: a DECLARED not-installed instance still red: {plan.blockers}")
    declared = next(i for i in plan.instances if i.id == "memex-cloud")
    check(declared.state == "not-installed" and declared.reason,
          "ARM 24: the declaration did not reach the report, so the exemption is invisible")

    # ── ARM 24b: the roster REFUSES what it cannot act on, and the committed one is valid ───────
    with tempfile.TemporaryDirectory() as scratch:
        folder = Path(scratch) / ".github" / "acr-retention"
        folder.mkdir(parents=True)
        target = folder / ROSTER_PATH

        def roster_of(document: str) -> tuple[dict, list[str]]:
            target.write_text(document, encoding="utf-8")
            return read_instance_roster(scratch)

        _, problems = roster_of('{"instances": [{"id": "x", "state": "asleep", "reason": "r"}]}')
        check(any("expected one of" in problem for problem in problems),
              "ARM 24b: an unknown roster state was accepted — a typo would silently exempt an "
              "installation from ever having to answer")
        _, problems = roster_of('{"instances": [{"id": "x", "state": "retired"}]}')
        check(any("no `reason`" in problem for problem in problems),
              "ARM 24b: a retirement with no reason was accepted; that is the hand list again")
        _, problems = roster_of('{"instances": [{"state": "retired", "reason": "r"}]}')
        check(any("no `id`" in problem for problem in problems),
              "ARM 24b: a roster entry with no id was accepted")
        target.write_text("{ not json", encoding="utf-8")
        _, problems = read_instance_roster(scratch)
        check(problems, "ARM 24b: an UNREADABLE roster read as an empty one — the same "
                        "not-checked-spelled-as-clean confusion this job exists to remove")
        target.unlink()
        _, problems = read_instance_roster(scratch)
        check(problems, "ARM 24b: a MISSING roster read as an empty one")

    roster, problems = read_instance_roster(str(HERE.parent.parent))
    check(not problems,
          f"ARM 24b: this repository's own {ROSTER_PATH} does not validate: {problems}")

    # ── ARM 24c: the report states the WINDOW it is racing, and cannot state it from nothing ────
    windows, problem = retention_windows(str(HERE.parent.parent))
    check(windows and not problem,
          f"ARM 24c: this repository's own retention record yielded no statement of the window: "
          f"{problem}")
    # Whichever state the record is in, it must SAY which — a window with the `--ago` that defines
    # it, or a declared pause naming what re-enables the task. Never an empty list.
    check(any("--ago" in line for line in windows)
          or (any("PAUSED since" in line for line in windows)
              and any("re-enable when:" in line for line in windows)),
          f"ARM 24c: the record stated neither an `--ago` window nor a declared pause with its "
          f"re-enable condition: {windows}")
    with tempfile.TemporaryDirectory() as scratch:
        empty, problem = retention_windows(scratch)
        check(not empty and problem,
              "ARM 24c: a MISSING retention record reported windows, or reported no problem — "
              "an unstatable window must say so, never print as an empty list")

    # ── ARM 32: another registry is NAMED, and the declaration must be COMPLETE ─────────────────
    # 🚨 THE LIVE CASE. On 2026-09-13 the scheduled run died saying installation `build` "pins no
    # image at all" — while its overlay pinned two images in `cr.meshweaver.cloud`. The lane was
    # red, nothing was locked, and `pause.reEnableWhen` reads "lock-pinned-digests is green", so
    # the false sentence was standing between the fleet and re-enabling cleanup.
    FLEET_UNLOCKABLE = {"cr.meshweaver.cloud": ("fleet-unlockable", "ours, not lockable here")}
    THIRD_PARTY = {"ghcr.io": ("third-party", "somebody else's")}
    foreign_scan = _scan2("Systemorph/Memex", FIXTURE_OVERLAY_FOREIGN,
                          "deployments/aks/build/values.build.public.yaml")
    check([h for h, _, _, _ in foreign_scan.foreign] == ["cr.meshweaver.cloud"] * 2,
          f"ARM 32: the foreign extractor did not see the two `cr.meshweaver.cloud` pins in the "
          f"real overlay shape: {foreign_scan.foreign}")
    check(not foreign_scan.pins,
          f"ARM 32: the ACR extractor claimed a pin from a non-ACR registry: {foreign_scan.pins}")

    plan, _, _ = _drive(clean1, clean2 + [foreign_scan], FakeRegistry(_inventory(), FAKE_TAGS),
                        probe=_answers())
    check(any("does not account for" in b and "cr.meshweaver.cloud" in b for b in plan.blockers),
          f"ARM 32: an UNDECLARED registry did not red naming it: {plan.blockers}")
    check(not any("pins no image at all" in b for b in plan.blockers),
          "ARM 32: the run still says an overlay with two pins in it 'pins no image at all'. That "
          "sentence is FALSE and it sends the reader to fix an extractor that is working")

    plan, _, _ = _drive(clean1, clean2 + [foreign_scan], FakeRegistry(_inventory(), FAKE_TAGS),
                        probe=_answers(), dispositions=FLEET_UNLOCKABLE)
    check(not plan.blockers,
          f"ARM 32: a DECLARED fleet-unlockable installation still blocked the run: {plan.blockers}")
    check([i.id for i in plan.instances if i.out_of_scope] == ["build"],
          "ARM 32: a declared out-of-scope installation was not marked as such, so it would be "
          "counted among the answered and read as PROTECTED")

    # 🚨 A `third-party` disposition must NOT make an installation out of scope: its portal images
    # are then accounted for by nothing that holds our images, which is a different incident.
    plan, _, _ = _drive(clean1, clean2 + [foreign_scan], FakeRegistry(_inventory(), FAKE_TAGS),
                        probe=_answers(),
                        dispositions={"cr.meshweaver.cloud": ("third-party", "wrong on purpose")})
    check(any("every one of them declared `third-party`" in b for b in plan.blockers),
          f"ARM 32: an installation whose ONLY images are declared third-party passed as out of "
          f"scope — nothing it runs is then accounted for by any registry of ours: {plan.blockers}")
    check(not any(i.out_of_scope for i in plan.instances),
          "ARM 32: a third-party disposition marked an installation OUT OF SCOPE")

    # 🚨 HALF COVERED IS NOT COVERED — the case a per-instance field cannot see.
    both = _scan2("Systemorph/Memex",
                  FIXTURE_OVERLAY_INSTANCE + '\nextra:\n'
                  '  image: "cr.meshweaver.cloud/memex-migration:3.0.0-ci.8411"\n',
                  "deployments/aks/half/values.half.public.yaml")
    plan, _, _ = _drive(clean1, clean2 + [both], FakeRegistry(_inventory(), FAKE_TAGS),
                        probe=_answers(), dispositions=FLEET_UNLOCKABLE)
    check(any("Half its running" in b for b in plan.blockers),
          f"ARM 32: an installation pinning in BOTH registries passed. Its ACR half would be "
          f"locked, the run would report success, and the other half — also what it is RUNNING — "
          f"would be protected by nothing: {plan.blockers}")
    # …and undeclared on a mixed overlay reds too, rather than being skipped because the ACR half
    # is non-empty (the branch a `if not repositories` guard never reaches).
    plan, _, _ = _drive(clean1, clean2 + [both], FakeRegistry(_inventory(), FAKE_TAGS),
                        probe=_answers())
    check(any("does not account for" in b for b in plan.blockers),
          f"ARM 32: a mixed-registry overlay with NO declaration was silently accepted because it "
          f"had in-scope repositories: {plan.blockers}")

    # 🚨 THE DECLARATION MUST COVER THE COMPLETE FOREIGN SET. One declared host plus one undeclared
    # one must not read as covered.
    two_hosts = _scan2("Systemorph/Memex",
                       FIXTURE_OVERLAY_FOREIGN + '\nsidecar:\n'
                       '  image: "quay.io/thing/sidecar:9.9"\n',
                       "deployments/aks/two/values.two.public.yaml")
    plan, _, _ = _drive(clean1, clean2 + [two_hosts], FakeRegistry(_inventory(), FAKE_TAGS),
                        probe=_answers(), dispositions=FLEET_UNLOCKABLE)
    check(any("quay.io" in b and "does not account for" in b for b in plan.blockers),
          f"ARM 32: a SECOND, undeclared registry was ignored because the first one was declared — "
          f"the declaration has to cover the whole foreign set: {plan.blockers}")

    # 🚨 A `*.azurecr.io` THAT IS NOT THIS REGISTRY IS FOREIGN. Extracting it as an in-scope
    # repository would look it up in — and lock it against — the wrong registry.
    other_acr = FIXTURE_OVERLAY_INSTANCE.replace("meshweaver.azurecr.io", "somebodyelse.azurecr.io")
    check(not extract_overlay_pins(other_acr, "meshweaver")[0],
          "ARM 32: a pin in a DIFFERENT *.azurecr.io was extracted as this lane's repository")
    check([h for h, _, _ in extract_foreign_pins(other_acr, "meshweaver")]
          == ["somebodyelse.azurecr.io"] * 2,
          f"ARM 32: a pin in a DIFFERENT *.azurecr.io was not reported as foreign: "
          f"{extract_foreign_pins(other_acr, 'meshweaver')}")
    check(len(extract_overlay_pins(FIXTURE_OVERLAY_INSTANCE, "meshweaver")[0]) == 2,
          "ARM 32: this registry's OWN pins stopped being extracted once the host is compared")

    # 🚨 A COMMENT IS PROSE, NOT A PIN — and one of the fleet's two `ghcr.io` references is exactly
    # that. With an undeclared registry now a blocker, matching inside a comment reds over a
    # sentence; this issue's own 2026-09-08 hand count found 11 of 31 tokens in comments.
    commented = ('# ghcr.io/actions/actions-runner:2.337.0 is what the runner is built from\n'
                 + FIXTURE_OVERLAY_FOREIGN)
    check([h for h, _, _ in extract_foreign_pins(commented, "meshweaver")]
          == ["cr.meshweaver.cloud"] * 2,
          f"ARM 32: a reference inside a COMMENT was extracted as a pin: "
          f"{extract_foreign_pins(commented, 'meshweaver')}")
    check(not extract_overlay_pins("# meshweaver.azurecr.io/memex-portal-ai:3.0.0\n",
                                   "meshweaver")[0],
          "ARM 32: an ACR reference inside a COMMENT was extracted as a pin")

    # 🚨 HELM'S SPLIT SHAPE, FOR A FOREIGN REGISTRY. The ACR path has always handled it; without
    # the same for foreign, an overlay written that way extracts as NOTHING and falls back into
    # `pins no image at all` — this bug in its second shape.
    split = """
config:
  memex_portal:
    Hosting__Deployment: "split"
portal:
  image:
    repository: cr.meshweaver.cloud/memex-portal-ai
    tag: "3.0.0-ci.8411"
ingress:
  host: "split.example.cloud"
"""
    check([h for h, _, _ in extract_foreign_pins(split, "meshweaver")] == ["cr.meshweaver.cloud"],
          f"ARM 32: helm's split `repository:`/`tag:` shape produced NO foreign pin, so an overlay "
          f"written that way still falls into 'pins no image at all': "
          f"{extract_foreign_pins(split, 'meshweaver')}")

    # The disposition table itself refuses the shapes that turn it into a blanket exemption.
    with tempfile.TemporaryDirectory() as scratch:
        folder = Path(scratch) / ".github" / "acr-retention"
        folder.mkdir(parents=True)
        target = folder / ROSTER_PATH

        def dispositions_of(document: str):
            target.write_text(document, encoding="utf-8")
            return read_registry_dispositions(scratch)

        _, problems = dispositions_of('{"instances": []}')
        check(any("no `registries` table" in problem for problem in problems),
              f"ARM 32: a roster with NO registries table accounted for every registry: {problems}")
        _, problems = dispositions_of(
            '{"registries": {"x.io": {"disposition": "ignore", "reason": "r"}}}')
        check(any("expected one of" in problem for problem in problems),
              f"ARM 32: an unknown disposition was accepted: {problems}")
        _, problems = dispositions_of(
            '{"registries": {"x.io": {"disposition": "third-party"}}}')
        check(any("no `reason`" in problem for problem in problems),
              f"ARM 32: a registry declared with no reason was accepted: {problems}")
        target.write_text(
            '{"registries": {}, "instances": [{"id": "x", "state": "live",'
            ' "registry": "cr.meshweaver.cloud"}]}', encoding="utf-8")
        _, problems = read_instance_roster(scratch)
        check(any("per-instance `registry`" in problem for problem in problems),
              f"ARM 32: a per-instance `registry` key was accepted — it can never be shown to "
              f"cover every registry the fleet pins in: {problems}")

    dispositions, problems = read_registry_dispositions(str(HERE.parent.parent))
    check(not problems,
          f"ARM 32: this repository's own `registries` table does not validate: {problems}")
    check(set(dispositions) >= {"cr.meshweaver.cloud", "ghcr.io"},
          f"ARM 32: the two hosts measured across the fleet's overlays on 2026-09-13 are not both "
          f"declared: {sorted(dispositions)}")

    # ── ARM 33a: the DRY RUN — the other registry's protected set is DERIVED and PRINTED ────────
    # 🚨 A lock-less registry has no object to write a protected set INTO, so the derivation itself
    # is the entire artifact — and a derivation nobody can read is not one. The nightly run already
    # extracts these references; without this it discards them. The arm drives the REAL `report()`
    # over the real `build` overlay shape and reads what an operator would read.
    plan, _, _ = _drive(clean1, clean2 + [foreign_scan], FakeRegistry(_inventory(), FAKE_TAGS),
                        probe=_answers(), dispositions=FLEET_UNLOCKABLE)
    check(len(plan.foreign_references) == 2,
          f"ARM 33a: the committed references to a registry this lane cannot lock were extracted "
          f"and then DISCARDED, so the one artifact a lock-less registry can have is never "
          f"produced: {plan.foreign_references}")
    _printed = io.StringIO()
    with contextlib.redirect_stdout(_printed):
        report(plan, clean1, clean2 + [foreign_scan], REGISTRY_DEFAULT, _inventory(),
               apply=False, release_enabled=False, root=str(HERE.parent.parent))
    _dry_run = _printed.getvalue()
    check("PROTECTED SET — registries this lane cannot lock" in _dry_run,
          f"ARM 33a: the run printed NO protected set for a registry it cannot lock, so what a "
          f"cleanup there must keep is derivable and unread: {_dry_run[-1500:]}")
    # 🚨 THE DISPOSITION IS PART OF THE LABEL (#4323). The first live run said "a cleanup must
    # KEEP" over `ghcr.io`, a host declared `third-party` where this fleet runs no cleanup at all —
    # the same sentence about a store we retain and a store somebody else retains.
    check("cr.meshweaver.cloud (declared fleet-unlockable): 2 committed reference(s) a cleanup "
          "must KEEP" in _dry_run,
          f"ARM 33a: the protected set did not name the host, its DISPOSITION and its count: "
          f"{_dry_run[-1500:]}")
    check("memex-portal-ai:3.0.0-ci.8411" in _dry_run,
          f"ARM 33a: the protected set printed a COUNT and not the references themselves. A "
          f"number nobody can check against the registry is not a dry run: {_dry_run[-1500:]}")
    # 🚨 AND IT MUST SAY WHAT IT IS NOT. A set complete for images and empty for plugin bundles,
    # reported as one number, is this whole mechanism's failure mode committed by its own report.
    check("PLUGIN BUNDLE family" in _dry_run and "floor, never a complete protected set" in _dry_run,
          f"ARM 33a: the protected set did not print its own INCOMPLETENESS, so a reader would "
          f"take a committed-pins floor for a complete answer: {_dry_run[-1500:]}")
    # 🚨 A `third-party` host gets a DIFFERENT SENTENCE, and the reason is printed with it (#4323).
    # This is the arm that would have caught the first live run: `ghcr.io` was labelled "a cleanup
    # must KEEP" over images this fleet runs no cleanup on — and two of the five turned out to be
    # OURS, on a host declared "never published by this fleet".
    _third_party_scan = _scan2("Systemorph/Memex", FIXTURE_OVERLAY_FOREIGN.replace(
        "cr.meshweaver.cloud", "ghcr.io"),
        "deployments/aks/memex-cloud/values.memexcloud.public.yaml")
    plan, _, _ = _drive(clean1, clean2 + [_third_party_scan],
                        FakeRegistry(_inventory(), FAKE_TAGS), probe=_answers(),
                        dispositions={"ghcr.io": ("third-party", "somebody else's")})
    _printed = io.StringIO()
    with contextlib.redirect_stdout(_printed):
        report(plan, clean1, clean2 + [_third_party_scan], REGISTRY_DEFAULT, _inventory(),
               apply=False, release_enabled=False, root=str(HERE.parent.parent))
    _tp = _printed.getvalue()
    check("(declared third-party)" in _tp and "NOT ours to retain" in _tp,
          f"ARM 33a: a THIRD-PARTY host was reported with the same 'a cleanup must KEEP' sentence "
          f"as a store this fleet actually retains: {_tp[-1200:]}")
    check("a cleanup must KEEP" not in _tp.split("(declared third-party)")[-1].split("🚨")[0],
          f"ARM 33a: the third-party block still claims a cleanup must keep its references: "
          f"{_tp[-1200:]}")
    check("so the declaration can be checked against what is really pinned" in _tp,
          "ARM 33a: the third-party block does not say WHY it is listed. Listing is what caught "
          "#4323 — two `systemorph/*` images on a host declared 'never published by this fleet' — "
          "and a count alone would have hidden it")

    # 🚨 AN UNDECLARED HOST GETS NO VERDICT AT ALL (#4324 review). It has already BLOCKED the run;
    # printing its references under "a cleanup must KEEP" hands the reader an actionable-looking
    # list produced by a run that refused — the unknown-reads-as-clean shape, one layer up.
    plan, _, _ = _drive(clean1, clean2 + [foreign_scan], FakeRegistry(_inventory(), FAKE_TAGS),
                        probe=_answers())          # no dispositions ⇒ undeclared ⇒ blocked
    check(plan.blockers, "ARM 33a: an UNDECLARED registry did not block the run, so the fallback "
                         "arm below would be testing a state that cannot happen")
    _printed = io.StringIO()
    with contextlib.redirect_stdout(_printed):
        report(plan, clean1, clean2 + [foreign_scan], REGISTRY_DEFAULT, _inventory(),
               apply=False, release_enabled=False, root=str(tempfile.gettempdir()))
    _undeclared = _printed.getvalue()
    check("UNCLASSIFIED" in _undeclared and "NO verdict about them" in _undeclared,
          f"ARM 33a: an UNDECLARED host was reported without saying so: {_undeclared[-1200:]}")
    check("a cleanup must KEEP" not in _undeclared,
          f"ARM 33a: an UNDECLARED host's references were printed under 'a cleanup must KEEP'. The "
          f"run REFUSED; a list that reads as actionable is the worst thing it can emit: "
          f"{_undeclared[-1200:]}")

    # 🚨 EVERY floating tag, not the string `latest` (#4324 review). `main`, `master`, `edge`,
    # `stable` and `nightly` move exactly as `latest` does and are PRESERVED by the extractor.
    for _moving in sorted(FLOATING_TAGS):
        _scan = _scan2("Systemorph/Memex",
                       FIXTURE_OVERLAY_FOREIGN.replace("3.0.0-ci.8411", _moving),
                       "deployments/aks/build/values.build.public.yaml")
        plan, _, _ = _drive(clean1, clean2 + [_scan], FakeRegistry(_inventory(), FAKE_TAGS),
                            probe=_answers(), dispositions=FLEET_UNLOCKABLE)
        _printed = io.StringIO()
        with contextlib.redirect_stdout(_printed):
            report(plan, clean1, clean2 + [_scan], REGISTRY_DEFAULT, _inventory(),
                   apply=False, release_enabled=False, root=str(HERE.parent.parent))
        _out = _printed.getvalue()
        # 🚨 THE CONTROL FIRST, then the check. `check(tag not in out or flagged)` passes VACUOUSLY
        # the day the fixture stops producing that reference — an arm that cannot fail.
        check(f":{_moving}" in _out,
              f"ARM 33a: the fixture produced NO reference tagged `{_moving}`, so the assertion "
              f"below would pass having checked nothing")
        check("a MOVING tag" in _out,
              f"ARM 33a: the floating tag `{_moving}` was printed as an ordinary protected pin. It "
              f"moves exactly as `latest` does, and checking one spelling misses the other five")

    # …and a fleet with no foreign reference prints no such section, rather than an empty one that
    # reads as "nothing needs protecting there".
    plan, _, _ = _drive(clean1, clean2, FakeRegistry(_inventory(), FAKE_TAGS), probe=_answers())
    _printed = io.StringIO()
    with contextlib.redirect_stdout(_printed):
        report(plan, clean1, clean2, REGISTRY_DEFAULT, _inventory(),
               apply=False, release_enabled=False, root=str(HERE.parent.parent))
    check("PROTECTED SET — registries this lane cannot lock" not in _printed.getvalue(),
          "ARM 33a: a fleet pinning in NO other registry still printed a protected-set section, "
          "which would read as an empty set rather than an absent question")

    # ── ARM 33: the SECOND question — what DELETES from a registry (#4230) ──────────────────────
    # 🚨 The arm that matters most is the LAST one, and it is the only one that is not about JSON:
    # a `reason` field reads exactly the same on the day a `CronJob` running a blob collection
    # lands beside the registry it describes. So the gate re-derives the CHART, and this drives a
    # sabotaged chart through it. Every arm is falsified in both directions — a good record passes
    # over a non-zero denominator, and each break fires on its own.
    _CHART = "deploy/helm/templates/registry"
    _GOOD_MAINTENANCE = "    maintenance:\n      uploadpurging:\n        enabled: true\n        age: 168h\n"
    # The registry's real ACL shape: the ONE rule carrying `delete` matches the publisher account,
    # and every other rule is pull-only.
    _GOOD_ACL = ('    acl:\n'
                 '      - match: {account: {{ $r.publisherUsername | quote }}}\n'
                 '        actions: ["push", "pull", "delete"]\n'
                 '      - match: {account: "/.+/"}\n'
                 '        actions: ["pull"]\n')

    def _registry_root(scratch: str, registries: dict, *,
                       chart_body: str | None = _GOOD_MAINTENANCE,
                       acl: str = _GOOD_ACL,
                       extra_chart: tuple[str, str] | None = None,
                       extra_workflow: str | None = None,
                       publishing: dict[str, str] | None = None) -> str:
        root = Path(scratch)
        record = root / ".github" / "acr-retention"
        record.mkdir(parents=True, exist_ok=True)
        (record / ROSTER_PATH).write_text(
            json.dumps({"registries": registries, "instances": []}), encoding="utf-8")
        # 🚨 THE PUBLISHING LANES ARE ALWAYS WRITTEN, and by DEFAULT they push only to the registry
        # this lane LOCKS. The publication accounting runs on every invocation of the gate — it has
        # no `if` asking whether its input exists, because that is the trapdoor this repository
        # keeps paying for — so a fixture with no workflows at all would red every arm below over
        # a derivation that read nothing. Pushing solely to `LANE_REGISTRY` leaves these arms
        # testing exactly what they test, and ARM 34 supplies its own `publishing` bodies.
        workflows = root / ".github" / "workflows"
        workflows.mkdir(parents=True, exist_ok=True)
        for name in PUBLISHING_WORKFLOWS:
            if publishing is not None and name not in publishing:
                continue          # a fixture that OMITS a lane, so "renamed away" can be driven
            (workflows / name).write_text(
                (publishing or {}).get(name, f'env:\n  ACR: {LANE_REGISTRY}\n'
                 '    run: |\n'
                 '      docker buildx imagetools create --tag "${{ env.ACR }}/memex-portal-ai:v" '
                 '"${{ env.ACR }}/memex-portal-ai:staging"\n'), encoding="utf-8")
        if chart_body is not None:
            chart = root / _CHART
            chart.mkdir(parents=True, exist_ok=True)
            (chart / "configmap.yaml").write_text(
                "data:\n  config.yml: |\n    storage:\n      delete:\n        enabled: true\n"
                + chart_body + "  auth_config.yml: |\n" + acl, encoding="utf-8")
        if extra_chart:
            (root / _CHART / extra_chart[0]).write_text(extra_chart[1], encoding="utf-8")
        if extra_workflow:
            workflows = root / ".github" / "workflows"
            workflows.mkdir(parents=True, exist_ok=True)
            (workflows / "x.yml").write_text(extra_workflow, encoding="utf-8")
        return scratch

    def _fleet(**overrides) -> dict:
        entry = {
            "disposition": "fleet-unlockable",
            "reason": "ours, and this lane cannot lock it",
            "retention": {
                "rule": "nothing-deletes",
                "cleanupAuthorized": False,
                "reason": "measured; nothing deletes",
                "chart": _CHART,
                "deleteGrantedTo": "publisherUsername",
                "maintenance": {"uploadpurging": "168h"},
                "deleters": [
                    {"mechanism": "uploadpurging", "present": True, "verdict": "incomplete uploads only"},
                    {"mechanism": "blob GC", "present": False, "verdict": "no job anywhere"},
                ],
            },
        }
        entry["retention"].update(overrides.pop("retention", {}))
        entry.update(overrides)
        return {"cr.example": entry}

    def _verdict(registries: dict, **kwargs) -> tuple[int, str]:
        with tempfile.TemporaryDirectory() as scratch:
            root = _registry_root(scratch, registries, **kwargs)
            stdout = io.StringIO()
            with contextlib.redirect_stdout(stdout):
                code = check_registry_retention(root)
            return code, stdout.getvalue()

    # The CONTROL: a well-formed record over a chart that agrees passes, and says what it inspected.
    code, output = _verdict(_fleet())
    check(code == 0, f"ARM 33: a well-formed retention declaration was REJECTED — the gate would "
                     f"red on the fix, which is how a gate stops being read: {output}")
    check("1 chart(s) re-derived" in output,
          f"ARM 33: the passing run re-derived ZERO charts, so the chart arm inspected nothing "
          f"while reporting success: {output}")

    code, output = _verdict({"cr.example": {"disposition": "fleet-unlockable", "reason": "r"}})
    check(code == 1 and "has no `retention` block" in output,
          f"ARM 33: a registry declaring nothing about what DELETES from it passed. That is the "
          f"exact state cr.meshweaver.cloud was in until #4230: {output}")

    code, output = _verdict({})
    check(code == 1 and "ZERO registries were" in output,
          f"ARM 33: an EMPTY registries table passed — zero asked and zero problems read "
          f"identically, which is the confusion this family is made of: {output}")

    code, output = _verdict(_fleet(retention={"rule": "nothing-deletes", "deleters": [
        {"mechanism": "uploadpurging", "present": False, "verdict": "v"},
        {"mechanism": "blob GC", "present": False, "verdict": "v"}]}))
    check(code == 1 and "NOT ONE is `present: true`" in output,
          f"ARM 33: an enumeration in which NOTHING is present passed. It reads exactly like a "
          f"clean one and it inspected nothing: {output}")

    code, output = _verdict(_fleet(retention={"deleters": [
        {"mechanism": "uploadpurging", "present": "true", "verdict": "v"}]}))
    check(code == 1 and "expected true, false or" in output,
          f"ARM 33: `present` as the STRING \"true\" passed. Every `is True` reader in this file "
          f"treats it as absent, so one typed quote mark empties the enumeration: {output}")

    code, output = _verdict(_fleet(retention={"chart": ""}))
    check(code == 1 and "names no `chart`" in output,
          f"ARM 33: a `nothing-deletes` claim with no committed source to re-derive it FROM "
          f"passed — a claim that checks itself passes the day it stops being true: {output}")

    code, output = _verdict(_fleet(retention={"chart": "deploy/helm/templates/moved"}))
    check(code == 1 and "not a directory" in output,
          f"ARM 33: a record pointing at a chart that has MOVED passed, having checked nothing: "
          f"{output}")

    # 🚨 THE ONE THAT IS NOT ABOUT JSON. A blob GC arrives as a scheduled job beside the registry,
    # and the record beside it still reads "nothing deletes".
    code, output = _verdict(_fleet(), extra_chart=("gc.yaml", 'kind: "CronJob"\n'))
    check(code == 1 and "CronJob" in output and "Re-derive" in output,
          f"ARM 33: a CronJob rendered beside a registry the record says nothing deletes from "
          f"passed. That is how `registry garbage-collect` arrives: {output}")
    code, output = _verdict(_fleet(), extra_chart=("gc.yaml", "kind: Job\n"))
    check(code == 1 and "Job" in output,
          f"ARM 33: an UNQUOTED `kind: Job` escaped the chart arm — the chart writes `kind:` both "
          f"ways: {output}")

    # …and the timer that IS there must keep saying what the record says it says.
    code, output = _verdict(_fleet(), chart_body=_GOOD_MAINTENANCE.replace("168h", "24h"))
    check(code == 1 and "retains for 24h" in output,
          f"ARM 33: the deletion window MOVED in the chart and the record did not, and it passed. "
          f"A window that moved without a decision is the whole of #3438: {output}")
    code, output = _verdict(_fleet(), chart_body=_GOOD_MAINTENANCE + "      readonly:\n        enabled: true\n")
    check(code == 1 and "maintenance" in output,
          f"ARM 33: a NEW key under `maintenance:` — the one stanza where a distribution deletes "
          f"on a timer — was accepted without the record ever seeing it: {output}")
    code, output = _verdict(_fleet(), chart_body="    storage:\n      redirect:\n        disable: false\n")
    check(code == 1 and "carries nothing" in output,
          f"ARM 33: the `maintenance:` stanza VANISHING from the chart passed. The record then "
          f"declares a timer that is not there, and nobody learns which: {output}")

    # …and nothing anywhere executes a deletion against a registry.
    code, output = _verdict(_fleet(), extra_workflow="jobs:\n  x:\n    steps:\n      - run: crane delete cr.example/x@sha256:aa\n")
    check(code == 1 and "a crane deletion" in output,
          f"ARM 33: an executable deletion landed in a workflow while the record said nothing "
          f"deletes, and it passed: {output}")
    code, output = _verdict(_fleet(), extra_workflow="jobs:\n  x:\n    steps:\n      - run: curl -X DELETE https://cr.example/v2/x/manifests/sha256:aa\n")
    check(code == 1 and "an HTTP DELETE" in output,
          f"ARM 33: a raw HTTP DELETE against a registry passed: {output}")
    # 🚨 …and the sweep must NOT fire on PROSE. The registry's own ConfigMap explains, in a comment,
    # that blobs go only by `registry garbage-collect`. A sweep that reds on the sentence saying a
    # thing does not happen is the defect this file already paid for on 2026-09-07.
    code, output = _verdict(_fleet(), extra_workflow="# blobs are removed only by `registry garbage-collect`\njobs: {}\n")
    check(code == 0,
          f"ARM 33: a deleter named inside a COMMENT was read as a deleter, so the sentence saying "
          f"a thing does not happen would red the lane: {output}")

    # The disposition and the rule are checked against EACH OTHER, in both directions.
    code, output = _verdict({"x.io": {"disposition": "third-party", "reason": "r", "retention": {
        "rule": "not-ours", "reason": "nothing of ours is there",
        "deleters": [{"mechanism": "m", "present": True, "verdict": "v"}]}}})
    check(code == 1 and "still enumerates `deleters`" in output,
          f"ARM 33: a third-party registry enumerated somebody else's deleters, which reads as a "
          f"verdict about OUR artifacts: {output}")
    code, output = _verdict(_fleet(retention={"rule": "not-ours", "deleters": None}))
    check(code == 1 and "may not carry" in output,
          f"ARM 33: a `fleet-unlockable` registry — one holding OUR images — declared `not-ours`, "
          f"a blanket exemption wearing a retention statement's clothes: {output}")

    # A derived protected set must refuse on incomplete, and the gate holds that rather than review.
    _derived = {"rule": "derived-protected-set", "reason": "a cleanup exists",
                "protectedSet": {"axes": [
                    {"axis": "overlay pins", "derivedFrom": "Systemorph/Memex overlays",
                     "onIncomplete": "refuse"}]}}
    code, output = _verdict(_fleet(retention=dict(_derived)))
    check(code == 0, f"ARM 33: a well-formed derived protected set was rejected: {output}")
    _loose = json.loads(json.dumps(_derived))
    _loose["protectedSet"]["axes"][0]["onIncomplete"] = "warn"
    code, output = _verdict(_fleet(retention=_loose))
    check(code == 1 and "DELETES what it could not see" in output,
          f"ARM 33: an axis that merely WARNS when its derivation is incomplete passed. On a "
          f"registry with no lock that is not 'protects less', it is 'deletes more': {output}")
    _empty = json.loads(json.dumps(_derived))
    _empty["protectedSet"]["axes"] = []
    code, output = _verdict(_fleet(retention=_empty))
    check(code == 1 and "names no `protectedSet.axes`" in output,
          f"ARM 33: a cleanup deleting the complement of a set nobody wrote down passed: {output}")

    # ── ARM 33b: the three arms the #4315 review found MISSING ──────────────────────────────────
    # 🚨 (1) UNVERIFIED is not ABSENT. `present: false` used to carry both "measured, and it is not
    # there" and "nobody could look", and the second is the row that matters: the fleet registry's
    # storage account is provisioned outside the chart, so whether a blob lifecycle policy deletes
    # referenced layers cannot be read from any committed file. Filed as `false`, the declaration
    # read as fully measured over an open question.
    _open = {"mechanism": "blob lifecycle policy", "present": UNVERIFIED,
             "verdict": "cannot be read from a committed file",
             "verifiedBy": "one read on the storage account"}
    code, output = _verdict(_fleet(retention={"deleters": [
        {"mechanism": "uploadpurging", "present": True, "verdict": "uploads only"}, _open]}))
    check(code == 0, f"ARM 33b: a declaration carrying an UNVERIFIED mechanism was REJECTED. It "
                     f"must be sayable, or the only way to pass is to file the unknown as a "
                     f"measured absence — which is the defect: {output}")
    check("1 mechanism(s) UNVERIFIED" in output and "blob lifecycle policy" in output,
          f"ARM 33b: an UNVERIFIED mechanism did not PRINT, so a green run over an open question "
          f"reads exactly like a fully measured one: {output}")
    _nameless = dict(_open)
    del _nameless["verifiedBy"]
    code, output = _verdict(_fleet(retention={"deleters": [
        {"mechanism": "uploadpurging", "present": True, "verdict": "uploads only"}, _nameless]}))
    check(code == 1 and "names no `verifiedBy`" in output,
          f"ARM 33b: an unknown with no stated way to answer it passed — indistinguishable from "
          f"one nobody intends to answer: {output}")
    # …and what the unknown BLOCKS is the ACT, not the gate.
    code, output = _verdict(_fleet(retention={"cleanupAuthorized": True, "deleters": [
        {"mechanism": "uploadpurging", "present": True, "verdict": "uploads only"}, _open]}))
    check(code == 1 and "cleanupAuthorized: true" in output and "UNVERIFIED" not in output.split("::error")[0].split("\n")[0],
          f"ARM 33b: a cleanup was AUTHORIZED with a deleter nobody had ruled out. On a registry "
          f"with no lock that is a deleter that may be running beside the cleanup: {output}")
    code, output = _verdict(_fleet(retention={"cleanupAuthorized": None}))
    check(code == 1 and "not a boolean" in output,
          f"ARM 33b: a `nothing-deletes` record that never states whether it authorizes a cleanup "
          f"passed — the next reader supplies the default: {output}")

    # 🚨 (2) THE ACL IS THE ONLY EVIDENCE BEHIND ONE VERDICT, and it was not re-derived at all.
    # "an installation holding an instance key cannot delete" is not a grep result — change the
    # rule carrying `delete` to match every account and the verdict is false with every other arm
    # of this gate still green.
    _OPEN_ACL = ('    acl:\n'
                 '      - match: {account: "/.+/"}\n'
                 '        actions: ["push", "pull", "delete"]\n')
    code, output = _verdict(_fleet(), acl=_OPEN_ACL)
    check(code == 1 and "grants the `delete` action" in output,
          f"ARM 33b: an ACL granting `delete` to EVERY authenticated account passed while the "
          f"record said only the publisher may delete: {output}")
    code, output = _verdict(_fleet(), acl="    acl: []\n")
    check(code == 1 and "ZERO ACL rules" in output,
          f"ARM 33b: an ACL that could not be LOCATED passed. An ACL nobody found is not an ACL "
          f"that grants nothing — the check inspected nothing while reporting success: {output}")
    code, output = _verdict(_fleet(retention={"deleteGrantedTo": ""}))
    check(code == 1 and "names no `deleteGrantedTo`" in output,
          f"ARM 33b: a record resting a verdict on the ACL without saying which principal that "
          f"rule may name passed: {output}")
    check("ACL rule(s) read" in _verdict(_fleet())[1],
          "ARM 33b: the passing run does not print how many ACL rules it read, so zero inspected "
          "and zero bad are the same answer")

    # 🚨 (3) THE SPELLINGS ARE THE COMMAND LINE'S. `curl -XDELETE` and `--request=DELETE` execute
    # identically to the spaced forms and matched nothing.
    for _compact in ("curl -XDELETE https://cr.example/v2/x/manifests/sha256:aa",
                     "curl --request=DELETE https://cr.example/v2/x",
                     "curl -X 'DELETE' https://cr.example/v2/x"):
        code, output = _verdict(
            _fleet(), extra_workflow=f"jobs:\n  x:\n    steps:\n      - run: {_compact}\n")
        check(code == 1 and "an HTTP DELETE" in output,
              f"ARM 33b: `{_compact}` was not seen as a deletion. A sweep defeated by a keystroke "
              f"still prints a denominator and a clean verdict: {output}")

    # 🚨 THE SWEEP IS ONLY EVIDENCE ONCE IT HAS BEEN SHOWN ABLE TO MATCH. Every pattern, over a
    # synthetic file carrying each spelling — the control that a zero over the real repository is
    # a measurement rather than a broken regex.
    with tempfile.TemporaryDirectory() as scratch:
        workflows = Path(scratch) / ".github" / "workflows"
        workflows.mkdir(parents=True)
        (workflows / "all.yml").write_text(
            "a: curl -X DELETE https://r/v2/x/manifests/sha256:aa\n"
            "b: curl --request DELETE https://r/v2/x\n"
            "c: crane delete r/x\n"
            "d: skopeo delete docker://r/x\n"
            "e: regctl manifest delete r/x\n"
            "f: oras manifest delete r/x\n"
            "g: az acr repository delete --name r\n"
            "h: registry garbage-collect /etc/config.yml\n", encoding="utf-8")
        hits, scanned, _ = sweep_for_deleters(scratch)
        check(scanned == 1, f"ARM 33: the deleter sweep scanned {scanned} file(s), not 1 — the "
                            f"denominator it prints would describe nothing")
        check({description for _, _, description, _ in hits} == {
                  description for _, description in DELETER_PATTERNS},
              f"ARM 33: the deleter sweep did not match every spelling it claims to — a zero over "
              f"the real repository would then be a broken regex, not a measurement. Matched: "
              f"{sorted({d for _, _, d, _ in hits})}")

    # And this repository's own declaration validates, over a non-zero denominator.
    check(check_registry_retention(str(HERE.parent.parent)) == 0,
          "ARM 33: this repository's own `registries` retention declaration does not validate")

    # ── ARM 34: what this fleet PUBLISHES to a registry, DERIVED from the lanes (#4323) ──────────
    # 🚨 THE ARM FOR THE DEFECT NO OTHER ARM COULD SEE. `.github/acr-retention/instances.json` said
    # of `ghcr.io` *"never published by this fleet"* and *"Nothing this fleet produces is stored on
    # ghcr.io"*, and BOTH fields validated green — because every arm above asks the record what it
    # says, and nothing asked the WORKFLOWS what they do. Measured 2026-09-15 in the job log of
    # run 34918214035 (02:27Z): main-cd's promote job pushed TWELVE tags across three of our own
    # repositories to `ghcr.io/systemorph/`, on a run like every other promoting run. `release.yml`,
    # the lane #4323 named, had never run at all — 0 runs — so the continuous lane is the whole of
    # the publication and the issue's framing understated it.
    HERE_ROOT = str(HERE.parent.parent)

    # (a) THE MEASUREMENT. Derived from the committed lanes, not from the record it checks.
    _targets, _producers, _problems, _references = publish_targets(HERE_ROOT)
    check(not _problems,
          f"ARM 34: the publication derivation could not read this repository's own lanes: "
          f"{_problems}")
    check(_references > 0,
          "ARM 34: ZERO image references were read across the publishing workflows. A derivation "
          "that parses nothing reports exactly like a fleet that publishes nothing — which is the "
          "sentence #4323 is made of")
    check(_targets.get("ghcr.io") == {"systemorph/memex-portal-ai", "systemorph/memex-migration",
                                      "systemorph/mw-plugin-test"},
          f"ARM 34: the three repositories this fleet pushes to ghcr.io were not derived: "
          f"{sorted(_targets.get('ghcr.io', []))}. That set is the fact #4323 turns on")
    check(LANE_REGISTRY in _targets,
          f"ARM 34: the registry this lane LOCKS was not among the derived push targets: "
          f"{sorted(_targets)}. It is skipped by name, and a skip over something that was never "
          f"found is not a skip")
    # 🚨 `--tag` IS NOT THE ONLY PUSH, and this is TWO arms because two separate readers decide it.
    # `cr.meshweaver.cloud` receives every consumer-visible tag through `mirror-image-to-registry.sh`
    # — no `--tag` anywhere on the line — and a publication mechanism the derivation cannot see is a
    # host the accounting never has to ask about, which is this issue one register down.
    check("cr.meshweaver.cloud" in _targets,
          f"ARM 34: the fleet's own registry was not derived as a push target: {sorted(_targets)}. "
          f"Its publications go through mirror-image-to-registry.sh, so a derivation that reads "
          f"only `--tag` sees an entire registry as published-to by nothing")
    # …and the SECOND reader: that script's destinations routinely sit on the line AFTER the source,
    # behind a `\`. Measured 2026-09-15 by disabling the join: the host survives (phase D's call is
    # one line) and `mw-plugin-test` DISAPPEARS — so the host's presence proves nothing about
    # continuations and the REPOSITORY SET is what this arm has to assert. An arm whose subject is
    # decided elsewhere passes having checked nothing.
    check(_targets.get("cr.meshweaver.cloud") == {"memex-portal-ai", "memex-migration",
                                                  "mw-plugin-test"},
          f"ARM 34: the fleet's own registry was derived with "
          f"{sorted(_targets.get('cr.meshweaver.cloud', []))} rather than all three repositories. "
          f"`mw-plugin-test` reaches it through a mirror call whose destinations are on the "
          f"CONTINUATION line, and it is the one repository that vanishes when the join stops")

    # 🚨 A WORKFLOW SPELLS ONE COMMAND ACROSS LINES TWO WAYS, AND ONLY ONE HAS A BACKSLASH
    # (#4362 review). `main-cd.yml:1260` and `:1416` mirror the portal and migration staging tags to
    # the fleet registry inside a YAML FOLDED scalar — `run: >`, the arguments on the following
    # physical lines, no continuation character anywhere — and read line-by-line those calls
    # publish to NOTHING. Driven as a fixture rather than against the live file so the arm keeps
    # its subject when the workflow moves.
    _folded = ('jobs:\n  x:\n    steps:\n      - name: mirror\n        run: >\n'
               '          .github/scripts/mirror-image-to-registry.sh\n'
               '          "src.example/portal:staging"\n'
               '          "folded.example/portal:staging"\n'
               '      - name: next\n        run: echo done\n')
    _folded_lines = dict(_continued_lines(_folded))
    check(any("mirror-image-to-registry.sh" in line and "folded.example/portal:staging" in line
              for line in _folded_lines.values()),
          f"ARM 34: a YAML FOLDED `run: >` command was not joined, so a publication spelled the way "
          f"main-cd spells its fleet-registry mirrors is derived from nothing: {_folded_lines}")
    check(any("echo done" in line and "mirror-image-to-registry.sh" not in line
              for line in _folded_lines.values()),
          "ARM 34: the folded-scalar join swallowed the SIBLING step. A join that runs past the "
          "block's indentation would merge unrelated commands and invent push targets")

    # 🚨 AND THE HOST MAY BE A PLAIN SHELL VARIABLE. `release.yml` writes `"$ACR/$repo:$VERSION"`
    # off the same top-level `env:` map that main-cd reaches as `${{ env.ACR }}`. Expanding only
    # the expression form left `$ACR` as the host — which has no dot, so the Docker-Hub short-name
    # test DISCARDED it silently: a push target dropped with no error by the one derivation whose
    # job is to make a dropped push target impossible.
    with tempfile.TemporaryDirectory() as scratch:
        lanes = Path(scratch) / ".github" / "workflows"
        lanes.mkdir(parents=True)
        (lanes / "main-cd.yml").write_text(
            'env:\n  ACR: shell.example\n'
            '    run: |\n'
            '      docker buildx imagetools create --tag "$ACR/portal:v" "$ACR/portal:staging"\n',
            encoding="utf-8")
        (lanes / "release.yml").write_text(
            'env:\n  ACR: shell.example\n'
            '    run: |\n'
            '      docker buildx imagetools create --tag "${ACR}/migration:v" "x"\n',
            encoding="utf-8")
        _shell, _, _shell_problems, _ = publish_targets(scratch)
        check(_shell.get("shell.example") == {"portal", "migration"},
              f"ARM 34: a bare `$ACR` / `${{ACR}}` host was not expanded from the workflow's own "
              f"`env:` map: {_shell} {_shell_problems}")

        # …and an UNRESOLVABLE one is a PROBLEM, never a silent discard. The order of the two tests
        # is the whole arm: an unresolved host has no dot, so the short-name test would drop it.
        (lanes / "main-cd.yml").write_text(
            'env:\n  ACR: shell.example\n'
            '    run: |\n'
            '      docker buildx imagetools create --tag "$MYSTERY/portal:v" "x"\n',
            encoding="utf-8")
        _, _, _unresolved, _ = publish_targets(scratch)
        check(any("could not resolve" in problem for problem in _unresolved),
              f"ARM 34: an UNRESOLVED registry host was silently discarded as a Docker Hub short "
              f"name instead of reported. A push target dropped with no error is exactly the "
              f"defect this derivation exists to make impossible: {_unresolved}")

    # (b) THIS REPOSITORY'S OWN RECORD now accounts for all of it.
    _live, _ = check_publication_accounting(HERE_ROOT, json.loads(
        (Path(HERE_ROOT) / ".github" / "acr-retention" / ROSTER_PATH).read_text(encoding="utf-8")
    )["registries"])
    check(not _live,
          f"ARM 34: this repository's own `registries` table does not account for what it "
          f"publishes: {_live}")

    # A lane that pushes to one foreign host, used by every negative below.
    _PUBLISHING = {
        "main-cd.yml": 'env:\n  ACR: ' + LANE_REGISTRY + '\n'
                       '    run: |\n'
                       '      docker buildx imagetools create --tag "ghcr.example/us/portal:v" '
                       '"${{ env.ACR }}/portal:staging"\n',
        "release.yml": 'env:\n  ACR: ' + LANE_REGISTRY + '\n'
                       '    run: |\n'
                       '      docker buildx imagetools create --tag "${{ env.ACR }}/portal:v" '
                       '"${{ env.ACR }}/portal:staging"\n',
    }

    def _published(**overrides) -> dict:
        block = {
            "repositories": ["us/portal"],
            "producedBy": [".github/workflows/main-cd.yml"],
            "runFrom": "no installation",
            "retention": {"rule": "operator-retained", "operator": "Someone Else",
                          "reason": "their store, their retention", "cleanupAuthorized": False},
        }
        block["retention"].update(overrides.pop("retention", {}))
        block.update(overrides)
        return block

    def _mixed(**overrides) -> dict:
        entry = {
            "disposition": "third-party",
            "reason": "what we PULL here is somebody else's",
            "publishes": _published(**overrides.pop("publishes", {})),
            "retention": {"rule": "not-ours", "reason": "scoped to what we pull"},
        }
        entry.update(overrides)
        return {"ghcr.example": entry}

    def _accounting(registries: dict, publishing=_PUBLISHING) -> tuple[int, str]:
        return _verdict(registries, publishing=publishing)

    # 🚨 THE READER THAT ONLY LABELS MUST NOT RAISE, AND MUST NORMALIZE LIKE THE ONE THAT REDS
    # (#4362 review). It runs inside the LIVE locking run, where an AttributeError would replace a
    # named blocker with a stack trace; and a repository written with stray whitespace would satisfy
    # the accounting arm — which strips — while matching nothing here, leaving ARM 34b's blocker
    # silently inert over a declaration that reads complete.
    with tempfile.TemporaryDirectory() as scratch:
        record = Path(scratch) / ".github" / "acr-retention"
        record.mkdir(parents=True)
        (record / ROSTER_PATH).write_text('{"registries": ["not", "a", "mapping"]}',
                                          encoding="utf-8")
        check(read_registry_publications(scratch) == {},
              "ARM 34: a non-mapping `registries` raised or returned a verdict from the lenient "
              "publication reader. `read_registry_dispositions` reds on that shape; this one must "
              "hand the run back its named blocker, never a stack trace")
        (record / ROSTER_PATH).write_text(json.dumps({"registries": {"h.example": {
            "publishes": {"repositories": ["  us/portal  ", ""]}}}}), encoding="utf-8")
        check(read_registry_publications(scratch) == {"h.example": {"us/portal"}},
              f"ARM 34: the publication reader did not strip its repository names the way the "
              f"accounting arm does, so a padded value passes the gate and matches no overlay: "
              f"{read_registry_publications(scratch)}")

    # The CONTROL: a mixed host that declares its publication passes.
    code, output = _accounting(_mixed())
    check(code == 0,
          f"ARM 34: a host that DECLARES what this fleet publishes there was rejected — the gate "
          f"would red on the fix: {output}")
    check("2 registry(ies) pushed to" in output and "1 left for the table to account for" in output,
          f"ARM 34: the passing run printed no publication DENOMINATOR. 'nothing publishes here' "
          f"and 'nothing was parsed' are the same output without one: {output}")

    # 🚨 THE DEFECT ITSELF, as a negative control: `third-party` + `not-ours` over a host we push to.
    _bare = _mixed()
    _bare["ghcr.example"].pop("publishes")
    code, output = _accounting(_bare)
    check(code == 1 and "declares no `publishes` block" in output and "us/portal" in output,
          f"ARM 34: a `third-party` host this fleet PUSHES THREE REPOSITORIES TO passed with a "
          f"retention that says nothing of ours is stored there. That is #4323 exactly, and it "
          f"validated green for a day: {output}")

    # A host we push to that the table does not mention AT ALL.
    code, output = _accounting({"other.example": {
        "disposition": "third-party", "reason": "r",
        "retention": {"rule": "not-ours", "reason": "r"}}})
    check(code == 1 and "does not declare that host at all" in output,
          f"ARM 34: a registry this fleet publishes to and the table never names was accepted: "
          f"{output}")

    # 🚨 A `fleet-unlockable` host needs NO `publishes` — its host-level declaration already says
    # our images live there, and its `retention` block already has to say what keeps them. Without
    # this the accounting would demand a second declaration of the same fact on cr.meshweaver.cloud.
    code, output = _accounting({"ghcr.example": {
        "disposition": "fleet-unlockable", "reason": "ours",
        "retention": {"rule": "derived-protected-set", "reason": "r", "protectedSet": {"axes": [
            {"axis": "committed pins", "derivedFrom": "overlays", "onIncomplete": "refuse"}]}}}})
    check(code == 0,
          f"ARM 34: a `fleet-unlockable` host was made to declare `publishes` as well, so the "
          f"accounting demands the same fact twice: {output}")

    # EQUALITY IN BOTH DIRECTIONS — a missing repository is the gap, a stale one hides the next one.
    code, output = _accounting(_mixed(publishes={"repositories": ["us/other"]}))
    check(code == 1 and "does not name us/portal" in output and "names us/other" in output,
          f"ARM 34: a publication list that misses what we push AND names what we do not was "
          f"accepted in one or both directions: {output}")

    # The retention statement a publication may make, and the three it may not.
    code, output = _accounting(_mixed(publishes={"retention": {"rule": "nothing-deletes"}}))
    check(code == 1 and "expected one of operator-retained" in output,
          f"ARM 34: a publication into somebody else's store claimed `nothing-deletes` — a rule "
          f"whose evidence is a chart we render and an ACL we own, neither of which exists there: "
          f"{output}")
    code, output = _accounting(_mixed(publishes={"retention": {"operator": ""}}))
    check(code == 1 and "names no `operator`" in output,
          f"ARM 34: 'somebody else keeps it' passed with nobody named: {output}")
    code, output = _accounting(_mixed(publishes={"retention": {
        "deleters": [{"mechanism": "theirs", "present": False, "verdict": "none"}]}}))
    check(code == 1 and "does not operate" in output and "deleters" in output,
          f"ARM 34: a publication block enumerated the OPERATOR'S deleters. We cannot measure them "
          f"from a committed file, so the list would be a verdict about OUR artifacts resting on "
          f"nothing: {output}")
    code, output = _accounting(_mixed(publishes={"retention": {"cleanupAuthorized": True}}))
    check(code == 1 and "cleanupAuthorized" in output,
          f"ARM 34: a record authorized a cleanup in a store this fleet does not operate: {output}")
    # 🚨 AND THE STRING "true" — the spelling this file has already paid for twice, on `inForce`
    # and on `present`. Every `is True` reader treats it as absent, so a check that rejects only
    # the singleton lets it through as though a cleanup were forbidden (#4362 review).
    code, output = _accounting(_mixed(publishes={"retention": {"cleanupAuthorized": "true"}}))
    check(code == 1 and "cleanupAuthorized" in output,
          f"ARM 34: `cleanupAuthorized` written as the STRING \"true\" passed as if it were "
          f"`false`: {output}")
    code, output = _accounting(_mixed(publishes={"retention": {"cleanupAuthorized": None}}))
    check(code == 1 and "cleanupAuthorized" in output,
          f"ARM 34: an ABSENT `cleanupAuthorized` passed — it would default to whatever the next "
          f"reader assumes: {output}")

    # 🚨 A WHOLE-HOST STALE BLOCK (#4362 review). Looping over DERIVED hosts alone meant that if
    # both lanes stopped pushing to a host while its `publishes` block stayed, the host was never
    # visited — so the stale check and every validation beneath it passed having inspected nothing,
    # which reads exactly like a clean record.
    code, output = _verdict(_mixed(), publishing={
        "main-cd.yml": 'env:\n  ACR: ' + LANE_REGISTRY + '\n    run: |\n'
                       '      docker buildx imagetools create --tag "${{ env.ACR }}/portal:v" "x"\n',
        "release.yml": 'env:\n  ACR: ' + LANE_REGISTRY + '\n    run: |\n'
                       '      docker buildx imagetools create --tag "${{ env.ACR }}/portal:w" "x"\n',
    })
    check(code == 1 and "publication record outliving its publication" in output,
          f"ARM 34: a `publishes` block for a host NOTHING pushes to any more was never even "
          f"visited, so it exempted itself: {output}")

    # 🚨 `producedBy` IS HELD TO THE DERIVATION, not to the host's name appearing in the file — a
    # comment satisfies a substring test, and a misattributed producer reads as evidence.
    code, output = _accounting(_mixed(publishes={
        "producedBy": [".github/workflows/main-cd.yml", ".github/workflows/release.yml"]}))
    check(code == 1 and "no image reference to that host is derived" in output,
          f"ARM 34: `producedBy` named a lane that pushes only to the LANE registry as a producer "
          f"of a foreign host, and the substring test accepted it: {output}")
    code, output = _accounting(_mixed(publishes={"producedBy": [".github/workflows/release.yml"]}))
    check(code == 1 and ("does not name" in output or "no image reference" in output),
          f"ARM 34: a lane that DOES push to the host went unnamed by `producedBy` and nothing "
          f"noticed — a producer the record has not seen is a publication nobody reviewed: {output}")

    # `runFrom` — the whole difference between a publication and a source this lane must protect.
    code, output = _accounting(_mixed(publishes={"runFrom": ""}))
    check(code == 1 and "states no `runFrom`" in output,
          f"ARM 34: a publication block left unsaid whether any INSTALLATION runs from it: {output}")

    # `producedBy` has to name a lane that actually pushes THERE.
    code, output = _accounting(_mixed(publishes={"producedBy": [".github/workflows/gone.yml"]}))
    check(code == 1 and "not a file in this repository" in output,
          f"ARM 34: `producedBy` pointed at a moved lane and still checked: {output}")

    # A renamed publishing lane must RED, never quietly derive nothing.
    code, output = _verdict(_mixed(), publishing={"main-cd.yml": _PUBLISHING["main-cd.yml"]})
    check(code == 1 or "release.yml is missing" in output,
          f"ARM 34: a MISSING publishing lane did not red. A derivation that reads one of two "
          f"lanes reports exactly like one that read both: {output}")

    # ── ARM 34b: an installation that RUNS from a publication, and the report that names it ──────
    # 🚨 WITHOUT THIS THE `publishes` BLOCK IS PROSE. A host declared `third-party` about what the
    # fleet PULLS may still hold repositories the fleet PUBLISHES, and an overlay pinning one of
    # THOSE is not a third-party pin — it is an installation running OUR images from a store
    # nothing of ours retains, in the one branch (pins here AND there) that otherwise prints
    # success. It fires on nobody today, which is exactly the claim `publishes.runFrom` makes.
    _publication_scan = _scan2("Systemorph/Memex", FIXTURE_OVERLAY_FOREIGN.replace(
        "cr.meshweaver.cloud", "ghcr.io/systemorph"),
        "deployments/aks/memex-cloud/values.memexcloud.public.yaml")
    plan, _, _ = _drive(clean1, clean2 + [_publication_scan],
                        FakeRegistry(_inventory(), FAKE_TAGS), probe=_answers(),
                        dispositions={"ghcr.io": ("third-party", "somebody else's")},
                        publications_root=HERE_ROOT)
    check(any("PUBLISHES rather than retains" in blocker for blocker in plan.blockers),
          f"ARM 34b: an installation pinning `ghcr.io/systemorph/memex-portal-ai` — OUR image, on "
          f"a host declared third-party — was waved through as somebody else's: {plan.blockers}")

    # …and the NEGATIVE control: a genuinely third-party pin on the SAME host still passes. Without
    # it the arm above could be firing on the host rather than on the repository.
    _bootstrap_scan = _scan2("Systemorph/Memex", FIXTURE_OVERLAY_FOREIGN.replace(
        "cr.meshweaver.cloud/memex-portal-ai", "ghcr.io/distribution/distribution").replace(
        "cr.meshweaver.cloud/memex-migration", "ghcr.io/oras-project/oras"),
        "deployments/aks/memex-cloud/values.memexcloud.public.yaml")
    plan, _, _ = _drive(clean1, clean2 + [_bootstrap_scan],
                        FakeRegistry(_inventory(), FAKE_TAGS), probe=_answers(),
                        dispositions={"ghcr.io": ("third-party", "somebody else's")},
                        publications_root=HERE_ROOT)
    check(not any("PUBLISHES rather than retains" in blocker for blocker in plan.blockers),
          f"ARM 34b: `ghcr.io/distribution/distribution` — the registry service's own image, which "
          f"cannot come from the registry it boots — was read as one of ours. The arm would then "
          f"be firing on the HOST, and the host is precisely the wrong unit: {plan.blockers}")

    # 🚨 AND THE REPORT MUST NOT PRINT OUR OWN IMAGES UNDER "NOT ours to retain" — that is the
    # record's false sentence reproduced in the one artifact a reader checks it against.
    plan, _, _ = _drive(clean1, clean2 + [_publication_scan],
                        FakeRegistry(_inventory(), FAKE_TAGS), probe=_answers(),
                        dispositions={"ghcr.io": ("third-party", "somebody else's")},
                        publications_root=HERE_ROOT)
    _printed = io.StringIO()
    with contextlib.redirect_stdout(_printed):
        report(plan, clean1, clean2 + [_publication_scan], REGISTRY_DEFAULT, _inventory(),
               apply=False, release_enabled=False, root=HERE_ROOT)
    _mirror = _printed.getvalue()
    check("this fleet PUBLISHES here" in _mirror and "published by us" in _mirror,
          f"ARM 34b: the protected-set report did not distinguish OUR OWN repositories on a mixed "
          f"host: {_mirror[-1500:]}")
    _ours_block = _mirror.split("this fleet PUBLISHES here")[-1].split("\n\n")[0]
    check("NOT ours to retain" not in _ours_block,
          f"ARM 34b: the report printed this fleet's own published images under 'declared NOT ours "
          f"to retain' — the record's false sentence, reproduced by the report: {_ours_block}")

    # ── ARM 31: the decided WINDOW is asserted on the record, and the assertion can fail ────────
    # 🚨 The control that matters is the one this repository's own record FAILED on 2026-09-13:
    # `--ago 7d --keep 10` over `memex-portal-ai`, the image both production portals run. It is
    # driven here as a literal so the arm still fires the day the record is right.
    check(check_window_policy("x.yaml", "  - cmd: acr purge --filter 'memex-portal-ai:.*' "
                                        "--ago 7d --keep 10 --untagged"),
          "ARM 31: the exact step this repository carried until 2026-09-13 — a 7-day window with a "
          "ten-build quota over the image both production portals run — passed the window policy")
    check(any("--keep" in problem for problem in
              check_window_policy("x.yaml", "  - cmd: acr purge --filter 'a:.*' --ago 90d "
                                            "--keep 10 --untagged")),
          "ARM 31: a BUILD-COUNT QUOTA beside a generous age window passed. `--keep` counts NEWER "
          "BUILDS, so a repository republished many times a day ages its own manifests out in "
          "hours whatever `--ago` says — the age window cannot replace this half (#3842)")
    check(check_window_policy("x.yaml", "  - cmd: acr purge --filter 'a:.*' --untagged"),
          "ARM 31: a purge step with NO `--ago` at all passed — no age floor is not a long one")
    check(any("could not be read" in problem for problem in
              check_window_policy("x.yaml", "  - cmd: acr purge --filter 'a:.*' --ago forever "
                                            "--untagged")),
          "ARM 31: an UNREADABLE `--ago` passed. A window nobody could parse is a window nobody "
          "checked, and it must never spell the same as one that was checked and was fine")
    check(not check_window_policy("x.yaml", "  - cmd: acr purge --filter 'a:.*' --ago 30d "
                                            "--untagged"),
          "ARM 31: the DECIDED window itself was rejected — the gate would red on the fix, which "
          "is how a gate gets muted")
    check(not check_window_policy("x.yaml", "  - cmd: acr purge --filter 'a:.*' --ago 720h "
                                            "--untagged"),
          "ARM 31: 720h is 30 days and was rejected; the unit table is what makes this a duration "
          "comparison rather than a string match")
    check(parse_ago_days("7d") == 7 and parse_ago_days("1d12h") == 1.5
          and parse_ago_days("30") is None and parse_ago_days("30dx") is None,
          "ARM 31: the `--ago` parser is wrong — a bare number or a trailing character must be "
          "UNREADABLE, never a silent default")

    # ── ARM 31c: the window is read off the INVOCATION, and both `--keep` spellings count ──────
    # Review findings on #4213, each driven as its own arm.
    check(check_window_policy("x.yaml", "  - cmd: echo --ago 30d; acr purge --filter 'a:.*' "
                                        "--untagged"),
          "ARM 31c: a `--ago` sitting on the LINE but not in the `acr purge` INVOCATION satisfied "
          "the window. A `cmd:` is a shell line and may hold more than one command; the flag that "
          "counts is the one on the command that DELETES")
    check(any("BUILD-COUNT QUOTA" in problem for problem in
              check_window_policy("x.yaml", "  - cmd: acr purge --filter 'a:.*' --ago 30d "
                                            "--keep=10 --untagged")),
          "ARM 31c: `--keep=10` passed. It is the same quota as `--keep 10`, and a whitespace-only "
          "match reads the compliant spelling and misses the other")
    check(any("bare" in problem for problem in
              check_window_policy("x.yaml", "  - cmd: acr purge --filter 'a:.*' --ago 30d --keep")),
          "ARM 31c: a BARE `--keep` passed — a malformed step is not a compliant one")
    check(check_window_policy("x.yaml", "  - cmd: acr purge --filter 'a:.*' --ago --untagged"),
          "ARM 31c: a bare `--ago` with no duration passed, so the window was never checked")
    check(any("--ago 7d" in problem for problem in
              check_window_policy("x.yaml", "  - cmd: acr purge --filter 'a:.*' --ago 30d "
                                            "--untagged && acr purge --filter 'b:.*' --ago 7d "
                                            "--untagged")),
          "ARM 31c: a SECOND `acr purge` on the same line escaped the window check — every "
          "invocation deletes, so every invocation is checked")
    check(not check_window_policy("x.yaml", "  - cmd: acr purge --filter 'a:.*' --ago=30d "
                                            "--untagged"),
          "ARM 31c: the `--ago=30d` spelling of a COMPLIANT window was rejected")

    # ── ARM 31e: a purge step is read as TOKENS, so quoting cannot hide a flag or invent one ────
    # 🚨 The two attacks are opposite and both defeat a text search.
    check(check_window_policy("x.yaml", '  - cmd: acr purge --include-"locked" '
                                        "--filter 'a:.*' --ago 30d --untagged")
          == [] and
          any("--include-locked" in tokens for tokens in
              purge_invocations('  - cmd: acr purge --include-"locked" --filter \'a:.*\' '
                                "--ago 30d --untagged")[0]),
          "ARM 31e: `--include-\"locked\"` was not seen as `--include-locked`. The shell joins the "
          "quoted fragment and EXECUTES that option — it deletes every manifest the lock protects "
          "— while matching no search for the literal string")
    check(not purge_invocations('  - cmd: echo "acr purge --filter a:.* --ago 30d --untagged"')[0],
          "ARM 31e: text INSIDE A QUOTED ARGUMENT was read as a purge command. `echo \"acr purge "
          "…\"` deletes nothing, so reporting it as a compliant purge describes a command that "
          "does not run")
    check(purge_invocations("  - cmd: acr purge --filter 'a:.*' --ago 30d")[1] == ""
          and len(purge_invocations("  - cmd: acr purge --filter 'a:.*' --ago 30d")[0]) == 1,
          "ARM 31e: an ordinary single purge stopped being read")
    check(len(purge_invocations("  - cmd: acr purge --filter 'a:.*' --ago 30d && acr purge "
                                "--filter 'b:.*' --ago 7d")[0]) == 2,
          "ARM 31e: a second command after `&&` was not read as its own invocation")
    check(check_window_policy("x.yaml", '  - cmd: acr purge --filter "a:.*  --ago 30d'),
          "ARM 31e: a step with UNBALANCED QUOTES passed. It could not be tokenized at all, which "
          "means nothing in it was checked — and that must never spell the same as compliant")
    check(option_value(["acr", "purge", "--keep=10"], "--keep") == (True, "10")
          and option_value(["acr", "purge", "--keep", "10"], "--keep") == (True, "10")
          and option_value(["acr", "purge", "--keep", "--untagged"], "--keep") == (True, None)
          and option_value(["acr", "purge"], "--keep") == (False, None),
          "ARM 31e: the option reader disagrees with itself across the three spellings")

    # ── ARM 31d: a declaration whose switch is not a BOOLEAN is fail-open, and reds ─────────────
    for block in ("pause", "recordAheadOfRegistry"):
        check(any("not a JSON boolean" in problem for problem in
                  check_in_force_is_boolean({block: {"inForce": "true"}}, block)),
              f"ARM 31d: `{block}.inForce` as the STRING \"true\" was accepted. Every reader asks "
              "`is True`, which is False for a string — so a typed quote mark disarms the "
              "interlock, the overwrite guard and the coherence gate at once, while reading to a "
              "human as if it were armed")
        check(check_in_force_is_boolean({block: {}}, block),
              f"ARM 31d: `{block}` with no `inForce` was accepted; every reader treats its absence "
              "as NOT in force, so it declares nothing while looking like a declaration")
        check(check_in_force_is_boolean({block: "yes"}, block),
              f"ARM 31d: `{block}` as a scalar was accepted")
        check(any("present and null" in problem for problem in
                  check_in_force_is_boolean({block: None}, block)),
              f"ARM 31d: `{block}: null` was treated as NO declaration. `dict.get` answers None "
              "for an absent key and for a written null alike, so a present block declaring "
              "nothing would read exactly like a record that never had one and every reader would "
              "walk past it")
        check(not check_in_force_is_boolean({}, block),
              f"ARM 31d: an ABSENT `{block}` was reported as malformed — absent and wrong are "
              "different, and only one of them is a problem")
        check(not check_in_force_is_boolean({block: {"inForce": False}}, block),
              f"ARM 31d: a well-formed `{block}.inForce: false` was rejected")
    check(any("not a JSON boolean" in problem for problem in check_pause_coherence(
              {"tasks": [{"name": "t", "status": "Enabled"}],
               "pause": {"inForce": "true", "since": "s", "reason": "r", "reEnableWhen": "w"}})),
          "ARM 31d: the coherence gate passed an `Enabled` task sitting under a pause whose "
          "`inForce` is a string — the exact record the apply interlock would also walk past")

    # ── ARM 31b: the PAUSE declaration and the recorded statuses cannot contradict each other ───
    check(any("is recorded `Enabled`" in problem for problem in check_pause_coherence(
              {"tasks": [{"name": "t", "status": "Enabled"}],
               "pause": {"inForce": True, "since": "s", "reason": "r", "reEnableWhen": "w"}})),
          "ARM 31b: a record asserting BOTH an in-force pause and an Enabled purge task passed. "
          "`retention_windows` prints the window and never mentions the pause in that state, so "
          "the contradiction is invisible in the report")
    check(any("no `reEnableWhen`" in problem for problem in check_pause_coherence(
              {"tasks": [{"name": "t", "status": "Disabled"}],
               "pause": {"inForce": True, "since": "s", "reason": "r"}})),
          "ARM 31b: a pause with no re-enable condition passed — that is a retention that stopped, "
          "written as if it were a decision")
    check(check_pause_coherence({"tasks": [{"name": "t", "status": "Disabled"}]}),
          "ARM 31b: every task Disabled with NO declaration passed. A stopped retention and a "
          "stale record read identically, and only one of them is a decision")
    check(not check_pause_coherence({"tasks": [{"name": "t", "status": "Enabled"}]}),
          "ARM 31b: an ordinary running retention with no pause block was rejected")
    check(not check_pause_coherence(
              {"tasks": [{"name": "t", "status": "Disabled"}],
               "pause": {"inForce": True, "since": "s", "reason": "r", "reEnableWhen": "w"}}),
          "ARM 31b: this repository's own paused shape was rejected")

    # ── ARM 24d: a roster that declares one installation TWICE decides nothing, and reds ────────
    with tempfile.TemporaryDirectory() as scratch:
        folder = Path(scratch) / ".github" / "acr-retention"
        folder.mkdir(parents=True)
        (folder / ROSTER_PATH).write_text(
            '{"instances": [{"id": "x", "state": "retired", "reason": "old"},'
            ' {"id": "x", "state": "live", "reason": "back"}]}', encoding="utf-8")
        _, problems = read_instance_roster(scratch)
        check(any("declared TWICE" in problem for problem in problems),
              "ARM 24d: a duplicated roster entry was resolved by array order — which lets a stale "
              f"exemption outlive the line written to end it: {problems}")

    # ── ARM 25: a roster entry naming nobody is a stale exemption, and reds ─────────────────────
    plan, _, _ = _drive(clean1, clean2, FakeRegistry(_inventory(), FAKE_TAGS),
                        roster={"ghost": ("retired", "decommissioned in 2019", "")})
    check(any("ghost" in b and "exempts nothing" in b for b in plan.blockers),
          f"ARM 25: a roster entry for an installation no overlay declares did not red: {plan.blockers}")

    # ── ARM 26: an installation running an image the registry no longer carries reds ────────────
    plan, _, _ = _drive(clean1, clean2, FakeRegistry(_inventory(), FAKE_TAGS),
                        probe=_answers("9" * 40))
    check(any("NO manifest" in b and "memex-cloud" in b for b in plan.blockers),
          f"ARM 26: a running set absent from the registry did not red: {plan.blockers}")

    # ── ARM 27: the EXTRACTOR is controlled on every run, not inferred from the fleet ───────────
    # The 2026-09-12 failure, both ways round. A fleet-shaped absolute ("six repositories pin, so
    # zero is impossible") expired the day #3842 moved the satellites to run-time resolution, and
    # reddened the protection lane two hours before the purge. An instrument-shaped one cannot.
    empty_fleet = [_scan1("Systemorph/Resolver", """
name: resolves-at-runtime
env:
  MW_TEST_IMAGE: meshweaver.azurecr.io/mw-plugin-test
  MW_PORTAL_IMAGE: meshweaver.azurecr.io/memex-portal-ai
""")]
    plan, fails, _ = _drive(empty_fleet, clean2, FakeRegistry(_inventory(), FAKE_TAGS))
    check(not any("AXIS 1" in b for b in plan.blockers),
          f"ARM 27: a fleet that legitimately stopped pinning digests red on AXIS 1: {plan.blockers}")
    check(plan.axis1_image_repos == 1,
          f"ARM 27: the repository naming a platform image was not counted: {plan.axis1_image_repos}")

    nothing = [_scan1("Systemorph/Nothing", "name: no images here\n")]
    plan, _, _ = _drive(nothing, clean2, FakeRegistry(_inventory(), FAKE_TAGS))
    check(any("ZERO repositories naming a platform image" in b for b in plan.blockers),
          f"ARM 27: a fleet reaching for NO platform image at all did not red: {plan.blockers}")

    original_extract = consistency.extract
    try:
        consistency.extract = lambda filename, text: ([], [], [], None, [])
        problems = extractor_control()
    finally:
        consistency.extract = original_extract
    check(any("STOPPED MATCHING" in problem for problem in problems),
          "ARM 27: the digest extractor was broken and the control did not notice — which would "
          "make every 'no pins found' answer in the run worthless while it stayed green")

    # ── ARM 28: a tag lock that did not TAKE is a failure, not a count ──────────────────────────
    registry = FakeRegistry(_inventory(), FAKE_TAGS)
    registry.writes_take = False
    plan, fails, registry = _drive(clean1, clean2, registry)
    check(any("the tag lock WRITE succeeded" in f for f in fails),
          f"ARM 28: a tag lock that did not move the attribute was counted as protection: {fails}")
    check(plan.tags_locked_now == 0,
          "ARM 28: tag locks were COUNTED although none took")


    # ── ARM 30: a LOCKED INDEX DOES NOT PROTECT ITS CHILDREN, so the closure is protected ───────
    # Read verbatim from Azure/acr-cli `GetUntaggedManifests`: the `continue` for a locked manifest
    # comes BEFORE the walk that adds an index's children to the ignore list, so a locked index is
    # never walked and each untagged child is then judged on its own — no tags, past `--ago` ⇒
    # deleted, leaving the locked index pointing at nothing. Measured on the live registry
    # 2026-09-12: the index `memex` is RUNNING is locked and BOTH its platform manifests read
    # deleteEnabled true.
    plan, fails, registry = _drive(clean1, clean2, FakeRegistry(_inventory(), FAKE_TAGS))
    locked = {(repo, digest) for repo, digest, enabled in registry.writes if enabled is False}
    check(("memex-portal-ai", RUNNING_AMD64) in locked and
          ("memex-portal-ai", RUNNING_ARM64) in locked,
          "ARM 30: the platform manifests of a protected index were left purgeable — protecting the "
          "index alone makes them STRICTLY LESS safe than leaving it unprotected, because an "
          "unlocked tagged index would at least have been walked")
    check(plan.platform_manifests == 2,
          f"ARM 30: expected 2 platform manifests in the closure, got {plan.platform_manifests}")
    check(not any(m.digest in (RUNNING_AMD64, RUNNING_ARM64) for m in plan.release_candidates),
          "ARM 30: a platform manifest of a protected index was offered as an unlock candidate — "
          "it is pinned BY that index")

    # ── ARM 30b: a closure that could not be READ is not an empty closure ───────────────────────
    registry = FakeRegistry(_inventory(), FAKE_TAGS, children_error="az: request failed")
    plan, _, _ = _drive(clean1, clean2, registry)
    check(any("could not enumerate the platform manifests" in b for b in plan.blockers),
          f"ARM 30b: an unreadable index closure passed as complete: {plan.blockers}")

    # ── ARM 30c: an index that lists NOTHING is malformed, not single-architecture ──────────────
    registry = FakeRegistry(_inventory(), FAKE_TAGS, children={})
    plan, _, _ = _drive(clean1, clean2, registry)
    check(any("lists NO platform manifests" in b for b in plan.blockers),
          f"ARM 30c: an index with no children read as an ordinary image: {plan.blockers}")

    # ── ARM 29: the harness drives the SAME path as run(), or it guards nothing ─────────────────
    # A self-test whose subject moved and whose roots did not keeps passing about code that is no
    # longer what runs. Compare the calls, mechanically.
    import ast as _ast
    import inspect as _inspect

    def _calls(function) -> set[str]:
        tree = _ast.parse(textwrap.dedent(_inspect.getsource(function)))
        return {node.func.id for node in _ast.walk(tree)
                if isinstance(node, _ast.Call) and isinstance(node.func, _ast.Name)}

    # Excluded on purpose: the report (the harness asserts the PLAN, not its rendering), the
    # builtins, and the three inputs the harness takes as PARAMETERS rather than reading — the two
    # scanners and the roster file.
    run_calls = _calls(run) - {"report", "emit", "print", "Registry", "len", "bool", "sorted",
                               "set", "any", "all", "read_instance_roster",
                               "read_registry_dispositions",
                               "scan_overlays_local", "scan_overlays_remote"}
    missing = run_calls - _calls(_drive)
    check(not missing,
          f"ARM 29: run() calls {sorted(missing)} and the self-test harness does not, so those "
          "steps are unfalsified. Mirror them in _drive.")

    # 🚨 AND THE OTHER DIRECTION, which is the one that stayed green under falsification: every arm
    # above can pass while `run()` no longer PERFORMS the step, because the harness has its own copy
    # of the sequence. Deleting `extractor_control()` from `run()` alone left this self-test green
    # until this check existed. Name the steps the protection decision must make.
    required = {"extractor_control", "read_instance_roster", "read_registry_dispositions",
                "build_instances", "classify_foreign_registries",
                "resolve_running_sets", "resolve_and_classify", "classify_tags",
                "read_inventory", "build_plan", "apply_locks", "apply_tag_locks"}
    # expand_platform_closure is called from inside resolve_and_classify, so it is asserted here
    # by name rather than through run()'s own call list.
    if "expand_platform_closure" not in _calls(resolve_and_classify):
        check(False, "ARM 29: resolve_and_classify no longer expands an index to its platform "
                     "manifests, so a locked index would be protected with a hole under it")
    absent = required - _calls(run)
    check(not absent,
          f"ARM 29: run() no longer calls {sorted(absent)}. The self-test drives its own copy of "
          "the sequence, so every arm above would keep passing about a step the nightly job has "
          "stopped taking.")

    for line in failures:
        print(f"::error::self-test: {line}")
    if failures:
        return 1
    print("self-test: official-release protection, the live-installation axis and 25 arms — lock requested on both axes and both overlay shapes, idempotent, "
          "unreadable repo / truncated tree / zero pins / unclassified / malformed / gone / "
          "unresolved tag / indeterminate / unreadable registry all RED with nothing released, "
          "release arm off by default and live when enabled, report-only writes nothing, a lock "
          "write that exits 0 without taking and one whose read-back cannot answer are both RED "
          "and counted as protecting NOTHING, and the two existing pin extractors still agree. AXIS 3: the set an installation is RUNNING is locked though no file pins it, its migration twin with it, the TAG is locked beside the manifest, an installation that did not answer is INCOMPLETE and refuses the unlock arm, silence is never retirement, a stale roster entry and an unknown running set are RED, the digest extractor is controlled against a fixture rather than inferred from the fleet, a tag lock that did not take is counted as protecting NOTHING, a locked INDEX is expanded to the platform manifests acr-cli would otherwise collect out from under it, and the harness provably drives the same path as run(). THE RECORD: every recorded purge step is held to #3842's decided window — at least 30 days by age, no `--keep` build-count quota — with the exact `--ago 7d --keep 10` step this repository carried until 2026-09-13 driven as a literal control, a bare or unreadable `--ago` RED, and the decided window itself proven to PASS; and the pause declaration cannot contradict the statuses it is recorded beside, in either direction. The window is read off each `acr purge` COMMAND — TOKENIZED the way a shell would, so `--include-\"locked\"` is seen as the option it executes as and `echo \"acr purge …\"` is not a purge — every command on the line, `--keep=N` and a bare `--keep` count as quotas, a bare `--ago` is unchecked rather than compliant, and a declaration whose `inForce` is the STRING \"true\" — which every `is True` reader silently treats as absent — is RED in both blocks, as is a block written as an explicit `null`. ANOTHER REGISTRY: an installation whose overlay pins its images somewhere this lane cannot lock is NAMED rather than read as pinning zero (the real `build` overlay shape, whose two `cr.meshweaver.cloud` pins the ACR extractor sees as nothing), an UNDECLARED registry REDS wherever it appears, a declared `fleet-unlockable` one is counted on its own line saying protection there is UNVERIFIED, pinning in BOTH is RED because half covered is not covered, a `*.azurecr.io` that is not this registry is foreign, helm's split repository/tag shape is read, and a reference inside a COMMENT is prose. WHAT DELETES FROM IT (#4230, the SECOND question about the same unit): every declared registry must say what deletes from it or go RED, an empty table is zero-asked rather than clean, a `nothing-deletes` enumeration in which NOTHING is `present` inspected nothing, a `present` written as the STRING \"true\" is RED, the rule and the disposition are checked against EACH OTHER in both directions, a `derived-protected-set` axis that merely WARNS on an incomplete derivation is RED because on a registry with NO LOCK that deletes more rather than protecting less — and the arm that is not about JSON: the committed CHART is re-derived, so a `kind: Job`/`kind: CronJob` rendered beside the registry (quoted or bare), a `maintenance:` window that MOVED, a NEW key in that stanza, the stanza VANISHING, and an executable deletion anywhere in `deploy/`/`.github/` each go RED while the record still reads 'nothing deletes' — with the sweep's every spelling PROVEN to match on a synthetic control, and a deleter named inside a COMMENT proven NOT to. WHAT THIS FLEET PUBLISHES TO IT (#4323, the THIRD question about the same unit): the push targets are DERIVED from the publishing lanes — every `--tag` and every mirror-image-to-registry.sh destination, shell continuations joined, with the two readers falsified SEPARATELY because disabling the mirror call loses a whole registry while disabling the join loses one repository — a `third-party` host this fleet pushes to with no `publishes` block is RED (that is the defect, and it validated green for a day), an undeclared push target is RED, the declared repositories are held to the derived set in BOTH directions, `operator-retained` is the only rule a publication may claim and may enumerate no deleters and authorize no cleanup, a `fleet-unlockable` host is NOT made to declare the same fact twice, a renamed lane REDS rather than deriving nothing — and the two arms that stop the block being prose: an overlay pinning one of OUR published repositories is RED while the bootstrap image on the SAME host is not, and the report never prints our own images under 'declared NOT ours to retain'.")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description="Lock every manifest the fleet pins.")
    parser.add_argument("--repos", help="comma-separated owner/name list to scan")
    parser.add_argument("--discover", action="store_true",
                        help="enumerate the fleet from the App installation this token belongs to")
    parser.add_argument("--root", help="read one repository from a local checkout instead")
    parser.add_argument("--registry", default=REGISTRY_DEFAULT)
    parser.add_argument("--apply", action="store_true",
                        help="write the locks (default: report what it WOULD do)")
    parser.add_argument("--release-unpinned", action="store_true",
                        help="also RELEASE locks nothing pins any more — off by design; "
                             "MW_ACR_RELEASE_UNPINNED=true is the other way to ask")
    parser.add_argument("--describe-purge-file", metavar="PATH",
                        help="print, as JSON, the tokenized `acr purge` steps of one task "
                             "definition — the parser acr-retention-tasks.sh shares")
    parser.add_argument("--check-retention-record", metavar="ROOT",
                        help="assert the committed .github/acr-retention record still describes a "
                             "purge the lock can protect against; no credential, no network")
    parser.add_argument("--check-registry-retention", metavar="ROOT",
                        help="assert every registry the fleet pins in says what DELETES from it, "
                             "and that the committed chart still agrees (#4230); "
                             "no credential, no network")
    parser.add_argument("--self-test", action="store_true",
                        help="prove every arm fires and stays silent; no network")
    args = parser.parse_args()

    if args.self_test:
        return self_test()
    if args.describe_purge_file:
        return describe_purge_file(args.describe_purge_file)
    if args.check_retention_record:
        return check_retention_record(args.check_retention_record)
    if args.check_registry_retention:
        return check_registry_retention(args.check_registry_retention)
    if args.root:
        repos = [args.repos or "local"]
    elif bool(args.repos) == bool(args.discover):
        parser.error("give exactly one of --repos, --discover or --root")
    else:
        repos = (consistency.discover_repos() if args.discover
                 else [r.strip() for r in args.repos.split(",") if r.strip()])

    release_enabled = (args.release_unpinned
                       or os.environ.get("MW_ACR_RELEASE_UNPINNED", "").strip().lower() == "true")
    print(f"scanning {len(repos)} repository(ies) on both pin axes: {', '.join(repos)}")
    return run(repos, args.registry, args.apply, release_enabled, args.root)


if __name__ == "__main__":
    sys.exit(main())
