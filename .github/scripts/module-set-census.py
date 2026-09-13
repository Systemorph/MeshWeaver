#!/usr/bin/env python3
"""The one-build-per-assembly-name reading, over a PUBLICATION's whole module set (MeshWeaver#3732).

WHY THIS EXISTS SEPARATELY FROM THE LANES' OWN ASSERTION
--------------------------------------------------------
`node-repo-{gate,publish-bake}.yml` already assert "one assembly name, one BUILD" and they refuse on
divergence. They assert it over the set the bake COMPOSES — the `ext-modules` step — and that set is
small: measured on Reinsurance's gate, `4 MeshWeaver.* assembly file(s) across 4 bundle(s)`; on
Plugins' publish-bake, 5 across 5.

The defect #3732 was filed on did not live there. It was measured on a memex pod: ONE assembly
(`MeshWeaver.Markdown.Collaboration`) in **15 copies and THREE builds**, spread across the ~40
*module bundles* a publication carries — the population `node-repo-module-pack.yml` produces and
publishes to the registry, most of which no single bake composes. Every reading of the existing
assertion has therefore been green *and unable to say anything about this defect*, which is exactly
what a denominator of 4 against a population of 40 means. Two comments on #3732 make the point in
the instrument's own words: "The guard is real and it prints its denominator honestly; it is simply
not yet pointed at the population that had the defect."

WHERE THE WHOLE POPULATION IS IN ONE HAND
-----------------------------------------
Nowhere in a pack leg: each leg holds ONE bundle. The one place that sees the whole wave is
`node-repo-module-pack.yml`'s `verify` job, which already collects a receipt per module and says so
in its own comment ("IT IS ALSO THE ONLY PLACE THAT SEES THE WHOLE WAVE (#3310)"). So the census is
taken per bundle, where the bytes are, and recorded ON THE RECEIPT; the verdict is read across every
receipt of the lane.

`census`  — per bundle, in the pack leg: every `MeshWeaver.*` assembly the bundle carries, digested
            from the bundle's OWN bytes, with the role it plays (`declared` = the module's entry
            assembly per `manifest.json`'s `module.assemblyName`; `riding` = a sibling that came
            along because something referenced it).
`verdict` — across the lane's receipts, in `verify`: the same arithmetic the lanes do, over the real
            population, with its denominator printed on EVERY run.

🚨 REPORT, NOT REFUSAL — AND THE REASON IS EVIDENCE, NOT TIMIDITY
-----------------------------------------------------------------
`verdict` exits 0 on divergence unless `--enforce` is passed, and no lane passes it yet.

That is deliberate and it is the honest order. Nobody has ever read this population: the instrument
is what #3732's closing condition asks for and it does not exist, so what a refusal here would
REFUSE is unknown. A `reuse` leg (`ledger.decision == "reuse"`) republishes bytes packed in an
earlier run by construction, so a shared sibling riding both a reused bundle and a freshly built one
would diverge — possibly on every wave, in every satellite, the moment this landed. `verify` is a
REQUIRED context in five satellites; turning an unread measurement into a refusal there is how a new
guard takes CD down fleet-wide.

The population is not unguarded in the meantime — it is guarded LATE, at the consumer, where
`PublishedBundleCatalogue` answers `SealedSetInconsistent` and holds the roll for the whole fleet.
Moving that refusal earlier is the point; doing it before one wave has been read is guessing.

So: read the denominator line on the first waves, then promote with `--enforce` and the evidence of
what it refuses. Until then this prints a number nobody had, which is the missing half.

🚨 The denominator is printed whether the reading is clean or not, because "measured, and it was
clean" and "measured nothing" are two different sentences and a check aimed at an empty directory
says the second while looking like the first.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import sys
import zipfile
from pathlib import Path

MODULES_PREFIX = "meshweaver/modules/"
MANIFEST = "meshweaver/manifest.json"


def _is_meshweaver_assembly(rel: str) -> bool:
    """`MeshWeaver.*.dll` or `MeshWeaver.dll`, directly in the modules folder — the same set the
    lanes' `find -maxdepth 1 -name 'MeshWeaver.*.dll' -o -name 'MeshWeaver.dll'` selects."""
    if not rel.startswith(MODULES_PREFIX):
        return False
    name = rel[len(MODULES_PREFIX):]
    if "/" in name or not name.endswith(".dll"):
        return False
    return name == "MeshWeaver.dll" or name.startswith("MeshWeaver.")


def census(bundle: Path) -> list[dict]:
    """Every `MeshWeaver.*` assembly in one module bundle, digested from the bundle's own bytes.

    Read straight out of the archive rather than from an unpacked folder: two bundles that declare
    the same entry assembly unpack into the SAME directory, and the second copy overwrites the
    first — the accident of glob order that made this defect invisible in the first place.
    """
    with zipfile.ZipFile(bundle) as archive:
        declared = ""
        try:
            manifest = json.loads(archive.read(MANIFEST).decode("utf-8"))
            declared = (manifest.get("module") or {}).get("assemblyName") or ""
        except (KeyError, ValueError, UnicodeDecodeError):
            declared = ""
        rows = []
        for info in sorted(archive.infolist(), key=lambda i: i.filename):
            if info.is_dir() or not _is_meshweaver_assembly(info.filename):
                continue
            simple = info.filename[len(MODULES_PREFIX):-len(".dll")]
            digest = hashlib.sha256(archive.read(info.filename)).hexdigest()
            rows.append({
                "name": simple,
                "sha256": digest,
                "role": "declared" if simple == declared else "riding",
            })
    return rows


