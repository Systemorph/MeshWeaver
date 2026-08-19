#!/usr/bin/env python3
"""Which MeshWeaver packages are published on nuget.org but no longer built here.

A module that LEAVES the platform stops being packed — but every version it already
published stays on nuget.org, listed, and keeps showing up in search and in
`dotnet add package` completion. That is a package the platform no longer ships and no
longer patches, offered to consumers as if it were current.

🚨 DERIVED, never a hand-kept list. The set is
    (published under the MeshWeaver prefix)  MINUS  (packable projects in this tree)
so it stays correct on its own as further modules move out (#1882, #1752 …). A checked-in
list would be one more thing to forget, and the day it is wrong it is wrong in the
direction of unlisting something we still ship.

Exit codes: 0 = computed (orphans may be empty), 2 = could not reach nuget.org.
"""
import argparse, json, os, re, subprocess, sys, urllib.request, urllib.error
from pathlib import Path

SEARCH = "https://azuresearch-usnc.nuget.org/query"
PREFIX = "MeshWeaver"


def published(prefix: str) -> set[str]:
    """Every package id on nuget.org matching the prefix (prerelease included)."""
    ids, skip = set(), 0
    while True:
        url = f"{SEARCH}?q={prefix}&prerelease=true&take=100&skip={skip}"
        try:
            with urllib.request.urlopen(url, timeout=60) as response:
                page = json.load(response)
        except (urllib.error.URLError, TimeoutError, OSError) as error:
            print(f"error: cannot reach nuget.org ({error})", file=sys.stderr)
            sys.exit(2)
        data = page.get("data", [])
        if not data:
            break
        # The query is a full-text search, so it also returns packages that merely MENTION
        # MeshWeaver. Only ids in our own namespace may ever be considered for unlisting.
        ids.update(x["id"] for x in data
                   if x["id"] == prefix or x["id"].startswith(prefix + "."))
        skip += 100
        if skip >= page.get("totalHits", 0):
            break
    return ids


def packable(root: Path) -> set[str]:
    """Package ids this tree still produces.

    The id is the assembly/project name unless <PackageId> overrides it. A project that
    sets IsPackable=false produces nothing and is therefore NOT evidence that we still
    ship it — but it is also not evidence that we don't, so it is simply excluded here
    and the orphan check below is what decides.
    """
    ids = set()
    for project in root.rglob("*.csproj"):
        # 🚨 RELATIVE to root, never the absolute path. Testing project.parts would match a
        # directory anywhere ABOVE the repo — a checkout under ~/.worktrees/ excluded every
        # project, the tree looked like it packed NOTHING, and the orphan set became "every
        # package we have ever published". The dry-run default is what caught it; this is why
        # the default stays.
        #
        # Only build output is skipped — NOT tools/ or test/. Over-counting what we pack can
        # only SPARE a package; under-counting unlists a live one. MeshWeaver.Compiler.Cli is
        # the worked example: it ships from tools/ behind -p:PackCompilerTool=true, and a
        # tools/ exclusion listed it as orphaned.
        relative = project.relative_to(root).parts
        if any(part in {"bin", "obj", ".worktrees"} for part in relative):
            continue
        text = project.read_text(encoding="utf-8", errors="replace")

        # EVERY declared <PackageId>, regardless of the condition it sits under. Packing can
        # be conditional — MeshWeaver.Compiler.Cli is IsPackable=false by default and packs
        # only under -p:PackCompilerTool=true, with its id declared inside that same
        # PropertyGroup. Reading IsPackable first and skipping made the project invisible, and
        # a package we very much still ship was reported as orphaned. An id that appears
        # anywhere in a csproj is evidence we own it.
        declared = set(re.findall(r"<PackageId>\s*([^<]+?)\s*</PackageId>", text, re.I))
        ids |= declared

        if re.search(r"<IsPackable>\s*false\s*</IsPackable>", text, re.I) and not declared:
            continue
        if not declared:
            ids.add(project.stem)
    return ids


def versions(package_id: str) -> list[str]:
    """Every published version of one package (the flat container is the cheap index)."""
    url = f"https://api.nuget.org/v3-flatcontainer/{package_id.lower()}/index.json"
    try:
        with urllib.request.urlopen(url, timeout=60) as response:
            return json.load(response).get("versions", [])
    except urllib.error.HTTPError as error:
        if error.code == 404:
            return []
        raise


