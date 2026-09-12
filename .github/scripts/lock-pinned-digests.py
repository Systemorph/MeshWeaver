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


def extract_overlay_pins(text: str) -> tuple[list[tuple[str, str]], list[tuple[str, str]]]:
    """(pins, floating) as (acr_repo, tag) pairs, from one overlay file's text."""
    pins: list[tuple[str, str]] = []
    floating: list[tuple[str, str]] = []

    def add(repo: str, tag: str) -> None:
        pair = (repo, tag)
        if tag.lower() in FLOATING_TAGS:
            if pair not in floating:
                floating.append(pair)
        elif pair not in pins:
            pins.append(pair)

    for match in OVERLAY_INLINE_RE.finditer(text):
        add(match.group("repo"), match.group("tag"))

    lines = text.splitlines()
    for index, line in enumerate(lines):
        repo_match = OVERLAY_REPOSITORY_RE.match(line)
        if not repo_match or TEMPLATED_RE.search(line):
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
    unreadable: str | None = None


def scan_overlays_remote(gh_repo: str) -> OverlayScan:
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
        pins, floating = extract_overlay_pins(text)
        scan.pins.extend((repo, tag, path) for repo, tag in pins)
        scan.floating.extend((repo, tag, path) for repo, tag in floating)
        scan.instances.extend((ident, host, path)
                              for ident, host in extract_overlay_instances(text))
    return scan


def scan_overlays_local(root: str, gh_repo: str) -> OverlayScan:
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
        pins, floating = extract_overlay_pins(text)
        scan.pins.extend((repo, tag, rel) for repo, tag in pins)
        scan.floating.extend((repo, tag, rel) for repo, tag in floating)
        scan.instances.extend((ident, host, rel) for ident, host in extract_overlay_instances(text))
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


def read_instance_roster(root: str) -> tuple[dict[str, tuple[str, str]], list[str]]:
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
    roster: dict[str, tuple[str, str]] = {}
    problems: list[str] = []
    for entry in document.get("instances", []):
        ident = str(entry.get("id", "")).strip()
        state = str(entry.get("state", "")).strip()
        reason = str(entry.get("reason", "")).strip()
        if not ident:
            problems.append(f"{ROSTER_PATH}: an entry has no `id`.")
            continue
        if state not in ROSTER_STATES:
            problems.append(f"{ROSTER_PATH}: `{ident}` has state {state!r}; expected one of "
                            + ", ".join(sorted(ROSTER_STATES)) + ".")
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
        roster[ident] = (state, reason)
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
            state, reason = roster.get(ident, ("live", ""))
            instances[ident] = Instance(id=ident, host=host, source=source, state=state,
                                        reason=reason)
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
        if not repositories:
            plan.blockers.append(
                f"AXIS 3 — installation `{instance.id}` runs core {instance.commit[:7]} and its "
                f"overlay ({instance.source}) pins no image at all, so there is no repository in "
                "which to protect what it runs.")
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
    for instance in sorted(plan.instances, key=lambda i: i.id):
        if instance.state != "live":
            emit(f"        {instance.id:<18} {instance.state} — {instance.reason}")
        elif instance.commit and not instance.error:
            emit(f"        {instance.id:<18} core {instance.commit[:7]} "
                 f"({instance.version or 'no version'}) → {len(instance.manifests)} manifest(s) "
                 "in its closure")
        else:
            emit(f"        {instance.id:<18} 🚨 NOT ACCOUNTED FOR — {instance.error}")
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
            axis2.append(scan_overlays_local(local_root, gh_repo))
        else:
            axis1.append(consistency.scan_remote(gh_repo))
            axis2.append(scan_overlays_remote(gh_repo))

    inventory, inventory_errors = read_inventory(registry)
    plan = build_plan(axis1, axis2)

    # 🚨 THE INSTRUMENT BEFORE THE MEASUREMENT. Every "no pins here" answer below is only worth the
    # extractor that produced it, so the extractor is exercised against known input first.
    plan.blockers.extend(extractor_control())

    # AXIS 3 — the installations, asked what they are RUNNING rather than what they should run.
    roster, roster_problems = read_instance_roster(local_root or ".")
    plan.blockers.extend(roster_problems)
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
        return [], "no ENABLED purge step is recorded"
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
            # 🚨 THE `cmd:` LINES, NOT THE FILE. Both recorded definitions EXPLAIN in a comment
            # that they do not pass `--include-locked`; a whole-file grep fires on that prose and
            # reds over the sentence saying the guard is satisfied. Measured 2026-09-07.
            if "--include-locked" in step:
                problems.append(
                    f"{yaml_path.name} has a purge step passing --include-locked:\n"
                    f"      {step.strip()[:160]}\n"
                    "    That flag DELETES LOCKED MANIFESTS, which is the entire protection "
                    "lock-pinned-digests.py provides. Every pin the fleet holds becomes purgeable "
                    "again, silently, while the lock job keeps reporting that it protected them.")
            if "acr purge" not in step:
                problems.append(f"{yaml_path.name} has a `cmd:` step that is not an `acr purge`: "
                                f"{step.strip()[:120]} — this record is for retention tasks.")

    # 🚨 THE DENOMINATOR. Zero recorded tasks, or zero steps across them, reads exactly like a
    # clean record while having inspected nothing — the confusion #3438 is made of.
    print(f"retention record: {len(tasks)} task(s), {steps_seen} purge step(s) inspected.")
    if not tasks:
        problems.append("tasks.json records ZERO tasks. The registry had two on 2026-09-07, so "
                        "zero means the record was emptied, not that retention stopped.")
    if tasks and steps_seen == 0:
        problems.append("ZERO purge steps were found across every recorded task, so the "
                        "--include-locked assertion inspected nothing.")

    for problem in problems:
        print(f"::error::{problem}")
    if problems:
        return 1
    print("  no recorded purge step passes --include-locked, so `acr purge` skips locked "
          "manifests — which is what makes a lock a protection.")
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


