#!/usr/bin/env python3
"""Assert that every rule block the fleet shares is identical in every repo that carries it.

WHY THIS EXISTS
---------------
Each repo's ``CLAUDE.md`` is a one-line ``@AGENTS.md`` include, so ``AGENTS.md`` is not reference
material someone consults when they remember to — it is the instruction file loaded into every
agent's context, in that repo, on every session. Divergence between those files means agents
BEHAVE differently per repo, and nothing in the estate could see it.

MeshWeaver.Plugins#705 measured the result across seven repos and asked for a decision. The
decision (maintainer, 2026-08-30) was: add a drift gate — the same shape as the i18n mirror guard,
which compares the localisation catalogues by VALUE rather than merely by key.

WHAT IT ASSERTS
---------------
For every block in ``.github/shared-rules.json``:

  1. PRESENCE  — every repo listed in ``required-in`` carries the block markers. A repo that drops
     them, or that was missed in a rollout, is RED and named. This is the half a per-repo
     self-check structurally cannot do: a repo with no pull request never runs its own gate, so
     "six of seven repos were updated" looks exactly like "all of them were".
  2. SAMENESS  — the marked region is byte-identical to the hub's, after ONE normalisation
     (markdown soft-wrap collapsed) and with each declared slot's content masked out.
  3. WELL-FORMEDNESS — markers balance, no block is opened twice in a file, every slot the text
     declares is one the manifest declares, and every slot the manifest declares appears exactly
     once. A marker typo cannot silently reduce the gate to checking nothing.

FAIL-CLOSED
-----------
Every unreadable input is a FAILURE, never a pass:

  * a repo whose ``AGENTS.md`` cannot be fetched  -> red, naming the HTTP status and what it means
  * a hub whose own copy of a block is missing    -> red (nothing to compare against)
  * a manifest that lists zero blocks             -> red (a gate with no subject is decorative)
  * an unparseable manifest                       -> red

There is no code path that reports success on absent evidence, and no flag that turns one on.

USAGE
-----
    check-shared-rules.py                              # fetch every repo from GitHub
    check-shared-rules.py --local Systemorph/MeshWeaver=AGENTS.md
                                                       # ...but read THIS repo from the checkout,
                                                       # so a PR is gated on its own diff
    check-shared-rules.py --self-test                  # prove the gate fires on its defects

``--local`` is not a skip: the file still has to be present, well-formed and identical. It exists
so a pull request is judged on the tree it proposes rather than on the branch it will replace.
"""

from __future__ import annotations

import argparse
import difflib
import json
import os
import re
import sys
import urllib.error
import urllib.request
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
MANIFEST = REPO_ROOT / ".github" / "shared-rules.json"
AGENTS_FILE = "AGENTS.md"

BLOCK_BEGIN = re.compile(r"<!--\s*shared-rule:begin\s+([A-Za-z0-9_-]+)\s*-->")
BLOCK_END = re.compile(r"<!--\s*shared-rule:end\s+([A-Za-z0-9_-]+)\s*-->")
SLOT = re.compile(r"<!--\s*slot:([A-Za-z0-9_-]+)\s*-->(.*?)<!--\s*/slot\s*-->", re.DOTALL)
SLOT_OPEN = re.compile(r"<!--\s*slot:([A-Za-z0-9_-]+)\s*-->")
SLOT_CLOSE = re.compile(r"<!--\s*/slot\s*-->")


class Problem(Exception):
    """A drift or a malformed input. Carries the sentence the run should print."""


# ───────────────────────────── extraction + canonicalisation ─────────────────────────────


def extract_block(text: str, block_id: str) -> str | None:
    """Return the raw region between this block's markers, or None if it is absent.

    Raises Problem when the markers are present but malformed — an unbalanced or duplicated
    marker must never degrade to "absent", because "absent" is a state some repos legitimately
    have (they are not listed for that block) while "malformed" is always a defect.
    """
    opens = [m for m in BLOCK_BEGIN.finditer(text) if m.group(1) == block_id]
    closes = [m for m in BLOCK_END.finditer(text) if m.group(1) == block_id]
    if not opens and not closes:
        return None
    if len(opens) != 1 or len(closes) != 1:
        raise Problem(
            f"block '{block_id}' has {len(opens)} begin marker(s) and {len(closes)} end "
            f"marker(s); expected exactly one of each"
        )
    if closes[0].start() < opens[0].end():
        raise Problem(f"block '{block_id}' has its end marker before its begin marker")
    return text[opens[0].end():closes[0].start()]


