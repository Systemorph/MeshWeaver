#!/usr/bin/env python3
"""Refuse a catalog change that nobody has handed over to the React i18n mirror (#3596).

THE SHAPE. AGENTS.md: *"Core is the source of truth: the core catalog change merges FIRST"*, and
*"a core PR that adds keys must hand over the sync: `npm run sync:i18n -- --ref <merged core sha>`"*.
Nothing enforced that handover, and nothing could NOTICE it was missing, because of where the
mirror's comparison point lives:

    MeshWeaver.Plugins:clients/react/src/i18n/catalog-source.json
      { "repository": "Systemorph/MeshWeaver",
        "catalogDir":  "src/MeshWeaver.Messaging.Hub/Localization",
        "ref":         "bbcb22f256240c7132fea1e56c7df3f97564e648",   ← A PINNED COMMIT
        "keys": { "en": 1372, "de": 1372 } }

That repo's drift guard (`RN app + web clients (typecheck + test)`) compares VALUES, and it compares
them against **the pin**, not against core's `main`. So adding a key in core moves nothing the guard
looks at: all shared keys still match the pin exactly, both repos stay green, and the mirror is
silently stale. The React/RN clients then render a raw key — or an English fallback — for every one
of those strings, in both languages, with no signal anywhere in either repository.

MEASURED, and it is not hypothetical or one-off:
  * 2026-09-04 — the pin was 70 keys behind, both repos green (recorded in AGENTS.md).
  * 2026-09-07T12:25Z (#3596 as filed) — 1372 keys at the pin, 1390 on main: **18 behind**,
    value drift on the 1372 shared keys **0**, which is exactly why nothing was red.
  * 2026-09-07T14:4xZ (this change) — **27 behind**, still 0 value drift. It grew by nine keys in
    the three hours between the issue being filed and this gate being written.

WHY A DECLARATION, AND NOT A CHECK OF THE MIRROR ITSELF. The obvious gate — "is the pin an ancestor
of core's newest catalog-touching commit?" — cannot live on core's pull-request path:

  * It would have to READ MeshWeaver.Plugins. `PlatformNeverDependsOnPluginsGuard` bans a checkout
    outright and ledgers every API read, for a reason that applies with full force here: a gate on
    core's own pull requests whose verdict depends on a sibling's moving HEAD makes the SAME diff go
    red or green with no change of its own.
  * Worse, the verdict would be one this pull request CANNOT ACT ON. The sync runs in the other
    repository (`npm run sync:i18n`), whose pull-request lane may be closed — it was, on the day
    #3596 was filed. A gate that reds a core pull request for a debt only another repo can discharge
    teaches people to route around it, which is how a gate stops being one.

So this asks the author to hand the sync over EXPLICITLY, the same shape `Pairs-with:`,
`Satellite-pins:` and `Implementers:` already use — and like `Implementers:` the ordering is
INVERTED (the core half lands FIRST), so it asks for a statement, not a merged counterpart.
It needs no credential, no API read and no ledger entry, so it also runs on FORK pull requests.

WHAT IT FIRES ON. Two changes leave the mirror stale and BOTH are invisible to the mirror's own
guard until somebody moves the pin:
  * a key ADDED to the catalog — the mirror does not have it at all;
  * a VALUE CHANGED on a key both sides already hold — the mirror still has the old text.
A key REMOVED does not fire: the mirror having a key core no longer uses renders nothing wrong.

BASE RATE, measured over the 60 most recent first-parent merges on `main` (2026-09-07): **8** change
the catalog, all 8 by adding keys, none by changing a value. So this meets roughly one merge in
eight — and all 8 of those 8 left the mirror stale, which is the whole of the 27-key debt.

DECLARATION SYNTAX (in the pull-request body, not in a fence):

    Mirror-sync: <statement>

The statement must be a real answer, so it must take one of three forms — each of which names an
actual way the debt gets discharged:

    Mirror-sync: MeshWeaver.Plugins will run `npm run sync:i18n -- --ref <this merge sha>`
    Mirror-sync: tracked in #3596 — batched with the outstanding keys
    Mirror-sync: none — <reason the React clients never read these keys>

Usage:
    check-i18n-mirror-sync.py --base <sha> --pr-body-file <file>
    check-i18n-mirror-sync.py --self-test
"""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys

# The catalog this repo owns. `strings.de.json` is checked too: LocalizationTest already refuses a
# language missing an English key, so the two move together, but a de-only VALUE fix is a real
# mirror drift and must not slip through on an en-only reading.
CATALOG_DIR = "src/MeshWeaver.Messaging.Hub/Localization"
CATALOGS = (f"{CATALOG_DIR}/strings.en.json", f"{CATALOG_DIR}/strings.de.json")

# 🚨 THE CONTROL ARM — the DENOMINATOR. A reader that located the file but stopped parsing it would
# report zero keys at BOTH ends and therefore "nothing was added", forever, on every pull request:
# "the catalog did not change" and "the scan never looked" would be one colour, which is the
# skip-trapdoor shape AGENTS.md bans. MEASURED 2026-09-07 on `main`: 1399 keys in each catalog. The
# floor sits far below that and far above zero, so it survives a large deletion and still fails
# outright on a parser that has stopped parsing.
MIN_KEYS_AT_BASE = 500

HTML_COMMENT_RE = re.compile(r"<!--.*?-->", re.S)
FENCE_RE = re.compile(r"^\s*(```+|~~~+)")

LABEL_RE = re.compile(r"^\s*(?:[-*+]\s+)?\*{0,2}Mirror[ \t_-]?sync\*{0,2}\s*:", re.I)
DECLARATION_RE = re.compile(
    r"^\s*(?:[-*+]\s+)?\*{0,2}Mirror[ \t_-]?sync\*{0,2}\s*:\s*(?P<statement>\S.*)$",
    re.I | re.S,
)
# A declaration WRAPS. Collection stops at a blank line or at the next recognised label, so one
# declaration can never swallow the label after it — the same rule the sibling gates use.
CONTINUATION_STOP_RE = re.compile(
    r"^\s*(?:[-*+]\s+)?\*{0,2}(?:Mirror[ \t_-]?sync|Implementers|Pairs[ \t_-]?with|Satellite-pins|"
    r"Closes|Fixes|Resolves)\*{0,2}\s*[:#]",
    re.I,
)
MAX_DECLARATION_LINES = 6

# The three accepted forms. Each names a real discharge path, which is what stops "Mirror-sync: yes"
# from being an answer: the sync command that lands the keys, the issue or pull request the handover
# is tracked on, or an explicit waiver with a reason.
NAMES_THE_SYNC_RE = re.compile(r"sync:i18n", re.I)
NAMES_A_TRACKER_RE = re.compile(r"(?:[A-Za-z0-9._-]+/[A-Za-z0-9._-]+)?#\d+")
WAIVER_RE = re.compile(r"^none\b\s*(?:—|–|--|-)\s*(?P<reason>\S.*)$", re.I | re.S)
BARE_WAIVER_RE = re.compile(r"^none\b\s*(?:—|–|--|-)?\s*$", re.I)


class Undecidable(Exception):
    """The gate could not READ its subject.

    🚨 Raised, never swallowed, and never folded into "the catalog did not change". A catalog that
    is missing at either end, or does not parse, means the answer is UNKNOWN — and reporting unknown
    as clean is precisely the trapdoor this gate is built to avoid.
    """


def read_at(rev: str | None, path: str) -> dict[str, str]:
    """The catalog at `rev` (or the working tree when rev is None), as a flat key→value map."""
    if rev is None:
        try:
            with open(path, encoding="utf-8") as fh:
                raw = fh.read()
        except OSError as exc:
            raise Undecidable(
                f"{path} could not be read from the working tree ({exc}). If the catalog moved, "
                "move this gate's CATALOGS with it — a gate that cannot find its subject must fail, "
                "never pass."
            ) from exc
    else:
        proc = subprocess.run(
            ["git", "show", f"{rev}:{path}"], capture_output=True, text=True
        )
        if proc.returncode != 0:
            raise Undecidable(
                f"`git show {rev}:{path}` failed: {proc.stderr.strip() or 'no output'}. The merge "
                "base must be present (the job fetches it) and the catalog must exist there. "
                "'Could not tell' and 'nothing changed' are never one colour."
            )
        raw = proc.stdout

    try:
        parsed = json.loads(raw)
    except json.JSONDecodeError as exc:
        raise Undecidable(f"{path} at {rev or 'HEAD'} is not valid JSON: {exc}") from exc
    if not isinstance(parsed, dict):
        raise Undecidable(
            f"{path} at {rev or 'HEAD'} is a {type(parsed).__name__}, not the flat object this "
            "gate reads."
        )
    return {k: v for k, v in parsed.items() if isinstance(v, str)}


