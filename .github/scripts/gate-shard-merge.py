#!/usr/bin/env python3
"""gate-shard-merge.py — fold a sharded node-repo gate's logs into ONE, and refuse a fan-out
whose parts do not add up to the whole.

    gate-shard-merge.py --logs <dir> --shards N --gate-result <result> --out <file>
    gate-shard-merge.py --self-test

═══ WHY A FOLD RATHER THAN A CONCATENATION ═══

The gate log is not just a transcript: a caller's Tests-area ratchet READS it
(MeshWeaver.Plugins' `scripts/check-test-suites.py --gate-log`, MeshWeaver.Manufacturing's
equivalent), and that parser takes **the LAST** `=== mw-plugin-test summary ===` block in the file
— deliberately, so a re-run appended to one log reads as the current one. Concatenating N shard
logs would therefore ratchet 1/N of the NodeTypes and report a clean sweep over the rest: a FALSE
PASS, produced silently, by the very step that was supposed to make sharding invisible.

So the shards' summaries are MERGED into a single block with a single terminal verdict, and this
file is where the invariant that makes that honest is enforced.

═══ THE INVARIANT: the slices must be a DISJOINT COVER ═══

Every shard prints, BEFORE it installs anything, a line built by `GateShardPlan.Describe`:

    shard 2/4: gating 15 of 59 discovered package(s) — A, B, …; installing 3 support package(s)
    gated on another shard: Store, AI, Maps

That line is the shard's RECEIPT. Reading all N of them answers the one question no single shard
can: was every discovered package gated, exactly once? Three ways that can fail, each of which
otherwise renders as a green wall:

  * a shard's log is MISSING          → that slice was never gated, and an absent artifact is the
                                        same shape as "nothing to check";
  * two shards claim the same package → one of them double-judged it (and its verdict is then a
                                        coin toss between two meshes);
  * the union is SHORT of the total   → a package fell between the slices.

A shard that discovered a DIFFERENT total is its own failure: the shards ran against one commit, so
disagreeing about how many packages exist means one of them read a different tree.

🚨 An unsharded run (`--shards 1`) carries no plan line at all — that is the pre-fan-out tester,
which prints no such line — so a single shard is folded as a straight copy and the cover check is
satisfied by construction. Never extend that exemption to N>1: it is the one case where "no
receipt" genuinely means "no fan-out".

Stdlib only. Exit 0 = the fold is sound; 1 = it is not, with the reason as a ::error:: annotation.
"""
from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

SUMMARY_HEADER = "=== mw-plugin-test summary ==="

# `GateReport.WriteSummary` writes exactly one of these, last.
VERDICT_RE = re.compile(r"^(ALL GREEN\.|GREEN — |GATE FAILED)")

# `GateShardPlan.Describe`.
PLAN_RE = re.compile(
    r"^shard (?P<index>\d+)/(?P<total>\d+): gating (?P<gated>\d+) of (?P<discovered>\d+) "
    r"discovered package\(s\) — (?P<names>.*?); installing \d+ support package\(s\)")

# `[PASS] Store (116 node(s), 10 type(s))`, optionally trailed by an `[upstream: …]` /
# `[support: …]` marker. The marker is what says a line is NOT this shard's verdict.
PACKAGE_RE = re.compile(
    r"^\[(?P<label>PASS|FAIL|DEBT)\] (?P<id>\S+) "
    r"(?:\((?P<nodes>\d+) node\(s\), (?P<types>\d+) type\(s\)\)|\(counts unavailable)"
    r"(?P<marker>.*)$")


class ShardLog:
    """One shard's parsed evidence."""

    def __init__(self, path: Path, text: str):
        self.path = path
        self.text = text
        self.plan = None
        for line in text.splitlines():
            match = PLAN_RE.match(line)
            if match:
                self.plan = match
                break
        self.blocks, self.verdict = _summary_blocks(text)

    @property
    def gated_ids(self) -> list[str]:
        """Package ids this shard JUDGED — support/upstream entries excluded."""
        return [pid for pid, marker, _ in self.blocks if not marker]


