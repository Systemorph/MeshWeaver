#!/usr/bin/env python3
"""seal-packages.py — the publication seals PER PACKAGE (MeshWeaver.Plugins#3507).

    seal-packages.py withhold --bake <dir> --withhold "<pkg> <pkg>…" --state <bake-scope state dir>
                              [--github-output <file>]
    seal-packages.py listing  --bake <dir> --content <repo checkout> --commit <sha>
                              [--withhold "<pkg>…"] [--state <dir>] [--out <file>]
    seal-packages.py --self-test

WHY. A sealed publication used to be ONE listing for ONE commit: `_complete` named every bundle,
`source-commit.txt` named the commit all of them were baked from. A caller whose packages are
published per package (MeshWeaver.Plugins: a package waits while its lock is unsettled or its
platform floor unstamped, and everything depending on it waits with it) could therefore seal only a
commit at which NOTHING waited — and under steady merging that commit almost never exists: the
`plugins` publication stood still from 06:56Z on 2026-10-12 while every merge of the day waited
behind somebody else's pending package.

So each run seals every package that IS publishable at its commit and carries the previously sealed
build forward for every package that waits. The listing this script writes —
`sealed-packages.json`, beside `source-commit.txt`, uploaded before the sentinel — names, PER
PACKAGE, the build that is sealed: its bundle, the commit it was baked from, its version, what it
requires, and whether it was carried.

THE TWO HALVES.

  withhold   runs after the bake and BEFORE the carry-forward. It deletes every withheld package's
             freshly baked `<pkg>.zip` (and its module package `<pkg>.module.nupkg`, lower-cased,
             under `modules/`), so `carry-forward-bundles.sh` — the SAME verified carry the narrowed
             bake uses (digest + publication stamp, refuse-never-shrink) — fetches the previously
             sealed bytes in their place. A withheld package the sealed publication never listed
             stays OUT (it has never been sealed for this identity; there is nothing to carry). A
             publication that could not be READ is a refusal: carrying forward from an unknown
             publication either shrinks it or seals a guess.

  listing    runs after the carry-forward. It writes the per-package listing and REFUSES a seal in
             which a package's declared `requires` on another package of the SAME listing is not
             met by the version the listing seals for it — a carried dependency held at 1.21.1
             under a fresh dependent declaring `^1.23.0` is exactly the set Systemorph/MeshWeaver#6192
             saw a portal refuse wholesale after it had landed. Refused HERE, before anything is
             written, the existing publication stays sealed and intact.

The publication commit (`source-commit.txt`) stays THIS run's commit: the never-seal-backwards rule
and every reader that orders publications keep reading it. A reader that wants the commit ONE
package's bytes came from reads the listing, and falls back to `source-commit.txt` for every bundle
of a publication sealed before the listing existed (SealedPublicationIndex.PackagesOf).
"""
from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import zipfile
from pathlib import Path

LISTING = "sealed-packages.json"
SCHEMA = "mw-sealed-packages/1"
MODULES_DIR = "modules"

# bake-scope.sh's artefacts (its `read_publication_for_carry`), one directory per run.
STATE_PUBLICATION = "publication-state"          # sealed | absent | unreadable <reason>
STATE_BUNDLES = "published-bundles.txt"          # the sealed `_complete`
STATE_MODULES = "published-modules.txt"          # the sealed `modules/_index` (absent: none sealed)
STATE_COMMIT = "published-commit.txt"            # the sealed `source-commit.txt`
STATE_PACKAGES = "published-packages.json"       # the sealed per-package listing (absent: legacy)
STATE_CARRY_MODULES = "carry-modules.txt"        # what `withhold` asks the module carry to fetch


# ─── THE ONE reading of a requirement — MeshWeaver.Plugin.Packaging.PackageRequirement, in Python ───
# Two readings of one range either never converge or never fire, and both are silent (#6067). So
# this is the C# rule case for case: caret/tilde/>=/>/=/bare/empty/*; anything else is None
# (unverifiable), and the caller REFUSES None — a floor nothing can check is not a met floor.
_VERSION = re.compile(r"^\s*v?(\d+)(?:\.(\d+))?(?:\.(\d+))?(?:-([0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?\s*$")


