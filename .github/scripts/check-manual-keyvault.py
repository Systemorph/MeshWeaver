#!/usr/bin/env python3
"""check-manual-keyvault.py — no doc, runbook, skill or script tells a human to read or write a Key Vault secret by hand.

(The name on this first line is load-bearing: node-repo-validate.yml fetches this file at platform-ref and
refuses a body whose first 400 bytes do not name it.)

WHY THIS EXISTS (policy `secrets-write-only-entry`, Doc/Architecture/SecretsWriteOnlyEntry)
--------------------------------------------------------------------------------------------
Every secret the fleet uses is entered through a GUI in the app that owns it. That GUI is WRITE-ONLY:
it sets or replaces a value and shows only the value's status and fingerprint. The vault is written
by one governed identity that can never read a value back, and read only by the pods. So a sentence
such as `az keyvault secret set --vault-name … --value …` in a runbook is not documentation. It is
an instruction to go around the design, and every such line in the tree once trained the next person
(or agent) to do exactly that. This gate stops new ones from appearing.

THE RULE
--------
A tracked text file may not contain a Key Vault secret DATA-PLANE command:

    az keyvault secret set | show | download | set-attributes | delete | purge | recover | backup | restore
    az keyvault set-policy … --secret-permissions

unless one of these holds:

  * the file matches a glob in the allow file (default `.github/manual-keyvault.allow`), one per line:
    `<glob>  — <reason>`. That is where the GOVERNED implementation lives (the operator's
    `hosting-kv-*` verbs, which run as the writer identity), plus its tests and this gate itself;
  * the line, or one of the 3 lines above it, carries the marker WITH ITS REASON:
    `kv-break-glass: <why no GUI can do this>` (at least 20 characters). The marker is for a documented
    BREAK-GLASS procedure; a bare marker exempts nothing.

A command split over lines with a trailing backslash is joined first, so a continuation cannot hide one.

An allow entry that matches no offending file is STALE and fails too, so the list can only shrink.

USAGE
-----
  check-manual-keyvault.py [--root DIR] [--allow FILE]   gate the tree at DIR (default: cwd)
  check-manual-keyvault.py --self-test                   prove the gate fires and stays silent

Exit 1 on any violation. Each violation is a `::error` annotation carrying the file and the line.
"""
from __future__ import annotations

import argparse
import contextlib
import fnmatch
import io
import re
import subprocess
import sys
import tempfile
from pathlib import Path

PATTERN = re.compile(
    r"az\s+keyvault\s+(?:secret\s+(?:set-attributes|set|show|download|delete|purge|recover|backup|restore)\b"
    r"|set-policy\b[^\n]*--secret-permissions)")
MARKER = "kv-break-glass"
MARKER_WINDOW = 3
# The marker exempts a command only when it carries its REASON: `kv-break-glass: <why no GUI can do
# this>`, at least 20 characters of it. A bare marker exempts nothing.
MARKER_WITH_REASON = re.compile(re.escape(MARKER) + r"\s*:\s*\S.{19,}")


def logical_lines(lines: list[str]) -> list[tuple[int, str]]:
    """Joins shell/Markdown continuations (a trailing backslash) into ONE logical line, so a command
    split as `az keyvault secret \\` + `set …` is seen whole. Returns (index of its first physical
    line, joined text)."""
    out: list[tuple[int, str]] = []
    i = 0
    while i < len(lines):
        start, parts = i, [lines[i]]
        while parts[-1].rstrip().endswith("\\") and i + 1 < len(lines):
            parts[-1] = parts[-1].rstrip()[:-1]
            i += 1
            parts.append(lines[i])
        out.append((start, " ".join(parts)))
        i += 1
    return out
TEXT_SUFFIXES = {".md", ".sh", ".bash", ".py", ".yml", ".yaml", ".ps1", ".cs", ".json", ".bicep",
                 ".txt", ".tpl", ".razor", ".ts", ".js", ""}
DEFAULT_ALLOW = ".github/manual-keyvault.allow"


def tracked_files(root: Path) -> list[str]:
    try:
        out = subprocess.run(["git", "-C", str(root), "ls-files", "-z"], check=True,
                             capture_output=True).stdout.decode("utf-8", "replace")
        return [p for p in out.split("\0") if p]
    except (subprocess.CalledProcessError, FileNotFoundError):
        return [str(p.relative_to(root)) for p in root.rglob("*") if p.is_file() and ".git" not in p.parts]


def read_allow(path: Path) -> list[tuple[str, int]]:
    entries = []
    if not path.exists():
        return entries
    for number, raw in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        glob = line.split()[0]
        if "—" not in line and " - " not in line:
            print(f"::error file={path},line={number}::allow entry '{glob}' carries no reason — write `<glob>  — <why this may hold a vault command>`")
            entries.append((glob, -number))
            continue
        entries.append((glob, number))
    return entries


