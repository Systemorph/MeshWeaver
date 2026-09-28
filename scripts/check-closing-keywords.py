#!/usr/bin/env python3
"""Refuse a pull-request body whose closing keywords would close an issue a merge must not close.

THE SHAPE. GitHub closes an issue when a merged pull request's body contains
`close|closes|closed|fix|fixes|fixed|resolve|resolves|resolved` immediately before a reference to
it. The parser binds the keyword to the ONE reference that follows it and reads nothing else — not
the sentence around it, not a negation in front of it, not a qualifier behind it. That makes two
failure modes possible that no author intends, and a third that house policy forbids outright.

MEASURED — three real misfires in this repository on one day, all confirmed against the API
(`closingIssuesReferences` registered a closing link on all three, and the issue's own timeline
shows the merging app closing it within two seconds of the merge):

  * #5201 → #5057. The body read **"Does not close #5057"** — written to be explicit that the
    issue stays open. `close` sits immediately before `#5057`, so it closed it. Merged 10:45:11Z,
    closed 10:45:13Z, reopened by hand 10:47:22Z. The disclaimer's own words are what fired.
  * #5174 → #2299. The body opened `Closes #2299's classification half` and said two sentences
    later that the issue stays open for the half it did not fix. GitHub closed the whole issue.
    Merged 04:49:48Z, closed 04:49:49Z, reopened 06:20:44Z with the reason written out.
  * #5190 → #2299, again, 57 minutes after that deliberate reopen — by a DOCUMENTATION pull
    request whose body narrated the first misfire in the past tense: `… closed #2299 against its
    own body's statement …`. Prose ABOUT a close closes. (The same body also carried
    `` `Closes #2299's classification half` `` inside a code span, which fires nothing — see the
    code-span rule below.)

WHAT THIS REFUSES. Three independent checks, each with its own message and its own remedy:

  (a) A NEGATED keyword is a contradiction. The author's sentence says the issue stays open and
      the parser closes it. Remedy: `Refs #N` / `see #N` — no keyword before the number.
  (b) A `sev:H` / `sev:B` issue may not be closed by a merge. Those close on post-roll production
      verification, never on a merge (policy `severity-closes-on-verification`). Remedy: `Refs #N`
      now, close it after the roll — or, when the verification has ALREADY happened, the escape
      below.
  (c) A POSSESSIVE reference (`Closes #N's classification half`) claims to close a PART. GitHub
      closes the whole issue. Remedy: `Fixes the classification half of #N` — the keyword no
      longer sits before the number, so nothing closes and the sentence still reads.

🚨 WHY THREE AND NOT TWO. Measured against the incidents at the state each one had AT ITS MERGE:
(a) catches #5201 only; (b) catches #5201 and #5190; #5174 is caught by NEITHER, because #2299 was
labelled `sev:M` when it merged and was relabelled `sev:H` only after the reopen — 90 minutes
later. (c) is what catches it, and it is the narrowest of the three: a closing keyword whose
reference is immediately followed by `'s`. Without it the gate would cover two of the three
incidents it was built from.

THE ESCAPE — `Verified-closing: #N — <reason>`. A pull request that legitimately closes a
`sev:H`/`sev:B` after the verification has happened must have a way through, or the gate becomes a
wall and people route around it. The escape is explicit, per-issue and reasoned, in the pull-request
body where a reviewer reads it — the same shape as `Pairs-with:`, `Implementers:` and
`Mirror-sync:`, and the same spirit as a `scripts/*.allow` transitional entry: it names what it
releases and it carries a reason.

🚨 The escape releases check (b) ONLY. It cannot release (a): a body that says it does not close
the issue and an escape that says the close is verified are two statements that cannot both be
true, and the remedy is to delete the negation, not to out-vote it. It cannot release (c) either:
rewording a possessive costs four words and says what the author meant.

🚨 What the escape does NOT do is close the issue. `Verified-closing:` contains no closing keyword,
so a body carrying only the escape closes nothing. The escape says the close is allowed; a plain
`Closes #N` still does it. That is why an escape naming an issue the body does not close is
REFUSED as stale rather than ignored — it is the `Closes #A, #B` trap wearing a different hat, and
an escape nobody can act on is the kind of line that rots into every template.

🚨 WHAT THE ESCAPE CANNOT CHECK, stated plainly: whether the verification actually happened. No
regular expression can adjudicate that, and one that pretended to would be a worse gate than none —
it would teach authors which words to type. The escape's whole contribution is to make the close
DELIBERATE, PER-ISSUE and REVIEWABLE: it must name the issue, it must carry a reason, and the run
prints the reason beside the label the issue carried when the gate read it, so the claim is on the
record for the reviewer and for whoever reads the run afterwards.

WHAT THE PARSER SEES, and why this file mirrors it rather than improving on it:
  * A keyword binds ONE reference. `Closes #A, #B` closes #A only — so the gate reports each
    reference separately and never assumes a list was understood.
  * A keyword inside a code span or a fence fires nothing (measured: #3018's `` `Fixes #2897` ``
    left #2897 open). Both are stripped before the scan.
  * A keyword anywhere else fires — headings, future tense, past-tense narration, a disclaimer.
  * Cross-repository references (`owner/repo#N`) close in THAT repository. By default this gate
    reads issues in its own repository only; a cross-repository reference is named in the run and
    label-checked nowhere. A caller may explicitly protect a public repository. Satellite PRs use
    that mode for Systemorph/MeshWeaver: only explicit core references are checked, using the
    anonymous public REST API with positive and negative controls. A failure to prove that reader
    works is red, never an absent issue.

NOT ESTABLISHED (stated because a gate's blind spots belong with it, not in a commit message):
  * whether GitHub's parser reads a keyword inside a BLOCKQUOTE. This gate scans quoted lines, the
    fail-closed direction: a quoted `Closes #N` reds the gate and is reworded, which is cheap;
    skipping them would let a real close through if the parser does read them.
  * whether a closing keyword can close a PULL REQUEST. This gate resolves every reference and
    reports a pull-request target as closing nothing, without firing.

Usage:
    check-closing-keywords.py --repo <owner/repo> --pr-body-file <file>
    check-closing-keywords.py --repo <caller/repo> --protect-public-repo Systemorph/MeshWeaver \
        --only-protected-repos --pr-body-file <file>
    check-closing-keywords.py --self-test
"""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
import urllib.error
import urllib.request

# ── the parser this file mirrors ────────────────────────────────────────────────────────────────

KEYWORDS = r"close[sd]?|fix(?:e[sd])?|resolve[sd]?"

# The four reference spellings GitHub accepts, longest first so the alternation cannot bind the
# short form inside the long one.
REFERENCE = (
    r"(?:"
    r"https?://github\.com/(?P<urlslug>[A-Za-z0-9._-]+/[A-Za-z0-9._-]+)/issues/"
    r"|(?P<slug>[A-Za-z0-9._-]+/[A-Za-z0-9._-]+)#"
    r"|GH-"
    r"|#"
    r")(?P<number>\d+)(?![0-9])"
)

CLOSING_RE = re.compile(
    rf"(?<![A-Za-z0-9_])(?P<keyword>{KEYWORDS})\s*:?\s+{REFERENCE}(?P<possessive>['’]s)?",
    re.I,
)

