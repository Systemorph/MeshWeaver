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


@dataclass
class OverlayScan:
    gh_repo: str
    files: int = 0
    pins: list[tuple[str, str, str]] = field(default_factory=list)      # (repo, tag, where)
    floating: list[tuple[str, str, str]] = field(default_factory=list)
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
        pins, floating = extract_overlay_pins(path.read_text(encoding="utf-8", errors="replace"))
        scan.pins.extend((repo, tag, rel) for repo, tag in pins)
        scan.floating.extend((repo, tag, rel) for repo, tag in floating)
    return scan


# ── The registry, behind one seam so the self-test can drive it offline ─────────────────────────


class Registry:
    """Every registry interaction this job makes. Read methods first, then the one write."""

    def __init__(self, name: str) -> None:
        self.name = name
        self._tag_cache: dict[tuple[str, str], tuple[str | None, str]] = {}

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

    # -- the one write --------------------------------------------------------------------------

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
    axis2_sites: int = 0
    axis2_pairs: dict[tuple[str, str], list[str]] = field(default_factory=dict)


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
    plan.axis2_sites = axis2_sites

    # ---- the denominator rule, per axis -------------------------------------------------------
    unreadable_axis1 = any(scan.unreadable for scan in axis1)
    unreadable_axis2 = any(scan.unreadable for scan in axis2)
    if axis1_sites == 0 and not unreadable_axis1:
        plan.blockers.append(
            "AXIS 1 found ZERO digest pins across the whole fleet. Six repositories pinned at "
            "least one on 2026-09-06, so zero means the extractor stopped matching, not that "
            "pinning stopped. Run --self-test."
        )
    if axis2_sites == 0 and not unreadable_axis2:
        plan.blockers.append(
            "AXIS 2 found ZERO deployment-overlay tag pins across the whole fleet. Seven were "
            "measured on 2026-09-07 (Memex's three overlays and this repository's whisper chart), "
            "so zero means the overlay reader stopped matching, not that the overlays stopped "
            "pinning. Run --self-test."
        )
    return plan


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
           inventory: dict[str, list[Manifest]], apply: bool, release_enabled: bool) -> None:
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
    emit("")
    emit("    UNION — manifests the fleet pins")
    emit(f"      distinct manifests wanted                {len(plan.wanted)}")
    emit(f"      …already protected (deleteEnabled false) {len(plan.already_protected)}")
    emit(f"      …TO LOCK                                 {len(plan.to_lock)}")
    emit(f"      …that could NOT be protected             "
         f"{len([w for w in plan.wanted if w.problem])}")
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
            protected, why = registry.read_delete_enabled(entry.acr_repo, entry.digest)
            if protected is False:
                plan.locked_now += 1
                print(f"locked {entry.acr_repo}@{entry.digest}")
                continue
            failures.append(
                f"{entry.acr_repo}@{entry.digest}: the lock WRITE succeeded and the manifest still "
                + ("reads deleteEnabled=true" if protected is True
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
    resolve_and_classify(plan, registry, inventory, inventory_errors)

    failures: list[str] = []
    if apply:
        failures.extend(apply_locks(plan, registry))

    release_blocked = bool(plan.blockers) or bool(failures)
    if release_enabled and apply and not release_blocked:
        failures.extend(apply_releases(plan, registry))

    report(plan, axis1, axis2, registry_name, inventory, apply, release_enabled)

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
             f"({plan.locked_now} newly locked this run); "
             f"{plan.released_now} released.")
    else:
        emit(f"{len(plan.already_protected)} manifest(s) already protected, "
             f"{len(plan.to_lock)} would be locked. No registry mutation was made.")
    return 0


# ── The retention record: a credential-free assertion that runs on every pull request ──────────


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
                 repo_error: str | None = None) -> None:
        super().__init__("fake")
        self._manifests = manifests
        self._tags = tags
        self._repo_error = repo_error
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


def _scan2(gh_repo: str, text: str) -> OverlayScan:
    scan = OverlayScan(gh_repo=gh_repo, files=1)
    pins, floating = extract_overlay_pins(text)
    scan.pins = [(repo, tag, "deployments/aks/x/values.x.yaml") for repo, tag in pins]
    scan.floating = [(repo, tag, "deployments/aks/x/values.x.yaml") for repo, tag in floating]
    return scan


def _inventory(locked: set[tuple[str, str]] | None = None,
               extra: list[Manifest] | None = None) -> dict[str, list[Manifest]]:
    locked = locked or set()

    def make(acr_repo: str, digest: str, tags: list[str]) -> Manifest:
        return Manifest(acr_repo, digest, (acr_repo, digest) not in locked, True, tags)

    inventory = {
        "mw-plugin-test": [make("mw-plugin-test", TESTER, ["3.0.0-ci.7917"])],
        "memex-portal-ai": [make("memex-portal-ai", PORTAL, ["3.0.0-ci.7917"]),
                            make("memex-portal-ai", OVERLAY_PORTAL, ["3.0.0-ci.7926"])],
        "memex-migration": [make("memex-migration", OVERLAY_MIGRATION, ["3.0.0-ci.7926"])],
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


def _drive(axis1, axis2, registry: FakeRegistry, apply: bool = True,
           release_enabled: bool = False) -> tuple[Plan, list[str], FakeRegistry]:
    """The SAME sequence `run()` performs, minus the report — so the self-test falsifies the real
    decision path rather than a paraphrase of it. Its per-lock chatter is swallowed; the assertions
    read `registry.writes`, which is the thing that would actually reach the registry."""
    with contextlib.redirect_stdout(io.StringIO()):
        inventory, inventory_errors = read_inventory(registry)
        plan = build_plan(axis1, axis2)
        resolve_and_classify(plan, registry, inventory, inventory_errors)
        failures: list[str] = []
        if apply:
            failures.extend(apply_locks(plan, registry))
        if release_enabled and apply and not (plan.blockers or failures):
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
    check(len(plan.wanted) == 5, f"ARM 1: expected 5 wanted manifests, got {len(plan.wanted)}")

    # A floating tag is reported and NOT locked — locking `:latest` pins bytes that move.
    check(not any(repo == "memex-log-watcher" for repo, _, _ in registry.writes),
          "ARM 1: a FLOATING tag (`:latest`) was locked; it has no fixed manifest to protect")
    # A templated value is not a pin at all.
    check(all(digest != "{{" for _, digest, _ in registry.writes),
          "ARM 1: a `{{ … }}` template was read as a tag")

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

    for line in failures:
        print(f"::error::self-test: {line}")
    if failures:
        return 1
    print("self-test: 15 arms — lock requested on both axes and both overlay shapes, idempotent, "
          "unreadable repo / truncated tree / zero pins / unclassified / malformed / gone / "
          "unresolved tag / indeterminate / unreadable registry all RED with nothing released, "
          "release arm off by default and live when enabled, report-only writes nothing, a lock "
          "write that exits 0 without taking and one whose read-back cannot answer are both RED "
          "and counted as protecting NOTHING, and the two existing pin extractors still agree.")
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
