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

  * removals empty  -> PASS, UNLESS the body DECLARES a counterpart (MeshWeaver#7397): a
    declared `Pairs-with:` is resolved whatever the detector found, because the author is asserting
    a coupling the detector cannot see — a PackageReference or PackageVersion leaving core. Measured
    on this repo: `main~25 -> main` removes ZERO public types, so an ordinary pull request (which
    declares nothing) never meets this gate at all. `main~100 -> main` removes 116, all
    of them the Maps/Indexing carve-out (#2941) — the exact wave that produced #2689's incident
    shapes 4 and 5.
  * removals non-empty -> the pull-request body MUST carry a `Pairs-with:` declaration, and every
    declared counterpart must already be MERGED into its repository's DEFAULT BRANCH.

A PROJECT PATH IS SURFACE TOO (MeshWeaver#7415)
-----------------------------------------------
A repository whose CI checks this one out and `ProjectReference`s a project BY PATH breaks when
that `.csproj` is deleted or renamed — MSB9008 plus CS0246 — although no public type left `src/`.
MeshWeaver#7297 deleted `memex/aspire/Memex.Portal.ServiceDefaults/…csproj`; the surface report
(which scans `src/` only) reported nothing that concerned it, and MeshWeaver.Deploy's REQUIRED
`Chart guards (vs platform)` went red on every Deploy pull request until Deploy adapted. So the
diff's deleted and renamed `.csproj` files (`--base`, merge base to HEAD, rename-aware) are a
second removal list with its own control arm (a base tree with fewer than
MIN_PROJECTS_AT_BASE project files is a scan that read nothing), and either list being non-empty
asks for the same declaration. A rename is a removal: the old path is what a consumer names.

The repositories a counterpart may be DECLARED in are the register's `repos` plus its
`pair-only-repos` — repositories that consume this one by checkout but carry no shared rule
blocks (MeshWeaver.Deploy). Before #7415 a `Pairs-with:` naming Deploy was refused as "not a
sibling", so the author of #7297 could not have declared the half that broke even had they known.

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
import subprocess
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

# The same control arm for the PROJECT-PATH list (#7415). MEASURED 2026-10-28: the tree carries 64
# `.csproj` files (67 at #7297's base). A floor well below that survives a carve-out and still
# fails a listing that read the wrong tree or nothing at all.
MIN_PROJECTS_AT_BASE = 20
PROJECT_SUFFIX = ".csproj"

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
        register = json.loads(path.read_text(encoding="utf-8"))
        repos = register["repos"]
    except (OSError, ValueError, KeyError, TypeError) as e:
        raise PairResolutionError(f"cannot read the fleet register {FLEET_REGISTER}: {e}") from e
    fleet = {r for r in repos if isinstance(r, str) and r != THIS_REPO}
    if len(fleet) < 2:
        raise PairResolutionError(
            f"{FLEET_REGISTER} lists {len(fleet)} sibling repo(s) — the register did not expand, "
            "so no declaration could be validated against it"
        )
    # Repositories that consume this one by CHECKOUT (and so can be the counterpart of a removal)
    # but carry no shared rule blocks, so they are not in `repos` (#7415: MeshWeaver.Deploy).
    # Optional; a present key of the wrong shape is unreadable, never ignored.
    extra = register.get("pair-only-repos", [])
    if not isinstance(extra, list) or not all(isinstance(r, str) and "/" in r for r in extra):
        raise PairResolutionError(
            f"{FLEET_REGISTER} `pair-only-repos` must be a list of owner/repo names, got {extra!r}"
        )
    return fleet | {r for r in extra if r != THIS_REPO}


# ─────────────────────────────── the project-path report ───────────────────────────────


def _git(root: Path, *args: str) -> str:
    try:
        return subprocess.run(
            ["git", "-C", str(root), *args], check=True, capture_output=True, text=True,
        ).stdout
    except (OSError, subprocess.CalledProcessError) as e:
        detail = getattr(e, "stderr", "") or str(e)
        raise PairResolutionError(f"git {' '.join(args)} failed: {detail.strip()}") from e


def parse_name_status_z(raw: str) -> list[dict]:
    """Parse `git diff -z --name-status` into removal entries.

    `-z` because a plain listing C-quotes a non-ASCII path. A deletion is `D\\0path\\0`; a rename
    is `R<score>\\0old\\0new\\0`. Any other status in a listing filtered to D/R is a parse failure,
    never a skipped record.
    """
    fields = raw.split("\0")
    if fields and fields[-1] == "":
        fields.pop()
    out: list[dict] = []
    i = 0
    while i < len(fields):
        status = fields[i]
        if status == "D" and i + 1 < len(fields):
            out.append({"path": fields[i + 1], "status": "deleted"})
            i += 2
        elif status.startswith("R") and i + 2 < len(fields):
            out.append({"path": fields[i + 1], "status": "renamed", "to": fields[i + 2]})
            i += 3
        else:
            raise PairResolutionError(f"unexpected record in the project-path listing: {fields[i:i + 3]!r}")
    return out


def project_report(root: Path, base: str) -> dict:
    """The `.csproj` files this diff deletes or renames, merge base -> HEAD, with the denominator."""
    at_base = sum(
        1 for p in _git(root, "ls-tree", "-r", "-z", "--name-only", base).split("\0")
        if p.endswith(PROJECT_SUFFIX)
    )
    listing = _git(root, "diff", "-z", "--name-status", "-M", "--diff-filter=DR", base, "HEAD",
                   "--", f"*{PROJECT_SUFFIX}")
    return {"projectsAtBase": at_base, "removed": parse_name_status_z(listing)}


# ─────────────────────────────── the API resolver ───────────────────────────────


def _api(url: str, token: str, urlopen=None, sleep=None, clock=None) -> dict:
    """One GET through the shared classifier. An OUTAGE (rate limit, 5xx, no connection past the
    budget) raises `PairResolutionError` whose message STARTS with "NO VERDICT — GitHub API …", so
    the red names an outage to re-run after, never a grant the token "lacks, or is rate-limited"
    (MeshWeaver#7448). A deliberate refusal keeps the sentence that names its fix."""
    reader = _github_reader()
    prefix = "https://api.github.com/"
    if not url.startswith(prefix):
        raise PairResolutionError(f"{url} is not under {prefix}")
    path = url[len(prefix):]
    kwargs = {k: v for k, v in (("urlopen", urlopen), ("sleep", sleep), ("clock", clock)) if v is not None}
    try:
        return reader.api_get(path, token=token, user_agent="meshweaver-cross-repo-pair-gate", **kwargs)
    except reader.ApiUnavailable as e:
        raise PairResolutionError(e.sentence()) from e
    except reader.ApiRefused as e:
        code = str(e).split()[1] if len(str(e).split()) > 1 else "?"
        meaning = {
            "401": "the App token is invalid or expired",
            "403": "the token is valid but lacks pull-requests:read here (a plain refusal, not a rate limit)",
            "404": "no such pull request, or the App is not installed on that repo (GitHub reports "
                   "an invisible private repo as 404, not 403)",
        }.get(code, f"HTTP {code}")
        raise PairResolutionError(f"{url} -> {code}: {meaning}") from e
    except ValueError as e:
        raise PairResolutionError(f"{url} -> the answer is not JSON: {e}") from e


def _github_reader():
    """`scripts/read-current-pr-body.py` — the ONE GitHub REST classification every CI gate shares:
    a rate-limit 403/429, a 5xx or a dropped connection is retried to its reset inside a bounded
    budget and otherwise reported as NO VERDICT, never as a missing grant (MeshWeaver#7448)."""
    import importlib.util
    path = Path(__file__).resolve().parents[1] / "scripts" / "read-current-pr-body.py"
    spec = importlib.util.spec_from_file_location("read_current_pr_body", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


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


def evaluate(surface: dict, body: str, fleet: set[str], resolve,
             projects: dict | None) -> tuple[int, list[str]]:
    """Returns (exit code, lines to print). Every failure path returns 1; there is no other.

    `projects` is the project-path report (`project_report`). It is REQUIRED: `None` means the
    listing never ran, and a gate that read no project paths must not pass on "none left".
    """
    out: list[str] = []

    if not isinstance(projects, dict):
        return 1, ["::error::No project-path report — the deleted/renamed `.csproj` listing never "
                   "ran, so a project path leaving this repo (MeshWeaver#7415) would go unseen."]
    projects_at_base = projects.get("projectsAtBase")
    if not isinstance(projects_at_base, int) or projects_at_base < MIN_PROJECTS_AT_BASE:
        return 1, [
            f"::error::The project-path report says the base tree holds {projects_at_base} "
            f"`{PROJECT_SUFFIX}` file(s), below the floor of {MIN_PROJECTS_AT_BASE}. The listing "
            "read the wrong tree or nothing, so 'no project path left' would be a green on zero "
            "evidence. Check that the merge base was fetched."
        ]
    removed_projects = projects.get("removed")
    if not isinstance(removed_projects, list):
        return 1, ["::error::The project-path report has no `removed` list."]

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
    out.append(
        f"Base tree holds {projects_at_base} `{PROJECT_SUFFIX}` file(s); "
        f"this diff deletes or renames {len(removed_projects)} of them."
    )
    declarations = parse_declarations(body)
    if not removed and not removed_projects:
        # 🚨 A DECLARED pair is resolved even when the detector found nothing (MeshWeaver#7397).
        # The detector sees public types, public members and project paths — not a
        # PackageReference or PackageVersion leaving core, which a satellite can depend on just as
        # hard (#7205 dropped YamlDotNet; #7179 dropped pins a satellite read versionless). An
        # author who writes `Pairs-with:` is asserting a coupling the detector cannot see, and a
        # gate that returned before reading it held nothing while the body claimed it did.
        if not any(isinstance(d, (PairRef, Unparseable)) for d in declarations):
            out.append("No public type or member left src/ and no project path moved in this "
                       "diff — no cross-repo pair to gate.")
            return 0, out
        out.append("No public type or member left src/ and no project path moved, but the body "
                   "DECLARES a counterpart — resolving it, because the declaration is the author "
                   "asserting a coupling the detector cannot see (a dependency or pin removal).")

    for entry in removed[:40]:
        out.append(f"  [{entry.get('category')}] {entry.get('assembly')} :: {entry.get('fullName')}")
    if len(removed) > 40:
        out.append(f"  … and {len(removed) - 40} more.")
    for entry in removed_projects[:40]:
        moved = f" -> {entry['to']}" if entry.get("to") else ""
        out.append(f"  [project-{entry.get('status')}] {entry.get('path')}{moved}")
    if len(removed_projects) > 40:
        out.append(f"  … and {len(removed_projects) - 40} more.")
    if removed_projects:
        out.append(
            "A project path leaving this repo breaks every repository whose CI checks this one out "
            "and ProjectReferences that path (MSB9008 + CS0246) — MeshWeaver#7415: #7297 moved a "
            "project and MeshWeaver.Deploy's required `Chart guards (vs platform)` went red on every "
            "Deploy pull request. This gate cannot see who references it; check each repository "
            f"a counterpart may live in: {', '.join(sorted(fleet))}."
        )

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
            f"src/ and moves {len(removed_projects)} project path(s), and declares no counterpart. "
            "Public surface leaving this repo can red a plugin "
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


# ── a PROJECT PATH leaving this repo (#7415) ──
_NO_PROJECTS = {"projectsAtBase": 64, "removed": []}
# MeshWeaver#7297 exactly: no public type left src/, one project file deleted outside it.
_PROJECTS_7297 = {"projectsAtBase": 67, "removed": [
    {"path": "memex/aspire/Memex.Portal.ServiceDefaults/Memex.Portal.ServiceDefaults.csproj",
     "status": "deleted"},
]}
_PROJECTS_RENAMED = {"projectsAtBase": 64, "removed": [
    {"path": "src/MeshWeaver.Hosting/MeshWeaver.Hosting.csproj", "status": "renamed",
     "to": "src/MeshWeaver.Hosting.Core/MeshWeaver.Hosting.Core.csproj"},
]}
_FLEET_WITH_DEPLOY = _FLEET | {"Systemorph/MeshWeaver.Deploy"}

# (label, surface, body, resolver, projects, fleet, should_pass, substrings)
PROJECT_SELF_TESTS: list[tuple] = [
    (
        # 🚨 THE #7415 SHAPE. Before it, this row PASSED: the surface report was empty, so the
        # gate said "no cross-repo pair to gate" while Deploy's required check was about to go red.
        "a DELETED project file with no declaration fails, though no public type left src/",
        {"publicTypesAtBase": _HEALTHY, "removed": []},
        "chore(platform): core drops memex/ (delete half)",
        _resolver(_pr()), _PROJECTS_7297, _FLEET, False,
        ("declares no counterpart", "[project-deleted] memex/aspire/Memex.Portal.ServiceDefaults",
         "MSB9008"),
    ),
    (
        "a RENAMED project file is a removal too — the old path is what a consumer names",
        {"publicTypesAtBase": _HEALTHY, "removed": []},
        "Rename the hosting project.",
        _resolver(_pr()), _PROJECTS_RENAMED, _FLEET, False,
        ("declares no counterpart",
         "[project-renamed] src/MeshWeaver.Hosting/MeshWeaver.Hosting.csproj -> "),
    ),
    (
        "…the move names every repository a counterpart may live in, read from the register",
        {"publicTypesAtBase": _HEALTHY, "removed": []},
        "Pairs-with: Systemorph/MeshWeaver.Plugins#4333\n",
        _resolver(_pr()), _PROJECTS_7297, _FLEET_WITH_DEPLOY, True,
        ("Systemorph/MeshWeaver.Deploy", "may follow"),
    ),
    (
        # 🚨 Before #7415 the register had no way to name a checkout consumer that carries no
        # shared rule blocks, so this declaration was refused as "not a sibling repository".
        "a counterpart in a PAIR-ONLY repository (MeshWeaver.Deploy) is declarable and resolved",
        {"publicTypesAtBase": _HEALTHY, "removed": []},
        "Pairs-with: Systemorph/MeshWeaver.Plugins#4333\nPairs-with: Systemorph/MeshWeaver.Deploy#37\n",
        _resolver(_pr()), _PROJECTS_7297, _FLEET_WITH_DEPLOY, True,
        ("ok  Systemorph/MeshWeaver.Deploy#37", "may follow"),
    ),
    (
        "…and an OPEN Deploy counterpart holds the deleting half, like any other",
        {"publicTypesAtBase": _HEALTHY, "removed": []},
        "Pairs-with: Systemorph/MeshWeaver.Deploy#37\n",
        _resolver(_pr(merged=False, state="open")), _PROJECTS_7297, _FLEET_WITH_DEPLOY, False,
        ("still OPEN",),
    ),
    (
        "a missing project-path report FAILS — the listing never ran",
        {"publicTypesAtBase": _HEALTHY, "removed": []},
        "", _resolver(_pr()), None, _FLEET, False,
        ("No project-path report",),
    ),
    (
        "a project-path report whose base tree holds almost no project file FAILS",
        {"publicTypesAtBase": _HEALTHY, "removed": []},
        "", _resolver(_pr()), {"projectsAtBase": 2, "removed": []}, _FLEET, False,
        ("below the floor", "green on zero"),
    ),
    (
        # 🚨 THE #7397 SHAPE. #7205 dropped a package dependency from core (no public type, no
        # project path) and DECLARED its satellite counterpart; before the fix this row PASSED
        # with "no cross-repo pair to gate" while the counterpart was still open.
        "a DECLARED counterpart is resolved though the detector found no removal — OPEN holds it",
        {"publicTypesAtBase": _HEALTHY, "removed": []},
        "Drops a package dependency.\n\nPairs-with: Systemorph/MeshWeaver.Plugins#4227\n",
        _resolver(_pr(merged=False, state="open")), _NO_PROJECTS, _FLEET, False,
        ("DECLARES a counterpart", "still OPEN", "must land LAST"),
    ),
    (
        "…and once that declared counterpart has merged, the change may follow",
        {"publicTypesAtBase": _HEALTHY, "removed": []},
        "Pairs-with: Systemorph/MeshWeaver.Plugins#4227\n",
        _resolver(_pr()), _NO_PROJECTS, _FLEET, True,
        ("ok  Systemorph/MeshWeaver.Plugins#4227", "may follow"),
    ),
    (
        "…a malformed declaration on a no-removal diff FAILS rather than reading as none",
        {"publicTypesAtBase": _HEALTHY, "removed": []},
        "Pairs-with: MeshWeaver.Plugins 4227\n",
        _resolver(_pr()), _NO_PROJECTS, _FLEET, False,
        ("Unparseable declaration",),
    ),
    (
        "…and `Pairs-with: none` on a no-removal diff still passes",
        {"publicTypesAtBase": _HEALTHY, "removed": []},
        "Pairs-with: none — nothing outside core consumed the dropped package\n",
        _raising_resolver("must not be called"), _NO_PROJECTS, _FLEET, True,
        ("no cross-repo pair to gate",),
    ),
    (
        "an ordinary diff that moves no project still never meets the gate",
        {"publicTypesAtBase": _HEALTHY, "removed": []},
        "Fixes a typo.", _resolver(_pr()), _NO_PROJECTS, _FLEET, True,
        ("no project path moved",),
    ),
]


def _git_fixture_report() -> tuple[bool, str]:
    """End to end over a REAL repository: the listing finds a deletion and a rename, counts the
    base denominator, and ignores a modified project and a deleted non-project file."""
    import tempfile
    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        _git(root, "init", "-q")
        _git(root, "config", "user.email", "self-test@example.com")
        _git(root, "config", "user.name", "self-test")
        _git(root, "config", "commit.gpgsign", "false")
        for i in range(MIN_PROJECTS_AT_BASE + 5):
            d = root / "src" / f"P{i}"
            d.mkdir(parents=True)
            (d / f"P{i}.csproj").write_text(f"<Project><!-- {i} --></Project>\n", encoding="utf-8")
        (root / "notes.md").write_text("x\n", encoding="utf-8")
        _git(root, "add", "-A")
        _git(root, "commit", "-q", "-m", "base")
        base = _git(root, "rev-parse", "HEAD").strip()
        (root / "src/P0/P0.csproj").unlink()
        (root / "src/P1").rename(root / "src/P1Moved")
        (root / "src/P1Moved/P1.csproj").rename(root / "src/P1Moved/P1Moved.csproj")
        (root / "src/P2/P2.csproj").write_text("<Project><!-- changed --></Project>\n", encoding="utf-8")
        (root / "notes.md").unlink()
        _git(root, "add", "-A")
        _git(root, "commit", "-q", "-m", "head")
        report = project_report(root, base)
    got = sorted((e["status"], e["path"], e.get("to", "")) for e in report["removed"])
    want = [("deleted", "src/P0/P0.csproj", ""),
            ("renamed", "src/P1/P1.csproj", "src/P1Moved/P1Moved.csproj")]
    ok = report["projectsAtBase"] == MIN_PROJECTS_AT_BASE + 5 and got == want
    return ok, f"projectsAtBase={report['projectsAtBase']} removed={got}"


def self_test() -> int:
    import tempfile
    failed = 0

    def check(label, code, lines, should_pass, expected) -> None:
        nonlocal failed
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

    for label, surface, body, resolve, should_pass, expected in SELF_TESTS:
        code, lines = evaluate(surface, body, _FLEET, resolve, _NO_PROJECTS)
        check(label, code, lines, should_pass, expected)
    for label, surface, body, resolve, projects, fleet, should_pass, expected in PROJECT_SELF_TESTS:
        code, lines = evaluate(surface, body, fleet, resolve, projects)
        check(label, code, lines, should_pass, expected)

    extra = 0
    # The `-z` listing parser: a deletion and a rename parse; any other record is a FAILURE.
    extra += 1
    parsed = parse_name_status_z("D\0a/A.csproj\0R097\0b/B.csproj\0c/C.csproj\0")
    if parsed != [{"path": "a/A.csproj", "status": "deleted"},
                  {"path": "b/B.csproj", "status": "renamed", "to": "c/C.csproj"}]:
        failed += 1
        print(f"SELF-TEST FAILED: the -z listing parser read {parsed!r}")
    else:
        print("ok: the -z listing parser reads a deletion and a rename")
    extra += 1
    try:
        parse_name_status_z("M\0a/A.csproj\0")
    except PairResolutionError:
        print("ok: an unexpected listing record fails closed")
    else:
        failed += 1
        print("SELF-TEST FAILED: an unexpected listing record was skipped instead of failing")

    extra += 1
    ok, detail = _git_fixture_report()
    if ok:
        print(f"ok: the project-path listing over a real repository ({detail})")
    else:
        failed += 1
        print(f"SELF-TEST FAILED: the project-path listing over a real repository read {detail}")

    # A register that did not expand must fail closed, and that is not reachable through
    # `evaluate` — it happens while READING the register, so it is proven separately.
    for register, why in [
        ({"repos": [THIS_REPO]}, "a register naming only this repo validates nothing"),
        ({"nope": []}, "a register with no `repos` key is unreadable"),
        ({"repos": [THIS_REPO, "Systemorph/MeshWeaver.Plugins", "Systemorph/Memex"],
          "pair-only-repos": "Systemorph/MeshWeaver.Deploy"},
         "a `pair-only-repos` that is not a list is unreadable, never ignored"),
    ]:
        extra += 1
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

    # …and a PAIR-ONLY repository is part of the declarable set (#7415).
    extra += 1
    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        (root / ".github").mkdir()
        (root / FLEET_REGISTER).write_text(json.dumps({
            "repos": [THIS_REPO, "Systemorph/MeshWeaver.Plugins", "Systemorph/Memex"],
            "pair-only-repos": ["Systemorph/MeshWeaver.Deploy"],
        }), encoding="utf-8")
        fleet = read_fleet(root)
    if fleet == {"Systemorph/MeshWeaver.Plugins", "Systemorph/Memex", "Systemorph/MeshWeaver.Deploy"}:
        print("ok: a pair-only repository is declarable")
    else:
        failed += 1
        print(f"SELF-TEST FAILED: read_fleet with a pair-only repository returned {sorted(fleet)}")

    total = len(SELF_TESTS) + len(PROJECT_SELF_TESTS) + extra
    if failed:
        print(f"\n{failed} of {total} self-test(s) failed — the gate cannot be trusted.")
        return 1
    print(f"\nAll {total} self-tests passed.")
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
    ap.add_argument(
        "--base",
        help="the MERGE BASE to list deleted/renamed project files against (#7415) — the same "
        "commit the surface report was computed from",
    )
    ap.add_argument("--self-test", action="store_true", help="prove the gate is not vacuous")
    args = ap.parse_args()

    if args.self_test:
        return self_test()
    if not args.surface_json or not args.pr_body_file or not args.base:
        ap.error("--surface-json, --pr-body-file and --base are required unless --self-test is given")

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

    try:
        projects = project_report(Path(args.root), args.base)
    except PairResolutionError as e:
        print(f"::error::Cannot list the project files this diff deletes or renames: {e}")
        return 1

    code, lines = evaluate(surface, body, fleet, github_resolver(token), projects)
    for line in lines:
        print(line)
    return code


if __name__ == "__main__":
    sys.exit(main())