def _parse(text: str | None):
    m = _VERSION.match(text or "")
    if not m or (text or "").strip().startswith("v"):
        return None
    pre = m.group(4)
    return (int(m.group(1)), int(m.group(2) or 0), int(m.group(3) or 0)), pre


def _key(parsed) -> tuple:
    (major, minor, patch), pre = parsed
    # NuGet order: a release sorts above its own prereleases.
    return (major, minor, patch, 1 if pre is None else 0, pre or "")


def satisfies(range_text: str | None, version: str) -> bool | None:
    """Whether `version` meets `range_text` — None when either cannot be read."""
    have = _parse(version)
    if have is None:
        return None
    constraint = (range_text or "").strip()
    if constraint in ("", "*"):
        return True
    for op in (">=", ">"):
        if constraint.startswith(op):
            floor = _parse(constraint[len(op):])
            if floor is None:
                return None
            return _key(have) >= _key(floor) if op == ">=" else _key(have) > _key(floor)
    if constraint[0] in "^~":
        floor = _parse(constraint[1:])
        if floor is None:
            return None
        (major, minor, patch), _ = floor
        if constraint[0] == "^":
            ceiling = (major + 1, 0, 0) if major > 0 else (0, minor + 1, 0) if minor > 0 else (0, 0, patch + 1)
        else:
            ceiling = (major, minor + 1, 0)
        return _key(floor) <= _key(have) < (*ceiling, 1, "")
    pinned = _parse(constraint[1:] if constraint.startswith("=") else constraint)
    return None if pinned is None else _key(have) == _key(pinned)


def dependency_id(entry: str) -> str:
    return entry.split("@", 1)[0].strip()


def range_of(entry: str) -> str:
    return entry.split("@", 1)[1].strip() if "@" in entry else ""


# ─── reading the content tree ────────────────────────────────────────────────────────────────────
def _json(text: str | None) -> dict | None:
    if text is None:
        return None
    try:
        value = json.loads(text)
    except json.JSONDecodeError:
        return None
    return value if isinstance(value, dict) else None


def _requires(index: dict | None) -> list[str]:
    content = (index or {}).get("content") or {}
    requires = content.get("requires") if isinstance(content, dict) else None
    return sorted(str(r) for r in requires) if isinstance(requires, list) else []


def _read(path: Path) -> str | None:
    try:
        return path.read_text(encoding="utf-8")
    except (OSError, UnicodeDecodeError):
        return None


def _git_show(repo: Path, commit: str, rel: str) -> str | None:
    done = subprocess.run(["git", "-C", str(repo), "show", f"{commit}:{rel}"],
                          capture_output=True, text=True)
    return done.stdout if done.returncode == 0 else None


def fresh_entry(repo: Path, package: str, commit: str) -> dict:
    lock = _json(_read(repo / package / "manifest.lock"))
    return {"package": package, "bundle": f"{package}.zip", "sourceCommit": commit,
            "version": (lock or {}).get("version"), "requires": _requires(_json(_read(repo / package / "index.json"))),
            "carried": False}


def legacy_entry(repo: Path, package: str, commit: str | None) -> dict:
    """A carried package of a publication sealed BEFORE the listing existed: every bundle of it was
    baked from `source-commit.txt`, so its version and requires are that commit's committed files."""
    if not commit:
        return {"package": package, "bundle": f"{package}.zip", "sourceCommit": None, "version": None,
                "requires": [], "carried": True}
    lock = _json(_git_show(repo, commit, f"{package}/manifest.lock"))
    index = _json(_git_show(repo, commit, f"{package}/index.json"))
    return {"package": package, "bundle": f"{package}.zip", "sourceCommit": commit,
            "version": (lock or {}).get("version"), "requires": _requires(index), "carried": True}


