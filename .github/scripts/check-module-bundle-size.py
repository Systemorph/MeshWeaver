#!/usr/bin/env python3
"""Is a packed module bundle small enough for the registry that will receive it?

    check-module-bundle-size.py --bundle <Module>.module.nupkg --limit 30000000 [--warn-ratio 0.8]
    exit 0 = under the limit (a ::warning:: at or above warn-ratio × limit), breakdown printed
    exit 1 = over the limit — ::error:: with the per-file breakdown, largest first

🚨 WHY (Systemorph/MeshWeaver.Plugins, main run 42641639266, 2026-11-09). Two Mail bundles of
~33.4 MB were packed, inspected, uploaded as artifacts and handed to the registry, every gate green —
and the registry refused both with 413 against its 30,000,000-byte request bound. Earlier runs got an
unlabelled 500 for the same bytes. The size of a bundle is known the moment it is packed; learning it
from the registry, at the very end of a trunk run, after the module's suite, is learning it from
production. This gate states it where the bundle is made, with the files that make it up, so a
bundle growing toward the bound is a WARNING on the pull request that grows it and an ERROR before
anything is handed over.

🚨 THE LIMIT IS THE RECEIVER'S, DECLARED BY THE CALLER — never raised here to make a red go away. The
registry's bound is its server's request-body limit (Kestrel's default MaxRequestBodySize,
30,000,000 bytes, which the registry names in its 413). A bundle over it is fixed by carrying less
(see Doc/Architecture/ModuleClosureAccounting), and a registry that genuinely needs a larger bound
declares it there and in the caller's input together.

The breakdown reads the ZIP's own directory: compressed bytes per entry — what the upload pays — with
the uncompressed size beside it.
"""
from __future__ import annotations

import argparse
import sys
import tempfile
import zipfile
from pathlib import Path

# ONE depth for every verdict, so the lane log reads the same run over run.
TOP = 25


def breakdown(bundle: Path) -> list[tuple[int, int, str]]:
    with zipfile.ZipFile(bundle) as archive:
        rows = [(info.compress_size, info.file_size, info.filename) for info in archive.infolist() if not info.is_dir()]
    return sorted(rows, key=lambda row: (-row[0], row[2]))


def report(rows: list[tuple[int, int, str]], top: int = TOP) -> list[str]:
    lines = [f"  {'compressed':>12}  {'raw':>12}  file"]
    for compressed, raw, name in rows[:top]:
        lines.append(f"  {compressed:>12,}  {raw:>12,}  {name}")
    if len(rows) > top:
        rest = rows[top:]
        lines.append(f"  {sum(r[0] for r in rest):>12,}  {sum(r[1] for r in rest):>12,}  … {len(rest)} more file(s)")
    return lines


def check(bundle: Path, limit: int, warn_ratio: float, out=print) -> int:
    size = bundle.stat().st_size
    rows = breakdown(bundle)
    share = size / limit
    headline = f"{bundle.name}: {size:,} bytes = {share:.0%} of the registry's {limit:,}-byte upload bound ({len(rows)} files)"
    if size > limit:
        out(f"::error::{headline}. The registry will refuse it with 413 and the module will reach no instance. "
            "Carry less — never raise the bound to make this pass (Doc/Architecture/ModuleClosureAccounting). Largest files:")
        for line in report(rows):
            out(line)
        return 1
    if share >= warn_ratio:
        out(f"::warning::{headline} — within {1 - share:.0%} of the bound. Largest files:")
    else:
        out(f"bundle size: {headline}. Largest files:")
    for line in report(rows):
        out(line)
    return 0


def self_test() -> int:
    failures = 0

    def expect(label: str, condition: bool, detail: object = "") -> None:
        nonlocal failures
        print(f"  {'ok  ' if condition else 'FAIL'} {label}" + ("" if condition else f" — {detail}"))
        if not condition:
            failures += 1

    import os
    with tempfile.TemporaryDirectory() as tmp:
        bundle = Path(tmp) / "M.module.nupkg"
        with zipfile.ZipFile(bundle, "w", zipfile.ZIP_STORED) as archive:
            archive.writestr("meshweaver/modules/Big.dll", os.urandom(6000))
            archive.writestr("meshweaver/modules/M.dll", os.urandom(1000))
            archive.writestr("meshweaver/manifest.json", b"{}")
        size = bundle.stat().st_size
        lines: list[str] = []
        expect("over the bound: exit 1", check(bundle, size - 1, 0.8, lines.append) == 1, lines)
        expect("…an ::error:: naming size and bound", lines[0].startswith("::error::") and f"{size:,}" in lines[0], lines[:1])
        expect("…with the largest file first", "Big.dll" in lines[2], lines[1:4])
        lines.clear()
        expect("at 90% of the bound: exit 0", check(bundle, int(size / 0.9), 0.8, lines.append) == 0, lines)
        expect("…with a ::warning::", lines[0].startswith("::warning::"), lines[:1])
        lines.clear()
        expect("well under: exit 0, no warning", check(bundle, size * 10, 0.8, lines.append) == 0 and not lines[0].startswith("::"), lines[:1])
        rows = [(i, i, f"f{i}") for i in range(30, 0, -1)]
        tail = report(rows, top=5)[-1]
        expect("a long breakdown sums the rest", "25 more file(s)" in tail, tail)
    print(f"{'PASS' if failures == 0 else 'FAIL'}: check-module-bundle-size self-test ({failures} failure(s))")
    return 1 if failures else 0


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--bundle", required=True, type=Path)
    parser.add_argument("--limit", required=True, type=int)
    parser.add_argument("--warn-ratio", type=float, default=0.8)
    args = parser.parse_args(argv)
    if args.limit <= 0:
        print(f"::error::--limit must be a positive byte count, got {args.limit}")
        return 1
    return check(args.bundle, args.limit, args.warn_ratio)


if __name__ == "__main__":
    if sys.argv[1:] == ["--self-test"]:
        sys.exit(self_test())
    sys.exit(main(sys.argv[1:]))
