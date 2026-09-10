#!/usr/bin/env python3
"""check-content-ci-path: fail closed when content CI leaves GitSync's publish path."""

from __future__ import annotations

import argparse
import tempfile
from pathlib import Path, PurePosixPath


DEFAULT_PATH = ".github/workflows/ci.yml"


def check(root: Path, expected: str = DEFAULT_PATH) -> list[str]:
    relative = PurePosixPath(expected)
    if relative.is_absolute() or ".." in relative.parts or not relative.parts:
        return [f"invalid content-CI path '{expected}'"]
    # Resolve each component by exact directory-entry name. Path.is_file() follows the host file
    # system's case rules, which would let CI.yml satisfy ci.yml on a default macOS checkout even
    # though GitHub's workflow path and the webhook discriminator are case-sensitive.
    candidate = root
    for part in relative.parts:
        try:
            candidate = next(entry for entry in candidate.iterdir() if entry.name == part)
        except (FileNotFoundError, NotADirectoryError, StopIteration):
            break
    else:
        if candidate.is_file():
            return []
    return [
        f"{expected} is missing — GitSync accepts BuildCompletion only from that exact workflow "
        "path; moving or renaming content CI would leave every installation on the previous build"
    ]


def self_test() -> int:
    failures: list[str] = []
    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        workflows = root / ".github" / "workflows"
        workflows.mkdir(parents=True)

        if not check(root):
            failures.append("a missing content workflow passed")
        (workflows / "other.yml").write_text("name: unrelated\n", encoding="utf-8")
        if not check(root):
            failures.append("an unrelated workflow substituted for content CI")
        (workflows / "CI.yml").write_text("name: wrong case\n", encoding="utf-8")
        if not check(root):
            failures.append("a case-mismatched Git path passed")
        (workflows / "CI.yml").unlink()
        (workflows / "ci.yml").mkdir()
        if not check(root):
            failures.append("a directory at the expected path passed")
        (workflows / "ci.yml").rmdir()
        (workflows / "ci.yml").write_text("name: content CI\n", encoding="utf-8")
        if check(root):
            failures.append("the exact content workflow failed")
        if not check(root, "../outside.yml"):
            failures.append("a path outside the repository passed")

    if failures:
        for failure in failures:
            print(f"::error::{failure}")
        return 1
    print("content-CI path guard: 6 assertions passed")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", default=".")
    parser.add_argument("--expected", default=DEFAULT_PATH)
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        return self_test()

    failures = check(Path(args.root), args.expected)
    for failure in failures:
        print(f"::error file={args.expected}::{failure}")
    if failures:
        return 1
    print(f"content-CI publish path: {args.expected} exists")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
