#!/usr/bin/env python3
"""check-ladder-coverage.py --index <file> --bundles-dir <dir>

The compatibility ladder must link EVERY module the sealed `plugins` publication carries
(MeshWeaver#7409). The publication states its own module set in `modules/_index`; `published-modules`
(main-cd.yml) hands that index on beside the bundles as `published-module-set.index`. This compares
the two and is RED when they differ:

  * a module the index lists but no bundle under <bundles-dir> provides — the ladder would report
    green over a module it never linked (the shape #7409 found: four of the fleet's modules linked,
    every other one unchecked);
  * a bundle under <bundles-dir> the index does not list — the ladder would be judging bytes the
    publication never sealed;
  * an empty or absent index — a verdict over no modules is not a verdict.

Names compare case-insensitively on the bundle FILE NAME (`<package>.module.nupkg`), which is how the
publication's index names them. Exit 0 prints the coverage as "N of N".
"""

import argparse
from pathlib import Path
import sys


def read_index(path: Path) -> list[str]:
    if not path.is_file():
        raise SystemExit(f"::error::the publication's module index '{path}' is absent — the ladder's "
                         "coverage cannot be measured, so no verdict can be given")
    names = [line.strip() for line in path.read_text(encoding="utf-8").splitlines() if line.strip()]
    if not names:
        raise SystemExit(f"::error::the publication's module index '{path}' lists no module — a ladder "
                         "over no modules has measured nothing")
    return names


def coverage_problems(index: list[str], bundles: list[Path]) -> list[str]:
    listed = {n.lower(): n for n in index}
    present = {b.name.lower(): b for b in bundles}
    problems = []
    for key in sorted(set(listed) - set(present)):
        problems.append(f"'{listed[key]}' is in the sealed publication but NOT on the ladder — "
                        "no bundle for it was linked, so a green ladder would say nothing about it")
    for key in sorted(set(present) - set(listed)):
        problems.append(f"'{present[key].name}' is on the ladder but NOT in the sealed publication's "
                        "index — the ladder would judge bytes the publication never sealed")
    return problems


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--index", required=True, type=Path)
    parser.add_argument("--bundles-dir", required=True, type=Path)
    args = parser.parse_args()
    index = read_index(args.index)
    if not args.bundles_dir.is_dir():
        print(f"::error::bundles directory '{args.bundles_dir}' does not exist — nothing was linked")
        return 1
    bundles = sorted(args.bundles_dir.rglob("*.module.nupkg"))
    problems = coverage_problems(index, bundles)
    for p in problems:
        print(f"::error::LADDER COVERAGE (#7409): {p}")
    if problems:
        return 1
    print(f"ladder coverage: {len(bundles)} of {len(index)} module(s) the sealed publication carries — "
          + " ".join(sorted(b.name for b in bundles)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