def _words(text: str | None) -> list[str]:
    return sorted({w for w in (text or "").split() if w})


def _lines(path: Path) -> list[str]:
    text = _read(path)
    return [line.strip() for line in (text or "").splitlines() if line.strip()]


# ─── withhold ────────────────────────────────────────────────────────────────────────────────────
def withhold(bake: Path, withheld: list[str], state: Path) -> dict:
    """Remove every withheld package's fresh bytes and say what the carry must fetch instead."""
    raw = (_read(state / STATE_PUBLICATION) or "").strip()
    status = raw.split(None, 1)[0] if raw else ""
    if status not in ("sealed", "absent"):
        why = raw.split(None, 1)[1] if " " in raw else "bake-scope.sh recorded no reading of the sealed publication"
        raise SystemExit(
            f"✗ seal-packages: {len(withheld)} package(s) wait ({' '.join(withheld)}), and their previously sealed "
            f"builds are carried forward from the sealed publication — which could not be read ({why}). Carrying "
            "from an unknown publication either SHRINKS it (every waiting package drops out of what every portal "
            "adopts) or seals a guess; nothing is written, and the existing publication stays sealed and intact.")
    listed = set(_lines(state / STATE_BUNDLES)) if status == "sealed" else set()
    listed_modules = set(_lines(state / STATE_MODULES)) if status == "sealed" else set()
    carried, absent, modules_carried = [], [], []
    for package in withheld:
        bundle = bake / f"{package}.zip"
        if bundle.exists():
            bundle.unlink()
        (carried if f"{package}.zip" in listed else absent).append(package)
        module = f"{package.lower()}.module.nupkg"
        fresh = bake / MODULES_DIR / module
        if fresh.exists():
            fresh.unlink()
        if module in listed_modules:
            modules_carried.append(module)
    (state / STATE_CARRY_MODULES).write_text("".join(f"{m}\n" for m in modules_carried), encoding="utf-8")
    return {"carried": carried, "absent": absent, "modulesCarried": modules_carried, "status": status}


def cmd_withhold(a: argparse.Namespace) -> int:
    withheld = _words(a.withhold)
    if not withheld:
        print("seal-packages: nothing is withheld — every package this run baked is sealed at this commit")
        if a.github_output:
            with open(a.github_output, "a", encoding="utf-8") as out:
                out.write("carry=false\ncarry-modules=false\n")
        return 0
    result = withhold(Path(a.bake), withheld, Path(a.state))
    print(f"seal-packages: {len(withheld)} package(s) wait — their fresh bytes are NOT sealed:")
    for p in result["carried"]:
        print(f"  · {p}: carried forward — the previously sealed {p}.zip is fetched and verified in its place")
    for p in result["absent"]:
        print(f"  · {p}: never sealed for this identity — it stays out of the listing until it is publishable")
    for m in result["modulesCarried"]:
        print(f"  · module {m}: carried forward from the sealed module set")
    print(f"::notice title=Sealed per package::{len(result['carried'])} waiting package(s) carried forward at their "
          f"previously sealed build, {len(result['absent'])} never sealed and left out; every other package this run "
          f"baked is sealed at this commit")
    if a.github_output:
        with open(a.github_output, "a", encoding="utf-8") as out:
            out.write(f"carry={'true' if result['carried'] else 'false'}\n")
            out.write(f"carry-modules={'true' if result['modulesCarried'] else 'false'}\n")
    return 0