def _summary_blocks(text: str) -> tuple[list[tuple[str, str, list[str]]], str | None]:
    """(package id, marker, its summary lines) for the LAST summary block, plus the verdict line.

    The marker is '' for a package this shard gated, and the `[upstream: …]` / `[support: …]`
    text otherwise. Raises ValueError when the log cannot be read as evidence at all — a
    truncated summary must never fold into a clean one.
    """
    if SUMMARY_HEADER not in text:
        raise ValueError(
            f"carries no '{SUMMARY_HEADER}' line, so it is not a gate log this fold can read. "
            f"Refusing: an unreadable shard must never disappear into a merged summary.")
    body = text.rsplit(SUMMARY_HEADER, 1)[1]

    blocks: list[tuple[str, str, list[str]]] = []
    verdict: str | None = None
    current: list[str] | None = None
    for line in body.splitlines():
        if VERDICT_RE.match(line):
            verdict = line
            break
        package = PACKAGE_RE.match(line)
        if package:
            current = [line]
            blocks.append((package["id"], package["marker"].strip(), current))
            continue
        if line.startswith("FATAL:"):
            # Kept with the block that follows it — it is the run's own fatal, not a package's.
            blocks.append(("", "FATAL", [line]))
            current = None
            continue
        if current is not None:
            current.append(line)
    if verdict is None:
        raise ValueError(
            "ends without its terminal verdict line (ALL GREEN. / GREEN — … / GATE FAILED …), so "
            "its summary is TRUNCATED. A partial summary folded into the merged log would read as "
            "'those types were not gated', silently excusing every one of them.")
    return blocks, verdict


def _shard_index(path: Path) -> int:
    """The shard number from `gate-log-<sha>-shard-<i>/gate.log`'s directory name."""
    match = re.search(r"-shard-(\d+)$", path.parent.name)
    return int(match.group(1)) if match else 0