def canonicalise(region: str, block_id: str, declared_slots: list[str]) -> tuple[str, dict[str, str]]:
    """Mask the declared slots, then collapse markdown soft-wrap.

    Returns (comparable text, {slot name: its content}). The slot NAME survives into the
    comparable text as ``<<name>>``, so a slot that moves, disappears or is renamed is drift
    exactly like a changed word — only the slot's CONTENT is exempt.
    """
    seen: list[str] = []
    # Remove the well-formed pairs; anything of either shape still standing is unbalanced.
    # (Do NOT also strip the leftovers before testing — that was this check's first form, and it
    # made the open-marker half of the condition dead: it deleted the very markers it then looked
    # for, so a stray <!--slot:x--> with no close reached the comparison unreported.)
    residual = SLOT.sub("", region)
    stray_open = SLOT_OPEN.search(residual)
    stray_close = SLOT_CLOSE.search(residual)
    if stray_open or stray_close:
        which = f"<!--slot:{stray_open.group(1)}--> with no <!--/slot-->" if stray_open \
            else "<!--/slot--> with no opening <!--slot:…-->"
        raise Problem(f"block '{block_id}' has an unbalanced slot marker: {which}")

    contents: dict[str, str] = {}

    def mask(m: re.Match[str]) -> str:
        name = m.group(1)
        if name not in declared_slots:
            raise Problem(
                f"block '{block_id}' uses slot '{name}', which the manifest does not declare "
                f"(declared: {declared_slots or 'none'}). A slot is an exemption from the "
                f"comparison, so it may only be created in .github/shared-rules.json — never "
                f"by editing AGENTS.md."
            )
        if name in seen:
            raise Problem(f"block '{block_id}' uses slot '{name}' more than once")
        seen.append(name)
        contents[name] = re.sub(r"\s+", " ", m.group(2)).strip()
        return f"<<{name}>>"

    masked = SLOT.sub(mask, region)
    missing = [s for s in declared_slots if s not in seen]
    if missing:
        raise Problem(
            f"block '{block_id}' is missing the declared slot(s) {missing}. Every declared slot "
            f"must appear exactly once, or the comparison silently covers less than it claims."
        )
    return re.sub(r"\s+", " ", masked).strip(), contents


def word_diff(hub_text: str, spoke_text: str, hub_label: str, spoke_label: str) -> list[str]:
    """A word-level diff, so the report names the drift instead of dumping both paragraphs."""
    out: list[str] = []
    sm = difflib.SequenceMatcher(None, hub_text.split(), spoke_text.split())
    for tag, i1, i2, j1, j2 in sm.get_opcodes():
        if tag == "equal":
            continue
        hub_frag = " ".join(hub_text.split()[i1:i2])
        spoke_frag = " ".join(spoke_text.split()[j1:j2])
        out.append(f"      {hub_label:<34} {hub_frag or '(nothing)'!r}")
        out.append(f"      {spoke_label:<34} {spoke_frag or '(nothing)'!r}")
        out.append("")
    return out


# ───────────────────────────────────── fetching ──────────────────────────────────────────


def fetch_agents_md(repo: str, token: str | None) -> str:
    """Read a repo's AGENTS.md from its default branch. Any failure is fatal, never a pass."""
    url = f"https://api.github.com/repos/{repo}/contents/{AGENTS_FILE}"
    req = urllib.request.Request(url)
    req.add_header("Accept", "application/vnd.github.raw")
    req.add_header("X-GitHub-Api-Version", "2022-11-28")
    req.add_header("User-Agent", "meshweaver-shared-rules-gate")
    if token:
        req.add_header("Authorization", f"Bearer {token}")
    try:
        with urllib.request.urlopen(req, timeout=30) as resp:
            return resp.read().decode("utf-8")
    except urllib.error.HTTPError as e:
        hint = {
            401: "the token is invalid or expired",
            403: "the token is valid but lacks contents:read on this repo (or is rate-limited)",
            404: "the repo, the branch or AGENTS.md does not exist — OR the token cannot see a "
                 "private repo, which GitHub reports as 404 rather than 403",
        }.get(e.code, "unexpected status")
        raise Problem(
            f"could not read {repo}/{AGENTS_FILE}: HTTP {e.code} ({hint}). This gate compares "
            f"what the repos actually contain, so an unreadable repo is a FAILURE — reporting "
            f"'no drift' here would be reporting on evidence we do not have."
        ) from e
    except urllib.error.URLError as e:
        raise Problem(f"could not reach github.com for {repo}: {e.reason}") from e


