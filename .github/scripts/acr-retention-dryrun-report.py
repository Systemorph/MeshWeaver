#!/usr/bin/env python3
"""Read one `acr purge --dry-run` task RUN log back as a verdict (MeshWeaver#3438).

`purge-old-images` runs nightly as a REPORT: both its steps carry `--dry-run`, so acr-cli logs what
the decided 30-day window WOULD delete and deletes nothing. The record's `dryRun.liftWhen` asks for
at least one of those nightly runs to be read back before `--dry-run` is dropped. Until this script
no governed instrument read a task RUN at all — `RegistryRetention` compared task DEFINITIONS — so
the read-back had no path but a hand `az acr task logs`, which is break-glass.

`acr-retention-tasks.sh verify` fetches the newest completed run of every enabled dry-run task and
hands its log here. This prints, per repository, how many tags and untagged manifests the run says
it would delete, then every listed reference, then a one-line summary LAST (so a log tail carries
it) — and it REFUSES (exit 1)
rather than report a number it cannot stand behind:

  * a step that is not marked `DRY RUN:` — the run may have deleted, and a count of "would delete"
    lines would then read a real deletion as a report;
  * a `Number of deleted tags:` / `Number of deleted manifests:` summary — acr-cli prints those only
    when it DID delete;
  * `Failed to complete purge` — the step stopped part-way, so its counts cover an unknown subset;
  * fewer `Number of … to be deleted` summaries than the task has purge steps — a step that never
    reported is not a step that would delete nothing;
  * a summary count that disagrees with the `Would delete:` lines the same step listed.

The line shapes are acr-cli's own (`cmd/acr/purge.go`): `DRY RUN: The following output shows what
WOULD be deleted …`, `Would delete: <login>/<repo>:<tag>` / `…/<repo>@<digest>` (optionally
`(reason: …)`), `Number of tags to be deleted: N`, `Number of manifests to be deleted: N`. An ACR
task log prefixes nothing onto a step's stdout, but this matches by search, never by anchor.

    acr-retention-dryrun-report.py --log FILE --task NAME --steps N
"""
from __future__ import annotations

import argparse
import re
import sys
from collections import OrderedDict

DRY_RUN_MARK = "DRY RUN: The following output shows what WOULD be deleted"
WOULD_DELETE = re.compile(r"Would delete: (?P<ref>\S+?)(?: \(reason: [^)]*\))?\s*$")
TAGS_SUMMARY = re.compile(r"Number of tags to be deleted: (\d+)")
MANIFESTS_SUMMARY = re.compile(r"Number of manifests to be deleted: (\d+)")
DELETED_SUMMARY = re.compile(r"Number of deleted (tags|manifests): (\d+)")
FAILED = "Failed to complete purge"


def parse(text: str, steps: int) -> tuple[dict, list[str]]:
    """Return ({repo: {"tags": [...], "manifests": [...]}}, errors)."""
    errors: list[str] = []
    repos: "OrderedDict[str, dict]" = OrderedDict()
    marks = 0
    summaries: list[tuple[int, int]] = []
    pending_tags: int | None = None
    listed_tags = listed_manifests = 0
    for line in text.splitlines():
        if DRY_RUN_MARK in line:
            marks += 1
            continue
        if FAILED in line:
            errors.append(f"a step reports `{line.strip()}` — its counts cover an unknown subset")
            continue
        m = DELETED_SUMMARY.search(line)
        if m:
            errors.append(f"a step reports `{m.group(0)}` — acr-cli prints that only when it "
                          "DELETED; this run was not a dry run")
            continue
        m = WOULD_DELETE.search(line)
        if m:
            ref = m.group("ref")
            # `<login>/<repo>:<tag>` or `<login>/<repo>@<digest>`; the repo may itself contain `/`.
            path = ref.split("/", 1)[1] if "/" in ref else ref
            if "@" in path:
                repo, kind = path.split("@", 1)[0], "manifests"
                listed_manifests += 1
            elif ":" in path:
                repo, kind = path.rsplit(":", 1)[0], "tags"
                listed_tags += 1
            else:
                errors.append(f"cannot read the reference in `{line.strip()}`")
                continue
            repos.setdefault(repo, {"tags": [], "manifests": []})[kind].append(ref)
            continue
        m = TAGS_SUMMARY.search(line)
        if m:
            pending_tags = int(m.group(1))
            continue
        m = MANIFESTS_SUMMARY.search(line)
        if m:
            if pending_tags is None:
                errors.append("a `Number of manifests to be deleted` summary has no tag summary before it")
            summaries.append((pending_tags or 0, int(m.group(1))))
            pending_tags = None
    if marks < steps:
        errors.append(f"{marks} step(s) are marked `DRY RUN:` and the task has {steps} purge step(s) "
                      "— an unmarked step may have deleted")
    if len(summaries) < steps:
        errors.append(f"{len(summaries)} step summary(ies) for {steps} purge step(s) — a step that "
                      "never reported is not a step that would delete nothing")
    if summaries:
        want_tags = sum(t for t, _ in summaries)
        want_manifests = sum(mf for _, mf in summaries)
        if want_tags != listed_tags or want_manifests != listed_manifests:
            errors.append(f"the step summaries say {want_tags} tag(s) / {want_manifests} manifest(s) and "
                          f"the log lists {listed_tags} / {listed_manifests} `Would delete:` line(s)")
    return repos, errors


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--log", required=True)
    ap.add_argument("--task", required=True)
    ap.add_argument("--steps", type=int, required=True)
    a = ap.parse_args(argv)
    with open(a.log, encoding="utf-8", errors="replace") as f:
        text = f.read()
    repos, errors = parse(text, a.steps)
    print(f"  {a.task}: dry-run report, per repository (tags / untagged manifests it would delete):")
    if not repos:
        print("    (no repository lists anything to delete)")
    for repo, kinds in repos.items():
        print(f"    {repo}: {len(kinds['tags'])} tag(s), {len(kinds['manifests'])} manifest(s)")
    if repos:
        print(f"  {a.task}: every reference the run lists:")
    for repo, kinds in repos.items():
        for ref in kinds["tags"] + kinds["manifests"]:
            print(f"    would delete {ref}")
    if errors:
        for e in errors:
            print(f"::error::{a.task}: {e}.")
        print(f"  {a.task}: NO VERDICT — the run log does not support a dry-run reading.")
        return 1
    total_t = sum(len(k["tags"]) for k in repos.values())
    total_m = sum(len(k["manifests"]) for k in repos.values())
    print(f"  {a.task}: the newest run would delete {total_t} tag(s) and {total_m} manifest(s) across "
          f"{len(repos)} repository(ies), over {a.steps} dry-run step(s); it deleted nothing.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