def fold(logs: dict[int, str], shards: int) -> tuple[str, list[str]]:
    """(merged log text, problems). An empty problem list means the cover holds."""
    problems: list[str] = []
    parsed: dict[int, ShardLog] = {}
    for index in range(1, shards + 1):
        if index not in logs:
            problems.append(
                f"shard {index}/{shards} produced NO gate log. Its slice of the packages was "
                f"therefore never gated — and an absent artifact reads exactly like 'nothing to "
                f"check', which is why this is an error rather than a shorter merge.")
            continue
        try:
            parsed[index] = ShardLog(Path(f"shard-{index}"), logs[index])
        except ValueError as error:
            problems.append(f"shard {index}/{shards}'s log {error}")

    # ── the cover, from the shards' own plan receipts ──
    if shards > 1:
        discovered: set[int] = set()
        claimed: dict[str, list[int]] = {}
        for index, shard in sorted(parsed.items()):
            if shard.plan is None:
                problems.append(
                    f"shard {index}/{shards} printed no shard plan line. Either it did not run "
                    f"with --shard (it gated the WHOLE set, so some package was judged twice) or "
                    f"the pinned image predates the flag. Refusing to fold evidence whose "
                    f"provenance cannot be read.")
                continue
            if int(shard.plan["index"]) != index or int(shard.plan["total"]) != shards:
                problems.append(
                    f"shard {index}/{shards}'s log says it gated shard "
                    f"{shard.plan['index']}/{shard.plan['total']} — the artifact and its contents "
                    f"disagree about which slice this is.")
                continue
            discovered.add(int(shard.plan["discovered"]))
            for pid in shard.gated_ids:
                claimed.setdefault(pid, []).append(index)

        if len(discovered) > 1:
            problems.append(
                f"the shards disagree about how many packages exist: {sorted(discovered)}. They "
                f"ran against ONE commit, so one of them read a different tree — no cover can be "
                f"checked against a moving total.")
        elif discovered and not problems:
            total = discovered.pop()
            duplicated = sorted(p for p, owners in claimed.items() if len(owners) > 1)
            if duplicated:
                problems.append(
                    f"{len(duplicated)} package(s) were gated by MORE THAN ONE shard "
                    f"({', '.join(duplicated)}) — a verdict must belong to exactly one shard.")
            if len(claimed) != total:
                missing = total - len(claimed)
                problems.append(
                    f"the shards gated {len(claimed)} package(s) between them but discovered "
                    f"{total} — {missing} fell between the slices and NOTHING judged them.")

    # ── the merged log ──
    out: list[str] = []
    for index in sorted(parsed):
        out.append(f"───────── shard {index}/{shards} ─────────")
        out.append(parsed[index].text.rsplit(SUMMARY_HEADER, 1)[0].rstrip())
        out.append("")
    out.append(SUMMARY_HEADER)
    seen: set[str] = set()
    support: list[tuple[str, list[str]]] = []
    for index in sorted(parsed):
        for pid, marker, lines in parsed[index].blocks:
            if marker and pid:
                support.append((pid, lines))
                continue
            if pid and pid in seen:
                continue
            if pid:
                seen.add(pid)
            out.extend(lines)
    # 🚨 A support/upstream entry is emitted ONLY for a package no shard gated. Emitting it beside
    # the owner's entry would list the package twice, and the ratchet's own
    # "declared N type(s) but M per-type lines parsed" invariant would then reject the merged log —
    # correctly, since a package cannot have two verdicts.
    for pid, lines in support:
        if pid not in seen:
            seen.add(pid)
            out.extend(lines)
    verdicts = [parsed[i].verdict for i in sorted(parsed) if parsed[i].verdict]
    failed = [v for v in verdicts if v and v.startswith("GATE FAILED")]
    debt = [v for v in verdicts if v and v.startswith("GREEN — ")]
    if problems:
        out.append(f"GATE FAILED — the shard fold refused this run: {problems[0]}")
    elif failed:
        out.append(failed[0])
    elif debt:
        out.append(debt[0])
    else:
        out.append("ALL GREEN.")
    return "\n".join(out) + "\n", problems


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0])
    parser.add_argument("--logs", type=Path, help="directory holding the downloaded shard logs")
    parser.add_argument("--shards", type=int, help="how many shards the plan job scheduled")
    parser.add_argument("--gate-result", default="success",
                        help="the matrix job's aggregate result, for the report")
    parser.add_argument("--out", type=Path, help="where to write the folded gate.log")
    parser.add_argument("--self-test", action="store_true", dest="self_test")
    args = parser.parse_args()

    if args.self_test:
        return self_test()
    for name in ("logs", "shards", "out"):
        if getattr(args, name) is None:
            parser.error(f"--{name} is required (or use --self-test)")

    logs: dict[int, str] = {}
    if args.logs.is_dir():
        for path in sorted(args.logs.rglob("gate.log")):
            index = _shard_index(path)
            if index:
                logs[index] = path.read_text(encoding="utf-8", errors="replace")
            elif args.shards == 1:
                logs[1] = path.read_text(encoding="utf-8", errors="replace")

    merged, problems = fold(logs, args.shards)
    args.out.write_text(merged, encoding="utf-8")
    print(f"folded {len(logs)} of {args.shards} shard log(s) into {args.out} "
          f"({len(merged.splitlines())} line(s); gate job result: {args.gate_result})")
    for problem in problems:
        print(f"::error title=Sharded gate did not cover its packages::{problem}", file=sys.stderr)
    return 1 if problems else 0


# ── self-test: the fold must be able to FAIL, and each way it can ────────────────────────────

def _log(index: int, total: int, discovered: int, gated: list[str],
         support: list[str] = (), verdict: str = "ALL GREEN.") -> str:
    names = ", ".join(gated) or "(none)"
    sup = ", ".join(support) or "(none)"
    lines = [
        f"shard {index}/{total}: gating {len(gated)} of {discovered} discovered package(s) — "
        f"{names}; installing {len(support)} support package(s) gated on another shard: {sup}",
        "",
        SUMMARY_HEADER,
    ]
    for pid in gated:
        lines.append(f"[PASS] {pid} (3 node(s), 1 type(s))")
        lines.append(f"    ok  {pid}/Type: compile=Ok render=ok tests=ok")
    for pid in support:
        lines.append(f"[PASS] {pid} (3 node(s), 0 type(s)) [support: installed, gated on another shard]")
    lines.append(verdict)
    return "\n".join(lines) + "\n"