def _receipts(directory: Path, lane: str, declared: list[str]) -> list[dict]:
    """The receipts to read, optionally narrowed to one lane and one call's matrix.

    🚨 **THE WORKFLOW PASSES NEITHER FILTER, AND THAT IS THE POINT.** `verify`'s own build
    accounting narrows by `--lane` and `--declared` because it answers *"did THIS call build what it
    was asked to"*, where attributing a sibling call's evidence to this one was a real defect
    (Plugins#1077). This reading asks the opposite question — *"does this PUBLICATION carry one
    assembly name at two builds"* — and a publication is composed from SEVERAL calls: MeshWeaver.
    Plugins invokes the lane twice in one run (`modules-floor` and `modules-rest`), with different
    lane stamps, and the 15-copies/3-builds measurement this whole reading exists for spanned
    exactly those calls. Narrowing by lane here would make each verifier drop the other call's
    receipts and report a confident zero — the same denominator error as the composed-set guard,
    one level up. So the caller downloads every receipt in the RUN and passes no filter, and the
    verdict prints which lanes it folded so a reader can see whether both calls were present.

    The parameters stay because narrowing is occasionally the right question to ask by hand, and
    because a filter that exists but is not exercised is a filter nobody has watched work.
    """
    wanted = set(declared)
    out = []
    for path in sorted(directory.glob("*.json")):
        try:
            receipt = json.loads(path.read_text())
        except ValueError:
            continue
        if lane and receipt.get("lane") != lane:
            continue
        if wanted and receipt.get("module") not in wanted:
            continue
        out.append(receipt)
    return out


def verdict(receipts: list[dict], enforce: bool, out=sys.stdout) -> int:
    """The lanes' arithmetic, over the publication's whole module set. Returns the exit code."""
    copies: list[tuple[str, str, str, str]] = []   # (name, sha256, module, role)
    silent: list[str] = []
    for receipt in receipts:
        module = receipt.get("module") or "<unnamed>"
        assemblies = receipt.get("assemblies")
        if assemblies is None:
            # 🚨 A receipt that carries no census is NOT a clean bundle — it is a bundle nobody
            # measured, and folding it into a green count is how "absent" comes to read as "agrees".
            # Named separately, on every run, with the reason: a producer at an older scripts ref.
            silent.append(module)
            continue
        for row in assemblies:
            copies.append((row.get("name", ""), row.get("sha256", ""), module, row.get("role", "")))

    measured = [r for r in receipts if r.get("assemblies") is not None]
    names = sorted({c[0] for c in copies})
    shared = sorted({n for n in names if len({c[2] for c in copies if c[0] == n}) > 1})
    diverged = sorted({n for n in names if len({c[1] for c in copies if c[0] == n}) > 1})

    lanes = sorted({r.get("lane") or "<unstamped>" for r in receipts})
    print(
        f"publication module set: {len(copies)} MeshWeaver.* assembly file(s) across "
        f"{len(measured)} module bundle(s) of {len(receipts)} read, {len(names)} distinct "
        f"name(s), {len(shared)} carried by more than one bundle, {len(diverged)} carried at more "
        f"than one BUILD",
        file=out,
    )
    # 🚨 WHICH CALLS WERE FOLDED, on every run. A publication is composed from several calls of this
    # lane, and this reading is only publication-wide if it saw them all — so the lanes are named
    # rather than assumed. One lane where the repo makes two calls is a reading that has already
    # narrowed itself, and nothing else on the line would show it.
    print(f"  folded {len(lanes)} lane(s): {', '.join(lanes) if lanes else '<none>'}", file=out)
    if silent:
        print(
            f"  not measured: {len(silent)} bundle(s) carry no assembly census on their receipt "
            f"({', '.join(silent[:10])}{', …' if len(silent) > 10 else ''}) — their producer ran at "
            f"a scripts ref that predates MeshWeaver#3732's census, so this reading says nothing "
            f"about them",
            file=out,
        )

    for name in diverged:
        builds: dict[str, list[str]] = {}
        for copy_name, digest, module, role in copies:
            if copy_name != name:
                continue
            builds.setdefault(digest, []).append(
                f"{module} ({'the declared module' if role == 'declared' else 'a sibling riding it'})")
        print(
            f"  {name} reaches this publication as {len(builds)} different builds — a consumer that "
            f"installs more than one of these bundles keeps whichever it loads first, and the "
            f"loser's bytes are never in memory (MeshWeaver#3732)",
            file=out,
        )
        for digest, carriers in sorted(builds.items()):
            print(f"      {digest[:16]}  {', '.join(sorted(carriers))}", file=out)

    if diverged and enforce:
        print(
            f"::error::the publication's module set carries {len(diverged)} assembly name(s) at "
            f"more than one build. Build the whole set in ONE compilation, or stop the divergent "
            f"copy riding — Doc/Architecture/ModuleOwnedSiblingsRide.",
            file=out,
        )
        return 1
    if diverged:
        # A finding with no refusal behind it must not be spelled like a pass. `::warning::` is the
        # loudest thing a report may be, and it names why it is not red.
        print(
            f"::warning::the publication's module set carries {len(diverged)} assembly name(s) at "
            f"more than one build ({', '.join(diverged)}). This reading is a REPORT: no lane passes "
            f"--enforce yet, because what a refusal here would refuse has never been measured "
            f"(MeshWeaver#3732). The consumer still refuses this set late, as SealedSetInconsistent.",
            file=out,
        )
    return 0


