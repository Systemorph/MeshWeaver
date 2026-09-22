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
  * Cross-repository references (`owner/repo#N`) close in THAT repository. This gate reads issues
    in its own repository only; a cross-repository reference is named in the run and label-checked
    nowhere. That limit is printed on every run rather than being silent.

NOT ESTABLISHED (stated because a gate's blind spots belong with it, not in a commit message):
  * whether GitHub's parser reads a keyword inside a BLOCKQUOTE. This gate scans quoted lines, the
    fail-closed direction: a quoted `Closes #N` reds the gate and is reworded, which is cheap;
    skipping them would let a real close through if the parser does read them.
  * whether a closing keyword can close a PULL REQUEST. This gate resolves every reference and
    reports a pull-request target as closing nothing, without firing.

Usage:
    check-closing-keywords.py --repo <owner/repo> --pr-body-file <file>
    check-closing-keywords.py --self-test
"""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys

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


def escapes(body: str, repo: str) -> tuple[dict[int, str], list[str]]:
    """({issue number: reason}, [declarations REFUSED, with the reason]).

    🚨 A line that starts `Verified-closing:` and does not qualify is a FAILURE, never an ignored
    line. An author who believes they declared something and a gate that believes they did not is
    the disagreement these gates exist to remove, and from the outside it is indistinguishable
    from a skip.
    """
    released: dict[int, str] = {}
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
        numbers = [
            int(r.group("number"))
            for r in ANY_REFERENCE_RE.finditer(split.group("refs"))
            if (r.group("slug") or r.group("urlslug") or repo).lower() == repo.lower()
        ]
        if not numbers:
            refused.append(
                f"`{shown}` — the declaration names no issue in {repo}. The escape is "
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
        for n in numbers:
            released[n] = reason
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


# ── the verdict ─────────────────────────────────────────────────────────────────────────────────


def evaluate(body: str, repo: str, resolve) -> tuple[list[str], list[str], list[str]]:
    """(errors, notes, honoured) — errors is what makes the gate red."""
    errors: list[str] = []
    notes: list[str] = []
    honoured: list[str] = []

    released, refused = escapes(body, repo)
    errors.extend(f"A `Verified-closing:` declaration was refused: {r}" for r in refused)

    refs = closing_references(body, repo)
    if not refs:
        notes.append("No closing keyword binds any issue reference in this body.")
    closed_here = {r.number for r in refs if r.local and not r.negation}

    for n, reason in sorted(released.items()):
        if n not in closed_here:
            errors.append(
                f"`Verified-closing: #{n}` releases nothing: no closing keyword in this body "
                f"binds #{n} (a negated one does not count — delete the negation instead). "
                "The escape permits a close; it does not perform one, so a plain `Closes "
                f"#{n}` still has to be there. Remove the declaration or add the keyword."
            )
        else:
            notes.append(
                f"Escape declared for #{n} — it releases the severity check for that issue and "
                "nothing else."
            )

    # One report per (issue, problem): a body that writes the same closing keyword five times has
    # one defect, not five, while a negation and a possessive aimed at the SAME issue are two
    # different mistakes with two different remedies and are both named.
    seen: set[tuple[str, int, bool, bool]] = set()
    for ref in refs:
        key = (ref.slug.lower(), ref.number, bool(ref.negation), ref.possessive)
        if key in seen:
            continue
        seen.add(key)

        if ref.negation:
            errors.append(
                f"NEGATED CLOSING KEYWORD — `{ref.text}` (negated by “{ref.negation}”). "
                "GitHub matches the keyword immediately before the number and reads NOTHING of the "
                "sentence around it, so the words that make this a disclaimer are the words that "
                f"close #{ref.number} on merge — measured on #5201/#5057, closed two seconds "
                "after the merge. Write `Refs "
                f"#{ref.number}` or `see #{ref.number}` instead. An escape cannot release this: "
                "if you do mean to close it, delete the negation."
            )
            continue

        if ref.possessive:
            errors.append(
                f"POSSESSIVE CLOSING REFERENCE — `{ref.text}` claims to close a PART of "
                f"#{ref.number}; GitHub closes the whole issue. Measured on #5174/#2299: the body "
                "said two sentences later that the issue stays open for the half it did not fix, "
                "and the merge closed it anyway, which left a live root with no open record. Move "
                "the keyword off the number — `Fixes the <half> of "
                f"#{ref.number}` closes nothing and still reads."
            )
            continue

        if not ref.local:
            notes.append(
                f"`{ref.text}` closes in {ref.slug}, not in {repo} — this gate reads "
                f"{repo}'s issues only, so no label was checked for it."
            )
            continue

        issue = resolve(repo, ref.number)
        if issue is None:
            notes.append(f"`{ref.text}` — #{ref.number} does not exist here; it closes nothing.")
            continue
        if issue["is_pull_request"]:
            notes.append(
                f"`{ref.text}` — #{ref.number} is a pull request, not an issue; a closing "
                "keyword closes no pull request."
            )
            continue

        blocking = [lbl for lbl in issue["labels"] if lbl in BLOCKING_LABELS]
        if not blocking:
            notes.append(
                f"`{ref.text}` closes #{ref.number} "
                f"(labels: {', '.join(issue['labels']) or 'none'}) — allowed."
            )
            continue

        label = blocking[0]
        if ref.number in released:
            honoured.append(
                f"#{ref.number} is `{label}` and is closed by this body under a declared "
                f"verification: {released[ref.number]}"
            )
            continue

        errors.append(
            f"RELEASE-BLOCKING ISSUE CLOSED BY A MERGE — `{ref.text}` closes #{ref.number}, "
            f"which carries `{label}`. A `sev:B`/`sev:H` issue closes on POST-ROLL PRODUCTION "
            "VERIFICATION, never on a merge: the merge puts the fix on main, and what the label "
            "gates is whether the defect is gone from the running portal (policy "
            "`severity-closes-on-verification`). Closing it here takes it out of the release "
            "readiness count on evidence nobody has. Write `Refs "
            f"#{ref.number}` and close it after the roll — or, if the verification has already "
            f"happened, declare it: `Verified-closing: #{ref.number} — <what was verified, and "
            "where>`."
        )

    return errors, notes, honoured


def run(body: str, repo: str, resolve) -> int:
    errors, notes, honoured = evaluate(body, repo, resolve)

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

    try:
        with open(args.pr_body_file, encoding="utf-8") as fh:
            body = fh.read()
    except OSError as exc:
        print(f"::error::the pull-request body file could not be read: {exc}")
        return 1

    try:
        return run(body, args.repo, resolve_via_gh)
    except Undecidable as exc:
        print(f"::error::the closing-keyword gate could not read its subject: {exc}")
        return 1


if __name__ == "__main__":
    sys.exit(main())