def scan(root: Path, allow_file: Path) -> int:
    allow = read_allow(allow_file)
    errors = sum(1 for _, n in allow if n < 0)
    used: set[str] = set()
    for rel in tracked_files(root):
        suffix = Path(rel).suffix.lower()
        if suffix not in TEXT_SUFFIXES:
            continue
        path = root / rel
        try:
            text = path.read_text(encoding="utf-8")
        except (UnicodeDecodeError, OSError):
            continue
        if "keyvault" not in text:
            continue
        lines = text.splitlines()
        hits = []
        for start, logical in logical_lines(lines):
            if not PATTERN.search(logical):
                continue
            window = lines[max(0, start - MARKER_WINDOW): start + 1]
            if any(MARKER_WITH_REASON.search(w) for w in window):
                continue
            hits.append(start + 1)
        if not hits:
            continue
        matched = [g for g, n in allow if n > 0 and fnmatch.fnmatch(rel, g)]
        if matched:
            used.update(matched)
            continue
        for number in hits:
            print(f"::error file={rel},line={number}::a manual Key Vault secret command. Secrets are entered through the "
                  f"write-only GUI of the app that owns them and written by the governed writer identity "
                  f"(Doc/Architecture/SecretsWriteOnlyEntry). Point the reader at that GUI. Only a documented "
                  f"break-glass procedure may keep the command, marked `{MARKER}: <why no GUI can do this>` on or "
                  f"up to {MARKER_WINDOW} lines above it.")
            errors += 1
    for glob, number in allow:
        if number > 0 and glob not in used:
            print(f"::error file={allow_file},line={number}::stale allow entry '{glob}' — no tracked file it matches holds a "
                  f"vault command any more. Delete the line: the list only shrinks.")
            errors += 1
    if errors:
        print(f"check-manual-keyvault: {errors} violation(s)")
        return 1
    print(f"check-manual-keyvault: clean — no manual Key Vault secret command outside {len(used)} allowed path glob(s) "
          f"and marked break-glass procedures")
    return 0


def self_test() -> int:
    failures = 0

    def case(name: str, files: dict[str, str], allow: str, expect: int) -> None:
        nonlocal failures
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            for rel, body in files.items():
                (root / rel).parent.mkdir(parents=True, exist_ok=True)
                (root / rel).write_text(body, encoding="utf-8")
            (root / DEFAULT_ALLOW).parent.mkdir(parents=True, exist_ok=True)
            (root / DEFAULT_ALLOW).write_text(allow, encoding="utf-8")
            # The synthetic trees' own ::error lines would read as real annotations in CI.
            with contextlib.redirect_stdout(io.StringIO()):
                got = scan(root, root / DEFAULT_ALLOW)
        verdict = "ok" if got == expect else "FAIL"
        if got != expect:
            failures += 1
        print(f"  self-test {verdict}: {name} (exit {got}, expected {expect})")

    case("a runbook telling a human to set a secret FIRES",
         {"docs/runbook.md": "Then run:\n\naz keyvault secret set --vault-name V --name x --value y\n"}, "", 1)
    case("a value READ (show --query value) FIRES",
         {"docs/runbook.md": "PW=$(az keyvault secret show --vault-name V --name pw --query value -o tsv)\n"}, "", 1)
    case("a download FIRES", {"scripts/x.sh": "az keyvault secret download --vault-name V --name y --file f\n"}, "", 1)
    case("an access-policy grant of secret permissions FIRES",
         {"docs/a.md": "az keyvault set-policy -n V --object-id o --secret-permissions get list\n"}, "", 1)
    case("a marked break-glass procedure WITH its reason is SILENT",
         {"docs/b.md": "<!-- kv-break-glass: the control plane itself is down, so no GUI can file the write -->\naz keyvault secret set --vault-name V --name x --file f\n"}, "", 0)
    case("a BARE marker (no reason) exempts nothing",
         {"docs/b.md": "# kv-break-glass\naz keyvault secret set --vault-name V --name x --file f\n"}, "", 1)
    case("a marker with a too-short reason exempts nothing",
         {"docs/b.md": "# kv-break-glass: because\naz keyvault secret set --vault-name V --name x --file f\n"}, "", 1)
    case("a marker further than 3 lines up does not count",
         {"docs/b.md": "kv-break-glass: the control plane itself is down, so no GUI can file it\n\n\n\n\naz keyvault secret set --vault-name V --name x --file f\n"}, "", 1)
    case("a command split with a line continuation FIRES",
         {"docs/c.md": "az keyvault secret \\\n  set --vault-name V --name x --value y\n"}, "", 1)
    case("a command split after `az keyvault` FIRES",
         {"scripts/c.sh": "az keyvault \\\n  secret show --vault-name V \\\n  --name pw --query value\n"}, "", 1)
    case("the governed implementation under an allowed glob is SILENT",
         {"deploy/bin/hosting-kv-set": "az keyvault secret set --vault-name \"$v\" --name \"$o\" --file f\n"},
         "deploy/bin/*  — the governed writer verbs\n", 0)
    case("a STALE allow entry FIRES", {"docs/c.md": "nothing here\n"}, "deploy/bin/*  — the governed writer verbs\n", 1)
    case("an allow entry without a reason FIRES",
         {"deploy/bin/x": "az keyvault secret set --name x --file f\n"}, "deploy/bin/*\n", 1)
    case("metadata-only listing is SILENT",
         {"docs/d.md": "az keyvault secret list --vault-name V --query '[].name'\naz keyvault show -n V\n"}, "", 0)
    if failures:
        print(f"check-manual-keyvault self-test: {failures} case(s) FAILED — the gate cannot be trusted")
        return 1
    print("check-manual-keyvault self-test: every case behaves")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--root", default=".")
    parser.add_argument("--allow", default=None)
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        return self_test()
    root = Path(args.root).resolve()
    allow = Path(args.allow) if args.allow else root / DEFAULT_ALLOW
    return scan(root, allow)


if __name__ == "__main__":
    sys.exit(main())