def self_test() -> int:
    """Pin the two ways `packable` has silently lied. Both failed toward UNLISTING A LIVE
    PACKAGE, which is why they are pinned rather than remembered."""
    import tempfile
    failures = []

    with tempfile.TemporaryDirectory() as temporary:
        # A checkout that itself lives under an excluded directory name. The exclusion must be
        # judged RELATIVE to the root, or every project vanishes and everything looks orphaned.
        root = Path(temporary) / ".worktrees" / "checkout"
        (root / "src" / "MeshWeaver.Thing").mkdir(parents=True)
        (root / "src" / "MeshWeaver.Thing" / "MeshWeaver.Thing.csproj").write_text(
            "<Project><PropertyGroup></PropertyGroup></Project>", encoding="utf-8")

        # Conditionally packed: IsPackable=false by default, id declared under an opt-in.
        (root / "tools" / "Tester").mkdir(parents=True)
        (root / "tools" / "Tester" / "Tester.csproj").write_text(
            "<Project><PropertyGroup><IsPackable>false</IsPackable></PropertyGroup>"
            "<PropertyGroup Condition=\"'$(PackTool)' == 'true'\"><IsPackable>true</IsPackable>"
            "<PackageId>MeshWeaver.Tool.Cli</PackageId></PropertyGroup></Project>",
            encoding="utf-8")

        # Genuinely not packable and declaring nothing — must NOT count as shipped.
        (root / "src" / "MeshWeaver.Private").mkdir(parents=True)
        (root / "src" / "MeshWeaver.Private" / "MeshWeaver.Private.csproj").write_text(
            "<Project><PropertyGroup><IsPackable>false</IsPackable></PropertyGroup></Project>",
            encoding="utf-8")

        found = packable(root)
        if "MeshWeaver.Thing" not in found:
            failures.append("a project under a .worktrees checkout was not seen as packable")
        if "MeshWeaver.Tool.Cli" not in found:
            failures.append("a conditionally-packed PackageId was not harvested")
        if "MeshWeaver.Private" in found:
            failures.append("an IsPackable=false project declaring no id counted as shipped")

    for failure in failures:
        print(f"FAIL: {failure}")
    print("self-test: " + ("PASSED" if not failures else f"{len(failures)} FAILURE(S)"))
    return 1 if failures else 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--root", default=".", help="repository root to scan")
    parser.add_argument("--prefix", default=PREFIX)
    parser.add_argument("--apply", action="store_true",
                        help="actually unlist (default: report only)")
    parser.add_argument("--api-key", default=os.environ.get("NUGET_API_KEY", ""))
    parser.add_argument("--keep", action="append", default=[],
                        help="package id to spare even when orphaned (repeatable)")
    parser.add_argument("--self-test", action="store_true",
                        help="run the packable() regression checks and exit")
    args = parser.parse_args()

    if args.self_test:
        return self_test()

    root = Path(args.root).resolve()
    on_nuget = published(args.prefix)
    built = packable(root)
    spared = set(args.keep)
    orphans = sorted(on_nuget - built - spared)

    print(f"published on nuget.org : {len(on_nuget)}")
    print(f"packable in this tree  : {len(built)}")
    if spared:
        print(f"spared by --keep       : {', '.join(sorted(spared))}")
    print(f"ORPHANED               : {len(orphans)}")
    for package_id in orphans:
        print(f"  {package_id}  ({len(versions(package_id))} version(s))")

    if not orphans:
        print("\nNothing to unlist.")
        return 0

    if not args.apply:
        print("\nreport only — re-run with --apply (and an API key) to unlist these.")
        return 0

    if not args.api_key:
        print("error: --apply needs an API key (NUGET_API_KEY)", file=sys.stderr)
        return 2

    # `dotnet nuget delete` UNLISTS on nuget.org — it does not erase. Existing pins keep
    # resolving by exact version; the package simply stops appearing in search and in
    # latest-version resolution. That is the reversible, standard deprecation path, and it
    # is why this is safe to automate at all.
    failed = 0
    for package_id in orphans:
        for version in versions(package_id):
            result = subprocess.run(
                ["dotnet", "nuget", "delete", package_id, version,
                 "--source", "https://api.nuget.org/v3/index.json",
                 "--api-key", args.api_key, "--non-interactive"],
                capture_output=True, text=True)
            state = "unlisted" if result.returncode == 0 else "FAILED"
            if result.returncode != 0:
                failed += 1
                print(f"  {state}: {package_id} {version} — "
                      f"{(result.stderr or result.stdout).strip().splitlines()[-1:]}")
            else:
                print(f"  {state}: {package_id} {version}")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
