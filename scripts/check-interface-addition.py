#!/usr/bin/env python3
"""Refuse an ADDED interface member that nobody has checked against the implementers (#3465).

THE SHAPE (CrossRepoPairGate → "A tenth shape", measured on #3446 / MeshWeaver.Plugins#1415).
The `Cross-repo pair (public surface)` gate next door triggers on REMOVAL — a public type leaving
`src/`, a public member leaving a type that stays. This one is the mirror image, and it is the half
that had no cover at all:

    🚨 A forwarder rescues a CALLER. It cannot rescue an IMPLEMENTER.

MeshWeaver#3446 added `GetConnection`, `GetAccessToken` and `ExchangeAndStore` to `IEaGraphAuth`
and kept default-implemented forwarders for the retiring `…Async` surface. Every CALLER therefore
kept compiling, the pair gate was correctly silent (nothing was removed), Copilot approved and core
`main` went green. Then MeshWeaver.Plugins moved its platform pin onto the sealed set:

    src/MeshWeaver.Mail.MicrosoftGraph.Test/ExecutiveAssistantDraftLifecycleTests.cs(438,44):
      error CS0535: 'FakeEaGraphAuth' does not implement interface member
                    'IEaGraphAuth.ExchangeAndStore(string, string, string)'    … and ×2 more

`FakeEaGraphAuth` IMPLEMENTS the interface, so a new member OBLIGES it to supply one, and nothing
on the core side can forward that away. The two halves then DEADLOCKED on the release critical
path: the pin move (Plugins#1415) could not compile without the adaptation, and the adaptation
(Plugins#1416) could not compile without the pin. Plugins `main` was dark for hours and three
sessions were needed to unwind it.

🚨 #3446's forwarder strategy was CORRECT and must not be read as the mistake — it is what kept
every caller working and made the core change safe to land alone. The gap is that callers and
implementers have different compatibility rules, and only the caller half was ever covered.

WHY THE EXISTING COVER CANNOT FIRE.
  * The pair gate triggers on a REMOVAL. An addition is not one, by construction.
  * The other partial cover — a dependent's own CI reacting to core's release event — cannot help
    either, for the reason CrossRepoPairGate already records: for a dependent that PINS core, the
    pin bump IS the integration test. Plugins' `platform-ref` job resolves the same pin on the
    release event as on a pull request, so the event moves what is BAKED, not what `src/` compiles
    against. The break is invisible until somebody moves the pin — which is when it is dearest.

WHY A DECLARATION, AND WHY NOT THE PAIR GATE'S "MERGED COUNTERPART" VERDICT.
The ordering for an addition is INVERTED. For a removal the deleting half lands LAST, so demanding
a MERGED counterpart is exactly right. For an addition the core half lands FIRST — the dependent's
adaptation cannot compile until core's change is pinned, which is precisely why Plugins#1416 was
correctly held as a draft. A gate demanding a merged counterpart here would have DEMANDED the
deadlock it exists to prevent. So this gate asks for a statement, not a precondition: name each
obliging member and say what you found. It is core-only — no checkout, no API read, no credential,
no ledger entry, nothing that can go red on somebody else's HEAD — so it also runs on FORK pull
requests, where the credentialed gates cannot.

🚨 SUPPLYING A DEFAULT IMPLEMENTATION SILENCES IT, AND THAT IS THE POINT. A default interface
member keeps every implementer compiling, so the detector reports nothing for one. The gate must
never tax the fix.

WHAT IT CANNOT DO, said plainly. It does not know WHO implements the interface — core cannot
enumerate a private repository's implementers, and in-mesh C# is invisible to every compiler here.
It makes a human find out, with the grep below, and say what they found. That is weaker than
deriving the answer and is the honest price of staying inside the dependency direction.

🚨 And FOUR ways to oblige an implementer are invisible to the detector. All four were run against
it rather than assumed (the control in the same run reports correctly, so a blank is a real blind
spot); Doc/Architecture/CrossRepoPairGate → "Four ways to oblige an implementer that this gate does
NOT see" carries the table:

  * an interface gains a BASE INTERFACE — the detector diffs member sets, not base lists;
  * an interface gains an OVERLOAD of a name it already declares — member granularity is the NAME;
  * an existing DEFAULT member is made `abstract` — nothing is added or removed (shape 7);
  * a `protected abstract` member is added to a public abstract class — only public members are
    indexed, so it is not an addition at all.

DECLARATION SYNTAX (in the pull-request body, not in a fence):

    Implementers: <Type.Member>[, <Type.Member>…] — <what you checked and what you found>

Every obliging member must be named across the declarations. There is deliberately NO blanket form:
"nothing implements it" is a claim about a repository this one cannot see, so it is made per member
or not at all.

Usage:
    check-interface-addition.py --surface-json <path> --pr-body-file <file>
    check-interface-addition.py --self-test
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

# 🚨 THE CONTROL ARMS — the DENOMINATOR, and there are two of them because `publicTypesAtBase`
# does not constrain this shape at all. A parser that located every public type but stopped
# recognising the word `interface` would publish a healthy 1900 types and ZERO obligations, forever,
# and "no obliging additions" would be indistinguishable from "the scan never looked". MEASURED
# 2026-09-06 on `main`: 130 public interfaces and 449 implementer obligations under `src/`. Both
# floors sit far below that and far above zero, so they survive a carve-out wave and still fail
# outright on a matcher that has stopped matching.
MIN_PUBLIC_INTERFACES_AT_BASE = 50
MIN_OBLIGATIONS_AT_BASE = 150

# 🚨 #3489 added two more ways to oblige an implementer, and each needs its OWN arm for the same
# reason: `implementerObligationsAtBase` does not constrain either. A parser that found every
# interface and every abstract member but stopped reading BASE LISTS would report healthy 132/452
# and zero base edges forever. MEASURED 2026-09-07 on `main`: 26 interface base edges and 5
# protected-abstract obligations under `src/`.
#
# The protected floor is 1, not "far below 5", and that is deliberate rather than lazy: with a true
# value this small there is no room for a floor that is both meaningful and survivable, so it is
# set to catch exactly the failure a floor CAN catch here — the scan returning nothing at all. The
# differential control in `check-parser-delta.py` (#3492) is what catches a partial regression, and
# it is the right instrument for it because it compares two parsers over one tree rather than one
# parser against a guess about the codebase.
MIN_INTERFACE_BASE_EDGES_AT_BASE = 5
MIN_PROTECTED_OBLIGATIONS_AT_BASE = 1

# 🚨 All three shapes are the SAME verdict — an outside implementer must now write code it did not
# have to write — so they share one declaration mechanism and one gate. Splitting them would ask an
# author to learn three spellings of one obligation.
TRIGGER_CATEGORIES = frozenset({
    "implementer-obliging-added",             # #3465 — a member on an interface / abstract class
    "implementer-obliging-base-added",        # #3489a — an interface gains a BASE interface
    "implementer-obliging-protected-added",   # #3489d — a `protected abstract` on a public abstract class
})
TRIGGER_CATEGORY = "implementer-obliging-added"  # kept: the self-test fixtures name it directly

HTML_COMMENT_RE = re.compile(r"<!--.*?-->", re.S)
FENCE_RE = re.compile(r"^\s*(```+|~~~+)")

# The em dash is what the sibling gates use; the ASCII forms are accepted too so a declaration is
# never rejected over typography. The reason after it must be non-empty.
LABEL_RE = re.compile(r"^\s*(?:[-*+]\s+)?\*{0,2}Implementers\*{0,2}\s*:", re.I)
DECLARATION_RE = re.compile(
    r"^\s*(?:[-*+]\s+)?\*{0,2}Implementers\*{0,2}\s*:\s*(?P<names>.*?)\s*(?:—|–|--|-)\s*"
    r"(?P<reason>\S.*)$",
    re.I | re.S,
)
# 🚨 A declaration WRAPS, and it must still be one. Three qualified member names plus a sentence
# saying what was checked does not fit on one line, so the natural way to write this is names on the
# first line and `— reason` on the next. A single-line matcher would read that as no declaration at
# all — the gate would fire on correct work, and a gate that rejects correct work is how people
# learn to route around it. Collection stops at a blank line or at the next recognised label, so a
# declaration can never swallow the one after it.
CONTINUATION_STOP_RE = re.compile(
    r"^\s*(?:[-*+]\s+)?\*{0,2}(?:Implementers|Pairs[ \t_-]?with|Satellite-pins|Closes|Fixes|"
    r"Resolves)\*{0,2}\s*[:#]",
    re.I,
)
MAX_DECLARATION_LINES = 6

# A waiver that rests on a live-mesh sweep must rest on a sweep that RAN — the same rule
# check-cross-repo-pair.py applies to `Pairs-with: none`, and for the same reason: in-mesh C# can
# implement a core interface and no compiler here can see it, so the sweep is the only instrument.
# `searched: false` is the #2741 no-embedding-provider shape, which #3137 read as "no callers".
SWEEP_DID_NOT_RUN_RE = re.compile(r"searched\W{0,3}false", re.I)
SWEEP_RAN_RE = re.compile(r"searched\W{0,3}true", re.I)
SWEEP_CLAIM_RE = re.compile(r"\b(?:sweep|swept|search_chunks)\b", re.I)


class Undecidable(Exception):
    """The gate could not READ its subject.

    🚨 Raised, never swallowed, and never folded into "nothing was added". A surface report that is
    missing, truncated, produced by a detector predating this shape, or starved of interfaces means
    the answer is UNKNOWN — and reporting unknown as clean is the skip-trapdoor shape AGENTS.md
    bans: it makes "the gate never ran" and "the gate passed" the same colour.
    """


def strip_non_prose(body: str) -> str:
    """Remove HTML comments and fenced code blocks.

    Both can only REMOVE candidate declarations, so this fails closed: quoting the syntax in a fence
    (as this script's own docstring and the doc page do) never declares anything, and hiding a real
    declaration in one makes the gate red rather than green.
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
    """Every `Implementers:` declaration in the body, each joined with its continuation lines."""
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
    """(member tokens declared, declarations REFUSED with the reason they were refused).

    🚨 A line that starts `Implementers:` and does not parse is a FAILURE, never an ignored line —
    the same rule check-cross-repo-pair.py applies to `Pairs-with:`. An author who believes they
    declared something and a gate that believes they did not is the disagreement these gates exist
    to remove, and it is indistinguishable from a skip.
    """
    names: list[str] = []
    refused: list[str] = []
    for block in _blocks(body):
        shown = block if len(block) <= 160 else block[:157] + "…"
        m = DECLARATION_RE.match(block)
        if not m:
            refused.append(
                f"`{shown}` — expected `Implementers: <Type.Member>[, …] — <what you checked and "
                "what you found>`. A declaration with no reason after the dash is not one, and a "
                "line that starts `Implementers:` and does not parse is a failure rather than an "
                "ignored line"
            )
            continue
        reason = m.group("reason")
        if SWEEP_DID_NOT_RUN_RE.search(reason):
            refused.append(
                f"`{shown}` — the cited sweep did not run: `searched: false` means the deployment "
                "has NO embedding provider and NOTHING was searched (#2741). Sweep on a deployment "
                "whose index is live and quote its `searched: true`, or give a reason that does not "
                "rest on the sweep"
            )
            continue
        if SWEEP_CLAIM_RE.search(reason) and not SWEEP_RAN_RE.search(reason):
            refused.append(
                f"`{shown}` — a reason that cites a live-mesh sweep must quote the envelope's "
                "`searched: true`; a sweep answer without it is the #2741 shape read as clean"
            )
            continue
        tokens = [t for t in (r.strip().strip("`").strip() for r in m.group("names").split(",")) if t]
        if not tokens:
            # `Implementers: — reason` parses but declares NOTHING. The gate would still fire (the
            # members stay undeclared), but the message would say "undeclared" to an author who
            # believes they declared — the same author-versus-gate disagreement an unparseable line
            # produces, so it is named the same way rather than left to be inferred.
            refused.append(
                f"`{shown}` — the declaration names no member. It must list each obliging member "
                "before the dash: `Implementers: IFoo.Bar — <what you checked>`"
            )
            continue
        names.extend(tokens)
    return names, refused


def covers(token: str, full_name: str) -> bool:
    """Does `token` name `full_name`?

    A dotted SUFFIX match, case-insensitively: `ExchangeAndStore`, `IEaGraphAuth.ExchangeAndStore`
    and the fully-qualified name all name the same member, and an author writes whichever is
    shortest. A different member's name is not a suffix of this one, so the match cannot drift.
    """
    want = [s for s in full_name.casefold().split(".") if s]
    got = [s for s in token.casefold().split(".") if s]
    return bool(got) and len(got) <= len(want) and want[len(want) - len(got):] == got


def read_report(path: Path) -> tuple[list[dict], int, int]:
    """The obliging additions in a surface report, plus both denominators.

    🚨 Every unreadable shape RAISES. A report missing this shape's fields was produced by a
    detector that predates it, and "the field is absent" must never be read as "the count is zero".
    """
    try:
        payload = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError) as e:
        raise Undecidable(f"cannot read the surface report {path}: {e}") from e
    if not isinstance(payload, dict):
        raise Undecidable(f"{path} is not a surface report object")

    added = payload.get("added")
    if not isinstance(added, list):
        raise Undecidable(
            f"{path} has no `added` list — it was not produced by "
            "check-type-forwards.py --surface-json, or it was truncated"
        )
    interfaces = payload.get("publicInterfacesAtBase")
    obligations = payload.get("implementerObligationsAtBase")
    base_edges = payload.get("interfaceBaseEdgesAtBase")
    protected = payload.get("protectedObligationsAtBase")
    for field, value in (("publicInterfacesAtBase", interfaces),
                         ("implementerObligationsAtBase", obligations),
                         ("interfaceBaseEdgesAtBase", base_edges),
                         ("protectedObligationsAtBase", protected)):
        if not isinstance(value, int):
            raise Undecidable(
                f"{path} carries no integer `{field}`. That field IS this gate's denominator, so a "
                "report without it cannot say whether the scan looked — most likely the report came "
                "from a check-type-forwards.py that predates the implementer shape (#3465)"
            )
    if interfaces < MIN_PUBLIC_INTERFACES_AT_BASE:
        raise Undecidable(
            f"the base tree declares {interfaces} public interface(s), below the floor of "
            f"{MIN_PUBLIC_INTERFACES_AT_BASE}. The detector examined nothing, or examined the wrong "
            "tree — so 'no obliging additions' would be a green on zero evidence. Check that the "
            "merge base was fetched and that src/ was scanned"
        )
    if obligations < MIN_OBLIGATIONS_AT_BASE:
        raise Undecidable(
            f"the base tree declares {obligations} implementer obligation(s), below the floor of "
            f"{MIN_OBLIGATIONS_AT_BASE}. The member classifier has stopped recognising an abstract "
            "interface member — the interfaces were found and their obligations were not, which is "
            "this gate reporting a clean tree while blind"
        )
    if base_edges < MIN_INTERFACE_BASE_EDGES_AT_BASE:
        raise Undecidable(
            f"the base tree declares {base_edges} interface base edge(s), below the floor of "
            f"{MIN_INTERFACE_BASE_EDGES_AT_BASE}. The base-list reader has stopped reading base "
            "lists — the interfaces were found and their inheritance was not, so an interface "
            "gaining a base would pass unchallenged (#3489a)"
        )
    if protected < MIN_PROTECTED_OBLIGATIONS_AT_BASE:
        raise Undecidable(
            f"the base tree declares {protected} protected-abstract obligation(s), below the floor "
            f"of {MIN_PROTECTED_OBLIGATIONS_AT_BASE}. That scan has returned nothing at all, which "
            "is how it read before #3489d: `protected` is not in MEMBER_MODIFIERS, so a helper that "
            "reads it will answer `set()` and the shape becomes invisible again"
        )
    return [e for e in added if isinstance(e, dict) and e.get("category") in TRIGGER_CATEGORIES], \
        interfaces, obligations


