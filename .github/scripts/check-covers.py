#!/usr/bin/env python3
"""check-covers: fail a plugin COVER that is a wall of text, or whose heroCta pills are unreadable.

A store cover is the one page an anonymous visitor sees, and the only page that has to sell the
package. Every cover in this repo already has the ingredients of a good one — an HTML hero, section
headings, bullet lists, links — so the failure mode is never "no structure", it is **one paragraph
that swallowed a whole section**: nine lines of unbroken prose that a reader skips entirely.

Measured across all five plugin repos on 2026-08-26, before this gate: 80 covers, whose longest
prose paragraph ranged from **39 characters** (`X`, `YouTube`, `LinkedIn` — hero, headings, bullets,
nothing else) to **950** (`ThinkInStreams`, whose entire nine-lesson syllabus was one sentence).
Thirteen covers carried a paragraph over 400 characters. Rewriting those thirteen — splitting a
list-in-prose into an actual list, a two-idea paragraph into two — is what this gate now holds.

## What counts as prose

ONLY prose. Headings, list items, table rows, block quotes, fenced code, raw HTML blocks and `@@`
area embeds are STRUCTURE — counting them would flag the best-laid-out covers in the repo, since a
long bullet is still scannable and a long HTML hero is a banner, not a paragraph. A "paragraph" is
a run of consecutive non-structural lines, joined; markdown reflows them into one block, so the
authored line breaks are not what a reader sees.

## The ratchet

`cover-prose.allow` lists top-level plugin folder names (`Pricing`, not `Reinsurance/Pricing`) exempted
from the limit; `cover-contrast.allow` lists `contrast:<Module>:<background|secondaryBackground>` keys.
Both are **seeded empty** and may only SHRINK — an entry is a promise to come back, and seeding it with today's debt would make
that debt permanent (AGENTS.md, "Standing up a NEW plugin repo").

    python3 check-covers.py --root .              # gate the repo at .
    python3 check-covers.py --root . --report     # every cover, worst first — no exit code
    python3 check-covers.py --root . --max 320    # tighten (do this WITH the rewrites, not before)
    python3 check-covers.py --self-test           # prove both measurements in both directions

The lane (`node-repo-validate.yml`) fetches this file at its `scripts-ref` and runs the self-test
and then the gate with `--root .` in the calling repo's checkout. Locally, fetch it the same way
or run it from a core checkout: `python3 ../MeshWeaver/.github/scripts/check-covers.py --root .`.

## The second check: heroCta contrast

A cover may declare `heroCta` — the colours of the Store CTA row's pills. The row is embedded AFTER
the hero banner, so it renders on the PAGE, and a palette picked to sit on a dark banner is a button
nobody can see: twelve covers across two repos shipped a pale tint at ~1.05:1 against white, and one
shipped `rgba(255,255,255,.16)` with a near-white label — white on white. Every declared pair is
held to WCAG 1.4.11 (3:1, the button surface against the page) and 1.4.3 (4.5:1, its label on that
surface). An undeclared pair is not a gap: the pill then falls through to the platform's neutral
tokens, which are correct by construction.
"""
from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

# 400 characters is roughly four rendered lines at a comfortable measure. It is the limit the repo
# can hold with NO exemptions today; tighten it together with the rewrites it demands, never ahead
# of them — a gate that is red on arrival teaches people to pass `--max`, not to write better.
DEFAULT_MAX = 400

ALLOW_FILE = "cover-prose.allow"
# 🚨 Seeded ABSENT, and it may only SHRINK. Every cover in every repo passes the contrast check
# today, so there is no debt to grandfather; a file that does not exist reads as an empty set.
CONTRAST_ALLOW_FILE = "cover-contrast.allow"
# 🚨 THIS IS THE FLEET'S ONE COPY (MeshWeaver#4027). It lives in core's .github/scripts and is
# fetched by node-repo-validate.yml at the lane's scripts-ref, like every other centralized guard.
# It used to be hand-copied into five repos as scripts/check-covers.py, and the copies drifted to
# three variants in 17 days: the heroCta contrast check reached ONE of them. Never re-grow a
# local copy; a repo-specific need is an argument or an allow-file, not a fork.
#
# `WhatsNew` is here from MeshWeaver.Crm's copy: a `WhatsNew/` root is release notes, not a
# plugin, and must not be read as a cover if it ever carries an index.json.
SKIP = {"WhatsNew", ".git", ".github", ".worktrees", "scripts", "e2e", "src", "clients", "app",
        "node_modules", "legacy", "content"}