# Any reference at all, keyword or not — used to tell "the body mentions #N" from "the body closes
# #N" when an escape is checked for staleness.
ANY_REFERENCE_RE = re.compile(REFERENCE, re.I)

HTML_COMMENT_RE = re.compile(r"<!--.*?-->", re.S)
FENCE_RE = re.compile(r"^\s*(```+|~~~+)")
# A code span is a run of backticks closed by an equal run. Replaced by a space rather than removed,
# so stripping can never JOIN two words into a keyword that was not written.
CODE_SPAN_RE = re.compile(r"(?P<ticks>`+)(?:(?!(?P=ticks)).)*?(?P=ticks)", re.S)

# 🚨 The negation window is TWO words, measured against the shapes that actually occur
# ("does not close", "does not yet close", "will not fix", "doesn't close", "cannot resolve") and
# against the false positive a wider window would produce ("not only does this fix #N", where the
# negation is three words back and means the opposite). Widening it costs a false red on a
# legitimate close, which is the direction that turns a gate into a wall.
NEGATION_WORDS = frozenset({"not", "never", "cannot", "neither", "nor", "without"})
NEGATION_WINDOW = 2
# Emphasis and heading punctuation is invisible to GitHub's parser and must be invisible here:
# "**Does not close #5057**" is the measured incident and its negation sits behind two asterisks.
EMPHASIS_CHARS = "*_~`#>"
SENTENCE_END = ".!?;"

ESCAPE_LABEL_RE = re.compile(
    r"^\s*(?:[-*+]\s+)?\*{0,2}Verified[ \t_-]?closing\*{0,2}\s*:", re.I
)
ESCAPE_RE = re.compile(
    r"^\s*(?:[-*+]\s+)?\*{0,2}Verified[ \t_-]?closing\*{0,2}\s*:\s*(?P<statement>\S.*)$",
    re.I | re.S,
)
# A declaration WRAPS. Collection stops at a blank line or at the next recognised label, so one
# declaration can never swallow the label after it — the same rule the sibling gates use.
CONTINUATION_STOP_RE = re.compile(
    r"^\s*(?:[-*+]\s+)?\*{0,2}(?:Verified[ \t_-]?closing|Mirror[ \t_-]?sync|Implementers|"
    r"Pairs[ \t_-]?with|Satellite-pins|Closes|Fixes|Resolves)\*{0,2}\s*[:#]",
    re.I,
)
MAX_DECLARATION_LINES = 6

ESCAPE_SPLIT_RE = re.compile(r"^(?P<refs>[^—–]*?)\s*(?:—|–|--|\s-\s)\s*(?P<reason>.+)$", re.S)
# 🚨 A reason floor, and a list of words that are not reasons. Neither adjudicates the evidence —
# see the escape note in the module docstring — they refuse a statement that is not one.
MIN_REASON_CHARS = 20
EMPTY_AFFIRMATIONS = frozenset(
    {"yes", "no", "ok", "okay", "done", "verified", "true", "n/a", "na", "tbd", "confirmed"}
)

BLOCKING_LABELS = ("sev:B", "sev:H")

# 🚨 A BOUND ON THE WORK, because the resolver is a subprocess per SUBJECT and the job is capped at
# five minutes. Matches are deduplicated and resolutions are memoised by issue number, so the cost
# is the count of DISTINCT local subjects — but nothing in a pull-request body bounds THAT, and a
# gate that is killed at its cap produces no verdict, which reads as "the gate did not run". Over
# the cap it exits RED naming the count, the same "could not read my subject" shape the Undecidable
# path uses. The worst of the 99 merged bodies measured (two days to 2026-09-22T11:17Z) resolves
# TWO subjects, so this sits ~30x above anything real and cannot become a wall.
MAX_RESOLVED_SUBJECTS = 60
# Anonymous REST is limited to 60 requests/hour per source IP. The protected satellite path needs
# two controlled reads plus one per distinct issue, so keep one PR well below that shared ceiling.
MAX_ANONYMOUS_ISSUE_SUBJECTS = 20
PUBLIC_ISSUE_READ_PROBES = {"systemorph/meshweaver": 5011}
PUBLIC_ISSUE_NOT_FOUND_SENTINEL = 2147483647


class Undecidable(Exception):
    """The gate could not READ its subject.

    🚨 Raised, never swallowed, and never folded into "nothing closes". A reference the gate could
    not resolve means the answer is UNKNOWN, and reporting unknown as clean is the trapdoor this
    gate exists to avoid.
    """


# ── prose ───────────────────────────────────────────────────────────────────────────────────────


def strip_non_prose(body: str) -> str:
    """Remove what GitHub's parser does not read: HTML comments, fenced blocks, code spans.

    Blockquotes are deliberately KEPT — see the module docstring: whether the parser reads them is
    not established, and scanning them is the fail-closed direction.
    """
    body = (body or "").replace("\r\n", "\n")
    body = HTML_COMMENT_RE.sub(" ", body)

    out: list[str] = []
    fence: str | None = None
    for line in body.split("\n"):
        m = FENCE_RE.match(line)
        if m:
            marker = m.group(1)
            if fence is None:
                fence = marker
            elif marker[0] == fence[0] and len(marker) >= len(fence):
                fence = None
            out.append("")
            continue
        out.append("" if fence is not None else line)
    text = "\n".join(out)

    return CODE_SPAN_RE.sub(lambda m: " " * len(m.group(0)), text)


def negation_before(prose: str, start: int) -> str | None:
    """The negating word within NEGATION_WINDOW words before `start`, or None.

    Emphasis punctuation is stripped because it is invisible to GitHub; a sentence boundary stops
    the walk because a negation in the previous sentence negates the previous sentence.
    """
    head = prose[max(0, start - 160) : start]
    words = head.split()
    for word in reversed(words[-NEGATION_WINDOW:]):
        bare = word.strip(EMPHASIS_CHARS + "()[]\"'“”,")
        if not bare:
            continue
        if bare[-1] in SENTENCE_END:
            return None
        lowered = bare.lower()
        if lowered in NEGATION_WORDS or lowered.endswith("n't") or lowered.endswith("n’t"):
            return bare
    return None


class Reference:
    """One `KEYWORD <reference>` match: what it says and what the parser will do with it."""

    def __init__(self, match: re.Match[str], prose: str, repo: str):
        self.keyword = match.group("keyword")
        self.number = int(match.group("number"))
        self.slug = match.group("slug") or match.group("urlslug") or repo
        self.explicit_repository = bool(match.group("slug") or match.group("urlslug"))
        self.local = self.slug.lower() == repo.lower()
        self.possessive = bool(match.group("possessive"))
        self.negation = negation_before(prose, match.start())
        self.text = " ".join(match.group(0).split())
        self.start = match.start()

    def __repr__(self) -> str:  # pragma: no cover - diagnostics only
        return f"<Reference {self.text!r} slug={self.slug} negated={self.negation!r}>"


def closing_references(body: str, repo: str) -> list[Reference]:
    prose = strip_non_prose(body)
    return [Reference(m, prose, repo) for m in CLOSING_RE.finditer(prose)]


def mentioned_numbers(body: str, repo: str) -> set[int]:
    """Every issue number the body references at all, keyword or not, in THIS repository."""
    prose = strip_non_prose(body)
    out: set[int] = set()
    for m in ANY_REFERENCE_RE.finditer(prose):
        slug = m.group("slug") or m.group("urlslug") or repo
        if slug.lower() == repo.lower():
            out.add(int(m.group("number")))
    return out