def self_test() -> int:
    failures: list[str] = []

    def check(name: str, condition: bool, detail: str = "") -> None:
        if not condition:
            failures.append(f"{name}: {detail}")

    # A sound fan-out folds green, lists every gated package exactly once, and drops the support
    # copies (whose owners are present).
    merged, problems = fold({
        1: _log(1, 2, 4, ["A", "C"], ["B"]),
        2: _log(2, 2, 4, ["B", "D"], ["A"]),
    }, 2)
    check("sound fan-out has no problems", not problems, str(problems))
    check("sound fan-out is green", merged.rstrip().endswith("ALL GREEN."), merged[-200:])
    for pid in "ABCD":
        check(f"{pid} appears once",
              merged.count(f"[PASS] {pid} (") == 1,
              f"count={merged.count(f'[PASS] {pid} (')}")
    check("no support marker survives when the owner is present",
          "[support:" not in merged.split(SUMMARY_HEADER)[-1], merged[-400:])

    # A missing shard is the finding, not a shorter merge.
    _, problems = fold({1: _log(1, 2, 4, ["A", "C"], ["B"])}, 2)
    check("a missing shard is refused", any("produced NO gate log" in p for p in problems),
          str(problems))

    # A package claimed twice.
    _, problems = fold({
        1: _log(1, 2, 4, ["A", "C"]),
        2: _log(2, 2, 4, ["A", "D"]),
    }, 2)
    check("a double-gated package is refused",
          any("MORE THAN ONE shard" in p for p in problems), str(problems))

    # A package no shard gated.
    _, problems = fold({
        1: _log(1, 2, 4, ["A"]),
        2: _log(2, 2, 4, ["B"]),
    }, 2)
    check("a package between the slices is refused",
          any("fell between the slices" in p for p in problems), str(problems))

    # Shards that read different trees.
    _, problems = fold({
        1: _log(1, 2, 4, ["A", "C"]),
        2: _log(2, 2, 5, ["B", "D"]),
    }, 2)
    check("a disagreed total is refused",
          any("disagree about how many packages exist" in p for p in problems), str(problems))

    # A shard that ran WITHOUT --shard prints no plan line: its evidence cannot be attributed.
    _, problems = fold({
        1: _log(1, 2, 4, ["A", "C"]),
        2: SUMMARY_HEADER + "\n[PASS] B (3 node(s), 0 type(s))\nALL GREEN.\n",
    }, 2)
    check("a shard with no plan line is refused",
          any("printed no shard plan line" in p for p in problems), str(problems))

    # A truncated summary (no verdict) must not fold into a clean one.
    _, problems = fold({
        1: _log(1, 2, 4, ["A", "C"]),
        2: _log(2, 2, 4, ["B", "D"]).rsplit("ALL GREEN.", 1)[0],
    }, 2)
    check("a truncated shard log is refused",
          any("TRUNCATED" in p for p in problems), str(problems))

    # A red shard carries its verdict into the merged log.
    merged, problems = fold({
        1: _log(1, 2, 4, ["A", "C"], verdict="GATE FAILED — 1 package(s) failed"),
        2: _log(2, 2, 4, ["B", "D"]),
    }, 2)
    check("a red shard folds red", merged.rstrip().endswith("GATE FAILED — 1 package(s) failed"),
          merged[-200:])
    check("a red shard is not itself a cover problem", not problems, str(problems))

    # The unsharded lane: no plan line, and that is correct — one shard IS the whole set.
    merged, problems = fold({1: SUMMARY_HEADER + "\n[PASS] A (3 node(s), 0 type(s))\nALL GREEN.\n"}, 1)
    check("shards=1 folds as a copy", not problems, str(problems))
    check("shards=1 keeps its package", "[PASS] A (" in merged, merged)

    for failure in failures:
        print(f"✗ {failure}", file=sys.stderr)
    print(f"{'✗' if failures else '✓'} gate-shard-merge self-test: "
          f"{len(failures)} failure(s)", file=sys.stderr)
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