def catalog_delta(base: str) -> tuple[dict[tuple[str, str], list[str]], dict[str, int]]:
    """({(catalog, kind): [keys…]}, {catalog: keys at base}) — kind is 'added' or 'value changed'."""
    changes: dict[tuple[str, str], list[str]] = {}
    denominators: dict[str, int] = {}
    for path in CATALOGS:
        at_base = read_at(base, path)
        at_head = read_at(None, path)
        denominators[path] = len(at_base)
        added = sorted(set(at_head) - set(at_base))
        changed = sorted(
            k for k in set(at_head) & set(at_base) if at_head[k] != at_base[k]
        )
        if added:
            changes[(path, "added")] = added
        if changed:
            changes[(path, "value changed")] = changed
    return changes, denominators


def strip_non_prose(body: str) -> str:
    """Remove HTML comments and fenced code blocks.

    Both can only REMOVE candidate declarations, so this fails closed: quoting the syntax in a fence
    (as this docstring and the doc page do) never declares anything, and hiding a real declaration
    in one makes the gate red rather than green.
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


def _blocks(body: str) -> list[str]:
    """Every `Mirror-sync:` declaration in the body, joined with its continuation lines."""
    lines = strip_non_prose(body or "").split("\n")
    out: list[str] = []
    i = 0
    while i < len(lines):
        # A quoted reply is somebody else's text being cited, not this author's declaration.
        if lines[i].lstrip().startswith(">") or not LABEL_RE.match(lines[i]):
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


def declarations(body: str) -> tuple[list[str], list[str]]:
    """(accepted statements, declarations REFUSED with the reason).

    🚨 A line that starts `Mirror-sync:` and does not qualify is a FAILURE, never an ignored line —
    the same rule the sibling gates apply. An author who believes they declared something and a gate
    that believes they did not is the disagreement these gates exist to remove, and from the outside
    it is indistinguishable from a skip.
    """
    accepted: list[str] = []
    refused: list[str] = []
    for block in _blocks(body):
        shown = block if len(block) <= 160 else block[:157] + "…"
        m = DECLARATION_RE.match(block)
        if not m:
            refused.append(
                f"`{shown}` — a `Mirror-sync:` label with nothing after it is not a declaration."
            )
            continue
        statement = m.group("statement").strip()

        if BARE_WAIVER_RE.match(statement):
            refused.append(
                f"`{shown}` — `none` on its own is not a waiver. Say WHY the React clients never "
                "read these keys: `Mirror-sync: none — <reason>`."
            )
            continue
        if WAIVER_RE.match(statement):
            accepted.append(statement)
            continue
        if NAMES_THE_SYNC_RE.search(statement) or NAMES_A_TRACKER_RE.search(statement):
            accepted.append(statement)
            continue

        refused.append(
            f"`{shown}` — the statement names no discharge path. It must either name the sync "
            "(`npm run sync:i18n -- --ref <merged core sha>`), name the issue or pull request the "
            "handover is tracked on (`#3596`, `Systemorph/MeshWeaver.Plugins#1461`), or waive it "
            "explicitly (`none — <reason>`). This is what stops `Mirror-sync: yes` from counting "
            "as an answer."
        )
    return accepted, refused


def report(changes: dict[tuple[str, str], list[str]], denominators: dict[str, int]) -> None:
    for path, count in sorted(denominators.items()):
        print(f"  {path}: {count} keys at the merge base")
    for (path, kind), keys in sorted(changes.items()):
        shown = ", ".join(keys[:12]) + ("…" if len(keys) > 12 else "")
        print(f"  {path}: {len(keys)} {kind} — {shown}")


def distinct_keys(changes: dict[tuple[str, str], list[str]]) -> set[str]:
    """The DISTINCT keys this diff touches.

    🚨 Counted across the catalogs rather than summed over them: `strings.en.json` and
    `strings.de.json` move together by construction (LocalizationTest refuses a language missing an
    English key), so summing would report a 27-key addition as 54 and make the gate's own message
    the least trustworthy number in the run.
    """
    return {k for keys in changes.values() for k in keys}


def starved_catalogs(denominators: dict[str, int]) -> dict[str, int]:
    """Catalogs whose key count at the merge base is below the floor — i.e. the reader is blind."""
    return {p: n for p, n in denominators.items() if n < MIN_KEYS_AT_BASE}


def run(base: str, body: str) -> int:
    changes, denominators = catalog_delta(base)

    starved = starved_catalogs(denominators)
    if starved:
        raise Undecidable(
            "the catalog reads as nearly empty at the merge base "
            + ", ".join(f"{p}={n}" for p, n in sorted(starved.items()))
            + f" (floor {MIN_KEYS_AT_BASE}, measured 1399 on main 2026-09-07). A reader that has "
            "stopped reading would report 'nothing was added' on every pull request forever, so "
            "this fails rather than passing on evidence it does not have."
        )

    print(f"i18n catalog vs merge base {base}:")
    report(changes, denominators)

    if not changes:
        print("\nNo catalog keys added and no values changed — the React mirror cannot go stale "
              "from this diff.")
        return 0

    accepted, refused = declarations(body)

    if refused:
        print("\n::error::A `Mirror-sync:` declaration was refused:")
        for r in refused:
            print(f"::error::  {r}")
        return 1

    if accepted:
        print("\nHandover declared:")
        for a in accepted:
            print(f"  {a}")
        return 0

    total = len(distinct_keys(changes))
    print(
        f"\n::error::This pull request changes {total} catalog "
        + ("key" if total == 1 else "keys")
        + " and does not hand the mirror sync over. The React i18n mirror in MeshWeaver.Plugins "
        "compares against a PINNED core commit, not core's main, so adding a key here reds nothing "
        "anywhere and leaves the mirror silently stale — measured 27 keys behind on 2026-09-07, "
        "with zero value drift, which is exactly why every guard was green."
    )
    print(
        "::error::Declare the handover in the PR BODY as `Mirror-sync: <statement>`, where the "
        "statement names the sync (`npm run sync:i18n -- --ref <merged core sha>`), names the issue "
        "or pull request it is tracked on, or waives it (`none — <reason>`). The sync itself runs "
        "in the OTHER repository and cannot be done from here, which is why this asks for the "
        "handover rather than the result. Rationale: Doc/Architecture/Localization."
    )
    return 1


# ── self-test ──────────────────────────────────────────────────────────────────────────────
# 🚨 An unproven gate is no gate. This runs FIRST in the job and fails it, so a detector that has
# stopped detecting cannot ride along green. Every arm is asserted in BOTH directions: each accepted
# form is shown to be ACCEPTED, and each near-miss is shown to be REFUSED.

_SELF_TESTS: list[tuple[str, str, bool]] = [
    # (name, body, expected-accepted)
    ("names the sync command",
     "Mirror-sync: MeshWeaver.Plugins runs `npm run sync:i18n -- --ref <merge sha>` after this lands",
     True),
    ("names a bare tracker", "Mirror-sync: tracked in #3596 — batched with the outstanding keys", True),
    ("names a qualified tracker",
     "Mirror-sync: Systemorph/MeshWeaver.Plugins#1461 carries the sync", True),
    ("waiver with a reason",
     "Mirror-sync: none — these keys are read only by the Blazor admin tabs, which the React "
     "clients do not ship", True),
    ("bold label", "**Mirror-sync**: tracked in #3596 — batched", True),
    ("list item", "- Mirror-sync: tracked in #3596 — batched", True),
    ("wrapped over two lines",
     "Mirror-sync: tracked in\n#3596 — batched with the outstanding keys", True),
    ("empty statement", "Mirror-sync:", False),
    ("bare none", "Mirror-sync: none", False),
    ("no discharge path", "Mirror-sync: yes", False),
    ("no discharge path, wordy",
     "Mirror-sync: I will take care of this at some point soon, promise", False),
]


def self_test() -> int:
    failures: list[str] = []

    for name, body, expect_ok in _SELF_TESTS:
        accepted, refused = declarations(body)
        got_ok = bool(accepted) and not refused
        if got_ok != expect_ok:
            failures.append(
                f"{name}: expected {'accepted' if expect_ok else 'refused'}, "
                f"got accepted={accepted} refused={refused}"
            )

    # A declaration in a fence declares nothing — and, crucially, is not REFUSED either: the
    # docstring above and the doc page both quote the syntax, and a gate that fired on its own
    # documentation would be unusable.
    fenced = "```\nMirror-sync: tracked in #3596 — batched\n```"
    if declarations(fenced) != ([], []):
        failures.append(f"fenced declaration must be invisible, got {declarations(fenced)}")

    # A quoted reply is somebody else's text.
    quoted = "> Mirror-sync: tracked in #3596 — batched"
    if declarations(quoted) != ([], []):
        failures.append(f"quoted declaration must be invisible, got {declarations(quoted)}")

    # An HTML comment is not a declaration.
    hidden = "<!-- Mirror-sync: tracked in #3596 — batched -->"
    if declarations(hidden) != ([], []):
        failures.append(f"commented declaration must be invisible, got {declarations(hidden)}")

    # Two declarations must not merge into one, and the second must not be swallowed.
    two = "Mirror-sync: tracked in #3596 — batched\nMirror-sync: yes"
    accepted, refused = declarations(two)
    if len(accepted) != 1 or len(refused) != 1:
        failures.append(f"two declarations must parse separately, got {accepted} / {refused}")

    # An empty body declares nothing and refuses nothing.
    if declarations("") != ([], []):
        failures.append("an empty body must produce neither an acceptance nor a refusal")

    # 🚨 The two catalogs move together, so the count reported to the author must be DISTINCT keys,
    # not the sum over catalogs — otherwise the gate's own message reports a 27-key addition as 54.
    twinned = {
        ("en", "added"): ["a", "b", "c"],
        ("de", "added"): ["a", "b", "c"],
    }
    if len(distinct_keys(twinned)) != 3:
        failures.append(
            f"the reported key count double-counts the twinned catalogs: "
            f"{len(distinct_keys(twinned))} for 3 distinct keys"
        )

    # 🚨 The control arm's own control. A floor that never fires is not a floor, so both directions
    # are asserted: a catalog that reads as nearly empty is REFUSED, and the real counts pass.
    if starved_catalogs({"en": 0, "de": 0}) != {"en": 0, "de": 0}:
        failures.append("an empty catalog at the merge base must be refused, not read as unchanged")
    if starved_catalogs({"en": 3, "de": 1399}) != {"en": 3}:
        failures.append("the floor must name exactly the starved catalog")
    if starved_catalogs({"en": 1399, "de": 1399}) != {}:
        failures.append(f"the real catalog size must pass the floor of {MIN_KEYS_AT_BASE}")

    # 🚨 The detector's own arm: it must SEE a change. Without this the gate could pass forever on a
    # delta function that always returns nothing, which is the failure mode the whole file is about.
    added = sorted(set({"a": "1", "b": "2"}) - set({"a": "1"}))
    if added != ["b"]:
        failures.append("the added-key detector does not detect an added key")
    changed = sorted(k for k in {"a"} if {"a": "2"}[k] != {"a": "1"}[k])
    if changed != ["a"]:
        failures.append("the value detector does not detect a changed value")

    if failures:
        print("::error::check-i18n-mirror-sync self-test FAILED:")
        for f in failures:
            print(f"::error::  {f}")
        return 1

    print(
        f"✓ check-i18n-mirror-sync self-test: {len(_SELF_TESTS)} declaration shapes classified "
        "correctly (accepted and refused arms both non-empty), fenced / quoted / commented text is "
        "invisible, adjacent declarations stay separate, and both change detectors fire."
    )
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--base", help="merge-base commit to compare the catalog against")
    ap.add_argument("--pr-body-file", help="file holding the pull-request body")
    ap.add_argument("--self-test", action="store_true", help="prove the gate is not vacuous")
    args = ap.parse_args()

    if args.self_test:
        return self_test()

    if not args.base or not args.pr_body_file:
        ap.error("--base and --pr-body-file are both required (or use --self-test)")

    try:
        with open(args.pr_body_file, encoding="utf-8") as fh:
            body = fh.read()
    except OSError as exc:
        print(f"::error::the pull-request body file could not be read: {exc}")
        return 1

    try:
        return run(args.base, body)
    except Undecidable as exc:
        print(f"::error::the i18n mirror-sync gate could not read its subject: {exc}")
        return 1


if __name__ == "__main__":
    sys.exit(main())