def _scan2(gh_repo: str, text: str, where: str = "deployments/aks/x/values.x.yaml") -> OverlayScan:
    scan = OverlayScan(gh_repo=gh_repo, files=1)
    pins, floating = extract_overlay_pins(text)
    scan.pins = [(repo, tag, where) for repo, tag in pins]
    scan.floating = [(repo, tag, where) for repo, tag in floating]
    scan.instances = [(ident, host, where) for ident, host in extract_overlay_instances(text)]
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
           roster: dict[str, tuple[str, str]] | None = None,
           probe=None) -> tuple[Plan, list[str], FakeRegistry]:
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
                                   roster={"memex-cloud": ("not-installed", "never stood up")},
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
          f"ARM 24c: the enabled purge windows could not be read from this repository's own "
          f"retention record: {problem}")
    check(any("--ago" in line for line in windows),
          f"ARM 24c: a window was reported without the `--ago` that defines it: {windows}")
    with tempfile.TemporaryDirectory() as scratch:
        empty, problem = retention_windows(scratch)
        check(not empty and problem,
              "ARM 24c: a MISSING retention record reported windows, or reported no problem — "
              "an unstatable window must say so, never print as an empty list")

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
                        roster={"ghost": ("retired", "decommissioned in 2019")})
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
                               "scan_overlays_local", "scan_overlays_remote"}
    missing = run_calls - _calls(_drive)
    check(not missing,
          f"ARM 29: run() calls {sorted(missing)} and the self-test harness does not, so those "
          "steps are unfalsified. Mirror them in _drive.")

    # 🚨 AND THE OTHER DIRECTION, which is the one that stayed green under falsification: every arm
    # above can pass while `run()` no longer PERFORMS the step, because the harness has its own copy
    # of the sequence. Deleting `extractor_control()` from `run()` alone left this self-test green
    # until this check existed. Name the steps the protection decision must make.
    required = {"extractor_control", "read_instance_roster", "build_instances",
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
          "and counted as protecting NOTHING, and the two existing pin extractors still agree. AXIS 3: the set an installation is RUNNING is locked though no file pins it, its migration twin with it, the TAG is locked beside the manifest, an installation that did not answer is INCOMPLETE and refuses the unlock arm, silence is never retirement, a stale roster entry and an unknown running set are RED, the digest extractor is controlled against a fixture rather than inferred from the fleet, a tag lock that did not take is counted as protecting NOTHING, a locked INDEX is expanded to the platform manifests acr-cli would otherwise collect out from under it, and the harness provably drives the same path as run().")
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
    parser.add_argument("--check-retention-record", metavar="ROOT",
                        help="assert the committed .github/acr-retention record still describes a "
                             "purge the lock can protect against; no credential, no network")
    parser.add_argument("--self-test", action="store_true",
                        help="prove every arm fires and stays silent; no network")
    args = parser.parse_args()

    if args.self_test:
        return self_test()
    if args.check_retention_record:
        return check_retention_record(args.check_retention_record)
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