# ── the escape ──────────────────────────────────────────────────────────────────────────────────


def _escape_blocks(body: str) -> list[str]:
    """Every `Verified-closing:` declaration, joined with its continuation lines.

    A quoted line is somebody else's text being cited, not this author's declaration — the same
    rule the sibling declaration gates apply. Fences and comments are already gone.
    """
    lines = strip_non_prose(body or "").split("\n")
    out: list[str] = []
    i = 0
    while i < len(lines):
        if lines[i].lstrip().startswith(">") or not ESCAPE_LABEL_RE.match(lines[i]):
            i += 1
            continue
        block = [lines[i].strip()]
        j = i + 1
        while j < len(lines) and len(block) < MAX_DECLARATION_LINES:
            nxt = lines[j]
            if not nxt.strip() or CONTINUATION_STOP_RE.match(nxt) or nxt.lstrip().startswith(">"):
                break
            block.append(nxt.strip())
            j += 1
        out.append(" ".join(block))
        i = j
    return out


def escapes(
    body: str,
    repo: str,
    checked_repositories: set[str] | None = None,
    only_checked_repositories: bool = False,
) -> tuple[dict[tuple[str, int], str], list[str]]:
    """({(repository, issue number): reason}, [declarations REFUSED, with the reason]).

    🚨 A line that starts `Verified-closing:` and does not qualify is a FAILURE, never an ignored
    line. An author who believes they declared something and a gate that believes they did not is
    the disagreement these gates exist to remove, and from the outside it is indistinguishable
    from a skip.
    """
    checked = {r.lower() for r in (checked_repositories or {repo})}
    released: dict[tuple[str, int], str] = {}
    refused: list[str] = []
    for block in _escape_blocks(body):
        shown = block if len(block) <= 160 else block[:157] + "…"
        m = ESCAPE_RE.match(block)
        if not m:
            refused.append(
                f"`{shown}` — a `Verified-closing:` label with nothing after it is not a "
                "declaration."
            )
            continue
        statement = m.group("statement").strip()
        split = ESCAPE_SPLIT_RE.match(statement)
        if not split:
            refused.append(
                f"`{shown}` — the declaration names no verification. Write "
                "`Verified-closing: #N — <what was verified, and where>`; the reason is the "
                "whole point of the escape."
            )
            continue
        references = [
            (
                (r.group("slug") or r.group("urlslug") or repo).lower(),
                int(r.group("number")),
            )
            for r in ANY_REFERENCE_RE.finditer(split.group("refs"))
        ]
        targets = [(target_repo, number) for target_repo, number in references if target_repo in checked]
        # A local Verified-closing declaration belongs to the caller's own gate, not the
        # cross-repository-only satellite scan. It cannot release a core issue because no
        # unqualified reference is interpreted as Systemorph/MeshWeaver in that mode.
        # Cross-only mode ignores declarations with no parsed protected target. The declaration
        # cannot release anything in scope without such a target, and reason prose is not a target.
        if only_checked_repositories and not targets:
            continue
        if not targets:
            refused.append(
                f"`{shown}` — the declaration names no issue in the checked repositories "
                f"({', '.join(sorted(checked))}). The escape is "
                "PER-ISSUE: `Verified-closing: #N — <reason>`, never a blanket waiver."
            )
            continue
        reason = " ".join(split.group("reason").split())
        if reason.strip(".").lower() in EMPTY_AFFIRMATIONS or len(reason) < MIN_REASON_CHARS:
            refused.append(
                f"`{shown}` — `{reason}` is not a verification. Say WHAT was verified and "
                "WHERE — the deployment, the reading, the run. This gate cannot check that the "
                "verification happened; it can only make sure the claim is on the record for a "
                "reviewer, which an empty one is not."
            )
            continue
        for target in targets:
            released[target] = reason
    return released, refused


# ── resolution ──────────────────────────────────────────────────────────────────────────────────


def resolve_via_gh(repo: str, number: int) -> dict | None:
    """The issue as GitHub sees it, or None when nothing is there.

    A 404 from a token that the job's preflight has PROVEN can read this repository's labels means
    the number names nothing, which closes nothing. Any other failure is Undecidable and reds.
    """
    proc = subprocess.run(
        ["gh", "api", "--method", "GET", f"repos/{repo}/issues/{number}"],
        capture_output=True,
        text=True,
        timeout=45,
    )
    if proc.returncode != 0:
        combined = (proc.stderr or "") + (proc.stdout or "")
        if "HTTP 404" in combined or '"status": "404"' in combined or "Not Found" in combined:
            return None
        raise Undecidable(
            f"`gh api repos/{repo}/issues/{number}` failed ({proc.returncode}): "
            f"{(proc.stderr or '').strip()[:300] or 'no output'}. 'Could not tell' and 'nothing "
            "closes' are never one colour."
        )
    try:
        parsed = json.loads(proc.stdout)
    except json.JSONDecodeError as exc:
        raise Undecidable(f"the REST response for #{number} is not JSON: {exc}") from exc
    if not isinstance(parsed, dict) or parsed.get("number") != number:
        raise Undecidable(
            f"the REST response for #{number} identifies something else "
            f"({parsed.get('number') if isinstance(parsed, dict) else type(parsed).__name__})."
        )
    return {
        "number": number,
        "is_pull_request": "pull_request" in parsed,
        "state": parsed.get("state"),
        "labels": [
            lbl["name"]
            for lbl in parsed.get("labels", [])
            if isinstance(lbl, dict) and isinstance(lbl.get("name"), str)
        ],
    }


def _public_issue_request(repo: str, number: int) -> dict | None:
    """Read a public issue anonymously; distinguish a proven 404 from every other failure."""
    request = urllib.request.Request(
        f"https://api.github.com/repos/{repo}/issues/{number}",
        headers={
            "Accept": "application/vnd.github+json",
            "X-GitHub-Api-Version": "2022-11-28",
            "User-Agent": "MeshWeaver-closing-keyword-gate",
        },
    )
    try:
        with urllib.request.urlopen(request, timeout=5) as response:
            raw = response.read()
    except urllib.error.HTTPError as exc:
        if exc.code == 404:
            return None
        response_body = exc.read(1024).decode("utf-8", errors="replace")
        headers = exc.headers or {}
        remaining = headers.get("X-RateLimit-Remaining", "")
        reset = headers.get("X-RateLimit-Reset", "")
        retry_after = headers.get("Retry-After", "")
        if (
            exc.code == 429
            or remaining == "0"
            or "rate limit" in str(exc.reason).lower()
            or "rate limit" in response_body.lower()
        ):
            details = ", ".join(
                part for part in (
                    f"x-ratelimit-reset={reset}" if reset else "",
                    f"retry-after={retry_after}s" if retry_after else "",
                ) if part
            )
            raise Undecidable(
                f"anonymous GitHub REST read of {repo}#{number} was rate-limited "
                f"(HTTP {exc.code}{'; ' + details if details else ''}); no retry was attempted. "
                "Wait until the indicated window resets before rerunning the gate."
            ) from exc
        raise Undecidable(
            f"anonymous GitHub REST read of {repo}#{number} returned HTTP {exc.code}; "
            "only a controlled 404 proves absence."
        ) from exc
    except (urllib.error.URLError, TimeoutError, OSError) as exc:
        raise Undecidable(
            f"anonymous GitHub REST read of {repo}#{number} failed: {exc}; "
            "a network failure is not an absent issue."
        ) from exc

    try:
        parsed = json.loads(raw)
    except (json.JSONDecodeError, UnicodeDecodeError) as exc:
        raise Undecidable(f"the anonymous REST response for {repo}#{number} is not JSON: {exc}") from exc
    if not isinstance(parsed, dict) or parsed.get("number") != number:
        raise Undecidable(
            f"the anonymous REST response for {repo}#{number} identifies something else "
            f"({parsed.get('number') if isinstance(parsed, dict) else type(parsed).__name__})."
        )
    expected_repository_url = f"https://api.github.com/repos/{repo}".lower()
    if str(parsed.get("repository_url", "")).lower() != expected_repository_url:
        raise Undecidable(
            f"the anonymous REST response for {repo}#{number} identifies repository "
            f"{parsed.get('repository_url')!r}, not {expected_repository_url}."
        )
    labels = parsed.get("labels")
    if not isinstance(labels, list) or any(
        not isinstance(label, dict) or not isinstance(label.get("name"), str) for label in labels
    ):
        raise Undecidable(f"the anonymous REST response for {repo}#{number} has no readable labels array.")
    return {
        "number": number,
        "is_pull_request": "pull_request" in parsed,
        "state": parsed.get("state"),
        "labels": [label["name"] for label in labels],
    }


