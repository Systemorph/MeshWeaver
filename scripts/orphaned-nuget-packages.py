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
import argparse, gzip, json, os, re, subprocess, sys, time, urllib.request, urllib.error
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


IS_PACKABLE = re.compile(r"<IsPackable>\s*(true|false)\s*</IsPackable>", re.I)
IMPORTS_ABOVE = re.compile(r"<Import\b[^>]*GetPathOfFileAbove", re.I)


def directory_default(project: Path, root: Path) -> bool:
    """The IsPackable a project INHERITS from the Directory.Build.props chain.

    🚨 This models MSBuild rather than guessing, and it errs toward TRUE — because the
    caller subtracts what we pack from what is published, so believing a project packs can
    only SPARE a package, while wrongly believing it does not is what unlists a live one.

    MSBuild auto-imports only the NEAREST Directory.Build.props; a chain continues solely
    because a props file explicitly imports the one above it (the GetPathOfFileAbove
    idiom this repository uses). So: walk up from the project; at each props file, an
    explicit <IsPackable> is the answer; otherwise continue ONLY if that file imports the
    one above. A props file that neither declares nor imports ends the chain at MSBuild's
    own default, which is true.
    """
    directory = project.parent
    while True:
        candidate = directory / "Directory.Build.props"
        if candidate.is_file():
            text = candidate.read_text(encoding="utf-8", errors="replace")
            declared = IS_PACKABLE.search(text)
            if declared:
                return declared.group(1).lower() == "true"
            if not IMPORTS_ABOVE.search(text):
                return True          # chain stops here; MSBuild's default is packable
        if directory == root or directory.parent == directory:
            return True
        directory = directory.parent