def self_test() -> int:
    """A round trip over real bytes, so a lane's preflight proves the helper it fetched WORKS —
    not merely that a file with the right name is on disk. Same convention as the other lane
    helpers; the full harness is `.github/scripts/test-module-set-census.py`."""
    import tempfile

    failures = []

    def expect(condition: bool, label: str) -> None:
        if not condition:
            failures.append(label)

    with tempfile.TemporaryDirectory() as raw:
        bundle = Path(raw) / "x.module.nupkg"
        with zipfile.ZipFile(bundle, "w") as archive:
            archive.writestr(MANIFEST, json.dumps({"module": {"assemblyName": "MeshWeaver.AI"}}))
            archive.writestr(f"{MODULES_PREFIX}MeshWeaver.AI.dll", b"AI")
            archive.writestr(f"{MODULES_PREFIX}MeshWeaver.Shared.dll", b"SHARED-1")
            archive.writestr(f"{MODULES_PREFIX}Newtonsoft.Json.dll", b"NOT-OURS")
        rows = census(bundle)
        expect(sorted(r["name"] for r in rows) == ["MeshWeaver.AI", "MeshWeaver.Shared"],
               "census selects the MeshWeaver.* assemblies and nothing else")
        expect({r["name"]: r["role"] for r in rows}["MeshWeaver.AI"] == "declared",
               "census marks the manifest's entry assembly as declared")

    one = hashlib.sha256(b"SHARED-1").hexdigest()
    two = hashlib.sha256(b"SHARED-2").hexdigest()
    same = [{"lane": "l", "module": "A", "assemblies": [{"name": "S", "sha256": one, "role": "riding"}]},
            {"lane": "l", "module": "B", "assemblies": [{"name": "S", "sha256": one, "role": "riding"}]}]
    differ = [same[0],
              {"lane": "l", "module": "B", "assemblies": [{"name": "S", "sha256": two, "role": "riding"}]}]
    import io as _io
    expect(verdict(same, enforce=True, out=_io.StringIO()) == 0,
           "identical copies of a riding sibling stay clean under --enforce")
    expect(verdict(differ, enforce=True, out=_io.StringIO()) == 1,
           "divergent copies are refused under --enforce")
    expect(verdict(differ, enforce=False, out=_io.StringIO()) == 0,
           "…and reported, not refused, without it")

    if failures:
        for failure in failures:
            print(f"::error::module-set-census self-test: {failure}", file=sys.stderr)
        return 1
    print("module-set-census.py self-test: ok")
    return 0


def main(argv: list[str] | None = None) -> int:
    if argv is None:
        argv = sys.argv[1:]
    if "--self-test" in argv:
        return self_test()

    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--self-test", action="store_true",
                        help="prove this helper works, over real bytes")
    sub = parser.add_subparsers(dest="command", required=True)

    one = sub.add_parser("census", help="the MeshWeaver.* assemblies one module bundle carries")
    one.add_argument("--bundle", required=True, type=Path)

    all_of = sub.add_parser("verdict", help="the one-build-per-name reading across a lane's receipts")
    all_of.add_argument("--receipts", required=True, type=Path)
    all_of.add_argument("--lane", default="")
    all_of.add_argument("--declared", default="[]",
                        help="the caller's matrix as JSON — [{module: …}, …] or [\"…\", …]")
    all_of.add_argument("--enforce", action="store_true",
                        help="exit 1 on divergence instead of reporting it")

    args = parser.parse_args(argv)

    if args.command == "census":
        print(json.dumps(census(args.bundle), separators=(",", ":")))
        return 0

    declared_raw = json.loads(args.declared or "[]")
    declared = [d.get("module", "") if isinstance(d, dict) else str(d) for d in declared_raw]
    if not args.receipts.is_dir():
        # 🚨 An absent directory is ZERO EVIDENCE and must say so in the denominator, never read as
        # a clean set. `verify` judges whether the receipts should have been there.
        print("publication module set: 0 MeshWeaver.* assembly file(s) across 0 module bundle(s) "
              "of 0 read, 0 distinct name(s), 0 carried by more than one bundle, 0 carried "
              "at more than one BUILD")
        print(f"  no receipts directory at {args.receipts} — nothing was measured")
        return 0
    return verdict(_receipts(args.receipts, args.lane, declared), args.enforce)


if __name__ == "__main__":
    sys.exit(main())