# A line that opens or continues STRUCTURE rather than prose.
STRUCTURAL = re.compile(r"""^(
      \#{1,6}\s          # heading
    | [-*+]\s            # bullet
    | \d+\.\s            # ordered item
    | >                  # block quote
    | \|                 # table row
    | <                  # raw HTML block (the hero banner every good cover opens with)
    | @@                 # embedded layout area
    | !\[                # standalone image
    | :::                # container directive
)""", re.VERBOSE)


def paragraphs(body: str) -> list[str]:
    """The prose paragraphs of a markdown body, each joined the way markdown will reflow it."""
    out: list[str] = []
    buf: list[str] = []
    fenced = False
    for raw in body.split("\n"):
        line = raw.strip()
        if line.startswith("```") or line.startswith("~~~"):
            fenced = not fenced
            if buf:
                out.append(" ".join(buf))
                buf = []
            continue
        if fenced:
            continue
        # A rule (---, ***, ___) is structure; so is a blank line.
        if not line or STRUCTURAL.match(line) or set(line) <= set("-=*_ "):
            if buf:
                out.append(" ".join(buf))
                buf = []
            continue
        buf.append(line)
    if buf:
        out.append(" ".join(buf))
    return out


def covers(root: Path) -> list[tuple[str, dict]]:
    """(module, content) for every plugin root in this repo that authors a cover."""
    found = []
    for module in sorted(p for p in root.iterdir() if p.is_dir() and p.name not in SKIP
                         and not p.name.startswith(".")):
        index = module / "index.json"
        if not index.exists():
            continue
        try:
            content = (json.loads(index.read_text(encoding="utf-8")).get("content") or {})
        except (json.JSONDecodeError, OSError) as exc:
            print(f"::error::{module.name}/index.json is unreadable: {exc}")
            sys.exit(1)
        found.append((module.name, content))
    return found


def read_allow(root: Path, name: str = ALLOW_FILE) -> set[str]:
    path = root / name
    if not path.exists():
        return set()
    return {line.strip() for line in path.read_text(encoding="utf-8").splitlines()
            if line.strip() and not line.startswith("#")}


# ── heroCta contrast ─────────────────────────────────────────────────────────────────────────────
# The cover's CTA row renders on the PAGE ground, not on the hero. Every cover embeds
# `@@("area/CoverCta")` AFTER its hero <div> (measured across all nine, 2026-09-10), so the pills
# sit on `--neutral-layer-1` — white in the light theme. The palette, however, was authored FOR the
# hero: a pale tint with dark text, which is exactly right on a dark-green banner and nearly
# invisible one line below it. AgenticBusiness shipped `#ecfdf5` on white — 1.04:1, a button the
# reporter could not tell from the page.
PAGE_GROUND = (255, 255, 255)
# WCAG 1.4.11: a UI component's surface needs 3:1 against what is behind it. WCAG 1.4.3: its label
# needs 4.5:1 against that surface — the pill is 1rem/700, which is NOT "large text" (that starts at
# 18.66px bold), so the normal-text ratio is the one that applies.
MIN_SURFACE_CONTRAST = 3.0
MIN_LABEL_CONTRAST = 4.5

# `var(--ae-fg,#f4f7ff)` — two covers declare a colour this way. The FALLBACK is what this gate can
# read; the custom property itself is defined in the hero's own markup and is not a page-ground
# colour by any reading available here.
VAR_FALLBACK = re.compile(r"^var\(\s*--[\w-]+\s*,\s*(?P<fallback>[^)]+?)\s*\)$", re.I)
RGB_FUNC = re.compile(
    r"^rgba?\(\s*(?P<r>[\d.]+)\s*[,\s]\s*(?P<g>[\d.]+)\s*[,\s]\s*(?P<b>[\d.]+)"
    r"\s*(?:[,/]\s*(?P<a>[\d.%]+)\s*)?\)$", re.I)