def evaluate(obliging: list[dict], body: str) -> tuple[int, list[str]]:
    """0 and an empty report when the diff is acceptable; 1 and the reasons when it is not."""
    if not obliging:
        return 0, []

    tokens, refused = declarations(body)
    undeclared = [
        e for e in obliging
        if not any(covers(t, str(e.get("fullName", ""))) for t in tokens)
    ]
    if not undeclared and not refused:
        return 0, []

    lines = [
        "This pull request ADDS member(s) to a public interface (or an abstract member to a public",
        "abstract class) with no default implementation. Every implementer OUTSIDE this repository",
        "then stops compiling with CS0535/CS0534, and no forwarder on this side can help: a",
        "forwarder rescues a CALLER, never an IMPLEMENTER. Nothing else here can see it — the pair",
        "gate triggers on removals, and a dependent that PINS the platform does not rebuild until",
        "somebody moves the pin. See Doc/Architecture/CrossRepoPairGate → \"A tenth shape\" (#3465;",
        "#3446 deadlocked the release wave and left a satellite's main dark for hours).",
        "",
    ]
    if refused:
        lines.append("Refused declaration(s) — these do NOT count as declared:")
        lines += [f"  • {r}" for r in refused]
        lines.append("")
    if undeclared:
        lines.append("Undeclared obliging addition(s):")
        lines += [f"  • {e.get('assembly')} :: {e.get('fullName')}" for e in undeclared]
        lines += [
            "",
            "Either give the member a DEFAULT IMPLEMENTATION — which keeps every implementer",
            "compiling and silences this gate, because it is the actual fix — or check who",
            "implements the interface and declare what you found:",
            "",
            "    grep -rn ': *ITheInterface\\b\\|, *ITheInterface\\b' \\",
            "        ../MeshWeaver.Plugins/src ../MeshWeaver.Plugins/test ../MeshWeaver.Education",
            "",
            "…and sweep the live mesh too (in-mesh C# compiles at RUNTIME and no build here sees",
            "it); a reason citing that sweep must quote the envelope's `searched: true`.",
            "",
            "Then add one line per member to the PULL REQUEST BODY (outside any code fence):",
            "",
            "    Implementers: IFoo.Bar — what you checked and what you found",
            "",
            "🚨 ORDERING IS INVERTED FROM `Pairs-with:`. The core half lands FIRST here: the",
            "dependent's adaptation cannot compile until this is pinned, which is why #3446's",
            "counterpart was correctly a draft. So this gate wants the coupling PREDICTED, not a",
            "merged counterpart — demanding one would recreate the deadlock it exists to prevent.",
            "There is deliberately no blanket form: \"nothing implements it\" is a claim about a",
            "repository this one cannot see, so it is made per member or not at all.",
        ]
    return 1, lines


