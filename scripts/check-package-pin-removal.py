#!/usr/bin/env python3
"""Refuse a `PackageVersion` removal that nobody has checked against the satellites (#3349).

THE SHAPE (CrossRepoPairGate → "An eighth shape", measured on #3344).
`MeshWeaver.Plugins/src/Directory.Packages.props` IMPORTS this repo's `Directory.Packages.props`,
so a satellite project may carry a versionless `<PackageReference Include="X" />` whose ONLY
version source is an entry here. Remove that entry — even as collateral in an unrelated withdrawal
— and the satellite stops restoring with `NU1010`.

Both repos stay green while it is broken. Nothing here consumes the package, so no compile, test or
gate in this repo can miss it; and the satellite pins this repo at `MW_PLATFORM_REF`, so ITS CI
reads the old list until somebody moves the pin, on a different day, in a different pull request.
The first lane that notices is `main-cd`, and by then the damage is that no set seals: #3344 cost
~80 minutes of sealing nothing, and the same line was the CVE remedy for GHSA-2m69-gcr7-jv3q, so
the removal was a security regression too.

WHY A DECLARATION AND NOT A DERIVATION.
The control the issue proposed — check out MeshWeaver.Plugins in core's pull-request lane and
`dotnet restore` it — cannot be built here. `PlatformNeverDependsOnPluginsGuard.
ThePullRequestGate_ReachesIntoNoPluginRepository` bans exactly that: both `repository:
Systemorph/MeshWeaver.Plugins` and any line reading `plugins-repo` are actionable hits, and the
pull-request gate must contain none. That ban is not incidental — a core verdict that depends on a
sibling's moving HEAD makes the SAME diff go red or green with no change of its own, which is the
failure the guard exists to prevent.

A hand-maintained list of the load-bearing entries is the other obvious control, and it was written
and discarded before this one: 47 of the satellite's 49 versionless references resolve here, so the
list would go red on core pull requests whenever Plugins legitimately drops a dependency — taxing
every unrelated change in this repo for a fact that lives in another one.

So this gate does what the repo already does for the same SHAPE one category over. A change here
that can silently break a satellite must be DECLARED in the pull-request body, exactly as
`Pairs-with:` declares a public-surface counterpart. It is core-only: no checkout, no API read, no
credential, no ledger entry, and nothing that can go red on somebody else's HEAD.

WHAT IT CANNOT DO, said plainly. It does not know whether a removed pin is load-bearing — it makes a
human find out, by running the grep the doc names. That is weaker than deriving the answer and is
the honest cost of staying inside the dependency direction. It would have caught #3344, which
removed an entry.

DECLARATION SYNTAX (in the pull-request body, not in a fence):

    Satellite-pins: <PackageId>[, <PackageId>…] — <what you checked and what you found>

Every removed id must be named across the declarations. There is no blanket form, deliberately:
"none of them matter" is the sentence that produced #3344.

Usage:
    check-package-pin-removal.py --base <sha> --pr-body-file <file>
    check-package-pin-removal.py --self-test
"""

from __future__ import annotations

import argparse
import re
import subprocess
import sys
from pathlib import Path

PROPS = "Directory.Packages.props"

# `<PackageVersion Include="X" Version="Y" />`, tolerant of attribute order and whitespace.
PACKAGE_VERSION_RE = re.compile(r"""<PackageVersion\b[^>]*?\bInclude\s*=\s*["']([^"']+)["']""", re.I)

HTML_COMMENT_RE = re.compile(r"<!--.*?-->", re.S)
FENCE_RE = re.compile(r"^\s*(```+|~~~+)")

# The em dash is what the doc and the sibling gate use; accept the ASCII forms too so a declaration
# is never rejected over typography. The reason after it must be non-empty.
DECLARATION_RE = re.compile(
    r"^\s*Satellite-pins\s*:\s*(?P<ids>[^\r\n]*?)\s*(?:—|--|-)\s*(?P<reason>\S[^\r\n]*)$",
    re.I,
)