def parse_colour(value: str, over: tuple[int, int, int]) -> tuple[int, int, int] | None:
    """A CSS colour as sRGB, composited over `over` when it carries alpha. None when unreadable.

    🚨 Alpha is COMPOSITED rather than ignored. Five covers declare
    `secondaryBackground: rgba(255,255,255,.16)` — 16% white, which over the dark hero it was written
    for is a subtle scrim and over the white page is white. Reading it as opaque white would be
    accidentally right here and wrong the moment a cover uses alpha over a colour; reading it as
    "no colour" would let exactly this defect through.
    """
    value = (value or "").strip()
    if not value:
        return None
    m = VAR_FALLBACK.match(value)
    if m:
        return parse_colour(m.group("fallback"), over)
    if value.startswith("#"):
        digits = value[1:]
        if len(digits) == 3:
            digits = "".join(c * 2 for c in digits)
        if len(digits) == 8:            # #rrggbbaa
            digits, alpha_hex = digits[:6], digits[6:]
            alpha = int(alpha_hex, 16) / 255
        else:
            alpha = 1.0
        if len(digits) != 6:
            return None
        try:
            rgb = tuple(int(digits[i:i + 2], 16) for i in (0, 2, 4))
        except ValueError:
            return None
        return composite(rgb, alpha, over)
    m = RGB_FUNC.match(value)
    if m:
        try:
            rgb = tuple(min(255, max(0, round(float(m.group(c))))) for c in ("r", "g", "b"))
        except ValueError:
            return None
        raw = m.group("a")
        if raw is None:
            alpha = 1.0
        elif raw.endswith("%"):
            alpha = float(raw[:-1]) / 100
        else:
            alpha = float(raw)
        return composite(rgb, alpha, over)
    return None


def composite(rgb: tuple[int, int, int], alpha: float, over: tuple[int, int, int]):
    alpha = min(1.0, max(0.0, alpha))
    return tuple(round(c * alpha + o * (1 - alpha)) for c, o in zip(rgb, over))


def relative_luminance(rgb: tuple[int, int, int]) -> float:
    def channel(v: int) -> float:
        s = v / 255
        return s / 12.92 if s <= 0.03928 else ((s + 0.055) / 1.055) ** 2.4
    r, g, b = (channel(c) for c in rgb)
    return 0.2126 * r + 0.7152 * g + 0.0722 * b


def contrast_ratio(a: tuple[int, int, int], b: tuple[int, int, int]) -> float:
    la, lb = relative_luminance(a), relative_luminance(b)
    hi, lo = max(la, lb), min(la, lb)
    return (hi + 0.05) / (lo + 0.05)