# ── self-test ────────────────────────────────────────────────────────────────────────────────────
# 🚨 An unproven gate is no gate: every case must FIRE on its defect and stay SILENT on its fix, and
# the PASSING rows carry as much weight as the failing ones — without them a gate that always failed
# would score identically. #3446's own three members are the fixture.

_INCIDENT = [
    {"assembly": "MeshWeaver.Mesh.Contract",
     "fullName": "MeshWeaver.Mesh.IEaGraphAuth.ExchangeAndStore",
     "category": TRIGGER_CATEGORY},
    {"assembly": "MeshWeaver.Mesh.Contract",
     "fullName": "MeshWeaver.Mesh.IEaGraphAuth.GetAccessToken",
     "category": TRIGGER_CATEGORY},
    {"assembly": "MeshWeaver.Mesh.Contract",
     "fullName": "MeshWeaver.Mesh.IEaGraphAuth.GetConnection",
     "category": TRIGGER_CATEGORY},
]

_HEALTHY = {"publicInterfacesAtBase": 130, "implementerObligationsAtBase": 449,
            "interfaceBaseEdgesAtBase": 26, "protectedObligationsAtBase": 5,
            "added": [], "removed": []}


def _report(**over) -> dict:
    out = dict(_HEALTHY)
    out.update(over)
    return out