# ─────────────────────────────────────── the gate ────────────────────────────────────────


def load_manifest(path: Path) -> dict:
    try:
        manifest = json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError:
        raise Problem(f"{path} does not exist — there is nothing for this gate to assert.")
    except json.JSONDecodeError as e:
        raise Problem(f"{path} is not valid JSON: {e}")
    blocks = manifest.get("blocks") or []
    if not blocks:
        raise Problem(
            f"{path} declares no blocks. A gate with no subject passes unconditionally, which is "
            f"indistinguishable from a gate that works — refusing to report success."
        )
    return manifest


def run(sources: dict[str, str], manifest: dict) -> tuple[list[str], list[str]]:
    """Compare every block across every repo. Returns (failures, report lines)."""
    failures: list[str] = []
    report: list[str] = []
    known_ids = {b["id"] for b in manifest["blocks"]}

    for block in manifest["blocks"]:
        bid = block["id"]
        hub = block["hub"]
        required = block["required-in"]
        slots = block.get("slots", [])
        report.append(f"── {bid}  (hub: {hub}; {len(required)} repos; slots: {slots or 'none'})")

        canon: dict[str, str] = {}
        for repo in required:
            text = sources.get(repo)
            if text is None:
                failures.append(f"[{bid}] {repo}: AGENTS.md was not read")
                continue
            try:
                region = extract_block(text, bid)
                if region is None:
                    failures.append(
                        f"[{bid}] {repo}: the block is MISSING. This repo is listed in "
                        f".github/shared-rules.json as carrying it, and its AGENTS.md has no "
                        f"<!-- shared-rule:begin {bid} --> … <!-- shared-rule:end {bid} --> "
                        f"markers. Either the rollout missed this repo, or someone removed the "
                        f"block (or its markers) here."
                    )
                    continue
                canon[repo], _ = canonicalise(region, bid, slots)
            except Problem as e:
                failures.append(f"[{bid}] {repo}: {e}")

        # Compare.
        if hub == "peers":
            groups: dict[str, list[str]] = {}
            for repo, txt in canon.items():
                groups.setdefault(txt, []).append(repo)
            if len(groups) > 1:
                biggest = max(groups.values(), key=len)
                ref = canon[biggest[0]]
                for txt, repos in groups.items():
                    if repos is biggest:
                        continue
                    for repo in repos:
                        detail = "\n".join(word_diff(ref, txt, f"{biggest[0]} (majority):", f"{repo}:"))
                        failures.append(
                            f"[{bid}] {repo}: DRIFTED from its peers. This block has no canonical "
                            f"home yet (hub: peers), so the gate holds the repos to agreeing with "
                            f"each other; it cannot tell you which side is right.\n{detail}"
                        )
            elif canon:
                report.append(f"     ✓ all {len(canon)} peers agree")
        else:
            hub_text = canon.get(hub)
            if hub_text is None:
                failures.append(
                    f"[{bid}] {hub}: the HUB's own copy is missing or malformed, so there is "
                    f"nothing authoritative to compare the {len(required) - 1} spokes against. "
                    f"Refusing to report success."
                )
            else:
                agreed = 0
                for repo, txt in canon.items():
                    if repo == hub:
                        continue
                    if txt == hub_text:
                        agreed += 1
                        continue
                    detail = "\n".join(word_diff(hub_text, txt, f"{hub} (hub):", f"{repo}:"))
                    failures.append(
                        f"[{bid}] {repo}: DRIFTED from the hub {hub}.\n{detail}"
                    )
                if agreed:
                    report.append(f"     ✓ {agreed} spoke(s) match the hub")

    # A marker for a block nobody declared is a typo that would otherwise check nothing.
    for repo, text in sources.items():
        for m in BLOCK_BEGIN.finditer(text):
            if m.group(1) not in known_ids:
                failures.append(
                    f"[{m.group(1)}] {repo}: AGENTS.md opens a shared-rule block that "
                    f".github/shared-rules.json does not declare. Nothing compares it, so it "
                    f"reads as protected while being unchecked — add it to the manifest or "
                    f"remove the markers."
                )
    return failures, report