# ─── listing ─────────────────────────────────────────────────────────────────────────────────────
def build_listing(bake: Path, repo: Path, commit: str, withheld: list[str], state: Path | None) -> dict:
    previous: dict[str, dict] = {}
    previous_commit = None
    if state is not None:
        prior = _json(_read(state / STATE_PACKAGES))
        for entry in (prior or {}).get("packages") or []:
            if isinstance(entry, dict) and entry.get("package"):
                previous[str(entry["package"])] = entry
        previous_commit = (_read(state / STATE_COMMIT) or "").strip() or None
        if previous_commit == "unknown":
            previous_commit = None
    waiting = set(withheld)
    entries: list[dict] = []
    for bundle in sorted(bake.glob("*.zip")):
        package = bundle.stem
        if package in waiting:
            if package in previous:
                entry = dict(previous[package])
                entry["carried"] = True
                entry["bundle"] = f"{package}.zip"
                entry["requires"] = sorted(str(r) for r in entry.get("requires") or [])
            else:
                entry = legacy_entry(repo, package, previous_commit)
        else:
            entry = fresh_entry(repo, package, commit)
        module = f"{package.lower()}.module.nupkg"
        if (bake / MODULES_DIR / module).is_file():
            entry["module"] = f"{MODULES_DIR}/{module}"
        else:
            entry.pop("module", None)
        entries.append(entry)
    if not entries:
        raise SystemExit("✗ seal-packages: the bake directory holds no bundle — a listing of nothing is not a seal")
    return {"schema": SCHEMA, "publicationCommit": commit, "packages": entries}


def floor_violations(listing: dict, repo: Path, withheld: list[str]) -> list[str]:
    by_name = {e["package"]: e for e in listing["packages"]}
    waiting = set(withheld)
    problems: list[str] = []
    for entry in listing["packages"]:
        for requirement in entry.get("requires") or []:
            dep = dependency_id(requirement)
            if not dep or dep == entry["package"]:
                continue
            sealed = by_name.get(dep)
            if sealed is None:
                if dep in waiting:
                    problems.append(f"{entry['package']} (sealed at {_short(entry.get('sourceCommit'))}) requires "
                                    f"{requirement}, but {dep} waits and this listing seals NO build of it")
                continue  # another repository's package, or one with no NodeType bundle at all
            have = sealed.get("version")
            verdict = satisfies(range_of(requirement), have) if have else None
            if verdict is True:
                continue
            if verdict is False:
                problems.append(f"{entry['package']} (sealed at {_short(entry.get('sourceCommit'))}) requires "
                                f"{requirement}, but this listing seals {dep} {have} "
                                f"({'carried at ' + _short(sealed.get('sourceCommit')) if sealed.get('carried') else 'fresh'})")
            else:
                problems.append(f"{entry['package']} requires {requirement}, and {dep}'s sealed version "
                                f"'{have}' cannot be checked against it — an unverifiable floor is not a met one")
    return problems


def _short(sha) -> str:
    return (sha or "an unknown commit")[:10]


