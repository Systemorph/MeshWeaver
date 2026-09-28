#!/usr/bin/env python3
"""Fail when a confidential term appears in the tracked tree — in a TEXT file's content or in any file's PATH.

Binary files (images, PDFs, office documents, UTF-16 text) are checked by path only: `git grep -I`
skips their content. A confidential identifier inside a binary artifact is out of this gate's reach
and has to be caught in review.

This repository is public. Client names, client staff names and client infrastructure identifiers
(tenants, subscriptions, vaults, registries, hosts) must never be committed to it. The list of what
counts as confidential is itself confidential, so it is NOT in this repository: it arrives in the
environment variable CONFIDENTIAL_TERMS (the workflow maps the `CONFIDENTIAL_TERMS` secret into it),
one term per line, blank lines and `#` comments ignored, matched as a fixed string,
case-insensitively — as a SUBSTRING by default, or as a whole word when the line is written
`word:<term>` (for a short name that is also the inside of ordinary identifiers).

This script is repository-agnostic: any repository copies it and the workflow step unchanged.

  * Secret absent (a fork, a local run without it): prints a notice and exits 0. There is nothing to
    check against, and saying so is the honest verdict — the gate is the same repository's, and a
    fork cannot merge into it without passing the run that HAS the secret.
  * Secret present: every hit is reported as `path:line` (or `path` for a hit in the path itself)
    and the job fails. 🚨 The matched TERM is never printed — not the term, not the matched line,
    not an index into the list — because the log of a public repository is public too.

`--self-test` drives both arms against a scratch repository with synthetic terms; no secret needed.
"""
from __future__ import annotations

import os
import re
import subprocess
import sys
import tempfile
from pathlib import Path

ENV = "CONFIDENTIAL_TERMS"


def parse_terms(raw: str) -> list[str]:
    terms: list[str] = []
    for line in raw.splitlines():
        term = line.strip()
        if term and not term.startswith("#"):
            terms.append(term)
    return terms


def find_hits(root: str, terms: list[str]) -> tuple[list[str], int]:
    """(hits as `path:line` / `path`, number of tracked files scanned). Raises on a git failure."""
    substrings = [t for t in terms if not t.lower().startswith("word:")]
    words = [t[5:].strip() for t in terms if t.lower().startswith("word:") and t[5:].strip()]
    lowered = [t.lower() for t in substrings]
    word_patterns = [re.compile(r"(?<![0-9A-Za-z_])" + re.escape(w) + r"(?![0-9A-Za-z_])", re.I)
                     for w in words]
    files = subprocess.run(["git", "-C", root, "ls-files", "-z"], check=True,
                           capture_output=True).stdout.decode("utf-8", "surrogateescape")
    paths = [p for p in files.split("\0") if p]
    hits: list[str] = []
    for path in paths:
        if any(t in path.lower() for t in lowered) or any(p.search(path) for p in word_patterns):
            hits.append(f"{path} (file path)")
    seen: set[str] = set()
    for group, extra in ((substrings, []), (words, ["-w"])):
        if not group:
            continue
        with tempfile.NamedTemporaryFile("w", encoding="utf-8", delete=False) as handle:
            handle.write("\n".join(group) + "\n")
            pattern_file = handle.name
        try:
            # -I skips binary files, -F fixed strings, -i case-insensitive, -n line numbers,
            # -w whole words for the `word:` group. The matched line is DISCARDED below — only
            # path and line number leave this function.
            result = subprocess.run(
                ["git", "-C", root, "grep", "-I", "-n", "-i", "-F", "-z", *extra,
                 "-f", pattern_file, "--", "."],
                capture_output=True)
        finally:
            os.unlink(pattern_file)
        if result.returncode not in (0, 1):  # 1 = no match; anything else is a broken instrument
            raise RuntimeError(f"git grep exited {result.returncode}")
        for record in result.stdout.split(b"\n"):
            if not record:
                continue
            # With -z the fields are NUL-separated: path \0 line \0 content.
            parts = record.split(b"\0", 2)
            if len(parts) >= 2:
                # 🚨 Only DIGITS leave as the line number. Verified layout (git 2.50):
                # `path\0line\0content\n`. Should a git ever emit `path\0line:content`, the
                # content is cut here rather than printed — a leak must be impossible, not unlikely.
                line_no = re.match(rb"\d+", parts[1])
                where = parts[0].decode("utf-8", "surrogateescape")
                hit = f"{where}:{line_no.group().decode()}" if line_no else where
                if hit not in seen:
                    seen.add(hit)
                    hits.append(hit)
    return hits, len(paths)


