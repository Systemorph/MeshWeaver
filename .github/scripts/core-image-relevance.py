#!/usr/bin/env python3
"""Can this core diff change an image main-cd publishes? — core's half of "rebuild only on own inputs".

    python3 .github/scripts/core-image-relevance.py --changed changed.txt --total N
    python3 .github/scripts/core-image-relevance.py --self-test

Policy `image-rebuild-own-inputs` (Doc/Architecture/CdLedgerRecordsFailures → "Rebuild only on the
image's own inputs"): an image is rebuilt only when one of ITS inputs changed. MeshWeaver.Plugins
answers that for the host side (`scripts/portal-image-relevance.py`, which dispatches main-cd with
`rebuild: true`); this file answers it for core's own commits, so a merge that only touches tests,
agent instructions or repository prose no longer mints a five-image set and a fleet roll.

INPUT
-----
`--changed` is a newline-separated list of paths: every changed file since the newest PUBLISHED set,
with the PREVIOUS name of a renamed file listed too (a file renamed out of `src/` changed `src/`).
`--total` is the number of changed files the compare reported. The GitHub compare API returns at
most 300 files, so a `--total` at or above that cap means the list may be incomplete.

VERDICT (stdout, last line): `relevant=true` or `relevant=false`, preceded by one reason line.

FAIL OPEN, ALWAYS
-----------------
`relevant=false` needs a POSITIVE finding that EVERY changed path matches the not-image list below.
An empty, unreadable or possibly-truncated list is `relevant=true`. A missed rebuild ships a stale
image; a spurious one costs runner minutes.

THE NOT-IMAGE LIST — each entry states why no published image can contain it
-----------------------------------------------------------------------------
    clients/react-native/, clients/voice-gateway/
                        standalone clients; no Dockerfile in the set COPYs them
    test/               test projects; no project under src/, tools/, memex/ or deploy/ references
                        them, and none is published
    .claude/ .agents/ .codex/
                        agent skills and harness config — read by agents, never by a build
    AGENTS.md CLAUDE.md Readme.md LICENSE LICENSE-APACHE LICENSE-MIT MeshWeaver.sln.DotSettings
                        repository-root prose and IDE settings; no csproj, props or targets file
                        includes them

Everything else — `src/`, `samples/`, `tools/`, `memex/`, `deploy/`, `scripts/`, `.github/`, the
root build files (`Directory.*`, `*.targets`, `global.json`, `nuget.config`, `MeshWeaver.slnx`) and
any new top-level entry — is relevant. `.github/` stays relevant on purpose: main-cd reads its
scripts while it builds and bakes, so a change there can change what is published.
"""
from __future__ import annotations

import argparse
import re
import sys

COMPARE_FILE_CAP = 300

NOT_IMAGE = re.compile(
    r"^(?:"
    r"clients/(?:react-native|voice-gateway)/"
    r"|test/"
    r"|\.claude/|\.agents/|\.codex/"
    r"|(?:AGENTS\.md|CLAUDE\.md|Readme\.md|LICENSE|LICENSE-APACHE|LICENSE-MIT|MeshWeaver\.sln\.DotSettings)$"
    r")"
)


def decide(paths: list[str], total: int | None) -> tuple[bool, str]:
    # Exact names, never stripped: git permits leading/trailing spaces, and ` test/A.cs` is NOT
    # `test/A.cs` — stripping would turn an unknown path into an excluded one (a fail-closed skip).
    paths = [p for p in paths if p != ""]
    if not paths:
        return True, "no changed paths could be read — failing open"
    if total is None:
        return True, "the compare did not report how many files changed — failing open"
    if total >= COMPARE_FILE_CAP:
        return True, f"the compare reported {total} changed files (cap {COMPARE_FILE_CAP}) — the list may be truncated, failing open"
    relevant = [p for p in paths if not NOT_IMAGE.match(p)]
    if relevant:
        return True, f"{len(relevant)} of {len(paths)} changed path(s) can reach a published image, first: {relevant[0]}"
    return False, f"all {len(paths)} changed path(s) are outside every image's inputs (tests, agent config, root prose, standalone clients)"


def self_test() -> int:
    cases = [
        # (paths, total, expected_relevant, label)
        ([], 0, True, "empty list ⇒ relevant (fail open)"),
        (["test/MeshWeaver.Data.Test/X.cs"], None, True, "unknown total ⇒ relevant (fail open)"),
        (["test/A.cs"] * 3, 300, True, "total at the compare cap ⇒ relevant (possibly truncated)"),
        (["test/MeshWeaver.Data.Test/X.cs", "test/xunit.runner.json"], 2, False, "tests only ⇒ not relevant"),
        ([".claude/skills/ci/SKILL.md", ".agents/x", "AGENTS.md", "CLAUDE.md"], 4, False, "agent config + root prose ⇒ not relevant"),
        (["clients/react-native/App.tsx", "clients/voice-gateway/gw.py"], 2, False, "standalone clients ⇒ not relevant (the pre-existing rule)"),
        (["test/A.cs", "src/MeshWeaver.Data/B.cs"], 2, True, "one src path ⇒ relevant"),
        (["test/A.cs", ".github/scripts/publish-bake-bundles.sh"], 2, True, ".github ⇒ relevant (main-cd reads its scripts)"),
        (["test/A.cs", "Directory.Packages.props"], 2, True, "root build file ⇒ relevant"),
        (["samples/Graph/Data/x.json"], 1, True, "samples ⇒ relevant"),
        (["NEWDIR/file"], 1, True, "unknown top-level entry ⇒ relevant"),
        (["src/MeshWeaver.X/Moved.cs", "test/Moved.cs"], 1, True, "rename out of src (previous name listed) ⇒ relevant"),
        (["AGENTS.md.bak"], 1, True, "a root file merely PREFIXED by a listed name ⇒ relevant (anchored match)"),
        (["clients/react-native-web/x"], 1, True, "a sibling of a listed client ⇒ relevant"),
        ([" test/A.cs"], 1, True, "a leading space is part of the name — never stripped into test/ ⇒ relevant"),
        (["AGENTS.md "], 1, True, "a trailing space is part of the name ⇒ relevant"),
    ]
    failed = 0
    for paths, total, expected, label in cases:
        got, reason = decide(paths, total)
        ok = got == expected
        failed += not ok
        print(f"  {'✓' if ok else '✗'} {label}  [{reason}]")
    print(f"{len(cases) - failed} passed, {failed} failed")
    return 1 if failed else 0


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--changed")
    ap.add_argument("--total", type=int)
    ap.add_argument("--self-test", action="store_true")
    a = ap.parse_args()
    if a.self_test:
        return self_test()
    if not a.changed:
        ap.error("--changed is required")
    try:
        with open(a.changed, encoding="utf-8") as f:
            paths = f.read().splitlines()
    except OSError as e:
        print(f"could not read {a.changed}: {e} — failing open")
        print("relevant=true")
        return 0
    relevant, reason = decide(paths, a.total)
    print(reason)
    print(f"relevant={'true' if relevant else 'false'}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