def public_issue_resolver():
    """Build a public resolver whose positive and negative controls run before any verdict."""
    verified: set[str] = set()
    cache: dict[tuple[str, int], dict | None] = {}

    def resolve(repo: str, number: int) -> dict | None:
        normalized = repo.lower()
        if normalized not in PUBLIC_ISSUE_READ_PROBES:
            raise Undecidable(f"no positive public-read control is configured for {repo}.")
        if normalized not in verified:
            probe_number = PUBLIC_ISSUE_READ_PROBES[normalized]
            positive = _public_issue_request(repo, probe_number)
            if positive is None:
                raise Undecidable(
                    f"the public-read positive control {repo}#{probe_number} returned 404; "
                    "issue-label visibility was not proven."
                )
            negative = _public_issue_request(repo, PUBLIC_ISSUE_NOT_FOUND_SENTINEL)
            if negative is not None:
                raise Undecidable(
                    f"the public-read negative control {repo}#{PUBLIC_ISSUE_NOT_FOUND_SENTINEL} "
                    "did not return 404; the resolver cannot distinguish absence from a read failure."
                )
            cache[(normalized, probe_number)] = positive
            verified.add(normalized)
        key = (normalized, number)
        if key not in cache:
            cache[key] = _public_issue_request(repo, number)
        return cache[key]

    return resolve


# ── the verdict ─────────────────────────────────────────────────────────────────────────────────


def evaluate(
    body: str,
    repo: str,
    resolve,
    checked_repositories: set[str] | None = None,
    only_checked_repositories: bool = False,
) -> tuple[list[str], list[str], list[str]]:
    """(errors, notes, honoured) — errors is what makes the gate red."""
    errors: list[str] = []
    notes: list[str] = []
    honoured: list[str] = []
    checked = {r.lower() for r in (checked_repositories or {repo})}

    released, refused = escapes(body, repo, checked, only_checked_repositories)
    errors.extend(f"A `Verified-closing:` declaration was refused: {r}" for r in refused)

    refs = closing_references(body, repo)
    if only_checked_repositories:
        refs = [ref for ref in refs if ref.explicit_repository and ref.slug.lower() in checked]
    if not refs:
        notes.append(
            "No closing keyword binds an explicit reference to a protected repository."
            if only_checked_repositories
            else "No closing keyword binds any issue reference in this body."
        )
    closed_here = {
        (r.slug.lower(), r.number) for r in refs if r.slug.lower() in checked and not r.negation
    }

    # Bound by distinct repository/issue pairs before any REST work begins.
    subjects = {(r.slug.lower(), r.number) for r in refs if r.slug.lower() in checked}
    subject_limit = (
        MAX_ANONYMOUS_ISSUE_SUBJECTS if only_checked_repositories else MAX_RESOLVED_SUBJECTS
    )
    if len(subjects) > subject_limit:
        raise Undecidable(
            f"this body binds closing keywords to {len(subjects)} distinct issues in "
            f"{', '.join(sorted(checked))}, over the cap of {subject_limit}. Each distinct "
            "issue costs one API read, and a job killed at its cap produces no verdict at all. "
            "Refusing up front instead. Split this into smaller pull requests."
        )

    resolved: dict[tuple[str, int], dict | None] = {}

    def resolve_once(target_repo: str, number: int) -> dict | None:
        key = (target_repo.lower(), number)
        if key not in resolved:
            resolved[key] = resolve(target_repo, number)
        return resolved[key]

    for target, reason in sorted(released.items()):
        target_repo, number = target
        display = f"{target_repo}#{number}" if target_repo != repo.lower() else f"#{number}"
        if target not in closed_here:
            errors.append(
                f"`Verified-closing: {display}` releases nothing: no closing keyword in this body "
                f"binds {display} (a negated one does not count — delete the negation instead). "
                "The escape permits a close; it does not perform one, so a plain `Closes "
                f"{display}` still has to be there. Remove the declaration or add the keyword."
            )
        else:
            notes.append(
                f"Escape declared for {display} — it releases the severity check for that issue and "
                "nothing else."
            )

    seen: set[tuple[str, int, bool, bool]] = set()
    for ref in refs:
        key = (ref.slug.lower(), ref.number, bool(ref.negation), ref.possessive)
        if key in seen:
            continue
        seen.add(key)

        in_scope = ref.slug.lower() in checked
        target_reference = (
            f"{ref.slug}#{ref.number}" if not ref.local else f"#{ref.number}"
        )
        issue = resolve_once(ref.slug, ref.number) if in_scope else None
        also = ""
        if issue and not issue["is_pull_request"]:
            blocking_now = [lbl for lbl in issue["labels"] if lbl in BLOCKING_LABELS]
            if blocking_now:
                also = (
                    f" 🚨 And {ref.slug}#{ref.number} carries `{blocking_now[0]}`, so a merge may not close "
                    "it at all: a plain closing keyword here would be refused too (policy "
                    "`severity-closes-on-verification`)."
                )

        if ref.negation:
            errors.append(
                f"NEGATED CLOSING KEYWORD — `{ref.text}` (negated by “{ref.negation}”). "
                "GitHub matches the keyword immediately before the number and reads NOTHING of the "
                "sentence around it, so the words that make this a disclaimer are the words that "
                f"close {target_reference} on merge — measured on #5201/#5057, closed two seconds "
                "after the merge. Write `Refs "
                f"{target_reference}` or `see {target_reference}` instead. An escape cannot release this: "
                "if you do mean to close it, delete the negation." + also
            )
            continue

        if ref.possessive:
            errors.append(
                f"POSSESSIVE CLOSING REFERENCE — `{ref.text}` claims to close a PART of "
                f"{target_reference}; GitHub closes the whole issue. Measured on #5174/#2299: the body "
                "said two sentences later that the issue stays open for the half it did not fix, "
                "and the merge closed it anyway, which left a live root with no open record. Move "
                "the keyword off the number — `Fixes the <half> of "
                f"{target_reference}` closes nothing and still reads." + also
            )
            continue

        if not in_scope:
            notes.append(
                f"`{ref.text}` closes in {ref.slug}, not in {repo} — this gate reads only its "
                "configured repositories, so no label was checked for it."
            )
            continue

        if issue is None:
            notes.append(f"`{ref.text}` — {ref.slug}#{ref.number} does not exist; it closes nothing.")
            continue
        if issue["is_pull_request"]:
            notes.append(
                f"`{ref.text}` — {ref.slug}#{ref.number} is a pull request, not an issue; a closing "
                "keyword closes no pull request."
            )
            continue

        blocking = [lbl for lbl in issue["labels"] if lbl in BLOCKING_LABELS]
        if not blocking:
            notes.append(
                f"`{ref.text}` closes {ref.slug}#{ref.number} "
                f"(labels: {', '.join(issue['labels']) or 'none'}) — allowed."
            )
            continue

        label = blocking[0]
        target = (ref.slug.lower(), ref.number)
        if target in released:
            honoured.append(
                f"{ref.slug}#{ref.number} is `{label}` and is closed by this body under a declared "
                f"verification: {released[target]}"
            )
            continue

        errors.append(
            f"RELEASE-BLOCKING ISSUE CLOSED BY A MERGE — `{ref.text}` closes "
            f"{target_reference}, which carries `{label}`. A `sev:B`/`sev:H` issue closes on "
            "POST-ROLL PRODUCTION VERIFICATION, never on a merge: the merge puts the fix on main, "
            "and what the label gates is whether the defect is gone from the running portal "
            "(policy `severity-closes-on-verification`). Closing it here takes it out of the "
            "release readiness count on evidence nobody has. Write `Refs "
            f"{target_reference}` and close it after the roll — or, if the verification has already "
            f"happened, declare it: `Verified-closing: {target_reference} — <what was verified, "
            "and where>`."
        )

    return errors, notes, honoured