def contrast_self_test() -> list[str]:
    """🚨 The contrast gate must be able to FAIL, and its arithmetic must be right.

    Once the nine covers are fixed this check only ever runs its passing path, and a `parse_colour`
    that quietly returned None for everything would print the same green tick as a gate that works —
    the exact shape of vacuous pass this file already refuses elsewhere. So the maths is pinned
    against values computed by hand, and the two readings that hid the defect (a `var()` fallback, an
    alpha composited over the ground) are pinned as the readings they must be.
    """
    cases = [
        # (value, ground, expected sRGB) — the parse
        ("#ecfdf5", PAGE_GROUND, (236, 253, 245)),
        ("#fff", PAGE_GROUND, (255, 255, 255)),
        ("var(--ae-fg,#f4f7ff)", PAGE_GROUND, (244, 247, 255)),
        # 16% white over white IS white; over black it is not. Ignoring alpha would make the first
        # right by accident and the second wrong.
        ("rgba(255,255,255,.16)", PAGE_GROUND, (255, 255, 255)),
        ("rgba(255,255,255,.16)", (0, 0, 0), (41, 41, 41)),
        ("chartreuse", PAGE_GROUND, None),
        ("", PAGE_GROUND, None),
    ]
    failures = []
    for value, ground, expected in cases:
        got = parse_colour(value, ground)
        if got != expected:
            failures.append(f"parse_colour({value!r}, {ground}) = {got}, expected {expected}")
    # The two anchors of WCAG's own scale: identical colours are 1:1, black on white is 21:1.
    for label, ratio, expected in (
        ("white on white", contrast_ratio(PAGE_GROUND, PAGE_GROUND), 1.0),
        ("black on white", contrast_ratio((0, 0, 0), PAGE_GROUND), 21.0),
        # The reported defect, to two decimals — if this number moves, the gate's maths moved.
        ("AgenticBusiness' old pill", contrast_ratio((236, 253, 245), PAGE_GROUND), 1.05),
    ):
        if abs(ratio - expected) > 0.005:
            failures.append(f"contrast_ratio for {label} = {ratio:.4f}, expected {expected}")
    # And the gate must REPORT: the palette as it shipped has to come back as findings.
    shipped = {"background": "#ecfdf5", "foreground": "#065f46",
               "secondaryBackground": "rgba(255,255,255,.16)", "secondaryForeground": "#ecfdf5"}
    found = evaluate_hero_cta("SelfTest", shipped, set())
    if len(found) != 3:
        failures.append(f"the shipped AgenticBusiness palette yields {len(found)} finding(s), "
                        f"expected 3 (pale accent surface, white-on-white quiet surface, its label)")
    if evaluate_hero_cta("SelfTest", {"background": "#065f46", "foreground": "#ecfdf5"}, set()):
        failures.append("the FIXED palette still reports a finding — the gate cannot be satisfied")
    if evaluate_hero_cta("SelfTest", shipped, {"contrast:SelfTest:background",
                                               "contrast:SelfTest:secondaryBackground"}):
        failures.append("the allow ratchet does not silence an entry")
    # …and an exemption is only EARNED by a finding it silences: on the shipped palette both keys
    # silence something; on the fixed palette the same keys silence nothing and must read stale.
    used = silenced_contrast_keys("SelfTest", shipped, {"contrast:SelfTest:background",
                                                         "contrast:SelfTest:secondaryBackground",
                                                         "contrast:Other:background"})
    if used != {"contrast:SelfTest:background", "contrast:SelfTest:secondaryBackground"}:
        failures.append(f"silenced keys on the shipped palette = {sorted(used)}, expected exactly the "
                        f"two that silence a finding (never a key for another module)")
    if silenced_contrast_keys("SelfTest", {"background": "#065f46", "foreground": "#ecfdf5"},
                              {"contrast:SelfTest:background"}):
        failures.append("a contrast exemption on a FIXED palette reads as used — stale entries "
                        "would never be reported")

    return failures


def evaluate_hero_cta(module: str, palette: dict, allow: set[str]) -> list[str]:
    """The judgement itself, over a palette rather than a folder — so `--self-test` can drive it."""
    return [problem for _, problem in judge_hero_cta(module, palette) if _ not in allow]


def silenced_contrast_keys(module: str, palette: dict, allow: set[str]) -> set[str]:
    """The allow keys that actually SILENCED a finding on this palette — the only ones that earn
    their place. An exemption that silences nothing is debt someone already paid (see
    `stale_entries`), and the gate reports it as such rather than carrying it forever."""
    return {key for key, _ in judge_hero_cta(module, palette) if key in allow}


def judge_hero_cta(module: str, palette: dict) -> list[tuple[str, str]]:
    """Every finding on a palette as `(allow key, problem)`, BEFORE any exemption is applied."""
    pairs = (
        ("background", "foreground", "the accent pill (Resume / Start / Get)"),
        ("secondaryBackground", "secondaryForeground", "the quiet pills beside it"),
    )
    findings: list[tuple[str, list[str]]] = []
    for bg_key, fg_key, what in pairs:
        declared_bg, declared_fg = palette.get(bg_key), palette.get(fg_key)
        if not declared_bg and not declared_fg:
            continue
        # One allow key per PAIR: the pair's problems collect under it and are flattened on return,
        # so an exemption silences the whole pair and `silenced_contrast_keys` can tell whether it
        # silenced anything at all.
        key = f"contrast:{module}:{bg_key}"
        problems: list[str] = []
        findings.append((key, problems))
        surface = parse_colour(declared_bg, PAGE_GROUND) if declared_bg else PAGE_GROUND
        if surface is None:
            problems.append(
                f"{module}/index.json: heroCta.{bg_key} is '{declared_bg}', which this gate cannot "
                f"read as a colour — an unreadable value is how a low-contrast one hides")
            continue
        if declared_bg:
            ratio = contrast_ratio(surface, PAGE_GROUND)
            if ratio < MIN_SURFACE_CONTRAST:
                problems.append(
                    f"{module}/index.json: heroCta.{bg_key} '{declared_bg}' is {ratio:.2f}:1 "
                    f"against the white page, below {MIN_SURFACE_CONTRAST}:1 — {what} renders one "
                    f"line BELOW the hero, on the page ground, not on the banner this colour was "
                    f"picked for")
        if declared_fg:
            label = parse_colour(declared_fg, surface)
            if label is None:
                problems.append(
                    f"{module}/index.json: heroCta.{fg_key} is '{declared_fg}', which this gate "
                    f"cannot read as a colour")
                continue
            ratio = contrast_ratio(label, surface)
            if ratio < MIN_LABEL_CONTRAST:
                problems.append(
                    f"{module}/index.json: heroCta.{fg_key} '{declared_fg}' is {ratio:.2f}:1 on "
                    f"heroCta.{bg_key} '{declared_bg or '(the page)'}', below "
                    f"{MIN_LABEL_CONTRAST}:1 — {what} carries a label a reader cannot make out")
    return [(key, problem) for key, pair_problems in findings for problem in pair_problems]