def cmd_listing(a: argparse.Namespace) -> int:
    bake, repo = Path(a.bake), Path(a.content)
    withheld = _words(a.withhold)
    listing = build_listing(bake, repo, a.commit, withheld, Path(a.state) if a.state else None)
    problems = floor_violations(listing, repo, withheld)
    fresh = [e for e in listing["packages"] if not e.get("carried")]
    carried = [e for e in listing["packages"] if e.get("carried")]
    print(f"sealed per package: {len(listing['packages'])} package(s) — {len(fresh)} at this commit "
          f"{_short(a.commit)}, {len(carried)} carried at their previously sealed build")
    for e in carried:
        print(f"  · {e['package']} {e.get('version') or '?'} carried at {_short(e.get('sourceCommit'))}")
    if problems:
        print(f"::error title=Seal refused — a dependency floor would be unmet::{len(problems)} requirement(s) of a "
              "sealed package are not met by what the SAME listing seals. Sealing them would hand every portal a "
              "module set it must refuse wholesale (Systemorph/MeshWeaver#6192); nothing is written, and the existing "
              "publication stays sealed and intact.")
        for p in problems:
            print(f"::error::{p}")
        return 1
    out = Path(a.out) if a.out else bake / LISTING
    out.write_text(json.dumps(listing, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(f"listing written: {out} — every in-listing dependency floor is met")
    return 0


# ─── self-test ───────────────────────────────────────────────────────────────────────────────────
def self_test() -> int:
    failures: list[str] = []

    def expect(label: str, cond: bool, detail: str = "") -> None:
        print(f"  {'✓' if cond else '✗'} {label}" + (f" — {detail}" if detail and not cond else ""))
        if not cond:
            failures.append(label)

    print("the requirement reading is core's (PackageRequirement.Satisfies):")
    expect("a caret is met by a newer minor", satisfies("^1.21.0", "1.23.4") is True)
    expect("…and missed by the next major", satisfies("^1.21.0", "2.0.0") is False)
    expect("a caret on 0.x keeps the minor", satisfies("^0.2.1", "0.3.0") is False)
    expect("a bare version is an EXACT pin", satisfies("1.4.0", "1.9.0") is False)
    expect("a floor below is missed", satisfies("^1.23.0", "1.21.1") is False)
    expect("a prerelease of the ceiling stays under a caret (NuGet order)", satisfies("^1.21.0", "2.0.0-rc1") is True)
    expect("NEGATIVE CONTROL: an unreadable range is None, never met", satisfies("1.x", "1.2.0") is None)

    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        repo, bake, state = root / "repo", root / "bake", root / "state"
        for d in (repo, bake, state):
            d.mkdir()

        def git(*args: str) -> str:
            return subprocess.run(["git", "-C", str(repo), *args], check=True, capture_output=True,
                                  text=True).stdout.strip()

        git("init", "-q", "-b", "main")
        git("config", "user.email", "t@t")
        git("config", "user.name", "t")
        names = [f"P{i:02d}" for i in range(78)]

        def write(package: str, version: str, requires: list[str] | None = None) -> None:
            (repo / package).mkdir(exist_ok=True)
            content = {"version": version}
            if requires:
                content["requires"] = requires
            (repo / package / "index.json").write_text(json.dumps({"id": package, "content": content}))
            (repo / package / "manifest.lock").write_text(json.dumps({"version": version}))

        for n in names:
            write(n, "1.0.0")
        git("add", "-A")
        git("commit", "-qm", "the commit the previous seal covered")
        old = git("rev-parse", "HEAD")
        for n in names:
            write(n, "1.1.0")
        git("add", "-A")
        git("commit", "-qm", "the commit this run bakes")
        new = git("rev-parse", "HEAD")

        def bake_all(packages: list[str], modules: list[str] = ()) -> None:
            shutil.rmtree(bake)
            bake.mkdir()
            for p in packages:
                (bake / f"{p}.zip").write_text(f"fresh {p}")
            if modules:
                (bake / MODULES_DIR).mkdir()
                for m in modules:
                    (bake / MODULES_DIR / m).write_text(f"fresh {m}")

        def seal_previous(listing: list[dict] | None, bundles: list[str], modules: list[str] = ()) -> None:
            shutil.rmtree(state)
            state.mkdir()
            (state / STATE_PUBLICATION).write_text("sealed\n")
            (state / STATE_BUNDLES).write_text("".join(f"{b}\n" for b in bundles))
            (state / STATE_MODULES).write_text("".join(f"{m}\n" for m in modules))
            (state / STATE_COMMIT).write_text(old + "\n")
            if listing is not None:
                (state / STATE_PACKAGES).write_text(json.dumps({"schema": SCHEMA, "publicationCommit": old,
                                                                 "packages": listing}))

        def carry_forward(packages: list[str]) -> None:
            # What carry-forward-bundles.sh does for the names withhold deleted: the sealed bytes.
            for p in packages:
                if not (bake / f"{p}.zip").exists():
                    (bake / f"{p}.zip").write_text(f"sealed {p}")

        publishable, waiting = names[:41], names[41:]
        print("a run where 41 of 78 packages are publishable:")
        bake_all(names)
        seal_previous([{"package": n, "bundle": f"{n}.zip", "sourceCommit": old, "version": "1.0.0",
                        "requires": [], "carried": False} for n in names], [f"{n}.zip" for n in names])
        result = withhold(bake, waiting, state)
        expect("withhold deletes exactly the 37 waiting packages' fresh bundles",
               sorted(p.stem for p in bake.glob("*.zip")) == publishable)
        expect("…and names all 37 to be carried forward (all were sealed before)",
               result["carried"] == waiting and not result["absent"])
        carry_forward(waiting)
        listing = build_listing(bake, repo, new, waiting, state)
        fresh = [e for e in listing["packages"] if not e["carried"]]
        carried = [e for e in listing["packages"] if e["carried"]]
        expect("the listing names all 78", len(listing["packages"]) == 78)
        expect("41 are sealed fresh, at THIS commit, at this tree's version",
               len(fresh) == 41 and all(e["sourceCommit"] == new and e["version"] == "1.1.0" for e in fresh))
        expect("37 are carried, each at its PREVIOUSLY sealed commit and version",
               len(carried) == 37 and all(e["sourceCommit"] == old and e["version"] == "1.0.0" for e in carried))
        expect("NEGATIVE CONTROL: no waiting package is sealed at this commit",
               not any(e["package"] in waiting and e["sourceCommit"] == new for e in listing["packages"]))
        expect("the publication commit stays this run's (never-seal-backwards reads it)",
               listing["publicationCommit"] == new)
        expect("no floor is violated", floor_violations(listing, repo, waiting) == [])

        print("a carried build keeps its OWN commit across seals (carried twice):")
        seal_previous(listing["packages"], [f"{n}.zip" for n in names])
        bake_all(names)
        withhold(bake, waiting[:5], state)
        carry_forward(waiting[:5])
        again = build_listing(bake, repo, new, waiting[:5], state)
        by = {e["package"]: e for e in again["packages"]}
        expect("a package carried again still names the commit its bytes came from, not the publication before",
               all(by[p]["sourceCommit"] == old and by[p]["carried"] for p in waiting[:5]))
        expect("…and one that became publishable is sealed fresh", by[waiting[5]]["sourceCommit"] == new)

        print("a dependency floor the listing would leave unmet is REFUSED (the #6192 class):")
        write("P00", "1.1.0", ["P50@^1.1.0"])  # fresh P00 needs the NEW P50; P50 waits at 1.0.0
        bake_all(names)
        seal_previous(None, [f"{n}.zip" for n in names])
        withhold(bake, ["P50"], state)
        carry_forward(["P50"])
        bad = build_listing(bake, repo, new, ["P50"], state)
        problems = floor_violations(bad, repo, ["P50"])
        expect("refused, naming the dependent, the requirement and the carried version",
               len(problems) == 1 and "P00" in problems[0] and "P50@^1.1.0" in problems[0] and "1.0.0" in problems[0],
               str(problems))
        write("P00", "1.1.0", ["P50@^1.0.0"])  # the same shape, with a floor the carried build meets
        good = build_listing(bake, repo, new, ["P50"], state)
        expect("NEGATIVE CONTROL: a floor the carried build meets is sealed", floor_violations(good, repo, ["P50"]) == [])
        write("P00", "1.1.0", ["P77@^1.0.0"])
        bake_all(names[:77])
        seal_previous(None, [f"{n}.zip" for n in names[:77]])
        withhold(bake, ["P77"], state)
        never = build_listing(bake, repo, new, ["P77"], state)
        problems = floor_violations(never, repo, ["P77"])
        expect("a dependency that waits and was NEVER sealed is refused too",
               len(problems) == 1 and "seals NO build" in problems[0], str(problems))
        write("P00", "1.1.0", ["External@^9.0.0"])
        expect("NEGATIVE CONTROL: another repository's package is not this listing's to check",
               floor_violations(build_listing(bake, repo, new, [], None), repo, []) == [])
        write("P00", "1.1.0")

        print("a publication sealed before the listing existed (legacy):")
        bake_all(names)
        seal_previous(None, [f"{n}.zip" for n in names])
        withhold(bake, ["P60"], state)
        carry_forward(["P60"])
        legacy = {e["package"]: e for e in build_listing(bake, repo, new, ["P60"], state)["packages"]}
        expect("a carried package takes source-commit.txt and that commit's committed version",
               legacy["P60"]["sourceCommit"] == old and legacy["P60"]["version"] == "1.0.0" and legacy["P60"]["carried"])

        print("a waiting package that was never sealed stays OUT:")
        bake_all(names)
        seal_previous(None, [f"{n}.zip" for n in names[:70]])
        result = withhold(bake, ["P75"], state)
        expect("it is named absent, not carried", result["absent"] == ["P75"] and not result["carried"])
        expect("…and its fresh bytes are gone, so no listing can seal them", not (bake / "P75.zip").exists())

        print("module packages are carried with their package:")
        bake_all(names[:3], ["p01.module.nupkg", "p02.module.nupkg"])
        seal_previous(None, [f"{n}.zip" for n in names[:3]], ["p01.module.nupkg"])
        result = withhold(bake, ["P01", "P02"], state)
        expect("both fresh module packages are removed",
               not (bake / MODULES_DIR / "p01.module.nupkg").exists() and not (bake / MODULES_DIR / "p02.module.nupkg").exists())
        expect("the one the sealed module set lists is handed to the carry; the never-sealed one is not",
               _lines(state / STATE_CARRY_MODULES) == ["p01.module.nupkg"])

        print("an UNREADABLE publication refuses to carry:")
        (state / STATE_PUBLICATION).write_text("unreadable the pointer could not be followed\n")
        try:
            withhold(bake, ["P00"], state)
            refused = False
        except SystemExit as e:
            refused = "could not be read" in str(e)
        expect("refused, naming why — never a shrink and never a guess", refused)
        (state / STATE_PUBLICATION).unlink()
        try:
            withhold(bake, ["P00"], state)
            refused = False
        except SystemExit:
            refused = True
        expect("NEGATIVE CONTROL: no recorded reading at all refuses too", refused)

        print("the CLI writes the listing and exits RED on a violation:")
        write("P00", "1.1.0", ["P50@^1.1.0"])
        bake_all(names)
        seal_previous(None, [f"{n}.zip" for n in names])
        me = str(Path(__file__).resolve())
        subprocess.run([sys.executable, me, "withhold", "--bake", str(bake), "--withhold", "P50", "--state", str(state)],
                       check=True, capture_output=True)
        carry_forward(["P50"])
        red = subprocess.run([sys.executable, me, "listing", "--bake", str(bake), "--content", str(repo),
                              "--commit", new, "--withhold", "P50", "--state", str(state)], capture_output=True, text=True)
        expect("exit 1 and no listing written", red.returncode == 1 and not (bake / LISTING).exists(), red.stdout)
        write("P00", "1.1.0")
        green = subprocess.run([sys.executable, me, "listing", "--bake", str(bake), "--content", str(repo),
                                "--commit", new, "--withhold", "P50", "--state", str(state)], capture_output=True, text=True)
        written = _json(_read(bake / LISTING))
        expect("exit 0 and the listing names P50 carried",
               green.returncode == 0 and written is not None
               and any(e["package"] == "P50" and e["carried"] for e in written["packages"]), green.stdout + green.stderr)

    if failures:
        print(f"\nseal-packages self-test: {len(failures)} FAILED")
        return 1
    print("\nseal-packages self-test: all green")
    return 0


def main(argv: list[str]) -> int:
    if argv[:1] == ["--self-test"]:
        return self_test()
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    sub = parser.add_subparsers(dest="cmd", required=True)
    w = sub.add_parser("withhold")
    w.add_argument("--bake", required=True)
    w.add_argument("--withhold", default="")
    w.add_argument("--state", required=True)
    w.add_argument("--github-output")
    l = sub.add_parser("listing")
    l.add_argument("--bake", required=True)
    l.add_argument("--content", required=True)
    l.add_argument("--commit", required=True)
    l.add_argument("--withhold", default="")
    l.add_argument("--state")
    l.add_argument("--out")
    a = parser.parse_args(argv)
    return cmd_withhold(a) if a.cmd == "withhold" else cmd_listing(a)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
