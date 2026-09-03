#!/usr/bin/env python3
"""Refuse to merge a public-surface REMOVAL here until its cross-repo counterpart has LANDED.

WHY THIS EXISTS (MeshWeaver#2689)
---------------------------------
Core is the platform; MeshWeaver.Plugins and the four satellites consume it. When a change spans
both, nothing made the two halves land together — and the half that lands FIRST decides whether
somebody else's trunk goes red. Five incidents are recorded on #2689; the first is the shape:

    MeshWeaver#2678 ("the node-surface views leave the platform for a module") merged HERE and
    deleted ApiTokenLayoutAreas, GroupLayoutAreas, … from MeshWeaver.Graph. Its plugin half —
    MeshWeaver.Plugins#904 — was still OPEN. Measured consequence in that repo:

        MeshWeaver.AI.Test — 8 failures, all CodeCell*:
          No renderer is registered for area `Content` on hub `rbuergi/cell-…`

    …which failed the MeshWeaver.AI module bundle, which failed BOTH required compile gates. So
    every open pull request in that repo went red, and its main went red from 17:20 (last success
    16:48), on a change none of them made.

🚨 **Ordering is the whole invariant: the deleting half must land LAST.** Today it landed first,
and the repo that broke had no way to see it coming — the failure is invisible on the pull request
that causes it and surfaces in a DIFFERENT repository, on unrelated pull requests, minutes to
hours later. The people who see the red are never the people who caused it.

WHY NO OTHER GATE CAN CATCH IT
------------------------------
  * `check-type-forwards.py` answers BINARY compatibility — "can a module already published still
    bind this TypeRef?" — and is correctly silent on the cases where the answer is yes. #2678 was
    allow-listed there as "proven cross-repo moves nothing binds", which was TRUE and did not stop
    the other repo going red for two hours.
  * That script's `--sibling` flag could resolve a departure against a plugin checkout, and CI
    deliberately does not pass it: this repo is PUBLIC and the plugin repos are PRIVATE.
  * The plugin repo's own CI builds against the PUBLISHED core package, not core's main, so it is
    green until CD composes the two.

WHAT THIS GATE DOES
-------------------
It consumes the surface-removal REPORT that `check-type-forwards.py --surface-json` already
computes — one detector, proven by one set of self-tests, never a second one — and:

  * removals empty  -> PASS. Measured on this repo: `main~25 -> main` removes ZERO public types,
    so an ordinary pull request never meets this gate at all. `main~100 -> main` removes 116, all
    of them the Maps/Indexing carve-out (#2941) — the exact wave that produced #2689's incident
    shapes 4 and 5.
  * removals non-empty -> the pull-request body MUST carry a `Pairs-with:` declaration, and every
    declared counterpart must already be MERGED into its repository's DEFAULT BRANCH.

DECLARATION SYNTAX (in the pull-request body, one per line)
----------------------------------------------------------
    Pairs-with: Systemorph/MeshWeaver.Plugins#904
    Pairs-with: https://github.com/Systemorph/MeshWeaver.Plugins/pull/904
    Pairs-with: none — <reason, at least 12 characters>

🚨 A line that STARTS `Pairs-with:` and parses as neither is a FAILURE, never an ignored line. A
typo'd declaration that read as "no declaration" would be the same trapdoor as a gate that skips
on a missing input: the author believes they declared a pair, and the gate believes they did not.

🚨 Fenced code blocks and HTML comments are stripped BEFORE scanning, so this file's own examples
— and this gate's documentation page — cannot declare a pair by being quoted. Stripping can only
REMOVE candidate declarations, so it fails closed.

WHY "MERGED" RATHER THAN "GREEN"
--------------------------------
"Green and open" does not order anything: both halves are then free to merge in either order, and
#2678 merged first while its counterpart was green. Merged is the invariant stated on the issue,
and it subsumes red — a merged pull request passed its own repo's gates.

🚨 And merged is not enough on its own: the base is checked too. MeshWeaver#904 merged into
`feat/collaboration-module`, not `main`, which reads as "landed" in every summary view and shipped
nothing. So a counterpart must be merged into the DEFAULT BRANCH of its repository.

WHAT THIS GATE IS NOT
---------------------
It is NOT a checkout. It resolves a pull-request NUMBER the author declared, through the API,
under a GitHub App installation token — no plugin source enters core's build, and `dotnet build`
here still needs no sibling on disk. That is the line
`test/MeshWeaver.Documentation.Test/PlatformNeverDependsOnPluginsGuard.cs` draws, and the same
line `shared-rules` already sits on the permitted side of (it reads AGENTS.md from all seven
repos, on this same workflow, as a needs: of the required check). See
Doc/Architecture/RepositoryDependencyDirection § C.

It also cannot enumerate the consumers for you: the `none` escape is a declared, attributable
statement in the pull-request body, in the same spirit as a `scripts/type-forwards.allow` entry —
core cannot see a private repo's callers, so a human has to say. What the gate removes is the case
where nobody was ever asked.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
import urllib.error
import urllib.request
from dataclasses import dataclass
from pathlib import Path

FLEET_REGISTER = ".github/shared-rules.json"
THIS_REPO = "Systemorph/MeshWeaver"

# 🚨 THE CONTROL ARM. Every other field of the surface report can legitimately be empty on an
# ordinary pull request, so "this diff removed nothing" and "the scan read nothing" produce the
# same JSON — a gate that passed on the second would be the green-on-zero-evidence shape this
# repo's CI rules exist to prevent. MEASURED 2026-09-02: `src/` declares 1832 public top-level
# types across 35 assemblies. The floor is set an order of magnitude below that, so it survives a
# carve-out wave and still fails outright on a scan that read a fraction of the tree.
MIN_PUBLIC_TYPES_AT_BASE = 500

# `Pairs-with:` — optionally bulleted and/or bolded, as a pull-request body naturally writes it.
DECLARATION_RE = re.compile(
    r"^[ \t]*(?:[-*+][ \t]+)?\*{0,2}pairs[ \t_-]?with\*{0,2}[ \t]*:\*{0,2}[ \t]*(?P<value>.*?)[ \t]*$",
    re.IGNORECASE,
)
PAIR_REF_RE = re.compile(
    r"^(?:https?://github\.com/(?P<url_repo>[\w.-]+/[\w.-]+)/pull/(?P<url_no>\d+)"
    r"|(?P<repo>[\w.-]+/[\w.-]+)#(?P<no>\d+))"
    r"(?:[ \t]+.*)?$"
)
NONE_RE = re.compile(r"^none\b[ \t]*[-—–:,]?[ \t]*(?P<reason>.*)$", re.IGNORECASE)
MIN_REASON_CHARS = 12

# A `none` waiver that rests on a live-mesh sweep (AGENTS.md: search the live mesh with
# `search_chunks` before deleting public surface) must rest on a sweep that RAN. The envelope says
# so itself: `"searched": true`. `searched: false` is the #2741 no-embedding-provider shape, and a
# reason that speaks of a sweep without quoting the field is that shape read as clean (#3137).
SWEEP_DID_NOT_RUN_RE = re.compile(r"searched\W{0,3}false", re.IGNORECASE)
SWEEP_RAN_RE = re.compile(r"searched\W{0,3}true", re.IGNORECASE)
SWEEP_CLAIM_RE = re.compile(r"\b(?:sweep|swept|search_chunks)\b", re.IGNORECASE)

FENCE_RE = re.compile(r"^[ \t]*(```|~~~)")
HTML_COMMENT_RE = re.compile(r"<!--.*?-->", re.DOTALL)


class PairResolutionError(RuntimeError):
    """The counterpart could not be resolved. Always fatal — never a silent pass."""


@dataclass(frozen=True)
class PairRef:
    repo: str
    number: int
    raw: str


@dataclass(frozen=True)
class NoPair:
    reason: str
    raw: str


@dataclass(frozen=True)
class Unparseable:
    raw: str
    why: str


Declaration = PairRef | NoPair | Unparseable


# ─────────────────────────────── the body parser ───────────────────────────────


def strip_non_prose(body: str) -> str:
    """Remove HTML comments and fenced code blocks.

    Both can only REMOVE candidate declarations, so this fails closed: quoting the syntax in a
    fence (as this script's own docstring and the doc page do) never declares a pair, and hiding
    a real declaration in one makes the gate red rather than green.
    """
    body = HTML_COMMENT_RE.sub("", body)
    out: list[str] = []
    fence: str | None = None
    for line in body.replace("\r\n", "\n").split("\n"):
        m = FENCE_RE.match(line)
        if m:
            marker = m.group(1)
            if fence is None:
                fence = marker
                continue
            if marker == fence:
                fence = None
            continue
        if fence is None:
            out.append(line)
    return "\n".join(out)


def parse_declarations(body: str) -> list[Declaration]:
    found: list[Declaration] = []
    for line in strip_non_prose(body or "").split("\n"):
        # A quoted reply (`> Pairs-with: …`) is somebody else's text being cited, not this
        # author's declaration. Excluding it can only reduce what is accepted.
        if line.lstrip().startswith(">"):
            continue
        m = DECLARATION_RE.match(line)
        if not m:
            continue
        value = m.group("value").strip().strip("`").strip()
        if not value:
            found.append(Unparseable(line.strip(), "the declaration has no value"))
            continue
        ref = PAIR_REF_RE.match(value)
        if ref:
            repo = ref.group("repo") or ref.group("url_repo")
            number = int(ref.group("no") or ref.group("url_no"))
            found.append(PairRef(repo, number, line.strip()))
            continue
        none = NONE_RE.match(value)
        if none:
            reason = none.group("reason").strip()
            if len(reason.replace(" ", "")) < MIN_REASON_CHARS:
                found.append(Unparseable(
                    line.strip(),
                    f"`none` needs a REASON of at least {MIN_REASON_CHARS} characters — an "
                    "unexplained `none` is an unattributable waiver",
                ))
            elif SWEEP_DID_NOT_RUN_RE.search(reason):
                # 🚨 #3137's pull request cited a `search_chunks` sweep that answered
                # `"searched": false` and read it as "no callers". It means the deployment has NO
                # embedding provider and nothing was searched (#2741) — the envelope deliberately
                # carries no `count` so an absent field cannot be read as zero. A waiver resting on
                # it rests on nothing, so it is refused here rather than trusted.
                found.append(Unparseable(
                    line.strip(),
                    "the cited sweep did not run — `searched: false` means no embedding provider "
                    "and NOTHING was searched (#2741). Sweep on a deployment whose index is live "
                    "and quote its `searched: true`, or give a reason that does not rest on the sweep",
                ))
            elif SWEEP_CLAIM_RE.search(reason) and not SWEEP_RAN_RE.search(reason):
                found.append(Unparseable(
                    line.strip(),
                    "a reason that cites a live-mesh sweep must quote the envelope's "
                    "`searched: true` — a sweep answer without it is the #2741 shape read as clean",
                ))
            else:
                found.append(NoPair(reason, line.strip()))
            continue
        found.append(Unparseable(
            line.strip(),
            "expected `owner/repo#123`, a github.com pull URL, or `none — <reason>`",
        ))
    return found


# ─────────────────────────────── the fleet register ───────────────────────────────


def read_fleet(root: Path) -> set[str]:
    """The repositories a counterpart may live in — read, never hard-coded.

    Fails closed on every unreadable shape: an empty or missing register means this gate cannot
    say whether a declared repo is in the fleet, and "cannot say" must never read as "yes".
    """
    path = root / FLEET_REGISTER
    try:
        repos = json.loads(path.read_text(encoding="utf-8"))["repos"]
    except (OSError, ValueError, KeyError, TypeError) as e:
        raise PairResolutionError(f"cannot read the fleet register {FLEET_REGISTER}: {e}") from e
    fleet = {r for r in repos if isinstance(r, str) and r != THIS_REPO}
    if len(fleet) < 2:
        raise PairResolutionError(
            f"{FLEET_REGISTER} lists {len(fleet)} sibling repo(s) — the register did not expand, "
            "so no declaration could be validated against it"
        )
    return fleet


# ─────────────────────────────── the API resolver ───────────────────────────────


def _api(url: str, token: str) -> dict:
    req = urllib.request.Request(url, headers={
        "Authorization": f"Bearer {token}",
        "Accept": "application/vnd.github+json",
        "X-GitHub-Api-Version": "2022-11-28",
        "User-Agent": "meshweaver-cross-repo-pair-gate",
    })
    try:
        with urllib.request.urlopen(req, timeout=30) as resp:  # noqa: S310 — fixed https host
            return json.loads(resp.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        meaning = {
            401: "the App token is invalid or expired",
            403: "the token is valid but lacks pull-requests:read here, or is rate-limited",
            404: "no such pull request, or the App is not installed on that repo (GitHub reports "
                 "an invisible private repo as 404, not 403)",
        }.get(e.code, f"HTTP {e.code}")
        raise PairResolutionError(f"{url} -> {e.code}: {meaning}") from e
    except (urllib.error.URLError, TimeoutError, ValueError) as e:
        raise PairResolutionError(f"{url} -> could not reach api.github.com: {e}") from e


def github_resolver(token: str):
    def resolve(repo: str, number: int) -> dict:
        pr = _api(f"https://api.github.com/repos/{repo}/pulls/{number}", token)
        # 🚨 The DEFAULT BRANCH, read from the API rather than assumed to be `main`. A counterpart
        # merged into a feature branch reads as "landed" everywhere and ships nothing —
        # MeshWeaver.Plugins#904, the very pull request this gate's first incident is about,
        # merged into `feat/collaboration-module`.
        repo_info = _api(f"https://api.github.com/repos/{repo}", token)
        return {
            "merged": bool(pr.get("merged_at")),
            "state": str(pr.get("state", "")),
            "draft": bool(pr.get("draft")),
            "baseRef": str((pr.get("base") or {}).get("ref", "")),
            "defaultBranch": str(repo_info.get("default_branch", "")),
            "title": str(pr.get("title", "")),
            "url": str(pr.get("html_url", "")),
        }

    return resolve


# ─────────────────────────────── the verdict ───────────────────────────────


def evaluate(surface: dict, body: str, fleet: set[str], resolve) -> tuple[int, list[str]]:
    """Returns (exit code, lines to print). Every failure path returns 1; there is no other."""
    out: list[str] = []

    at_base = surface.get("publicTypesAtBase")
    if not isinstance(at_base, int) or at_base < MIN_PUBLIC_TYPES_AT_BASE:
        return 1, [
            f"::error::The surface report says the base tree declares {at_base} public top-level "
            f"type(s), below the floor of {MIN_PUBLIC_TYPES_AT_BASE}. The detector examined "
            "nothing, or examined the wrong tree — so 'no removals' would be a green on zero "
            "evidence. Check that the merge base was fetched and that src/ was scanned."
        ]

    removed = surface.get("removed")
    if not isinstance(removed, list):
        return 1, ["::error::The surface report has no `removed` list — it was not produced by "
                   "check-type-forwards.py --surface-json, or it was truncated."]

    out.append(
        f"Base tree declares {at_base} public top-level type(s) under src/; "
        f"this diff removes {len(removed)} public type(s) or member(s)."
    )
    if not removed:
        out.append("No public type or member left src/ in this diff — no cross-repo pair to gate.")
        return 0, out

    for entry in removed[:40]:
        out.append(f"  [{entry.get('category')}] {entry.get('assembly')} :: {entry.get('fullName')}")
    if len(removed) > 40:
        out.append(f"  … and {len(removed) - 40} more.")

    declarations = parse_declarations(body)
    bad = [d for d in declarations if isinstance(d, Unparseable)]
    if bad:
        for d in bad:
            out.append(f"::error::Unparseable declaration `{d.raw}` — {d.why}.")
        out.append(
            "::error::A line starting `Pairs-with:` that does not parse is a FAILURE, never an "
            "ignored line: an author who believes they declared a pair and a gate that believes "
            "they did not is exactly the state this gate exists to remove."
        )
        return 1, out

    if not declarations:
        out.append(
            f"::error::This pull request removes {len(removed)} public type(s) or member(s) from "
            "src/ and declares no counterpart. Public surface leaving this repo can red a plugin "
            "repo's trunk hours later, on pull requests that did not make the change "
            "(MeshWeaver#2689). Add ONE line to the pull-request body:"
        )
        out.append("::error::    Pairs-with: Systemorph/MeshWeaver.Plugins#904")
        out.append("::error::  …or, if no other repository is affected:")
        out.append("::error::    Pairs-with: none — <why nothing outside this repo referenced these>")
        out.append(
            "::error::The counterpart must be MERGED into its repo's default branch before this "
            "merges: the deleting half lands LAST."
        )
        return 1, out

    pairs = [d for d in declarations if isinstance(d, PairRef)]
    nones = [d for d in declarations if isinstance(d, NoPair)]
    if pairs and nones:
        out.append(
            "::error::This pull request declares BOTH a counterpart and `Pairs-with: none`. Those "
            "contradict; a reviewer cannot tell which is meant. Remove one."
        )
        for d in pairs + nones:
            out.append(f"::error::  {d.raw}")
        return 1, out

    if nones:
        for d in nones:
            out.append(f"Declared: no cross-repo counterpart — {d.reason}")
        out.append(
            "No counterpart to resolve. This is an attributable statement in the pull-request "
            "body, recorded here so it is reviewable; it is not a skip."
        )
        return 0, out

    failed = False
    for pair in pairs:
        if pair.repo not in fleet:
            out.append(
                f"::error::`{pair.raw}` names {pair.repo}, which is not a sibling repository in "
                f"{FLEET_REGISTER}. Known: {', '.join(sorted(fleet))}."
            )
            failed = True
            continue
        try:
            state = resolve(pair.repo, pair.number)
        except PairResolutionError as e:
            out.append(f"::error::Cannot resolve {pair.repo}#{pair.number}: {e}")
            failed = True
            continue

        where = f"{pair.repo}#{pair.number}" + (f" ({state['title']})" if state.get("title") else "")
        if not state["merged"]:
            what = (
                "still a DRAFT" if state.get("draft")
                else "still OPEN" if state.get("state") == "open"
                else f"CLOSED without merging (state={state.get('state')!r})"
            )
            out.append(
                f"::error::{where} is {what}. The deleting half must land LAST — merge the "
                f"counterpart first, then re-run this check. {state.get('url', '')}"
            )
            failed = True
            continue
        if state["baseRef"] != state["defaultBranch"]:
            out.append(
                f"::error::{where} merged into `{state['baseRef']}`, not "
                f"{pair.repo}'s default branch `{state['defaultBranch']}` — so it has NOT shipped "
                f"and this removal would still break that repo's trunk. {state.get('url', '')}"
            )
            failed = True
            continue
        out.append(f"  ok  {where} is merged into `{state['defaultBranch']}`.")

    if failed:
        return 1, out
    out.append("Every declared counterpart has landed — the deleting half may follow.")
    return 0, out


# ─────────────────────────────── self-test ───────────────────────────────
#
# 🚨 An unproven gate is no gate: every case must be shown to FIRE on its defect and stay SILENT
# on its fix. The PASSING rows carry as much weight as the failing ones — without them, a gate
# that always failed would score identically.

_HEALTHY = 1832
_REMOVED = [{
    "key": "MeshWeaver.Graph:MeshWeaver.Graph.ApiTokenLayoutAreas",
    "assembly": "MeshWeaver.Graph",
    "fullName": "MeshWeaver.Graph.ApiTokenLayoutAreas",
    "category": "departed",
    "landedIn": [],
}]
# MeshWeaver#3137 as the detector now reports it (check-type-forwards.py `member-removed`).
_REMOVED_MEMBERS = [
    {
        "key": "MeshWeaver.PluginCatalog:MeshWeaver.PluginCatalog.InstanceRegistryAuthenticator::CacheDuration",
        "assembly": "MeshWeaver.PluginCatalog",
        "fullName": "MeshWeaver.PluginCatalog.InstanceRegistryAuthenticator.CacheDuration",
        "category": "member-removed",
        "landedIn": [],
    },
    {
        "key": "MeshWeaver.PluginCatalog:MeshWeaver.PluginCatalog.InstanceRegistryAuthenticator::NegativeCacheDuration",
        "assembly": "MeshWeaver.PluginCatalog",
        "fullName": "MeshWeaver.PluginCatalog.InstanceRegistryAuthenticator.NegativeCacheDuration",
        "category": "member-removed",
        "landedIn": [],
    },
]
_FLEET = {"Systemorph/MeshWeaver.Plugins", "Systemorph/MeshWeaver.Education", "Systemorph/Memex"}


def _pr(**over) -> dict:
    base = {
        "merged": True, "state": "closed", "draft": False,
        "baseRef": "main", "defaultBranch": "main",
        "title": "the module half", "url": "https://github.com/x/y/pull/904",
    }
    base.update(over)
    return base


def _resolver(state: dict):
    def resolve(repo: str, number: int) -> dict:
        return state
    return resolve


def _raising_resolver(message: str):
    def resolve(repo: str, number: int) -> dict:
        raise PairResolutionError(message)
    return resolve


# (label, surface, body, resolver, should_pass, substrings the output must contain)
SELF_TESTS: list[tuple] = [
    (
        "an ordinary pull request removes nothing and never meets this gate",
        {"publicTypesAtBase": _HEALTHY, "removed": []},
        "Fixes a typo.",
        _resolver(_pr()),
        True,
        ("no cross-repo pair to gate",),
    ),
    (
        # THE #2678 SHAPE. Nine Graph view classes departed; the module half was open.
        "a removal with NO declaration fails, and says what to write",
        {"publicTypesAtBase": _HEALTHY, "removed": _REMOVED},
        "The node-surface views leave the platform for a module.",
        _resolver(_pr()),
        False,
        ("declares no counterpart", "Pairs-with: Systemorph/MeshWeaver.Plugins#904"),
    ),
    (
        "…and with the counterpart still OPEN it still fails — ordering is the invariant",
        {"publicTypesAtBase": _HEALTHY, "removed": _REMOVED},
        "Body.\n\nPairs-with: Systemorph/MeshWeaver.Plugins#904\n",
        _resolver(_pr(merged=False, state="open")),
        False,
        ("still OPEN", "must land LAST"),
    ),
    (
        "…a DRAFT counterpart says so specifically",
        {"publicTypesAtBase": _HEALTHY, "removed": _REMOVED},
        "Pairs-with: Systemorph/MeshWeaver.Plugins#904\n",
        _resolver(_pr(merged=False, state="open", draft=True)),
        False,
        ("still a DRAFT",),
    ),
    (
        "…a counterpart CLOSED without merging is an abandoned pair, not a landed one",
        {"publicTypesAtBase": _HEALTHY, "removed": _REMOVED},
        "Pairs-with: Systemorph/MeshWeaver.Plugins#904\n",
        _resolver(_pr(merged=False, state="closed")),
        False,
        ("CLOSED without merging",),
    ),
    (
        # 🚨 MeshWeaver.Plugins#904 — the pull request this gate's first incident is about —
        # merged into `feat/collaboration-module`. "Merged" alone would have passed it.
        "…MERGED INTO A FEATURE BRANCH has not shipped, and must not pass",
        {"publicTypesAtBase": _HEALTHY, "removed": _REMOVED},
        "Pairs-with: Systemorph/MeshWeaver.Plugins#904\n",
        _resolver(_pr(baseRef="feat/collaboration-module")),
        False,
        ("merged into `feat/collaboration-module`", "default branch"),
    ),
    (
        "…and merged into the default branch PASSES — the gate can be satisfied",
        {"publicTypesAtBase": _HEALTHY, "removed": _REMOVED},
        "Body.\n\nPairs-with: Systemorph/MeshWeaver.Plugins#904\n",
        _resolver(_pr()),
        True,
        ("is merged into `main`", "may follow"),
    ),
    (
        "a github.com pull URL is the same declaration",
        {"publicTypesAtBase": _HEALTHY, "removed": _REMOVED},
        "Pairs-with: https://github.com/Systemorph/MeshWeaver.Plugins/pull/904\n",
        _resolver(_pr()),
        True,
        ("is merged into `main`",),
    ),
    (
        "a bulleted, bolded declaration is how a body actually writes it",
        {"publicTypesAtBase": _HEALTHY, "removed": _REMOVED},
        "- **Pairs-with:** Systemorph/MeshWeaver.Plugins#904\n",
        _resolver(_pr()),
        True,
        ("is merged into `main`",),
    ),
    (
        "an explained `none` passes and is RECORDED, so the waiver is attributable",
        {"publicTypesAtBase": _HEALTHY, "removed": _REMOVED},
        "Pairs-with: none — internal helper, no repo outside core ever referenced it\n",
        _resolver(_pr()),
        True,
        ("no cross-repo counterpart", "internal helper"),
    ),
    (
        "…but a bare `none` does not — an unexplained waiver is unattributable",
        {"publicTypesAtBase": _HEALTHY, "removed": _REMOVED},
        "Pairs-with: none\n",
        _resolver(_pr()),
        False,
        ("needs a REASON",),
    ),
    (
        "…nor does `none` alongside a real counterpart — a reviewer cannot tell which is meant",
        {"publicTypesAtBase": _HEALTHY, "removed": _REMOVED},
        "Pairs-with: Systemorph/MeshWeaver.Plugins#904\nPairs-with: none — nothing else uses it\n",
        _resolver(_pr()),
        False,
        ("BOTH a counterpart and",),
    ),
    (
        # 🚨 The trapdoor this closes: a typo'd declaration reading as "no declaration" would put
        # the author and the gate in disagreement about whether a pair was declared.
        "an UNPARSEABLE declaration fails — it is never an ignored line",
        {"publicTypesAtBase": _HEALTHY, "removed": _REMOVED},
        "Pairs-with: MeshWeaver.Plugins 904\n",
        _resolver(_pr()),
        False,
        ("Unparseable declaration",),
    ),
    (
        "a repo outside the fleet register fails — the register is the closed set",
        {"publicTypesAtBase": _HEALTHY, "removed": _REMOVED},
        "Pairs-with: someone/else#1\n",
        _resolver(_pr()),
        False,
        ("not a sibling repository",),
    ),
    (
        # PRESENT is not VALID, applied to the answer rather than the credential.
        "an API failure fails the gate — an unresolvable counterpart is never a pass",
        {"publicTypesAtBase": _HEALTHY, "removed": _REMOVED},
        "Pairs-with: Systemorph/MeshWeaver.Plugins#904\n",
        _raising_resolver("404: the App is not installed on that repo"),
        False,
        ("Cannot resolve",),
    ),
    (
        # 🚨 THE CONTROL ARM. Without it, a detector that read the wrong tree reports zero
        # removals and this gate passes having examined nothing — indistinguishable from a
        # clean pull request.
        "a surface report whose base tree declares almost nothing FAILS, it does not pass",
        {"publicTypesAtBase": 3, "removed": []},
        "Pairs-with: none — nothing outside core referenced these\n",
        _resolver(_pr()),
        False,
        ("below the floor", "green on zero evidence"),
    ),
    (
        "…and a report with no `removed` list at all fails too",
        {"publicTypesAtBase": _HEALTHY},
        "",
        _resolver(_pr()),
        False,
        ("no `removed` list",),
    ),
    (
        # 🚨 This script's own docstring and Doc/Architecture/CrossRepoPairGate both quote the
        # syntax inside a fence. If a fence counted, documenting the gate would declare a pair.
        "a declaration inside a FENCED BLOCK is documentation, not a declaration",
        {"publicTypesAtBase": _HEALTHY, "removed": _REMOVED},
        "Write it like this:\n\n```\nPairs-with: Systemorph/MeshWeaver.Plugins#904\n```\n",
        _resolver(_pr()),
        False,
        ("declares no counterpart",),
    ),
    (
        "…and one inside an HTML comment is not one either",
        {"publicTypesAtBase": _HEALTHY, "removed": _REMOVED},
        "<!-- Pairs-with: Systemorph/MeshWeaver.Plugins#904 -->\n",
        _resolver(_pr()),
        False,
        ("declares no counterpart",),
    ),
    (
        "…nor is one in a QUOTED reply — that is somebody else's text being cited",
        {"publicTypesAtBase": _HEALTHY, "removed": _REMOVED},
        "> Pairs-with: Systemorph/MeshWeaver.Plugins#904\n",
        _resolver(_pr()),
        False,
        ("declares no counterpart",),
    ),
    (
        "an EMPTY body on a removing pull request fails rather than passing on nothing",
        {"publicTypesAtBase": _HEALTHY, "removed": _REMOVED},
        "",
        _resolver(_pr()),
        False,
        ("declares no counterpart",),
    ),
    # ── the sixth shape (#3103): a removed MEMBER is a removal, and a sweep that did not run is
    #    not a reason (#3137's pull request read `searched:false` as "no callers") ──
    (
        "a removed public MEMBER of a kept type meets the gate exactly like a removed type",
        {"publicTypesAtBase": _HEALTHY, "removed": _REMOVED_MEMBERS},
        "Instance-key resolution reads live mirrors (#3119).",
        _resolver(_pr()),
        False,
        ("declares no counterpart", "InstanceRegistryAuthenticator.CacheDuration", "member-removed"),
    ),
    (
        "`none` resting on a sweep that answered searched:false is REFUSED — nothing was searched",
        {"publicTypesAtBase": _HEALTHY, "removed": _REMOVED_MEMBERS},
        "Pairs-with: none — search_chunks sweep on memex: {\"searched\": false}, no callers found\n",
        _resolver(_pr()),
        False,
        ("did not run", "searched: false", "#2741"),
    ),
    (
        "`none` that CLAIMS a sweep without quoting `searched: true` is refused too",
        {"publicTypesAtBase": _HEALTHY, "removed": _REMOVED_MEMBERS},
        "Pairs-with: none — swept the live mesh, zero callers\n",
        _resolver(_pr()),
        False,
        ("must quote the envelope's `searched: true`",),
    ),
    (
        "`none` resting on a sweep that RAN (`searched: true`) passes",
        {"publicTypesAtBase": _HEALTHY, "removed": _REMOVED_MEMBERS},
        "Pairs-with: none — swept memex.meshweaver.cloud with search_chunks (searched:true), 0 callers; Plugins fixed in #1209\n",
        _resolver(_pr()),
        True,
        ("No counterpart to resolve",),
    ),
    (
        "`none` with a reason that never mentions a sweep is judged on its length alone, as before",
        {"publicTypesAtBase": _HEALTHY, "removed": _REMOVED_MEMBERS},
        "Pairs-with: none — the two constants were only read by the test this PR rewrites\n",
        _resolver(_pr()),
        True,
        ("No counterpart to resolve",),
    ),
]


def self_test() -> int:
    failed = 0
    for label, surface, body, resolve, should_pass, expected in SELF_TESTS:
        code, lines = evaluate(surface, body, _FLEET, resolve)
        text = "\n".join(lines)
        passed = code == 0
        missing = [s for s in expected if s not in text]
        if passed != should_pass or missing:
            failed += 1
            print(f"SELF-TEST FAILED: {label}")
            if passed != should_pass:
                print(f"  expected {'pass' if should_pass else 'FAIL'}, "
                      f"got {'pass' if passed else 'FAIL'}")
            for s in missing:
                print(f"  output never mentioned {s!r}")
            print("  ---\n  " + text.replace("\n", "\n  "))
        else:
            print(f"ok: {label}")

    # A register that did not expand must fail closed, and that is not reachable through
    # `evaluate` — it happens while READING the register, so it is proven separately.
    for register, why in [
        ({"repos": [THIS_REPO]}, "a register naming only this repo validates nothing"),
        ({"nope": []}, "a register with no `repos` key is unreadable"),
    ]:
        import tempfile
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / ".github").mkdir()
            (root / FLEET_REGISTER).write_text(json.dumps(register), encoding="utf-8")
            try:
                read_fleet(root)
            except PairResolutionError:
                print(f"ok: {why} — fails closed")
            else:
                failed += 1
                print(f"SELF-TEST FAILED: {why} — read_fleet returned instead of failing")

    if failed:
        print(f"\n{failed} self-test(s) failed — the gate cannot be trusted.")
        return 1
    print(f"\nAll {len(SELF_TESTS) + 2} self-tests passed.")
    return 0


# ─────────────────────────────── entry point ───────────────────────────────


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--surface-json", help="the report from check-type-forwards.py --surface-json")
    ap.add_argument(
        "--pr-body-file",
        help="file holding the pull-request body. A FILE, never an inline argument: a body is "
        "attacker-controlled text and interpolating it into a shell command is an injection.",
    )
    ap.add_argument("--root", default=".", help="repository root (for the fleet register)")
    ap.add_argument("--self-test", action="store_true", help="prove the gate is not vacuous")
    args = ap.parse_args()

    if args.self_test:
        return self_test()
    if not args.surface_json or not args.pr_body_file:
        ap.error("--surface-json and --pr-body-file are required unless --self-test is given")

    token = os.environ.get("GITHUB_TOKEN", "")
    if not token:
        print("::error::GITHUB_TOKEN is empty. This gate resolves a declared counterpart through "
              "the API and cannot report on one it could not read.")
        return 1

    try:
        surface = json.loads(Path(args.surface_json).read_text(encoding="utf-8"))
    except (OSError, ValueError) as e:
        print(f"::error::Cannot read the surface report {args.surface_json}: {e}. The detector "
              "did not run, so this gate has examined nothing.")
        return 1

    try:
        body = Path(args.pr_body_file).read_text(encoding="utf-8")
    except OSError as e:
        print(f"::error::Cannot read the pull-request body from {args.pr_body_file}: {e}")
        return 1

    try:
        fleet = read_fleet(Path(args.root))
    except PairResolutionError as e:
        print(f"::error::{e}")
        return 1

    code, lines = evaluate(surface, body, fleet, github_resolver(token))
    for line in lines:
        print(line)
    return code


if __name__ == "__main__":
    sys.exit(main())