def run(
    body: str,
    repo: str,
    resolve,
    checked_repositories: set[str] | None = None,
    only_checked_repositories: bool = False,
) -> int:
    errors, notes, honoured = evaluate(
        body, repo, resolve, checked_repositories, only_checked_repositories
    )

    print(f"Closing-keyword scan of the pull-request body ({len(body.encode('utf-8'))} bytes):")
    for note in notes:
        print(f"  {note}")
    for h in honoured:
        print(f"  ESCAPE HONOURED — {h}")

    if not errors:
        print("\nEvery closing keyword in this body closes something a merge is allowed to close.")
        return 0

    print("")
    for e in errors:
        print(f"::error::{e}")
    print(
        "::error::Rationale and the three measured misfires: "
        "Doc/Architecture/ClosingKeywordsAndIssueState."
    )
    return 1


# ── self-test ───────────────────────────────────────────────────────────────────────────────────
# 🚨 An unproven gate is no gate. This runs FIRST in the job and fails it, so a detector that has
# stopped detecting cannot ride along green. Every arm is asserted in BOTH directions, and the
# NEUTERED-DETECTOR control at the end proves the cases can actually fail.

# The three real bodies, quoted at the length that carries the match. Each is the text as the API
# returned it on the day it merged.
BODY_5201 = (
    "## The release re-cut mints the SAME id its first attempt was minting, and a build with no "
    "release is STAMPED (#5057)\n\n"
    "**Does not close #5057** — `sev:H` closes on post-roll production verification; the "
    "thread carries the exact read.\n\n### Root cause\n\n`NodeTypeBuildState.Bounded`'s "
    "`CreateBound` (10 s) stops this process **waiting**, not the create.\n"
)
BODY_5174 = (
    "Closes #2299's classification half. **The bounce underneath is NOT closed by this** — "
    "see *What this deliberately does not fix*, so the issue stays open for it.\n\n"
    "So on the pod-hub leg the arm could never fire — **947 occurrences on #2299**.\n"
)
BODY_5190 = (
    "Docs only. Conserves the work product of triaging the five newest core bugs.\n\n"
    "Plus the two traps the cluster produced: a closing keyword parses per issue number (PR "
    "#5174's `Closes #2299's classification half` closed #2299 against its own body's statement "
    "that the bounce half stays open — reopened, `sev:H`, now carrying #5177's evidence), and "
    "one episode can hold two fingerprints.\n"
)

# A resolver that answers from a table — no network in the self-test. #2299 and #5057 carry the
# labels they carried on the day; #4000 is an ordinary bug; #7777 is a pull request; #9999 is absent.
_FAKE_ISSUES = {
    5011: {"number": 5011, "is_pull_request": False, "state": "open",
           "labels": ["bug", "sev:H", "area:ci"]},
    2299: {"number": 2299, "is_pull_request": False, "state": "open",
           "labels": ["bug", "sev:H", "area:orleans"]},
    5057: {"number": 5057, "is_pull_request": False, "state": "open",
           "labels": ["bug", "sev:H", "area:mesh"]},
    4000: {"number": 4000, "is_pull_request": False, "state": "open", "labels": ["bug", "sev:M"]},
    4001: {"number": 4001, "is_pull_request": False, "state": "open", "labels": []},
    4002: {"number": 4002, "is_pull_request": False, "state": "open", "labels": ["bug", "sev:B"]},
    7777: {"number": 7777, "is_pull_request": True, "state": "open", "labels": []},
}


def _fake_resolve(repo: str, number: int) -> dict | None:
    return _FAKE_ISSUES.get(number)


REPO = "Systemorph/MeshWeaver"