# ───────────────────────────────────── self-test ─────────────────────────────────────────

_HUB = """intro
<!-- shared-rule:begin demo -->
**Rule.** The home is <!--slot:home-->core's tree<!--/slot--> — and that is that.
<!-- shared-rule:end demo -->
outro
"""
_MANIFEST = {
    "blocks": [
        {"id": "demo", "hub": "o/hub", "required-in": ["o/hub", "o/spoke"], "slots": ["home"]}
    ]
}


def self_test() -> int:
    """Prove the gate FIRES on each defect and stays SILENT on each legitimate difference.

    An unproven gate is no gate: until a check is shown to go red on its defect AND green on its
    fix, a green run is only evidence that it ran. Same posture as check-workflow-shell.py's
    self-test and affected-modules.py --self-test in MeshWeaver.Plugins.
    """
    cases: list[tuple[str, str, bool]] = [
        # (name, spoke text, expect_failure)
        ("identical copy", _HUB.replace("core's tree", "core's tree"), False),
        (
            "slot CONTENT differs (the declared exemption)",
            _HUB.replace("core's tree", "the `Widgets/` module folder"),
            False,
        ),
        (
            "re-wrapped at a different column",
            "intro\n<!-- shared-rule:begin demo -->\n**Rule.** The home is\n"
            "<!--slot:home-->core's\ntree<!--/slot--> —\nand that is\nthat.\n"
            "<!-- shared-rule:end demo -->\nouter\n",
            False,
        ),
        ("a word changed", _HUB.replace("and that is that", "and that is all"), True),
        ("emphasis dropped", _HUB.replace("**Rule.**", "Rule."), True),
        ("block absent entirely", "intro\nno markers here\nouter\n", True),
        ("begin marker removed", _HUB.replace("<!-- shared-rule:begin demo -->\n", ""), True),
        ("end marker removed", _HUB.replace("<!-- shared-rule:end demo -->\n", ""), True),
        ("block opened twice", _HUB + _HUB, True),
        (
            "slot removed (text inlined)",
            _HUB.replace("<!--slot:home-->core's tree<!--/slot-->", "core's tree"),
            True,
        ),
        (
            "undeclared slot invented in AGENTS.md",
            _HUB.replace("and that is that", "and <!--slot:sneaky-->anything at all<!--/slot-->"),
            True,
        ),
        (
            "slot renamed",
            _HUB.replace("slot:home", "slot:elsewhere"),
            True,
        ),
        (
            "text moved into the slot to dodge the compare",
            _HUB.replace(
                "<!--slot:home-->core's tree<!--/slot--> — and that is that.",
                "<!--slot:home-->core's tree — and that is something else<!--/slot-->.",
            ),
            True,
        ),
        (
            "an undeclared block is marked up",
            _HUB + "<!-- shared-rule:begin ghost -->x<!-- shared-rule:end ghost -->\n",
            True,
        ),
        # The two below are why the balance check does not strip what it is about to look for.
        # An earlier form of it did, which made the open-marker half dead: a stray opener was
        # deleted and then searched for, so it was never reported.
        ("slot opener with no closer", _HUB.replace("<!--/slot-->", ""), True),
        ("slot closer with no opener", _HUB.replace("<!--slot:home-->", ""), True),
    ]

    bad = 0
    for name, spoke, expect_failure in cases:
        failures, _ = run({"o/hub": _HUB, "o/spoke": spoke}, _MANIFEST)
        fired = bool(failures)
        ok = fired == expect_failure
        verdict = "ok" if ok else "WRONG"
        want = "fire" if expect_failure else "stay silent"
        print(f"  [{verdict:5}] should {want:11} — {name}")
        if not ok:
            bad += 1
            for f in failures:
                print(f"           {f.splitlines()[0]}")

    # The manifest guards themselves.
    for name, manifest, expect_failure in [
        ("empty manifest is refused", {"blocks": []}, True),
    ]:
        try:
            tmp = Path(os.environ.get("RUNNER_TEMP", "/tmp")) / "shared-rules-selftest.json"
            tmp.write_text(json.dumps(manifest), encoding="utf-8")
            load_manifest(tmp)
            fired = False
        except Problem:
            fired = True
        ok = fired == expect_failure
        print(f"  [{'ok' if ok else 'WRONG':5}] should fire        — {name}")
        if not ok:
            bad += 1

    # The hub itself must be checkable.
    failures, _ = run({"o/spoke": _HUB}, _MANIFEST)
    ok = bool(failures)
    print(f"  [{'ok' if ok else 'WRONG':5}] should fire        — hub unreadable (nothing to compare against)")
    if not ok:
        bad += 1

    if bad:
        print(f"\n::error::{bad} self-test case(s) behaved wrongly — this gate's verdict on the "
              f"real tree cannot be trusted until they pass.")
        return 1
    print(f"\nSelf-test: {len(cases) + 2} cases, all correct.")
    return 0


