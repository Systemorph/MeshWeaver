#!/usr/bin/env python3
"""check-abbreviated-shas.py — every abbreviated commit sha in a workflow file must have its FULL form in the same file.
(The name on this first line is load-bearing: node-repo-validate.yml fetches this file at its scripts
ref and refuses a body whose first 400 bytes do not name it — the same shape as
check-workflow-timeouts.py. Without it, every caller fetching the lane's guards at `main` failed with
"the fetched check-abbreviated-shas.py at main does not look like the guard" — Plugins#1566, 2026-09-09.)

🚨 WHY THIS EXISTS. A platform pin is moved by grepping the OLD 40-hex value and substituting the
new one — that is the documented procedure in every node repo's ci.yml, and it is right, because
editing the named variable reaches none of the `uses:`/`platform-ref:` literals a reusable
workflow's `with:` cannot read.

It has ONE blind spot, and it fired THREE TIMES IN THREE REPOS on 2026-09-09 alone
(MeshWeaver.Reinsurance, MeshWeaver.Crm, MeshWeaver.Manufacturing): the NARRATIVE comment names the
pin in ABBREVIATED form. A 40-hex substitution cannot see `7bfe2d907`, while the SET NAME in the
same sentence — `3.0.0-ci.8055` — is a separate substitution that DOES move. What is left is the
worst available state:

    # Current: 3.0.0-ci.8195 / core 7bfe2d907
                ^^^^^^^^^^^^^ new         ^^^^^^^^^ old

a comment that names the new set and the old commit, in one breath, confidently. Reinsurance's own
ci.yml records having had exactly this before ("the previous pin's comment said ci.6353 while the
pin was ci.6469"), which is what made it worth a guard rather than a fourth catch.

THE RULE. An abbreviated sha is fine — comments are more readable for it — PROVIDED the full form
appears somewhere in the same file. After a correct move the full form is there (it is the new pin),
so an abbreviation of it passes. After the defective move the old short sha prefixes nothing the
file still contains, and this fails naming the line.

🚨 It also makes a DELIBERATE historical mention express itself: "previously core <full sha>" passes
by carrying the full value, which is better documentation anyway — the reader can resolve it.

Exit 0 = every abbreviation resolves. Exit 1 = at least one dangles, or nothing was examined.
"""
import argparse
import pathlib
import re
import sys

# A commit sha written short. Word-bounded so `s8b8f3cc…` (a framework identity, which carries a
# leading `s`) and the 64-hex tail of `sha256:…` cannot match, and length-capped below 40 so a full
# sha is not its own abbreviation.
# 🚨 ANCHORED ON `core`, not on "hex that looks like a sha". Two rounds against real files taught
# this: a bare 7-39 hex pattern swept up 32-hex MODULE IDS (node-repo-module-pack.yml lists one per
# module), and narrowing to 7-12 still caught short IMAGE DIGESTS in prose
# (`memex-portal-ai@7e8c0b20`). Both are legitimate, neither is a pin, and a guard that reports
# them is one nobody keeps green.
#
# The defect has a NAME in front of it every time it has occurred: the narrative sentence reads
# "core <sha>", because that is how these files describe which platform commit the set was cut
# from. Anchoring there costs nothing real — a stale pin comment that does NOT say "core" is not
# the shape that has bitten — and it buys a rule that fires only on the thing it is for.
SHORT = re.compile(r"\bcore\s+([0-9a-f]{7,12})(?![0-9a-zA-Z_-])")
FULL = re.compile(r"(?<![0-9a-zA-Z_-])([0-9a-f]{40})(?![0-9a-zA-Z_-])")
# 🚨 A run id is all digits and 11 chars — valid hex, and NOT a sha. Requiring a letter is what
# stops this guard reporting every `actions/runs/34353419069` in every comment in the fleet, which
# would be a gate nobody could keep green and everybody would switch off.
HAS_LETTER = re.compile(r"[a-f]")


def check(files: dict[str, str]) -> list[str]:
    findings: list[str] = []
    # 🚨 WHAT THE DENOMINATOR IS, and what it is NOT. Counting abbreviations was wrong (a file
    # that pins in full and abbreviates nowhere is clean, and the self-test caught that). Counting
    # full shas was ALSO wrong, and running against the platform repo caught it: core does not pin
    # `Systemorph/MeshWeaver` lanes by sha — it IS MeshWeaver — so demanding one failed the very
    # repo the guard ships from. Vacuity here is covered by a sibling that owns it:
    # check-platform-pins.py already refuses a core checkout that names no ref. This one reports
    # its denominator and judges only what it can actually resolve.
    shas_seen = 0
    for name in sorted(files):
        text = files[name]
        full = set(FULL.findall(text))
        shas_seen += len(full)
        for number, line in enumerate(text.splitlines(), start=1):
            for short in SHORT.findall(line):
                if not HAS_LETTER.search(short):
                    continue          # a run id / issue number, not a sha
                if any(f.startswith(short) for f in full):
                    continue
                findings.append(
                    f"{name}:{number}: `{short}` is an abbreviated commit sha that prefixes no "
                    f"full sha in this file. Either it is STALE — a 40-hex substitution moved the "
                    f"pins and could not see this one, which is how a comment ends up naming the "
                    f"new set and the old commit — or it is a deliberate historical reference, in "
                    f"which case write the full 40-hex value so a reader can resolve it.\n"
                    f"      {line.strip()[:160]}")
    # 🚨 A guard that examined nothing must not report a pass: if the workflows moved, or the
    # pattern stopped matching what a sha looks like, this would go quietly green for ever.
    # 🚨 The one vacuity that is this guard's own: no workflow files at all means the subject moved.
    if not files:
        findings.append(
            "no workflow file was found under .github/workflows. This guard's subject has moved, "
            "and a gate that examines nothing must fail rather than report a pass.")
    return findings