# (name, body, expected-red)
_SELF_TESTS: list[tuple[str, str, bool]] = [
    # ── the three real misfires ──────────────────────────────────────────────────────────────
    ("#5201 — the negated keyword that closed #5057", BODY_5201, True),
    ("#5174 — the possessive that closed #2299", BODY_5174, True),
    ("#5190 — the docs PR that narrated a close and performed one", BODY_5190, True),

    # ── (a) negation ─────────────────────────────────────────────────────────────────────────
    ("does not close", "This does not close #4000; the other half is open.", True),
    ("doesn't close", "It doesn't close #4000.", True),
    ("will not fix", "This will not fix #4000 — only the symptom.", True),
    ("won't fix", "It won't fix #4000.", True),
    ("cannot resolve", "The change cannot resolve #4000 on its own.", True),
    ("can't resolve", "The change can't resolve #4000.", True),
    ("does not yet close", "This does not yet close #4000.", True),
    ("bold negation", "**Does not close #4000**", True),
    ("heading negation", "## Does not close #4000", True),
    ("negation two sentences back does NOT fire",
     "This is not a revert. Closes #4000.", False),
    ("`not only does this fix` does NOT fire",
     "Not only does this fix #4000, it also removes the watcher.", False),

    # ── (b) severity ─────────────────────────────────────────────────────────────────────────
    ("plain close of a sev:H", "Closes #5057", True),
    ("plain close of a sev:B", "Fixes #4002", True),
    ("plain close of a sev:M passes", "Closes #4000", False),
    ("plain close of an unlabelled issue passes", "Closes #4001", False),
    ("resolves a sev:H", "Resolves #2299 at last.", True),
    ("cross-repo close is not label-checked here",
     "Closes Systemorph/MeshWeaver.Plugins#2262", False),
    ("a pull-request target closes nothing", "Closes #7777", False),
    ("an absent number closes nothing", "Closes #9999", False),

    # ── (c) possessive ───────────────────────────────────────────────────────────────────────
    ("possessive on a sev:M still fires", "Closes #4000's classification half.", True),
    ("the reworded form passes", "Fixes the classification half of #4000.", False),
    ("curly-apostrophe possessive", "Closes #4000’s second half.", True),

    # ── the trap the parser already had ──────────────────────────────────────────────────────
    ("`Closes #A, #B` — only #A binds, and #A is allowed",
     "Closes #4000, #5057", False),
    ("`Closes #A, closes #B` — both bind, and #B is sev:H",
     "Closes #4000, closes #5057", True),

    # ── the other spellings GitHub accepts ───────────────────────────────────────────────────
    ("`GH-N` form", "Closes GH-5057", True),
    ("full issue URL", "Closes https://github.com/Systemorph/MeshWeaver/issues/5057", True),
    ("colon after the keyword", "Closes: #5057", True),
    ("newline between keyword and number", "Fixes\n#5057", True),
    ("a keyword in a heading", "## Closes #5057", True),
    ("a keyword in a table cell", "| Closes #5057 | the fix |", True),
    ("`unfixed #N` is not a keyword", "unfixed #5057 remains", False),
    ("`prefixes #N` is not a keyword", "prefixes #5057 with the namespace", False),

    # ── what the parser does not read ────────────────────────────────────────────────────────
    ("a keyword in a code span closes nothing", "See `Closes #5057` in the other body.", False),
    ("a keyword in a fence closes nothing", "```\nCloses #5057\n```", False),
    ("a keyword in an HTML comment closes nothing", "<!-- Closes #5057 -->", False),
    ("`Refs #N` passes", "Refs #5057 — the verification read is on the thread.", False),
    ("`see #N` passes", "see #5057 for the measurement", False),
    ("a body with no reference at all passes", "Docs only. No issue touched.", False),
    ("an empty body passes", "", False),

    # ── the escape ───────────────────────────────────────────────────────────────────────────
    ("escape releases a sev:H close",
     "Closes #5057\n\nVerified-closing: #5057 — verified on memex-cloud after the roll; the "
     "release node now names the attempted id.",
     False),
    ("escape releases a sev:B close",
     "Fixes #4002\n\nVerified-closing: #4002 — the crash-loop is gone on the rolled image; "
     "ten minutes of logs are clean.",
     False),
    ("bold escape label",
     "Closes #5057\n\n**Verified-closing**: #5057 — verified on the rolled portal, the log "
     "line carries the post-fix wording.",
     False),
    ("list-item escape",
     "Closes #5057\n\n- Verified-closing: #5057 — verified on the rolled portal, the log "
     "line carries the post-fix wording.",
     False),
    ("escape wrapped over two lines",
     "Closes #5057\n\nVerified-closing: #5057 — verified on the rolled portal,\nthe log line "
     "carries the post-fix wording.",
     False),
    ("escape for two issues at once",
     "Closes #5057, closes #4002\n\nVerified-closing: #5057, #4002 — both verified on the "
     "rolled portal; neither fingerprint has recurred.",
     False),
    ("escape does NOT release a negation",
     "Does not close #5057\n\nVerified-closing: #5057 — verified on the rolled portal, the "
     "log line carries the post-fix wording.",
     True),
    ("escape does NOT release a possessive",
     "Closes #5057's classification half\n\nVerified-closing: #5057 — verified on the rolled "
     "portal, the log line carries the post-fix wording.",
     True),
    ("a stale escape is refused",
     "Refs #5057\n\nVerified-closing: #5057 — verified on the rolled portal, the log line "
     "carries the post-fix wording.",
     True),
    ("an escape with no reason is refused", "Closes #5057\n\nVerified-closing: #5057", True),
    ("an empty escape is refused", "Closes #5057\n\nVerified-closing:", True),
    ("a bare affirmation is refused", "Closes #5057\n\nVerified-closing: #5057 — yes", True),
    ("a too-short reason is refused",
     "Closes #5057\n\nVerified-closing: #5057 — it works", True),
    ("an escape naming no issue is refused",
     "Closes #5057\n\nVerified-closing: everything — verified on the rolled portal, the log "
     "line carries the post-fix wording.",
     True),
    ("an escape on an allowed close is INERT, not refused",
     "Closes #4000\n\nVerified-closing: #4000 — verified on the rolled portal anyway; the "
     "label moved while this was open.",
     False),
    ("a quoted escape declares nothing, and the close still reds",
     "Closes #5057\n\n> Verified-closing: #5057 — verified on the rolled portal, the log line "
     "carries the post-fix wording.",
     True),
    ("a fenced escape declares nothing, and the close still reds",
     "Closes #5057\n\n```\nVerified-closing: #5057 — verified on the rolled portal, the log "
     "line carries the post-fix wording.\n```",
     True),
    ("the escape alone closes nothing and is refused as stale",
     "Verified-closing: #5057 — verified on the rolled portal, the log line carries the "
     "post-fix wording.",
     True),
]


def _neutered_variants():
    """Detectors with one arm disabled each, to prove the cases can fail.

    🚨 THE CONTROL. A self-test that passes against a detector which has stopped detecting proves
    nothing, and that is the commonest way a gate rots. Each variant below disables exactly one
    arm; the assertion is that the case list then goes RED. A variant that still passes every case
    means the cases do not cover that arm, and the self-test fails saying so.
    """
    import contextlib

    @contextlib.contextmanager
    def _patched(**attrs):
        module = sys.modules[__name__]
        old = {k: getattr(module, k) for k in attrs}
        for k, v in attrs.items():
            setattr(module, k, v)
        try:
            yield
        finally:
            for k, v in old.items():
                setattr(module, k, v)

    never_matches = re.compile(r"(?!x)x")
    return [
        # (a) blinded: nothing is a negation any more. The whole function is replaced rather than
        # its word list emptied, because the `n't` rule does not live in the list.
        ("negation detector", _patched(negation_before=lambda prose, start: None)),
        # (b) blinded: no label blocks any more.
        ("severity detector", _patched(BLOCKING_LABELS=())),
        # (c) blinded: the possessive group can never match.
        ("possessive detector",
         _patched(CLOSING_RE=re.compile(
             rf"(?<![A-Za-z0-9_])(?P<keyword>{KEYWORDS})\s*:?\s+{REFERENCE}"
             r"(?P<possessive>(?!x)x)?", re.I))),
        # The escape blinded open: every declaration releases everything.
        ("escape refusals", _patched(ESCAPE_LABEL_RE=never_matches)),
    ]


def _run_cases() -> list[str]:
    failures: list[str] = []
    for name, body, expect_red in _SELF_TESTS:
        try:
            errors, _, _ = evaluate(body, REPO, _fake_resolve)
        except Undecidable as exc:  # pragma: no cover - a resolver failure in the self-test
            failures.append(f"{name}: the resolver raised {exc}")
            continue
        got_red = bool(errors)
        if got_red != expect_red:
            failures.append(
                f"{name}: expected {'RED' if expect_red else 'green'}, got "
                f"{'RED' if got_red else 'green'}"
                + (f" — {errors[0][:160]}" if errors else "")
            )
    return failures