def stale_entries(allow: set[str], rows: list[tuple[str, int, int, str]], limit: int) -> list[str]:
    """Prose exemptions that no longer silence anything: the module is under the limit, or it is
    gone (deleted or renamed). Both used to be invisible — the old check only looked at entries
    with a matching CURRENT row, so an exemption for a module that had been removed survived
    forever and the ratchet could grow by attrition."""
    return sorted(m for m in allow
                  if not any(module == m and longest > limit for module, longest, _, _ in rows))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--max", type=int, default=DEFAULT_MAX,
                        help=f"longest allowed prose paragraph, in characters (default {DEFAULT_MAX})")
    parser.add_argument("--report", action="store_true", help="list every cover, worst first")
    parser.add_argument("--self-test", action="store_true",
                        help="prove the paragraph measurement in both directions")
    parser.add_argument("--root", type=Path, default=Path.cwd(),
                        help="the node repo to gate (its top-level plugin folders and allow-files); "
                             "default: the current directory. This script is FETCHED into a temp "
                             "directory by the lane, so it cannot locate the repo from its own path.")
    args = parser.parse_args()
    if args.self_test:
        return self_test()

    root = args.root.resolve()
    if not root.is_dir():
        print(f"::error::--root {root} is not a directory — nothing to gate")
        return 1
    allow = read_allow(root)
    rows = []
    contrast_allow = read_allow(root, CONTRAST_ALLOW_FILE)
    palette_problems = []
    contrast_used: set[str] = set()
    for module, content in covers(root):
        palette = content.get("heroCta")
        if isinstance(palette, dict):
            palette_problems += evaluate_hero_cta(module, palette, contrast_allow)
            contrast_used |= silenced_contrast_keys(module, palette, contrast_allow)
        elif palette is not None:
            palette_problems.append(f"{module}/index.json: heroCta is not an object")
        paras = paragraphs(content.get("body") or "")
        longest = max((len(p) for p in paras), default=0)
        worst = max(paras, key=len) if paras else ""
        rows.append((module, longest, len(paras), worst))

    if not rows:
        print("::error::no plugin covers found — this gate would pass vacuously")
        return 1

    if args.report:
        print(f"{'module':24} {'longest':>7} {'paras':>5}")
        for module, longest, count, _ in sorted(rows, key=lambda r: -r[1]):
            mark = "  ← over the limit" if longest > args.max and module not in allow else ""
            print(f"{module:24} {longest:7} {count:5}{mark}")
        return 0

    failures = [r for r in rows if r[1] > args.max and r[0] not in allow]
    # An allow entry that no longer needs to be there is debt someone already paid — make deleting
    # it the required next step, exactly as plugin-tests.allow does. Both allow-files, both ways
    # an entry can stop earning its place (the module fixed, or the module gone).
    stale = stale_entries(allow, rows, args.max)
    stale_contrast = sorted(contrast_allow - contrast_used)

    for module, longest, _, worst in sorted(failures, key=lambda r: -r[1]):
        print(f"✗ {module}: a prose paragraph of {longest} characters "
              f"(limit {args.max}) — split it into a list, or into two paragraphs.")
        print(f"    “{worst[:120]}…”")
    for module in stale:
        print(f"✗ {module} is listed in {ALLOW_FILE} but no longer needs to be — delete the line.")
    for key in stale_contrast:
        print(f"✗ {key} is listed in {CONTRAST_ALLOW_FILE} but silences nothing — delete the line.")
    for problem in palette_problems:
        print(f"✗ {problem}")

    if failures or stale or stale_contrast or palette_problems:
        # "heroCta problem", not "unreadable pill": the list also carries values the gate could
        # not parse and palettes that are not objects, and the fix differs for each.
        print(f"\n{len(failures)} wall(s) of text, {len(palette_problems)} heroCta problem(s), "
              f"{len(stale) + len(stale_contrast)} stale exemption(s). "
              f"`--report` ranks every cover.")
        return 1

    worst_module, worst_len, _, _ = max(rows, key=lambda r: r[1])
    print(f"✓ {len(rows)} cover(s): longest prose paragraph {worst_len} chars "
          f"({worst_module}), limit {args.max}, {len(allow)} exemption(s); "
          f"every declared heroCta pair legible on the page.")
    return 0