def self_test() -> int:
    import tempfile

    failures: list[str] = []

    def check(name: str, got, want) -> None:
        if got != want:
            failures.append(f"  {name}: got {got!r}, want {want!r}")

    def report_of(payload: dict):
        with tempfile.TemporaryDirectory() as tmp:
            p = Path(tmp) / "surface.json"
            p.write_text(json.dumps(payload), encoding="utf-8")
            return read_report(p)

    def raises(payload: dict) -> bool:
        try:
            report_of(payload)
        except Undecidable:
            return True
        return False

    # ── the trigger ──────────────────────────────────────────────────────────────────────────
    rc, out = evaluate(_INCIDENT, "an ordinary body with no declaration")
    check("#3446 undeclared FIRES", rc, 1)
    check("names each member", sum("IEaGraphAuth." in l for l in out), 3)
    check("says CS0535", any("CS0535" in l for l in out), True)

    # ── and it is silent on everything that is not the shape ─────────────────────────────────
    check("no obliging addition is silent", evaluate([], "")[0], 0)
    check(
        "an ordinary member addition is not this shape",
        evaluate([], "")[0],
        0,
    )

    # ── the declaration, in the forms an author actually writes ──────────────────────────────
    full = ("Implementers: IEaGraphAuth.ExchangeAndStore, IEaGraphAuth.GetAccessToken, "
            "IEaGraphAuth.GetConnection — FakeEaGraphAuth in the Plugins mail tests implements "
            "this; Plugins#1416 carries the adaptation and lands with the pin bump")
    check("a complete declaration PASSES", evaluate(_INCIDENT, full)[0], 0)
    check(
        "a bare member name names the member",
        evaluate(_INCIDENT, "Implementers: ExchangeAndStore, GetAccessToken, GetConnection — "
                            "checked both satellites, only FakeEaGraphAuth implements it")[0],
        0,
    )
    check(
        "the fully-qualified name names it too",
        evaluate(_INCIDENT[:1], "Implementers: MeshWeaver.Mesh.IEaGraphAuth.ExchangeAndStore — "
                                "only the test fake implements this")[0],
        0,
    )
    check(
        "a bulleted, bolded declaration is still one",
        evaluate(_INCIDENT[:1], "- **Implementers**: IEaGraphAuth.ExchangeAndStore — checked the "
                                "fleet, one implementer")[0],
        0,
    )

    # 🚨 A WRAPPED declaration is still one. Three qualified names plus a sentence does not fit on
    # one line, so this is how it actually gets written — and a single-line matcher read it as NO
    # declaration, firing on correct work. Found by running this gate's own falsification arm 2
    # against #3446's real report; the green arm was red for this reason and nothing else.
    wrapped = (
        "Implementers: IEaGraphAuth.GetConnection, IEaGraphAuth.GetAccessToken, "
        "IEaGraphAuth.ExchangeAndStore\n"
        "  — `FakeEaGraphAuth` in the Plugins mail tests implements this seam and needs all three;\n"
        "  the adaptation lands in the SAME pull request as the pin bump, because it cannot compile\n"
        "  against the old pin.\n"
        "\n"
        "Closes #3433\n"
    )
    check("a WRAPPED declaration is one", evaluate(_INCIDENT, wrapped)[0], 0)
    check(
        "…and it stops at the blank line rather than swallowing what follows",
        declarations(wrapped)[0],
        ["IEaGraphAuth.GetConnection", "IEaGraphAuth.GetAccessToken",
         "IEaGraphAuth.ExchangeAndStore"],
    )
    check(
        "one declaration never swallows the next label",
        len(_blocks("Implementers: IFoo.A — first\nPairs-with: none — second\n")),
        1,
    )

    # 🚨 An `Implementers:` line that does not PARSE is a failure, never an ignored line.
    rc, out = evaluate(_INCIDENT[:1],
                       "Implementers: IEaGraphAuth.ExchangeAndStore\n\nsome other paragraph")
    check("an unparseable declaration FIRES", rc, 1)
    check("…and says it was refused, not missing",
          any("Refused declaration" in l for l in out), True)

    # …and one that parses but names NOTHING is refused by name, not silently left "undeclared".
    rc, out = evaluate(_INCIDENT[:1], "Implementers: — I checked and it is fine")
    check("a declaration naming no member FIRES", rc, 1)
    check("…and says the declaration named no member",
          any("names no member" in l for l in out), True)

    # ── a PARTIAL declaration is not a pass, and only the undeclared one is named ─────────────
    partial = "Implementers: IEaGraphAuth.ExchangeAndStore — checked, one test fake implements it"
    rc, out = evaluate(_INCIDENT, partial)
    check("partial declaration FIRES", rc, 1)
    check("names the two undeclared", sum("IEaGraphAuth." in l for l in out), 2)
    check("does not name the declared", all("ExchangeAndStore" not in l for l in out), True)

    # ── the shapes that must NOT count as a declaration ───────────────────────────────────────
    one = _INCIDENT[:1]
    check("fenced is not a declaration",
          evaluate(one, "```\nImplementers: IEaGraphAuth.ExchangeAndStore — quoted\n```")[0], 1)
    check("commented is not a declaration",
          evaluate(one, "<!-- Implementers: IEaGraphAuth.ExchangeAndStore — no -->")[0], 1)
    check("a quoted reply is not a declaration",
          evaluate(one, "> Implementers: IEaGraphAuth.ExchangeAndStore — somebody else")[0], 1)
    check("a reasonless declaration is not one",
          evaluate(one, "Implementers: IEaGraphAuth.ExchangeAndStore")[0], 1)
    check("an empty reason is not one",
          evaluate(one, "Implementers: IEaGraphAuth.ExchangeAndStore — ")[0], 1)
    check("a DIFFERENT member does not cover this one",
          evaluate(one, "Implementers: IEaGraphAuth.GetConnection — checked that one")[0], 1)
    check("a longer name is not a suffix of a shorter one",
          covers("IOther.IEaGraphAuth.ExchangeAndStore",
                 "MeshWeaver.Mesh.IEaGraphAuth.ExchangeAndStore"), False)
    check("a partial segment does not match",
          covers("Store", "MeshWeaver.Mesh.IEaGraphAuth.ExchangeAndStore"), False)

    # ── a waiver must rest on a sweep that RAN (#2741, the #3137 shape) ───────────────────────
    rc, out = evaluate(one, "Implementers: IEaGraphAuth.ExchangeAndStore — swept the live mesh, "
                            "\"searched\": false, no implementers")
    check("`searched: false` is REFUSED", rc, 1)
    check("says why the sweep did not run", any("#2741" in l for l in out), True)
    check("a sweep claim without the field is refused",
          evaluate(one, "Implementers: IEaGraphAuth.ExchangeAndStore — swept the mesh, nothing")[0], 1)
    check("a sweep quoting `searched: true` passes",
          evaluate(one, "Implementers: IEaGraphAuth.ExchangeAndStore — search_chunks over the live "
                        "mesh answered \"searched\": true with no implementer")[0], 0)
    check("a reason that cites no sweep is judged on its own",
          evaluate(one, "Implementers: IEaGraphAuth.ExchangeAndStore — only the test fake in the "
                        "mail module implements this seam")[0], 0)

    # ── the DENOMINATOR. Every starved or unshaped report RAISES rather than reading clean ────
    check("a healthy report reads", report_of(_report(added=list(_INCIDENT)))[0], _INCIDENT)
    check("its denominators are returned", report_of(_report())[1:], (130, 449))
    check("a starved interface count raises", raises(_report(publicInterfacesAtBase=3)), True)
    check("a starved obligation count raises", raises(_report(implementerObligationsAtBase=9)), True)

    # ── #3489's two shapes: same verdict, same declaration, their own arms ────────────────────
    _BASE_ADDED = [{"key": "A:N.IStore:>IDisposable", "assembly": "A",
                    "fullName": "N.IStore : IDisposable",
                    "category": "implementer-obliging-base-added"}]
    _PROT_ADDED = [{"key": "A:N.Handler::Slot", "assembly": "A", "fullName": "N.Handler.Slot",
                    "category": "implementer-obliging-protected-added"}]
    check("🚨 #3489a: a gained BASE INTERFACE is a trigger",
          report_of(_report(added=list(_BASE_ADDED)))[0], _BASE_ADDED)
    check("🚨 #3489d: a gained PROTECTED ABSTRACT member is a trigger",
          report_of(_report(added=list(_PROT_ADDED)))[0], _PROT_ADDED)
    check("#3489a: undeclared, it fails", evaluate(_BASE_ADDED, "no declaration here")[0], 1)
    check("#3489d: undeclared, it fails", evaluate(_PROT_ADDED, "no declaration here")[0], 1)
    check("#3489: one declaration mechanism covers all three shapes",
          evaluate(_BASE_ADDED, "Implementers: N.IStore : IDisposable — nothing outside "
                                "implements this seam, checked the four satellites")[0], 0)
    # 🚨 An ORDINARY addition must still not trigger — otherwise these arms would score green on a
    # classifier that had started calling everything obliging.
    check("#3489: an ordinary member-added is still not a trigger",
          report_of(_report(added=[{"key": "A:N.X::Y", "assembly": "A", "fullName": "N.X.Y",
                                    "category": "member-added"}]))[0], [])
    check("a starved base-edge count raises", raises(_report(interfaceBaseEdgesAtBase=1)), True)
    check("a ZERO protected-obligation count raises — the pre-#3489 reading",
          raises(_report(protectedObligationsAtBase=0)), True)
    check("a report predating this shape raises",
          raises({"added": [], "removed": [], "publicTypesAtBase": 1900}), True)
    check("a report with no `added` list raises",
          raises(_report(added="not a list")), True)
    check("a non-integer denominator raises",
          raises(_report(publicInterfacesAtBase="lots")), True)
    with tempfile.TemporaryDirectory() as tmp:
        missing = Path(tmp) / "nope.json"
        raised = False
        try:
            read_report(missing)
        except Undecidable:
            raised = True
        check("a missing report raises", raised, True)

    # ── the real detector, on the real tree: the incident FIRES and today's main does not ─────
    root = Path(__file__).resolve().parent.parent
    fixture = root / "scripts" / "check-type-forwards.py"
    if (root / ".git").exists() and fixture.is_file():
        import subprocess

        with tempfile.TemporaryDirectory() as tmp:
            out_json = Path(tmp) / "surface.json"
            probe = subprocess.run(
                [sys.executable, str(fixture), "--base", "HEAD", "--surface-json", str(out_json)],
                cwd=root, capture_output=True, text=True)
            if probe.returncode == 0 and out_json.is_file():
                obliging, interfaces, obligations = read_report(out_json)
                check("HEAD vs the working tree obliges nobody", obliging, [])
                check("the real tree declares interfaces, not zero",
                      interfaces >= MIN_PUBLIC_INTERFACES_AT_BASE, True)
                check("the real tree declares obligations, not zero",
                      obligations >= MIN_OBLIGATIONS_AT_BASE, True)

    if failures:
        print("self-test FAILED:", file=sys.stderr)
        print("\n".join(failures), file=sys.stderr)
        return 1
    print("✓ check-interface-addition self-test: fires on #3446's three obliging members, stays "
          "silent on a diff that adds none and on a declared one, refuses a fenced, commented, "
          "quoted, reasonless, mis-named or unswept declaration, and RAISES on a starved or "
          "shape-less surface report rather than reading it as clean.")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(
        description="Refuse an undeclared implementer-obliging interface addition (#3465).")
    ap.add_argument("--surface-json", help="the report check-type-forwards.py --surface-json wrote")
    ap.add_argument("--pr-body-file", help="file holding the pull-request body")
    ap.add_argument("--self-test", action="store_true", help="prove the gate is not vacuous")
    args = ap.parse_args()

    if args.self_test:
        return self_test()
    if not args.surface_json or not args.pr_body_file:
        ap.error("--surface-json and --pr-body-file are required unless --self-test is given")

    try:
        obliging, interfaces, obligations = read_report(Path(args.surface_json))
    except Undecidable as e:
        # 🚨 UNKNOWN IS RED. Never "nothing was added" — see Undecidable's remarks.
        print(
            f"::error::This gate could not read its subject, so it does not know whether an "
            f"interface member was added: {e}. That is UNDECIDED, not clean — and a gate that "
            "spells the two the same way is the failure this repo's CI rules exist to prevent.",
            file=sys.stderr,
        )
        return 1

    print(
        f"The base tree declares {interfaces} public interface(s) carrying {obligations} "
        f"implementer obligation(s); this diff adds {len(obliging)} obliging member(s)."
    )
    if not obliging:
        print("No member was added that an outside implementer would have to write — nothing to "
              "declare.")
        return 0
    for entry in obliging:
        print(f"  [{entry.get('category')}] {entry.get('assembly')} :: {entry.get('fullName')}")

    body = Path(args.pr_body_file).read_text(encoding="utf-8")
    rc, report = evaluate(obliging, body)
    if rc == 0:
        print("Every obliging addition is declared.")
        return 0

    print("::error::" + report[0])
    for line in report[1:]:
        print(line)
    return 1


if __name__ == "__main__":
    sys.exit(main())