def strip_non_prose(body: str) -> str:
    """Remove HTML comments and fenced code blocks.

    Both can only REMOVE candidate declarations, so this fails closed: quoting the syntax in a
    fence (as this script's own docstring and the doc page do) never declares anything, and hiding
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


def declared_ids(body: str) -> set[str]:
    """Package ids named in `Satellite-pins:` declarations, case-preserved."""
    found: set[str] = set()
    for line in strip_non_prose(body or "").split("\n"):
        # A quoted reply is somebody else's text being cited, not this author's declaration.
        if line.lstrip().startswith(">"):
            continue
        m = DECLARATION_RE.match(line)
        if not m:
            continue
        for raw in m.group("ids").split(","):
            token = raw.strip().strip("`")
            if token:
                found.add(token)
    return found


def ids_in(text: str) -> set[str]:
    return set(PACKAGE_VERSION_RE.findall(text))


class Undecidable(Exception):
    """The gate could not READ its subject.

    🚨 Raised, never swallowed, and never folded into "nothing was removed". A shallow fetch that
    does not contain the merge base, a renamed or moved props file, a git failure — each of those
    means the answer is UNKNOWN, and reporting unknown as clean is the skip-trapdoor shape
    AGENTS.md bans: it makes "the gate never ran" and "the gate passed" the same colour. #3344
    slipped through precisely because every check was green over a fact nothing could see.
    """


def _git(args: list[str], cwd: Path) -> str:
    proc = subprocess.run(["git", *args], cwd=cwd, capture_output=True, text=True)
    if proc.returncode != 0:
        raise Undecidable(
            f"`git {' '.join(args)}` failed ({proc.returncode}): {proc.stderr.strip() or '(no stderr)'}"
        )
    return proc.stdout


def removed_ids(base: str, root: Path) -> tuple[set[str], int, int]:
    """Ids present in the props file at `base` and absent at HEAD, plus both counts.

    The counts are returned so the caller can refuse a STARVED read. A regex that stops matching —
    an attribute rewritten, the file moved — would otherwise report "nothing removed" forever,
    which is this gate reporting a clean tree while blind.
    """
    if not _git(["cat-file", "-t", base], root).strip():
        raise Undecidable(f"{base} does not name an object in this checkout")

    # `git show <base>:<path>` distinguishes "absent at base" from "base not fetched" only in its
    # stderr, and both are Undecidable here — the file has existed since the repo did, so either
    # answer means the read is wrong rather than that the list was empty.
    before_text = _git(["show", f"{base}:{PROPS}"], root)

    head_path = root / PROPS
    if not head_path.is_file():
        raise Undecidable(
            f"{PROPS} is absent from the working tree — if it moved, this gate is pointed at the "
            "wrong path and must be updated with it"
        )
    before = ids_in(before_text)
    after = ids_in(head_path.read_text(encoding="utf-8"))
    return before - after, len(before), len(after)


def evaluate(removed: set[str], body: str) -> tuple[int, list[str]]:
    """0 and an empty report when the diff is acceptable; 1 and the reasons when it is not."""
    if not removed:
        return 0, []

    declared = {d.casefold() for d in declared_ids(body)}
    undeclared = sorted(p for p in removed if p.casefold() not in declared)
    if not undeclared:
        return 0, []

    lines = [
        "This pull request REMOVES central package version(s) that a satellite may consume",
        "versionless. Nothing in this repository can detect that, and the satellite pins this repo,",
        "so both stay green while it is broken — see Doc/Architecture/CrossRepoPairGate → \"An",
        "eighth shape\" (#3344 cost ~80 minutes of sealing nothing, and reintroduced a CVE).",
        "",
        "Undeclared removal(s):",
    ]
    lines += [f"  • {p}" for p in undeclared]
    lines += [
        "",
        "Check each against the satellite, then declare it in the PULL REQUEST BODY:",
        "",
        "    grep -rn 'PackageReference Include=\"<id>\"' ../MeshWeaver.Plugins/src/*/*.csproj",
        "",
        "A versionless hit is a BLOCKER — keep the entry, or remove the reference there first.",
        "Then add one line per id (outside any code fence):",
        "",
        "    Satellite-pins: <id> — what you checked and what you found",
        "",
        "There is deliberately no blanket form: \"none of them matter\" is the sentence that",
        "produced #3344.",
    ]
    return 1, lines


# ── self-test ────────────────────────────────────────────────────────────────────────────────────
# 🚨 An unproven gate is no gate. This exercises BOTH directions — it must fire on an undeclared
# removal and stay silent on an ordinary diff — plus the shapes that decide whether the matcher is
# honest: a declaration inside a fence, a quoted reply, case folding, and a starved read.

def self_test() -> int:
    failures: list[str] = []

    def check(name: str, got, want) -> None:
        if got != want:
            failures.append(f"  {name}: got {got!r}, want {want!r}")

    # The extractor sees the real shape, in both attribute orders.
    props = (
        '<Project><ItemGroup>\n'
        '  <PackageVersion Include="A.B" Version="1.0" />\n'
        "  <PackageVersion Version=\"2.0\" Include='C.D' />\n"
        '  <PackageVersion  Include = "E.F"  Version="3" />\n'
        '</ItemGroup></Project>\n'
    )
    check("extract", ids_in(props), {"A.B", "C.D", "E.F"})
    check("extract ignores PackageReference", ids_in('<PackageReference Include="X" />'), set())

    # Fires on an undeclared removal.
    rc, report = evaluate({"SQLitePCLRaw.lib.e_sqlite3"}, "an ordinary body")
    check("undeclared removal fires", rc, 1)
    check("names the package", any("SQLitePCLRaw.lib.e_sqlite3" in l for l in report), True)

    # Silent when nothing was removed, whatever the body says.
    check("no removal is silent", evaluate(set(), "")[0], 0)

    # Accepts a declaration, including several ids on one line and case differences.
    body = "Satellite-pins: SQLitePCLRaw.lib.e_sqlite3, Other.Pkg — grepped both, no versionless hit"
    check("declared passes", evaluate({"SQLitePCLRaw.lib.e_sqlite3", "Other.Pkg"}, body)[0], 0)
    check(
        "declaration is case-insensitive",
        evaluate({"sqlitepclraw.lib.e_sqlite3"}, body)[0],
        0,
    )

    # A PARTIAL declaration is not a pass — the undeclared one still fires and is named alone.
    rc, report = evaluate({"SQLitePCLRaw.lib.e_sqlite3", "Missing.One"}, body)
    check("partial declaration fires", rc, 1)
    check("names only the undeclared", any("Missing.One" in l for l in report), True)
    check("does not name the declared", all("SQLitePCLRaw" not in l for l in report), True)

    # …and the shapes that must NOT count as a declaration.
    fenced = "```\nSatellite-pins: P — quoted in a fence\n```"
    check("fenced declaration is not one", evaluate({"P"}, fenced)[0], 1)
    check("commented declaration is not one", evaluate({"P"}, "<!-- Satellite-pins: P — no -->")[0], 1)
    check("quoted reply is not one", evaluate({"P"}, "> Satellite-pins: P — somebody else")[0], 1)
    check("reasonless declaration is not one", evaluate({"P"}, "Satellite-pins: P")[0], 1)
    check("empty reason is not one", evaluate({"P"}, "Satellite-pins: P — ")[0], 1)

    # A declaration naming a DIFFERENT package does not cover this one.
    check("wrong id does not cover", evaluate({"P"}, "Satellite-pins: Q — checked Q")[0], 1)

    # 🚨 UNDECIDABLE IS RAISED, NOT RETURNED AS CLEAN. A bad base and a moved props file must both
    # reach the caller as an exception; if either ever degraded to "nothing removed", this gate
    # would pass on no evidence — the failure it exists to prevent.
    import tempfile

    with tempfile.TemporaryDirectory() as tmp:
        empty = Path(tmp)
        subprocess.run(["git", "init", "-q"], cwd=empty, check=True)
        raised = False
        try:
            removed_ids("deadbeefdeadbeefdeadbeefdeadbeefdeadbeef", empty)
        except Undecidable:
            raised = True
        check("unknown base raises Undecidable", raised, True)

    root = Path(__file__).resolve().parent.parent
    if (root / ".git").exists() and (root / PROPS).is_file():
        # The real tree: HEAD against itself removes nothing, and the count is far above the floor.
        removed_here, before_here, _ = removed_ids("HEAD", root)
        check("HEAD vs HEAD removes nothing", removed_here, set())
        check("the real list is read, not starved", before_here >= 50, True)

    if failures:
        print("self-test FAILED:", file=sys.stderr)
        print("\n".join(failures), file=sys.stderr)
        return 1
    print("✓ check-package-pin-removal self-test: fires on an undeclared removal, stays silent on "
          "an ordinary diff, and refuses a fenced, commented, quoted, reasonless or mis-named "
          "declaration.")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(description="Refuse an undeclared PackageVersion removal (#3349).")
    ap.add_argument("--base", help="merge-base sha to compare the package list against")
    ap.add_argument("--pr-body-file", help="file holding the pull-request body")
    ap.add_argument("--self-test", action="store_true", help="prove the gate is not vacuous")
    args = ap.parse_args()

    if args.self_test:
        return self_test()
    if not args.base or not args.pr_body_file:
        ap.error("--base and --pr-body-file are required unless --self-test is given")

    root = Path(__file__).resolve().parent.parent
    try:
        removed, before, after = removed_ids(args.base, root)
    except Undecidable as e:
        # 🚨 UNKNOWN IS RED. Never "no removal detected" — see Undecidable's remarks.
        print(
            f"::error::This gate could not read {PROPS} at the merge base, so it does not know "
            f"whether a PackageVersion was removed: {e}. That is UNDECIDED, not clean — a shallow "
            "fetch that misses the merge base, or a moved props file, would otherwise report a "
            "clean tree forever. Fetch enough history (the lane fetches --depth=200) or repoint "
            "the gate, then re-run.",
            file=sys.stderr,
        )
        return 1

    # 🚨 A STARVED READ IS NOT A CLEAN ONE. If the props file suddenly declares almost nothing, the
    # matcher has stopped seeing its subject and "nothing was removed" is meaningless. main carried
    # 240+ entries when this was written; 50 is far below any legitimate tree and far above zero.
    if before < 50:
        print(
            f"::error::{PROPS} declared only {before} PackageVersion entries at {args.base} — this "
            "gate reads the list with a regex, and a count that low means it has stopped matching "
            "rather than that the list shrank. Fix the matcher; do not trust this run.",
            file=sys.stderr,
        )
        return 1

    print(f"{PROPS}: {before} entries at {args.base[:9]}, {after} at HEAD, {len(removed)} removed.")
    if not removed:
        print("No PackageVersion removed — nothing to declare.")
        return 0

    body = Path(args.pr_body_file).read_text(encoding="utf-8")
    rc, report = evaluate(removed, body)
    if rc == 0:
        print("Every removed entry is declared: " + ", ".join(sorted(removed)))
        return 0

    print("::error::" + report[0])
    for line in report[1:]:
        print(line)
    return 1


if __name__ == "__main__":
    sys.exit(main())