def load(root: pathlib.Path) -> dict[str, str]:
    directory = root / ".github" / "workflows"
    if not directory.is_dir():
        return {}
    return {
        str(p.relative_to(root)): p.read_text(encoding="utf-8", errors="replace")
        for p in sorted(directory.glob("*.yml")) + sorted(directory.glob("*.yaml"))
    }


SELF_TESTS: list[tuple[str, dict[str, str], bool]] = [
    # The shipped shape: an abbreviation whose full form is right there.
    ("an abbreviation backed by the full sha in the same file",
     {"ci.yml": "# Current: core 7bfe2d907\nuses: x@7bfe2d9073282473b0b6ba4b5b400f80b46bd5f7\n"}, True),
    # 🚨 THE DEFECT, exactly as it appeared three times on 2026-09-09.
    ("the narrative sha left behind by a 40-hex substitution",
     {"ci.yml": "# Current: 3.0.0-ci.8195 / core 7bfe2d907\n"
                "uses: x@174f5ab7711e1a107c0c047dfb0dd9ba3bc8b91c\n"}, False),
    # A deliberate history line passes by carrying the full value — which is better documentation.
    ("a historical reference written in full",
     {"ci.yml": "# Previously core 7bfe2d9073282473b0b6ba4b5b400f80b46bd5f7.\n"
                "uses: x@174f5ab7711e1a107c0c047dfb0dd9ba3bc8b91c\n"}, True),
    # 🚨 The false positives that would make this unkeepable, each pinned.
    ("a run id is all digits and is not a sha",
     {"ci.yml": "# see actions/runs/34353419069\nuses: x@174f5ab7711e1a107c0c047dfb0dd9ba3bc8b91c\n"}, True),
    ("an image digest is not an abbreviated sha",
     {"ci.yml": "MW_IMAGE_DIGEST: sha256:61d91c0bfd6a4fc882552d81b789b492093bc20d8030a5adfba4623f87eabd5c\n"
                "uses: x@174f5ab7711e1a107c0c047dfb0dd9ba3bc8b91c\n"}, True),
    ("a framework identity carries a leading letter and is not a sha",
     {"ci.yml": "# identity s8b8f3cc3067e3d9d01059c47f677953c\n"
                "uses: x@174f5ab7711e1a107c0c047dfb0dd9ba3bc8b91c\n"}, True),
    # 🚨 Non-vacuity, both ways.
    ("a tree that pins in full and abbreviates nowhere is CLEAN",
     {"ci.yml": "uses: x@174f5ab7711e1a107c0c047dfb0dd9ba3bc8b91c\n"}, True),
    # 🚨 …but a tree with no sha at ALL is vacuous: this guard would have nothing to resolve
    # abbreviations against and would go green for ever.
    # The platform repo's own shape: workflows that pin no core lane by sha. Nothing to resolve,
    # and nothing wrong — check-platform-pins.py owns "a core checkout must name a ref".
    ("a workflow file pinning no commit at all has nothing to resolve",
     {"ci.yml": "on: push\njobs:\n  a:\n    runs-on: ubuntu-latest\n"}, True),
    # 🚨 The two false positives measured against real files, each pinned so the anchor cannot be
    # widened back without a red.
    ("a short image digest in prose is not a pin",
     {"ci.yml": "# measured on memex-portal-ai@7e8c0b20, 331 files\n"
                "uses: x@174f5ab7711e1a107c0c047dfb0dd9ba3bc8b91c\n"}, True),
    # 🚨 …but an abbreviation with NO full sha anywhere still dangles, and still fails.
    ("an abbreviation with no full sha anywhere still dangles",
     {"ci.yml": "# core 7bfe2d907\non: push\n"}, False),
    ("no workflow files at all is VACUOUS, not clean", {}, False),
    # A 32-hex module id is not an abbreviated sha (measured on node-repo-module-pack.yml).
    ("a 32-hex module id is not an abbreviated sha",
     {"ci.yml": "#   MeshWeaver.AI  395909c22d3042d4b23fb055337db63b  (2 compiler builds)\n"}, True),
]


def self_test() -> int:
    failures = []
    for name, files, should_pass in SELF_TESTS:
        found = check(files)
        if (not found) != should_pass:
            verdict = "PASSED" if not found else f"FAILED ({found[0].splitlines()[0]})"
            failures.append(f"  {name}: {verdict} but should {'pass' if should_pass else 'fail'}")
    if failures:
        print(f"\n✗ check-abbreviated-shas self-test: {len(failures)} failure(s)")
        print("\n".join(failures))
        return 1
    print(f"✓ check-abbreviated-shas self-test: {len(SELF_TESTS)} cases, both directions "
          "(the defect, a deliberate history line, three false-positive shapes, and the vacuous ones)")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--root", default=".")
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()

    if args.self_test:
        return self_test()

    root = pathlib.Path(args.root)
    files = load(root)
    findings = check(files)
    if findings:
        print(f"::error::{len(findings)} dangling abbreviated sha(s):")
        for finding in findings:
            print(f"  - {finding}")
        return 1
    shas = sum(len(set(FULL.findall(t))) for t in files.values())
    shorts = sum(1 for t in files.values() for line in t.splitlines()
                 for m in SHORT.findall(line) if HAS_LETTER.search(m))
    print(f"check-abbreviated-shas: {shorts} abbreviation(s) across {len(files)} workflow file(s) "
          f"all resolve against {shas} full sha(s), root={root}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