def packable(root: Path) -> set[str]:
    """Package ids this tree still produces.

    The id is the assembly/project name unless <PackageId> overrides it. A project that
    sets IsPackable=false produces nothing and is therefore NOT evidence that we still
    ship it — but it is also not evidence that we don't, so it is simply excluded here
    and the orphan check below is what decides.

    🚨 IsPackable is INHERITED. Since 2026-09-07 this repository defaults it to false in the
    root Directory.Build.props (Doc/Architecture/NuGetPackageRetirement) and exactly one
    project opts back in, so reading only the csproj would report every project as packable
    and the orphan set would be empty — a subtraction that quietly retires nothing. The
    inherited value is resolved by directory_default() above.
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
        # only SPARE a package; under-counting unlists a live one. MeshWeaver.Compiler.Cli was
        # the worked example: it shipped from tools/ behind -p:PackCompilerTool=true, and a
        # tools/ exclusion listed it as orphaned. (That opt-in was removed in the 2026-09-07
        # retirement and the package is now deliberately orphaned — but the rule it taught is
        # about WHERE a package can ship from, and tools/ is still such a place.)
        relative = project.relative_to(root).parts
        if any(part in {"bin", "obj", ".worktrees"} for part in relative):
            continue
        text = project.read_text(encoding="utf-8", errors="replace")

        # EVERY declared <PackageId>, regardless of the condition it sits under. Packing can be
        # conditional: a project may be IsPackable=false by default and pack only under an opt-in
        # property, with its id declared inside that same conditional PropertyGroup. Reading
        # IsPackable first and skipping made such a project invisible, and a package we very much
        # still shipped was reported as orphaned. MeshWeaver.Compiler.Cli was that project until
        # its opt-in was removed on 2026-09-07; the shape is pinned by the self-test rather than
        # by any project currently in the tree. An id that appears anywhere in a csproj is
        # evidence we own it — and therefore evidence NOT to unlist it.
        declared = set(re.findall(r"<PackageId>\s*([^<]+?)\s*</PackageId>", text, re.I))
        ids |= declared

        own = IS_PACKABLE.search(text)
        is_packable = (own.group(1).lower() == "true") if own \
            else directory_default(project, root)
        if not is_packable and not declared:
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

    # 🚨 IsPackable is INHERITED from Directory.Build.props, and this repository now defaults it
    # to FALSE with exactly one opt-in. Reading only the csproj called every project packable and
    # the orphan set came out EMPTY — a subtraction that retires nothing while reporting success.
    # The mirror-image error is the dangerous one, so the chain rules are pinned in both
    # directions: inherit false, honour an explicit opt-in, and STOP at a props file that neither
    # declares IsPackable nor imports the one above (MSBuild auto-imports only the nearest, so the
    # chain is real only where a file continues it — and where it stops, the default is true).
    with tempfile.TemporaryDirectory() as temporary:
        root = Path(temporary)
        (root / "Directory.Build.props").write_text(
            "<Project><PropertyGroup><IsPackable>false</IsPackable></PropertyGroup></Project>",
            encoding="utf-8")

        (root / "src" / "MeshWeaver.Inherited").mkdir(parents=True)
        (root / "src" / "MeshWeaver.Inherited" / "MeshWeaver.Inherited.csproj").write_text(
            "<Project><PropertyGroup></PropertyGroup></Project>", encoding="utf-8")

        (root / "src" / "MeshWeaver.OptedIn").mkdir(parents=True)
        (root / "src" / "MeshWeaver.OptedIn" / "MeshWeaver.OptedIn.csproj").write_text(
            "<Project><PropertyGroup><IsPackable>true</IsPackable></PropertyGroup></Project>",
            encoding="utf-8")

        # A subtree whose own props declares nothing and does NOT import the root: MSBuild stops
        # there and the default is true, so the root's false must NOT reach through it.
        (root / "detached" / "MeshWeaver.Detached").mkdir(parents=True)
        (root / "detached" / "Directory.Build.props").write_text(
            "<Project><PropertyGroup><Nullable>enable</Nullable></PropertyGroup></Project>",
            encoding="utf-8")
        (root / "detached" / "MeshWeaver.Detached" / "MeshWeaver.Detached.csproj").write_text(
            "<Project><PropertyGroup></PropertyGroup></Project>", encoding="utf-8")

        # A subtree whose props declares nothing but DOES import the one above: the chain continues
        # and the root's false applies.
        (root / "chained" / "MeshWeaver.Chained").mkdir(parents=True)
        (root / "chained" / "Directory.Build.props").write_text(
            "<Project><Import Project=\"$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', "
            "'$(MSBuildThisFileDirectory)../'))\" /><PropertyGroup></PropertyGroup></Project>",
            encoding="utf-8")
        (root / "chained" / "MeshWeaver.Chained" / "MeshWeaver.Chained.csproj").write_text(
            "<Project><PropertyGroup></PropertyGroup></Project>", encoding="utf-8")

        found = packable(root)
        if "MeshWeaver.Inherited" in found:
            failures.append("a project inheriting IsPackable=false counted as shipped")
        if "MeshWeaver.OptedIn" not in found:
            failures.append("an explicit IsPackable=true did not override the inherited false")
        if "MeshWeaver.Detached" not in found:
            failures.append("a props file that neither declares nor imports did not stop the chain "
                            "at MSBuild's default (true) — this direction UNLISTS a live package")
        if "MeshWeaver.Chained" in found:
            failures.append("an importing props file did not carry the inherited false through")

    for failure in failures:
        print(f"FAIL: {failure}")
    print("self-test: " + ("PASSED" if not failures else f"{len(failures)} FAILURE(S)"))
    return 1 if failures else 0


def is_listed(package_id: str, version: str) -> bool | None:
    """Whether nuget.org still LISTS this version. None when it cannot be read.

    🚨 This is the ONLY evidence that an unlist happened. `dotnet nuget delete` returning 0 is
    not: it reports that the call was made, not the resulting state, and its output is easy to
    swallow. The first version of this script trusted the exit code, printed "unlisted: …"
    fifty-five times, and the registration still read listed=true ten minutes later — an outcome
    indistinguishable from success.
    """
    url = ("https://api.nuget.org/v3/registration5-gz-semver2/"
           f"{package_id.lower()}/{version.lower()}.json")
    request = urllib.request.Request(url, headers={"Accept-Encoding": "gzip"})
    try:
        with urllib.request.urlopen(request, timeout=45) as response:
            raw = response.read()
            if response.headers.get("Content-Encoding") == "gzip":
                raw = gzip.decompress(raw)
            return bool(json.loads(raw).get("listed"))
    except Exception:
        return None


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
    parser.add_argument("--verify-attempts", type=int, default=6,
                        help="how many times to re-read nuget.org before giving a verdict")
    parser.add_argument("--verify-wait", type=int, default=60,
                        help="seconds between verification attempts")
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
    called: list[tuple[str, str]] = []
    failed = 0
    for package_id in orphans:
        for version in versions(package_id):
            result = subprocess.run(
                ["dotnet", "nuget", "delete", package_id, version,
                 "--source", "https://api.nuget.org/v3/index.json",
                 "--api-key", args.api_key, "--non-interactive"],
                capture_output=True, text=True)
            # ALWAYS echo what the tool said. A silent success is how a no-op passes for a write.
            said = " | ".join(
                line.strip()
                for line in ((result.stdout or "") + (result.stderr or "")).splitlines()
                if line.strip())
            if result.returncode != 0:
                failed += 1
                print(f"  CALL FAILED: {package_id} {version} (exit {result.returncode}) — {said}")
            else:
                called.append((package_id, version))
                print(f"  called delete: {package_id} {version} — {said or '(no output)'}")

    if not called:
        return 1 if failed else 0

    # 🚨 VERIFY, then report. nuget.org rebuilds its registration blobs asynchronously, so a
    # still-listed reading immediately after the call is inconclusive rather than a failure —
    # poll, and state only what is observable. Never record an unlist that was not seen.
    print(f"\nverifying {len(called)} version(s) against nuget.org …")
    pending = list(called)
    for attempt in range(1, args.verify_attempts + 1):
        still = [(p, v) for p, v in pending if is_listed(p, v) is True]
        if not still:
            print(f"  VERIFIED on attempt {attempt}: every version now reads as unlisted")
            return 1 if failed else 0
        pending = still
        if attempt < args.verify_attempts:
            print(f"  attempt {attempt}: {len(pending)} still listed — waiting "
                  f"{args.verify_wait}s for the registration to rebuild")
            time.sleep(args.verify_wait)

    print(f"\n::error::{len(pending)} version(s) STILL read as listed after "
          f"{args.verify_attempts} checks. The delete call reported success, so this is either a "
          f"slower registration rebuild than expected or an API key without unlist rights. Do NOT "
          f"record these as retired until a later check reads false.")
    for package_id, version in pending[:20]:
        print(f"  still listed: {package_id} {version}")
    return 1


if __name__ == "__main__":
    sys.exit(main())