def self_test() -> int:
    failures = _run_cases()

    # The three real bodies must fail for the RIGHT reason, not merely fail.
    expectations = [
        (BODY_5201, "NEGATED CLOSING KEYWORD", "#5201"),
        (BODY_5174, "POSSESSIVE CLOSING REFERENCE", "#5174"),
        (BODY_5190, "RELEASE-BLOCKING ISSUE CLOSED BY A MERGE", "#5190"),
    ]
    for body, marker, which in expectations:
        errors, _, _ = evaluate(body, REPO, _fake_resolve)
        if not any(marker in e for e in errors):
            failures.append(
                f"{which}: expected an error naming {marker}, got {errors or 'nothing'}"
            )

    protected = {REPO.lower()}
    satellite_repo = "Systemorph/MeshWeaver.Plugins"
    core_close = "Closes Systemorph/MeshWeaver#5011"
    errors, _, _ = evaluate(
        core_close, satellite_repo, _fake_resolve, protected, only_checked_repositories=True
    )
    if not any("RELEASE-BLOCKING ISSUE CLOSED BY A MERGE" in error for error in errors):
        failures.append(f"satellite explicit core close must see core sev:H labels, got {errors}")
    elif not any("Refs Systemorph/MeshWeaver#5011" in error for error in errors):
        failures.append(f"the satellite remedy must preserve the explicit core reference: {errors}")

    errors, _, _ = evaluate(
        "This does not close Systemorph/MeshWeaver#5011",
        satellite_repo,
        _fake_resolve,
        protected,
        only_checked_repositories=True,
    )
    if not any(
        "Refs Systemorph/MeshWeaver#5011" in error and "NEGATED CLOSING KEYWORD" in error
        for error in errors
    ):
        failures.append(f"a negated satellite reference must retain the explicit core repo in its remedy: {errors}")

    errors, _, _ = evaluate(
        "Closes https://github.com/Systemorph/MeshWeaver/issues/5011",
        satellite_repo,
        _fake_resolve,
        protected,
        only_checked_repositories=True,
    )
    if not any("RELEASE-BLOCKING ISSUE CLOSED BY A MERGE" in error for error in errors):
        failures.append(f"a full core issue URL must be checked in the satellite lane, got {errors}")

    verified_core_close = (
        f"{core_close}\n\nVerified-closing: Systemorph/MeshWeaver#5011 — verified on the "
        "running core portal after its roll; the incident has not recurred in the logs."
    )
    errors, _, honoured = evaluate(
        verified_core_close, satellite_repo, _fake_resolve, protected, only_checked_repositories=True
    )
    if errors or not any("Systemorph/MeshWeaver#5011" in item for item in honoured):
        failures.append(f"an explicit verified core close must release only that core issue: {errors}, {honoured}")

    errors, _, _ = evaluate(
        "Closes #5011\n\nVerified-closing: #5011 — verified against the Systemorph/MeshWeaver core build after its roll.",
        satellite_repo,
        _fake_resolve,
        protected,
        only_checked_repositories=True,
    )
    if errors:
        failures.append(f"cross-only mode must ignore local unqualified references and escapes: {errors}")

    errors, _, _ = evaluate(
        "Closes Systemorph/MeshWeaver.Education#5011",
        satellite_repo,
        _fake_resolve,
        protected,
        only_checked_repositories=True,
    )
    if errors:
        failures.append(f"cross-only mode must not inspect unconfigured repositories: {errors}")

    satellite_resolutions: list[tuple[str, int]] = []

    def record_satellite_resolution(target_repo: str, number: int) -> dict | None:
        satellite_resolutions.append((target_repo, number))
        return _fake_resolve(target_repo, number)

    errors, _, _ = evaluate(
        f"Closes {satellite_repo}#5011",
        satellite_repo,
        record_satellite_resolution,
        protected,
        only_checked_repositories=True,
    )
    if errors or satellite_resolutions:
        failures.append(
            "cross-only mode must ignore an explicit caller-repository close without resolving it: "
            f"{errors}, resolver calls={satellite_resolutions}"
        )

    errors, _, _ = evaluate(
        "Closes Systemorph/MeshWeaver#5011\n\nVerified-closing: Systemorph/MeshWeaver#not-an-issue — verified on the core portal after its roll.",
        satellite_repo,
        _fake_resolve,
        protected,
        only_checked_repositories=True,
    )
    if not any("RELEASE-BLOCKING ISSUE CLOSED BY A MERGE" in error for error in errors):
        failures.append(f"a malformed core escape must not release a real protected close: {errors}")

    at_public_cap = "\n".join(
        f"Closes {REPO}#{number}"
        for number in range(12000, 12000 + MAX_ANONYMOUS_ISSUE_SUBJECTS)
    )
    try:
        errors, _, _ = evaluate(
            at_public_cap, satellite_repo, _fake_resolve, protected, only_checked_repositories=True
        )
        if errors:
            failures.append(f"exactly {MAX_ANONYMOUS_ISSUE_SUBJECTS} public subjects must be evaluated: {errors}")
    except Undecidable as exc:
        failures.append(f"exactly {MAX_ANONYMOUS_ISSUE_SUBJECTS} public subjects must be within the cap: {exc}")

    over_public_cap = f"{at_public_cap}\nCloses {REPO}#{12000 + MAX_ANONYMOUS_ISSUE_SUBJECTS}"
    try:
        evaluate(
            over_public_cap, satellite_repo, _fake_resolve, protected, only_checked_repositories=True
        )
        failures.append(f"more than {MAX_ANONYMOUS_ISSUE_SUBJECTS} public subjects must be refused")
    except Undecidable as exc:
        if f"over the cap of {MAX_ANONYMOUS_ISSUE_SUBJECTS}" not in str(exc):
            failures.append(f"public subject cap refusal must name its bound: {exc}")

    # The public resolver must exercise both controls before returning a verdict, cache the
    # positive read, and fail closed if either control stops proving its promised status.
    from unittest.mock import patch

    public_calls: list[int] = []

    def controlled_public_read(target_repo: str, number: int) -> dict | None:
        public_calls.append(number)
        if number == PUBLIC_ISSUE_READ_PROBES[REPO.lower()]:
            return _FAKE_ISSUES[number]
        if number == PUBLIC_ISSUE_NOT_FOUND_SENTINEL:
            return None
        return _FAKE_ISSUES.get(number)

    with patch(__name__ + "._public_issue_request", side_effect=controlled_public_read):
        resolver = public_issue_resolver()
        answer = resolver(REPO, 5011)
        resolver(REPO, 5011)
    if answer != _FAKE_ISSUES[5011] or public_calls != [5011, PUBLIC_ISSUE_NOT_FOUND_SENTINEL]:
        failures.append(f"public resolver must run positive/negative controls once and cache: {public_calls}")

    with patch(__name__ + "._public_issue_request", return_value=None):
        try:
            public_issue_resolver()(REPO, 5057)
            failures.append("public resolver must fail when its positive control is absent")
        except Undecidable:
            pass

    with patch(
        __name__ + "._public_issue_request",
        side_effect=lambda target_repo, number: (
            _FAKE_ISSUES[5011]
            if number == PUBLIC_ISSUE_NOT_FOUND_SENTINEL
            else _FAKE_ISSUES.get(number)
        ),
    ):
        try:
            public_issue_resolver()(REPO, 5057)
            failures.append("public resolver must fail when its negative control is not absent")
        except Undecidable:
            pass

    from email.message import Message
    from io import BytesIO

    rate_headers = Message()
    rate_headers["X-RateLimit-Remaining"] = "0"
    rate_headers["X-RateLimit-Reset"] = "1893456000"
    rate_error = urllib.error.HTTPError(
        "https://api.github.com/repos/Systemorph/MeshWeaver/issues/5057",
        403,
        "Forbidden",
        rate_headers,
        BytesIO(b'{"message":"API rate limit exceeded"}'),
    )
    with patch(__name__ + ".urllib.request.urlopen", side_effect=rate_error):
        try:
            _public_issue_request(REPO, 5057)
            failures.append("a REST rate-limit response must be Undecidable")
        except Undecidable as exc:
            if "rate-limited" not in str(exc) or "x-ratelimit-reset=1893456000" not in str(exc):
                failures.append(f"a REST rate-limit response must explain its reset, got: {exc}")

    # 🚨 THE WORK IS BOUNDED BY DISTINCT SUBJECTS, not by matches, not by problem-kind. Counted by
    # a resolver that records what it was asked, because "it is memoised" is the kind of claim that
    # stays true in prose long after a refactor has made it false.
    asked: list[int] = []

    def counting_resolve(repo: str, number: int) -> dict | None:
        asked.append(number)
        return _fake_resolve(repo, number)

    repeated = (
        "Closes #5057 and again closes #5057.\n"
        "This does not close #5057 either, and Closes #5057's half too.\n"
    )
    evaluate(repeated, REPO, counting_resolve)
    if asked != [5057]:
        failures.append(
            "one issue named four times under three shapes must be resolved ONCE; the resolver "
            f"was asked for {asked}"
        )

    # …and over the cap the gate refuses UP FRONT rather than being killed at the job's timeout.
    over = " ".join(f"Closes #{n}" for n in range(9000, 9000 + MAX_RESOLVED_SUBJECTS + 1))
    asked.clear()
    try:
        evaluate(over, REPO, counting_resolve)
        failures.append(
            f"{MAX_RESOLVED_SUBJECTS + 1} distinct subjects must be refused up front, not resolved"
        )
    except Undecidable as exc:
        if str(MAX_RESOLVED_SUBJECTS) not in str(exc):
            failures.append(f"the cap refusal must name the cap, got: {exc}")
        if asked:
            failures.append(
                f"the cap must be asserted BEFORE any resolution; {len(asked)} were made anyway"
            )

    # The control on the cap's own direction: exactly at the cap it still runs.
    at_cap = " ".join(f"Closes #{n}" for n in range(9000, 9000 + MAX_RESOLVED_SUBJECTS))
    asked.clear()
    try:
        evaluate(at_cap, REPO, counting_resolve)
    except Undecidable as exc:
        failures.append(f"a body exactly at the cap must still be evaluated, got: {exc}")
    if len(asked) != MAX_RESOLVED_SUBJECTS:
        failures.append(
            f"a body exactly at the cap must resolve {MAX_RESOLVED_SUBJECTS} subjects, "
            f"resolved {len(asked)}"
        )

    # 🚨 One defect, one push. A reference condemned by its WORDING is still resolved, so a
    # possessive (or a negation) aimed at a release-blocking issue says so in the same message —
    # otherwise the author rewords it to a plain close and is refused again on the next run.
    errors, _, _ = evaluate(BODY_5174, REPO, _fake_resolve)
    if not any("carries `sev:H`" in e for e in errors):
        failures.append(
            "#5174: the possessive message must also name the sev:H label, so rewording to a "
            f"plain close is not a second round trip. Got: {errors}"
        )
    errors, _, _ = evaluate("This does not close #4000.", REPO, _fake_resolve)
    if any("carries `sev" in e for e in errors):
        failures.append(
            "a negation aimed at a sev:M issue must NOT claim a release-blocking label: "
            f"{errors}"
        )

    # #5190's code-span occurrence must be invisible while its plain-prose one fires — the one
    # place the two rules meet in a real body.
    prose_5190 = strip_non_prose(BODY_5190)
    if "Closes #2299's classification half" in prose_5190:
        failures.append("#5190: the code-span occurrence survived stripping")
    if "closed #2299" not in prose_5190:
        failures.append("#5190: the plain-prose occurrence was stripped away")

    # 🚨 THE NEUTERED-DETECTOR CONTROL, in both directions: with an arm disabled the case list must
    # go RED, and with every arm live it must be green.
    for arm, patch in _neutered_variants():
        with patch:
            if not _run_cases():
                failures.append(
                    f"NEUTERED CONTROL: disabling the {arm} changed no verdict — the cases do "
                    "not cover it, so a detector that stopped detecting would ride along green."
                )

    if failures:
        print("::error::check-closing-keywords self-test FAILED:")
        for f in failures:
            print(f"::error::  {f}")
        return 1

    print(
        f"✓ check-closing-keywords self-test: {len(_SELF_TESTS)} bodies classified correctly "
        f"({sum(1 for _, _, red in _SELF_TESTS if red)} red, "
        f"{sum(1 for _, _, red in _SELF_TESTS if not red)} green), the three real misfires each "
        f"fail for their own reason, and all {len(_neutered_variants())} neutered-detector controls "
        "turn the case list red."
    )
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--repo", help="owner/repository whose issues this body can close")
    ap.add_argument(
        "--protect-public-repo",
        action="append",
        default=[],
        help="also check an explicitly referenced public repository (repeatable)",
    )
    ap.add_argument(
        "--only-protected-repos",
        action="store_true",
        help="ignore local and unconfigured references; require explicit references to protected repos",
    )
    ap.add_argument("--pr-body-file", help="file holding the pull-request body")
    ap.add_argument("--self-test", action="store_true", help="prove the gate is not vacuous")
    args = ap.parse_args()

    if args.self_test:
        return self_test()

    if not args.repo or not args.pr_body_file:
        ap.error("--repo and --pr-body-file are both required (or use --self-test)")

    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", args.repo):
        print(f"::error::--repo must identify one owner/repository, got {args.repo!r}")
        return 1

    protected: dict[str, str] = {}
    for protected_repo in args.protect_public_repo:
        if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", protected_repo):
            print(f"::error::--protect-public-repo must identify one owner/repository, got {protected_repo!r}")
            return 1
        normalized = protected_repo.lower()
        if normalized not in PUBLIC_ISSUE_READ_PROBES:
            print(f"::error::no positive public-read control is configured for {protected_repo}")
            return 1
        protected[normalized] = protected_repo
    if args.only_protected_repos and not protected:
        print("::error::--only-protected-repos requires at least one --protect-public-repo")
        return 1

    try:
        with open(args.pr_body_file, encoding="utf-8") as fh:
            body = fh.read()
    except OSError as exc:
        print(f"::error::the pull-request body file could not be read: {exc}")
        return 1

    try:
        checked = set(protected) if args.only_protected_repos else set(protected) | {args.repo.lower()}
        public_resolve = public_issue_resolver()

        def resolve(target_repo: str, number: int) -> dict | None:
            if target_repo.lower() in protected:
                return public_resolve(protected[target_repo.lower()], number)
            return resolve_via_gh(target_repo, number)

        return run(body, args.repo, resolve, checked, args.only_protected_repos)
    except Undecidable as exc:
        print(f"::error::the closing-keyword gate could not read its subject: {exc}")
        return 1


if __name__ == "__main__":
    sys.exit(main())