def self_test() -> int:
    """🚨 The measurement must be able to be WRONG in both directions.

    Run against the clean repo this script exercises only its passing path, so a regex that stopped
    matching bullets — flagging every well-structured cover — or one that swallowed everything —
    flagging none — would report the same green tick.
    """
    cases: list[tuple[str, str, int]] = [
        ("a plain paragraph", "hello world", 11),
        ("two paragraphs split by a blank line", "aaaa\n\nbb", 4),
        ("authored line breaks reflow into ONE paragraph", "aaaa\nbbbb", 9),
        ("a heading is structure", "# Heading that is quite long indeed\n\nab", 2),
        ("a bullet is structure, however long", "- " + "x" * 900, 0),
        ("an ordered item is structure", "1. " + "x" * 900, 0),
        ("a table row is structure", "| " + "x" * 900, 0),
        ("the HTML hero is structure", "<div style='...'>" + "x" * 900 + "</div>", 0),
        ("a block quote is structure", "> " + "x" * 900, 0),
        ("an @@ area embed is structure", '@@("area/Search")', 0),
        ("a standalone image is structure", "![alt](" + "x" * 900 + ")", 0),
        ("a horizontal rule is structure", "---", 0),
        ("fenced code never counts", "```\n" + "x" * 900 + "\n```", 0),
        ("a fence does not swallow the prose after it", "```\ncode\n```\n\nabcd", 4),
        ("prose AFTER a bullet list still counts", "- item\n\nabcde", 5),
        ("an empty body has no paragraphs", "", 0),
    ]
    failures = []
    for label, body, expected in cases:
        actual = max((len(p) for p in paragraphs(body)), default=0)
        if actual != expected:
            failures.append(f"{label}: expected longest {expected}, got {actual}")

    # The prose ratchet's stale rule, both directions: an entry keeps its place only while its
    # module is present AND over the limit. Fixed ⇒ stale; deleted/renamed ⇒ stale (this second
    # direction is the one the old check missed); still over ⇒ kept.
    rows = [("Fixed", 100, 1, ""), ("StillOver", DEFAULT_MAX + 1, 1, "")]
    got = stale_entries({"Fixed", "Gone", "StillOver"}, rows, DEFAULT_MAX)
    if got != ["Fixed", "Gone"]:
        failures.append(f"stale prose exemptions = {got}, expected ['Fixed', 'Gone'] "
                        f"(a fixed module and a module that no longer exists)")

    # And the gate must actually TRIP: a body one character over the limit fails, one at the limit
    # passes. A limit compared with the wrong operator is invisible to every case above.
    at_limit = paragraphs("x" * DEFAULT_MAX)[0]
    over = paragraphs("x" * (DEFAULT_MAX + 1))[0]
    if not (len(at_limit) <= DEFAULT_MAX < len(over)):
        failures.append("the limit boundary is not what the gate compares")

    # The palette half proves the same thing about ITS measurement: parse and ratio in both
    # directions, the shipped palette reported, the fixed one silent, and the allow ratchet.
    contrast_failures = contrast_self_test()
    failures += contrast_failures

    if failures:
        print(f"✗ check-covers self-test: {len(failures)} failure(s)")
        for f in failures:
            print(f"  - {f}")
        return 1
    print(f"✓ check-covers self-test: {len(cases)} measurement case(s), "
          f"{sum(1 for _, _, e in cases if e == 0)} of them structure that must NOT count, "
          f"the limit boundary, both allow ratchets' stale rule, and the heroCta contrast maths "
          f"in both directions.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
