#!/usr/bin/env python3
"""Does a packed module bundle declare EVERY static web asset its project source tree implies?

    check-bundle-static-assets.py --module MeshWeaver.Blazor.OpenStreetMap \\
        --project src/MeshWeaver.Blazor.OpenStreetMap/MeshWeaver.Blazor.OpenStreetMap.csproj \\
        --manifest $RUNNER_TEMP/inspect-<Module>/meshweaver/manifest.json \\
        --build-output <the directory the module was built/published into>
    exit 0 = every implied asset is declared (the denominator is printed either way)
    exit 1 = at least one is missing, named, with what implies it

🚨 WHY THIS EXISTS (Systemorph/MeshWeaver#2384). The lane's previous assertion was

    jq -e '(.module.staticAssets // []) | length > 0'   # …and only if the project had wwwroot/ or scoped CSS

and it is two different kinds of blind at once:

* **A COUNT CANNOT SEE A MISSING MEMBER.** `MeshWeaver.Blazor.OpenStreetMap` ships 7 vendored
  Leaflet files from its `wwwroot/`, so `7 > 0` passed — while `OpenStreetMapView.razor.js`, the one
  file its view actually imports, was absent from every bundle. Measured live on
  memex.meshweaver.cloud 2026-09-15: `leaflet.css` **200** (14,806 B), `leaflet-src.esm.js` **200**
  (424,589 B), `OpenStreetMapView.razor.js` **404** — out of the same landed module folder.
* **THE PRECONDITION SKIPPED THE WORST CASE.** `MeshWeaver.Blazor.AppleMaps` has no `wwwroot/` and
  no `*.razor.css`, so the `if` was false and the assertion never ran at all. Its whole asset
  surface is one collocated `.razor.js`, and it 404'd unexamined. A gate that asks about a
  different input than the one that breaks is not a weak gate; it is an absent one.

🚨 WHAT IT COMPARES. The bundle's declared `module.staticAssets` against the asset set the module
OWES, from two sources, each at the path `dotnet publish` lays it at:

1. `wwwroot/**` of the PROJECT source tree, verbatim   → `wwwroot/<relative>`
2. every file the BUILD emitted under its output's `wwwroot/` (`--build-output`): the assets a
   build extension's inputs imply — e.g. a GUI extension's collocated JS modules or CSS-isolation
   aggregate — which exist in no source tree at the path they are served from.
                                                       → `wwwroot/<relative>`

The platform encodes NO source kind's conventions here. Which assets an extension-compiled source
file implies is the EXTENSION's knowledge: it emits them, it refuses at
BUILD time an input whose asset cannot be produced, and its own repository tests that. What this
gate owns is the step after: the bundle must declare every asset the tree holds and the build
emitted, because a pack that drops one ships a 404 with every other gate green.

CONTAINMENT, never equality: an SDK-lane publish also materialises the module's DEPENDENCIES'
assets under `wwwroot/_content/<Dep>/…` and precompressed `.br`/`.gz` siblings; those are neither
demanded from the build output nor refused when declared.

🚨 IT RUNS FOR BOTH LANES AND HAS NO PRECONDITION. The property is about the BUNDLE, not about
which compiler produced it. A project that implies nothing prints `0 expected` and passes — that
is a stated zero, not a skipped check.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
import tempfile
from pathlib import Path

#: Directory names that are build residue or tooling, never project content. The same exclusions
#: `ProjectFile.DefaultGlob` applies, so the two producers of this set cannot disagree.
_EXCLUDED_DIRS = {"bin", "obj"}


def _skip(relative: Path) -> bool:
    return any(part in _EXCLUDED_DIRS or part.startswith(".") for part in relative.parts[:-1])


class CannotCompute(Exception):
    """The project disables an item glob this tree walk assumes. Fail closed, never guess."""


def _property_is_false(text: str, name: str) -> bool:
    return re.search(rf"<{name}\s*>\s*false\s*</{name}\s*>", text, re.IGNORECASE) is not None


def expected_assets(project: Path, module: str, build_output: Path | None = None) -> dict[str, str]:
    """The asset set the module owes, as {bundle path: why it is owed}.

    🚨 ``EnableDefaultItems`` / ``EnableDefaultContentItems`` = false means the project lists its
    own items, and a file on disk no longer implies an asset. This script is not an MSBuild
    evaluator, so it REFUSES rather than over- or under-stating the set — fail closed. (Measured
    2026-09-15: no project in MeshWeaver or MeshWeaver.Plugins sets either.)
    """
    directory = project.parent
    project_text = project.read_text(encoding="utf-8", errors="replace")
    for switch in ("EnableDefaultItems", "EnableDefaultContentItems"):
        if _property_is_false(project_text, switch):
            raise CannotCompute(
                f"{project.name} sets <{switch}>false</{switch}>, so a file on disk no longer "
                "implies a project item and this gate cannot state what the bundle owes. It "
                "refuses rather than guessing in either direction — teach it the project's own "
                "item declarations, or do not disable the glob.")
    implied: dict[str, str] = {}

    wwwroot = directory / "wwwroot"
    if wwwroot.is_dir():
        for file in sorted(wwwroot.rglob("*")):
            if not file.is_file():
                continue
            relative = file.relative_to(wwwroot)
            if _skip(relative):
                continue
            implied[f"wwwroot/{relative.as_posix()}"] = f"wwwroot/{relative.as_posix()} in the source tree"

    # Kind 2 — what the BUILD emitted (a build extension's assets). Its dependencies' _content and
    # the precompressed siblings an SDK publish adds are not the module's own assets.
    if build_output is not None:
        if not build_output.is_dir():
            raise CannotCompute(
                f"the build output {build_output} does not exist, so the assets the build emitted "
                "cannot be compared with the bundle — refusing rather than reading 'no build "
                "output' as 'nothing emitted'.")
        built = build_output / "wwwroot"
        if built.is_dir():
            for file in sorted(built.rglob("*")):
                if not file.is_file():
                    continue
                relative = file.relative_to(built)
                if relative.parts[0] == "_content" or file.suffix in (".br", ".gz"):
                    continue
                key = f"wwwroot/{relative.as_posix()}"
                implied.setdefault(key, f"{key} was emitted by the build ({built})")

    return implied


class CannotReadManifest(Exception):
    """The bundle manifest exists but could not be read as JSON."""


def declared_assets(manifest: Path) -> list[str]:
    # 🚨 utf-8-sig, not utf-8. The manifest is written by the PACK — .NET's default
    # UTF8Encoding emits a byte-order mark — while this gate is Python, whose "utf-8"
    # codec treats a BOM as content and makes json.loads raise. utf-8-sig decodes a
    # plain UTF-8 file identically and additionally strips the mark, so it is correct
    # for both producers and can never be the narrower choice.
    try:
        document = json.loads(manifest.read_text(encoding="utf-8-sig"))
    except (json.JSONDecodeError, UnicodeDecodeError, OSError) as exc:
        raise CannotReadManifest(f"{manifest}: {type(exc).__name__}: {exc}") from exc

    # 🚨 Parsing is not validating. `[]`, `{"module": []}` and a string `staticAssets` are all
    # valid JSON, and each one raises AttributeError/TypeError out of the `.get`/`list` below —
    # i.e. the crash this refusal exists to replace, one shape further in. Check the shape.
    if not isinstance(document, dict):
        raise CannotReadManifest(
            f"{manifest}: the manifest's top level is {type(document).__name__}, not an object")
    # Read each value BEFORE coercing it: `x or {}` silently rescues an empty list, so a
    # `"module": []` would pass a check written after the coercion and fail one written before.
    module = document.get("module")
    if module is None:
        module = {}
    if not isinstance(module, dict):
        raise CannotReadManifest(
            f"{manifest}: 'module' is {type(module).__name__}, not an object")
    assets = module.get("staticAssets")
    if assets is None:
        assets = []
    if not isinstance(assets, list) or not all(isinstance(a, str) for a in assets):
        raise CannotReadManifest(
            f"{manifest}: 'module.staticAssets' is not a list of strings "
            f"(got {type(assets).__name__})")
    return list(assets)


def check(module: str, project: Path, manifest: Path, *, build_output: Path | None = None,
          out=sys.stdout) -> int:
    try:
        implied = expected_assets(project, module, build_output)
    except CannotCompute as refusal:
        print(f"::error::{module}: {refusal}", file=out)
        return 1
    # 🚨 An unreadable manifest is a REFUSAL, not a crash. An uncaught traceback is
    # indistinguishable from the harness dying, and a reader chases the pack instead of
    # the one line that names the file — which is exactly what a UTF-8 BOM cost the whole
    # Plugins bundle lane on 2026-09-15.
    try:
        declared = declared_assets(manifest)
    except CannotReadManifest as refusal:
        print(f"::error::{module}: the bundle manifest could not be read, so its declared "
              f"static assets cannot be compared with what the project implies — {refusal}",
              file=out)
        return 1
    have = set(declared)
    missing = {path: why for path, why in sorted(implied.items()) if path not in have}

    built = sum(1 for why in implied.values() if "emitted by the build" in why)
    print(
        f"{module}: {len(implied)} asset(s) owed ({len(implied) - built} wwwroot file(s) in the "
        f"source tree, {built} emitted by the build), {len(declared)} declared by the bundle, "
        f"{len(missing)} missing",
        file=out)

    if not missing:
        return 0

    for path, why in missing.items():
        print(f"::error::{module}: the bundle declares no static asset '{path}' — {why}. "
              "A module's assets are served at _content/<Name>/<that same path>, so a browser "
              "asking for it gets a 404 and the view that imports it renders nothing, with the "
              "publish, the landing and the module load all green (Systemorph/MeshWeaver#2384).",
              file=out)
    print(f"::error::{module}: {len(missing)} of {len(implied)} owed static asset(s) are absent "
          "from the bundle. A COUNT cannot see this — the other assets are present, which is "
          "exactly how it shipped.", file=out)
    return 1


# ── self-test ───────────────────────────────────────────────────────────────────────────────
def _tree(root: Path, files: dict[str, str]) -> Path:
    for relative, content in files.items():
        path = root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content, encoding="utf-8")
    return root


def _manifest(root: Path, module: str, assets: list[str] | None) -> Path:
    path = root / "manifest.json"
    path.write_text(json.dumps({
        "plugin": "P", "version": "1.0.0",
        "module": {"assemblyName": module, "staticAssets": assets},
    }), encoding="utf-8")
    return path


#: The real packs, at the shapes that produced #2384, as source tree + what the build emitted.
#: OSM is why a COUNT passed (vendored wwwroot files present, the view's own module absent);
#: AppleMaps is why the PRECONDITION skipped (no wwwroot at all — its one asset exists only in the
#: build output).
_OSM_TREE = {"Osm.csproj": "<Project/>", "wwwroot/leaflet/leaflet.css": ".l{}",
             "wwwroot/leaflet/leaflet-src.esm.js": "//l"}
_OSM_BUILT = {"wwwroot/leaflet/leaflet.css": ".l{}", "wwwroot/leaflet/leaflet-src.esm.js": "//l",
              "wwwroot/OpenStreetMapView.module.js": "export function i(){}"}
_APPLE_TREE = {"Apple.csproj": "<Project/>", "AppleMapView.cs": "class A{}"}
_APPLE_BUILT = {"wwwroot/AppleMapView.module.js": "export function i(){}",
                "wwwroot/Apple.styles.css": ".a{}"}

_CASES: list[tuple[str, dict[str, str], dict[str, str] | None, list[str] | None, int, str]] = [
    # (name, source tree, build output (None = not given), declared staticAssets, exit, needle)
    ("OSM pre-fix — the tree's files present, the build-emitted module absent",
     _OSM_TREE, _OSM_BUILT, ["wwwroot/leaflet/leaflet.css", "wwwroot/leaflet/leaflet-src.esm.js"], 1,
     "OpenStreetMapView.module.js"),
    ("OSM fixed — the build-emitted module is declared",
     _OSM_TREE, _OSM_BUILT, ["wwwroot/leaflet/leaflet.css", "wwwroot/leaflet/leaflet-src.esm.js",
                             "wwwroot/OpenStreetMapView.module.js"], 0, "0 missing"),
    ("AppleMaps pre-fix — no wwwroot in the tree, staticAssets null (the SKIPPED case)",
     _APPLE_TREE, _APPLE_BUILT, None, 1, "AppleMapView.module.js"),
    ("AppleMaps fixed",
     _APPLE_TREE, _APPLE_BUILT, ["wwwroot/AppleMapView.module.js", "wwwroot/Apple.styles.css"], 0,
     "2 emitted by the build"),
    ("#2221's half — the build emitted a stylesheet aggregate the bundle does not declare",
     _APPLE_TREE, _APPLE_BUILT, ["wwwroot/AppleMapView.module.js"], 1, "Apple.styles.css"),
    ("a source-tree wwwroot file dropped from the bundle",
     _OSM_TREE, _OSM_BUILT, ["wwwroot/leaflet/leaflet.css", "wwwroot/OpenStreetMapView.module.js"], 1,
     "leaflet-src.esm.js"),
    ("a module with no assets at all — a STATED zero, not a skip",
     {"Plain.csproj": "<Project/>", "Thing.cs": "class T{}"}, {}, None, 0, "0 asset(s) owed"),
    ("no build output given — only the source tree is owed (negative control of the build kind)",
     _APPLE_TREE, None, None, 0, "0 emitted by the build"),
    ("an SDK-lane bundle declaring MORE than owed (deps' _content, .br/.gz) passes, and those are not demanded",
     _APPLE_TREE, {**_APPLE_BUILT, "wwwroot/_content/MeshWeaver.Dep/x.css": "//",
                   "wwwroot/Apple.styles.css.br": "//"},
     ["wwwroot/AppleMapView.module.js", "wwwroot/Apple.styles.css", "wwwroot/AppleMapView.module.js.br",
      "wwwroot/_content/MeshWeaver.Dep/x.css"], 0, "0 missing"),
    ("EnableDefaultItems=false — the walk would be a guess, so the gate REFUSES",
     {"NoGlob.csproj": "<Project><PropertyGroup><EnableDefaultItems>false</EnableDefaultItems></PropertyGroup></Project>",
      "wwwroot/x.js": "//"}, {}, ["wwwroot/x.js"], 1, "cannot state what the bundle owes"),
    ("build residue in the source tree is never an asset",
     {"Res.csproj": "<Project/>", "wwwroot/site.css": ".s{}", "obj/Release/Ghost.js": "//",
      "bin/Release/wwwroot/Ghost.css": "//"}, {}, ["wwwroot/site.css"], 0, "1 asset(s) owed"),
]


def self_test() -> int:
    import io

    failures = 0
    for index, (title, files, built, declared, want_exit, needle) in enumerate(_CASES):
        with tempfile.TemporaryDirectory() as raw:
            root = _tree(Path(raw) / "proj", files)
            project = next(root.glob("*.csproj"))
            module = project.stem
            build_output = _tree(Path(raw) / "out", built) if built is not None else None
            if build_output is not None:
                build_output.mkdir(parents=True, exist_ok=True)
            buffer = io.StringIO()
            got = check(module, project, _manifest(Path(raw), module, declared),
                        build_output=build_output, out=buffer)
            output = buffer.getvalue()
            problems = []
            if got != want_exit:
                problems.append(f"exit {got}, wanted {want_exit}")
            if needle not in output:
                problems.append(f"output does not mention {needle!r}")
            if problems:
                failures += 1
                print(f"  FAIL  [{index}] {title}: {'; '.join(problems)}\n{output}")
            else:
                print(f"  ok    [{index}] {title}")

    # A named build output that does not exist is a refusal, never "nothing emitted".
    with tempfile.TemporaryDirectory() as raw:
        root = _tree(Path(raw) / "proj", {"Gone.csproj": "<Project/>"})
        buffer = io.StringIO()
        got = check("Gone", next(root.glob("*.csproj")), _manifest(Path(raw), "Gone", None),
                    build_output=Path(raw) / "no-such-output", out=buffer)
        if got != 1 or "does not exist" not in buffer.getvalue():
            failures += 1
            print(f"  FAIL  [out] a missing build output must refuse: exit {got}, {buffer.getvalue()!r}")
        else:
            print("  ok    [out] a missing build output REFUSES instead of reading as 'nothing emitted'")

    # ── the manifest READER, which the table above cannot reach ────────────────────────────
    # Every case above writes its manifest with this file's own helper, so none of them can
    # see how a manifest written by SOMEONE ELSE decodes. These two do.
    reader_cases = [
        ("a BOM-prefixed manifest (as .NET writes it) is read, not rejected",
         lambda path: path.write_bytes(
             b"\xef\xbb\xbf" + json.dumps({
                 "plugin": "P", "version": "1.0.0",
                 "module": {"assemblyName": "Plain", "staticAssets": []},
             }).encode("utf-8")),
         0, "0 asset(s) owed"),
        ("a manifest that is not JSON REFUSES by name instead of raising",
         lambda path: path.write_text("{ not json", encoding="utf-8"),
         1, "the bundle manifest could not be read"),
        ("a STRUCTURALLY malformed manifest REFUSES too — parsing is not validating",
         lambda path: path.write_text('{"module": []}', encoding="utf-8"),
         1, "'module' is list, not an object"),
        ("a manifest whose top level is not an object REFUSES",
         lambda path: path.write_text("[]", encoding="utf-8"),
         1, "top level is list, not an object"),
    ]
    for title, write, want_exit, needle in reader_cases:
        with tempfile.TemporaryDirectory() as raw:
            root = _tree(Path(raw) / "proj", {"Plain.csproj": "<Project/>"})
            manifest = Path(raw) / "manifest.json"
            write(manifest)
            buffer = io.StringIO()
            try:
                got = check("Plain", next(root.glob("*.csproj")), manifest, out=buffer)
            except Exception as exc:                      # noqa: BLE001 — that IS the defect
                failures += 1
                print(f"  FAIL  [bom] {title}: raised {type(exc).__name__}: {exc}")
                continue
            output = buffer.getvalue()
            if got != want_exit or needle not in output:
                failures += 1
                print(f"  FAIL  [bom] {title}: exit {got} (wanted {want_exit}); "
                      f"output {output!r}")
            else:
                print(f"  ok    [bom] {title}")

    total = len(_CASES) + len(reader_cases) + 1
    fires = sum(1 for case in _CASES if case[4] == 1) + sum(1 for case in reader_cases if case[2] == 1) + 1
    print(f"\n{total} case(s) — {len(_CASES)} asset-shape + {len(reader_cases)} manifest-reader + 1 build-output: "
          f"{fires} must FAIL the gate, {total - fires} must PASS it.")
    if failures:
        print(f"FAILED — {failures} case(s) did not behave as stated")
        return 1
    print("PASS — the gate fires on every shape #2384 shipped through, and stays silent on the "
          "shapes that are legitimately assetless.")
    return 0


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--module", help="the module's assembly name")
    ap.add_argument("--project", help="path to the module project's .csproj")
    ap.add_argument("--manifest", help="path to the packed bundle's meshweaver/manifest.json")
    ap.add_argument("--build-output",
                    help="the directory the module was built or published into; every file under "
                         "its wwwroot/ is owed by the bundle")
    ap.add_argument("--self-test", action="store_true",
                    help="prove the gate fires and stays silent on the right inputs")
    args = ap.parse_args(argv)

    if args.self_test:
        return self_test()
    if not (args.module and args.project and args.manifest):
        ap.error("--module, --project and --manifest are all required")

    project, manifest = Path(args.project), Path(args.manifest)
    # 🚨 FAIL CLOSED on a missing input. "The asset set could not be computed" must never be
    # reported the same way as "it was computed and it is complete" — that equivalence is the
    # defect this file exists to prevent, one level up.
    if not project.is_file():
        print(f"::error::{args.module}: no project at {project}, so the bundle's static assets "
              "cannot be checked against anything.", file=sys.stderr)
        return 1
    if not manifest.is_file():
        print(f"::error::{args.module}: no bundle manifest at {manifest}.", file=sys.stderr)
        return 1
    return check(args.module, project, manifest,
                 build_output=Path(args.build_output) if args.build_output else None)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