def check(root: str) -> int:
    terms = parse_terms(os.environ.get(ENV, ""))
    if not terms:
        print(f"::notice::{ENV} is not set (a fork, or a run without the secret) — the confidential "
              "term check has nothing to compare against and did not run.")
        return 0
    hits, scanned = find_hits(root, terms)
    if not scanned:
        print("::error::no tracked files were found — the check read nothing, which is not a pass.")
        return 1
    if hits:
        for hit in hits:
            print(f"::error::confidential term found at {hit}")
        print(f"::error::{len(hits)} occurrence(s) of a confidential term in {scanned} tracked files. "
              "Replace them with neutral placeholders (see AGENTS.md, 'Confidential terms'). "
              "The terms themselves are deliberately not printed.")
        return 1
    print(f"confidential terms: {len(terms)} term(s) checked against {scanned} tracked files — none found.")
    return 0


def self_test() -> int:
    failures = 0

    def expect(condition: bool, message: str) -> None:
        nonlocal failures
        print(f"[{'PASS' if condition else 'FAIL'}] {message}")
        if not condition:
            failures += 1
            print(f"::error::self-test: {message}")

    expect(parse_terms("  Acme \n\n# comment\nGLOBEX\n") == ["Acme", "GLOBEX"],
           "terms are trimmed; blank lines and comments are ignored")
    with tempfile.TemporaryDirectory() as scratch:
        def git(*args: str) -> None:
            subprocess.run(["git", "-C", scratch, *args], check=True, capture_output=True)
        git("init", "-q")
        (Path(scratch) / "clean.md").write_text("nothing to see\n", encoding="utf-8")
        (Path(scratch) / "notes.md").write_text("line one\nmeet the aCmE team\nacme again\n",
                                                encoding="utf-8")
        (Path(scratch) / "globex-record.json").write_text("{}\n", encoding="utf-8")
        (Path(scratch) / "blob.bin").write_bytes(b"\x00\x01acme\x00")
        git("add", "-A")
        hits, scanned = find_hits(scratch, ["ACME", "globex"])
        expect(scanned == 4, f"every tracked file is scanned ({scanned})")
        expect("notes.md:2" in hits, f"a content hit is reported as path:line, case-insensitively: {hits}")
        expect("notes.md:3" in hits and len([h for h in hits if h.startswith("notes.md")]) == 2,
               f"EVERY content hit in a file is reported, not only the first: {hits}")
        expect("globex-record.json (file path)" in hits, f"a hit in a file PATH is reported: {hits}")
        expect(not any(h.startswith("blob.bin") for h in hits), "binary files are skipped")
        expect(not any("acme" in h.lower().replace("notes.md", "") for h in hits),
               "no reported hit carries the matched term or line content")
        (Path(scratch) / "words.md").write_text("Unzorblaxed here\nthe Initech team\n", encoding="utf-8")
        git("add", "-A")
        hits, _ = find_hits(scratch, ["word:initech", "word:zorblax"])
        expect(hits == ["words.md:2"],
               f"a `word:` term matches whole words only, never inside an identifier: {hits}")
        hits, _ = find_hits(scratch, ["zorblax"])
        expect(hits == ["words.md:1"], f"a plain term is a substring match: {hits}")
        hits, _ = find_hits(scratch, ["no-such-term"])
        expect(hits == [], "no hit when no term matches")
        saved = os.environ.pop(ENV, None)
        try:
            expect(check(scratch) == 0, "an absent secret skips with a notice (exit 0)")
            os.environ[ENV] = "acme\n"
            expect(check(scratch) == 1, "a present term fails the check")
            os.environ[ENV] = "no-such-term\n"
            expect(check(scratch) == 0, "a clean tree passes")
        finally:
            os.environ.pop(ENV, None)
            if saved is not None:
                os.environ[ENV] = saved
    if failures:
        print(f"::error::self-test: {failures} arm(s) failed")
        return 1
    print("self-test: all arms behaved as documented")
    return 0


def main() -> int:
    if "--self-test" in sys.argv[1:]:
        return self_test()
    root = next((a for a in sys.argv[1:] if not a.startswith("-")), ".")
    return check(root)


if __name__ == "__main__":
    sys.exit(main())