# ──────────────────────────────────────── main ───────────────────────────────────────────


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--self-test", action="store_true", help="prove the gate is non-vacuous, then exit")
    ap.add_argument(
        "--local",
        action="append",
        default=[],
        metavar="REPO=PATH",
        help="read REPO's AGENTS.md from PATH instead of GitHub (used for the repo the gate runs "
             "in, so a PR is judged on its own diff rather than on the branch it replaces)",
    )
    ap.add_argument("--manifest", default=str(MANIFEST))
    args = ap.parse_args()

    if args.self_test:
        return self_test()

    try:
        manifest = load_manifest(Path(args.manifest))
    except Problem as e:
        print(f"::error::{e}")
        return 1

    local: dict[str, Path] = {}
    for spec in args.local:
        if "=" not in spec:
            print(f"::error::--local expects REPO=PATH, got {spec!r}")
            return 1
        repo, path = spec.split("=", 1)
        local[repo] = Path(path)

    token = os.environ.get("GITHUB_TOKEN") or os.environ.get("GH_TOKEN")
    repos = manifest["repos"]

    print(f"Shared rule blocks: {len(manifest['blocks'])} across {len(repos)} repos")
    print(f"Reading AGENTS.md from {len(repos)} repos "
          f"({len(local)} from this checkout, {len(repos) - len(local)} from GitHub)\n")

    sources: dict[str, str] = {}
    read_failures: list[str] = []
    for repo in repos:
        try:
            if repo in local:
                p = local[repo]
                if not p.is_file():
                    raise Problem(f"--local path {p} does not exist")
                sources[repo] = p.read_text(encoding="utf-8")
                print(f"  {repo:38} <- {p} (this checkout)")
            else:
                sources[repo] = fetch_agents_md(repo, token)
                print(f"  {repo:38} <- github.com (default branch)")
        except Problem as e:
            read_failures.append(f"{repo}: {e}")

    print()
    failures: list[str] = []
    report: list[str] = []
    if read_failures:
        failures.extend(read_failures)
    if sources:
        f, report = run(sources, manifest)
        failures.extend(f)

    for line in report:
        print(line)

    summary = Path(os.environ.get("GITHUB_STEP_SUMMARY", os.devnull))
    if not failures:
        msg = (f"All {len(manifest['blocks'])} shared rule block(s) are identical everywhere they "
               f"are carried, across {len(sources)} repos.")
        print(f"\n✅ {msg}")
        with summary.open("a", encoding="utf-8") as fh:
            fh.write(f"### ✅ Shared rule blocks\n\n{msg}\n")
        return 0

    print(f"\n❌ {len(failures)} problem(s):\n")
    for f in failures:
        print(f"::error::{f.splitlines()[0]}")
        for line in f.splitlines()[1:]:
            print(line)
        print()
    with summary.open("a", encoding="utf-8") as fh:
        fh.write("### ❌ Shared rule block drift\n\n")
        fh.write("`AGENTS.md` is loaded into every agent session in its repo, so these blocks "
                 "differing means agents behave differently per repo.\n\n")
        for f in failures:
            fh.write(f"- {f.splitlines()[0]}\n")
        fh.write("\nThe register is `.github/shared-rules.json` in Systemorph/MeshWeaver; the "
                 "rationale is `Doc/Architecture/SharedRuleBlocks`.\n")
    return 1


if __name__ == "__main__":
    sys.exit(main())
