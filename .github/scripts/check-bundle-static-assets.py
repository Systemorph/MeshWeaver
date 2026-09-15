#!/usr/bin/env python3
"""Does a packed module bundle declare EVERY static web asset its project source tree implies?

    check-bundle-static-assets.py --module MeshWeaver.Blazor.OpenStreetMap \\
        --project src/MeshWeaver.Blazor.OpenStreetMap/MeshWeaver.Blazor.OpenStreetMap.csproj \\
        --manifest $RUNNER_TEMP/inspect-<Module>/meshweaver/manifest.json
    exit 0 = every implied asset is declared (the denominator is printed either way)
    exit 1 = at least one is missing, named, with what implies it

🚨 WHY THIS EXISTS (Systemorph/MeshWeaver#2384). The lane's previous assertion was

    jq -e '(.module.staticAssets // []) | length > 0'   # …and only if the project has wwwroot/ or *.razor.css

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

🚨 WHAT IT COMPARES. The bundle's declared `module.staticAssets` against the asset set the PROJECT
SOURCE TREE implies — the three kinds the Razor SDK produces, each at the path `dotnet publish`
lays it at (measured against SDK 10.0.400, 2026-09-15):

1. `wwwroot/**`, verbatim                          → `wwwroot/<relative>`
2. a collocated `Foo.razor.js` beside `Foo.razor`  → `wwwroot/<path relative to the project>`
3. the CSS-isolation aggregate, when the project has any `*.razor.css`
                                                   → `wwwroot/<Module>.styles.css`

CONTAINMENT, never equality: an SDK-lane publish also materialises the module's DEPENDENCIES'
assets under `wwwroot/_content/<Dep>/…` and precompressed `.br`/`.gz` siblings, and a bundle
declaring more than its own tree implies is correct.

🚨 AN UNPAIRED `*.razor.js` IS NOT EXPECTED — and that is not a loophole. The SDK refuses it
(`BLAZOR106`, an error, measured to fire for a file under `wwwroot/` as well), and so does
`ProjectBuild.EmitJsModules`; demanding a file no producer will ever emit would make this gate red
on a project whose real defect is a rename. The build is where that case fails, by name.

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


def expected_assets(project: Path, module: str) -> dict[str, str]:
    """The asset set the project tree implies, as {bundle path: why it is implied}.

    🚨 Two MSBuild properties would make this walk WRONG rather than merely coarse, so each is
    read and handled exactly:

    * ``EnableDefaultItems`` / ``EnableDefaultContentItems`` = false means the project lists its
      own items, and a file on disk no longer implies an asset. This script is not an MSBuild
      evaluator, so it REFUSES rather than over- or under-stating the set — fail closed. (Measured
      2026-09-15: no project in MeshWeaver or MeshWeaver.Plugins sets either, so this costs the
      fleet nothing today and cannot silently start guessing tomorrow.)
    * ``ScopedCssEnabled`` = false means the SDK emits no ``.styles.css`` aggregate, so neither
      does this gate expect one.
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
    scoped_css_enabled = not _property_is_false(project_text, "ScopedCssEnabled")
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

    # Kind 2 — collocated JS modules, PAIRED. A file already under wwwroot/ was counted above.
    for suffix in (".razor.js", ".cshtml.js"):
        for file in sorted(directory.rglob(f"*{suffix}")):
            if not file.is_file():
                continue
            relative = file.relative_to(directory)
            if _skip(relative) or relative.parts[0] == "wwwroot":
                continue
            component = file.with_name(file.name[: -len(".js")])
            if not component.is_file():
                # BLAZOR106 territory — the build refuses it; this gate must not demand it.
                continue
            implied[f"wwwroot/{relative.as_posix()}"] = (
                f"{relative.as_posix()} is collocated with {component.name}")

    # Kind 3 — the CSS-isolation aggregate, if and only if the project has scoped CSS.
    for file in directory.rglob("*.razor.css") if scoped_css_enabled else []:
        if file.is_file() and not _skip(file.relative_to(directory)):
            implied[f"wwwroot/{module}.styles.css"] = (
                f"the project has scoped CSS ({file.relative_to(directory).as_posix()}), so the SDK "
                "emits a .styles.css aggregate")
            break

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


def check(module: str, project: Path, manifest: Path, *, out=sys.stdout) -> int:
    try:
        implied = expected_assets(project, module)
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

    js_modules = sum(1 for p in implied if p.endswith((".razor.js", ".cshtml.js")))
    aggregate = 1 if f"wwwroot/{module}.styles.css" in implied else 0
    print(
        f"{module}: {len(implied)} asset(s) implied by the source tree "
        f"({len(implied) - js_modules - aggregate} wwwroot file(s), {js_modules} collocated JS "
        f"module(s), {aggregate} scoped-CSS aggregate), {len(declared)} declared by the bundle, "
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
    print(f"::error::{module}: {len(missing)} of {len(implied)} implied static asset(s) are absent "
          "from the bundle. A COUNT cannot see this — the other assets are present, which is "
          "exactly how it shipped.", file=out)
    if any(path.endswith((".razor.js", ".cshtml.js")) for path in missing):
        # 🚨 The one red whose cause is NOT in the diff in front of you. This is a TRUE failure —
        # the bundle really would 404 — and it clears itself, so it is named rather than softened.
        print(f"::error::{module}: the missing asset(s) are collocated JS modules. On the "
              "`container` lane the emitter is `mw-plugin-test` INSIDE THE PINNED PLATFORM IMAGE, "
              "not a script this lane fetches — so a lane still pinned to an image built before "
              "MeshWeaver#2384 reds here with nothing wrong in its own diff. The fix is to move "
              "this lane's platform set to one carrying that builder; the bundle it is producing "
              "right now genuinely 404s the file, so this must not be waved through.", file=out)
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


#: The real packs, at the shapes that produced #2384. OSM is why a COUNT passed; AppleMaps is why
#: the PRECONDITION skipped; GoogleMaps has a .razor.css and no wwwroot, so it satisfied the
#: precondition with the aggregate alone while its map JS was missing.
_OSM = {
    "Osm.csproj": "<Project/>",
    "wwwroot/leaflet/leaflet.css": ".l{}",
    "wwwroot/leaflet/leaflet-src.esm.js": "//l",
    "OpenStreetMapView.razor": "<div/>",
    "OpenStreetMapView.razor.js": "export function i(){}",
}
_APPLE = {
    "Apple.csproj": "<Project/>",
    "AppleMapView.razor": "<div/>",
    "AppleMapView.razor.js": "export function i(){}",
}
_GOOGLE = {
    "Google.csproj": "<Project/>",
    "GoogleMapView.razor": "<div/>",
    "GoogleMapView.razor.css": ".g{}",
    "GoogleMapView.razor.js": "export function i(){}",
}

_CASES: list[tuple[str, dict[str, str], list[str] | None, int, str]] = [
    # (name, source tree, declared staticAssets, expected exit, a string the output must contain)
    ("OSM pre-fix — 7 wwwroot files present, the view's own JS absent",
     _OSM, ["wwwroot/leaflet/leaflet.css", "wwwroot/leaflet/leaflet-src.esm.js"], 1,
     "OpenStreetMapView.razor.js"),
    ("OSM fixed — the collocated JS is declared",
     _OSM, ["wwwroot/leaflet/leaflet.css", "wwwroot/leaflet/leaflet-src.esm.js",
            "wwwroot/OpenStreetMapView.razor.js"], 0, "0 missing"),
    ("AppleMaps pre-fix — no wwwroot, no scoped CSS, staticAssets null (the SKIPPED case)",
     _APPLE, None, 1, "AppleMapView.razor.js"),
    ("AppleMaps fixed",
     _APPLE, ["wwwroot/AppleMapView.razor.js"], 0, "1 collocated JS module(s)"),
    ("GoogleMaps pre-fix — the aggregate satisfied the old count, the JS was still absent",
     _GOOGLE, ["wwwroot/Google.styles.css"], 1, "GoogleMapView.razor.js"),
    ("GoogleMaps fixed",
     _GOOGLE, ["wwwroot/Google.styles.css", "wwwroot/GoogleMapView.razor.js"], 0, "0 missing"),
    ("#2221's half — scoped CSS present, the aggregate not declared",
     _GOOGLE, ["wwwroot/GoogleMapView.razor.js"], 1, "Google.styles.css"),
    ("a wwwroot file dropped from the bundle",
     _OSM, ["wwwroot/leaflet/leaflet.css", "wwwroot/OpenStreetMapView.razor.js"], 1,
     "leaflet-src.esm.js"),
    ("a module with no assets at all — a STATED zero, not a skip",
     {"Plain.csproj": "<Project/>", "Thing.cs": "class T{}"}, None, 0, "0 asset(s) implied"),
    ("an UNPAIRED *.razor.js is not demanded — the build refuses it by name instead",
     {"Orphan.csproj": "<Project/>", "Stranded.razor.js": "export function s(){}"}, None, 0,
     "0 asset(s) implied"),
    ("an SDK-lane bundle declaring MORE than the tree implies (deps' _content, .br/.gz) passes",
     _APPLE, ["wwwroot/AppleMapView.razor.js", "wwwroot/AppleMapView.razor.js.br",
              "wwwroot/_content/MeshWeaver.Blazor/x.css"], 0, "0 missing"),
    ("ScopedCssEnabled=false — the SDK emits no aggregate, so neither is one demanded",
     {"NoCss.csproj": "<Project><PropertyGroup><ScopedCssEnabled>false</ScopedCssEnabled></PropertyGroup></Project>",
      "V.razor": "<div/>", "V.razor.css": ".v{}", "V.razor.js": "export function v(){}"},
     ["wwwroot/V.razor.js"], 0, "0 scoped-CSS aggregate"),
    ("EnableDefaultItems=false — the walk would be a guess, so the gate REFUSES",
     {"NoGlob.csproj": "<Project><PropertyGroup><EnableDefaultItems>false</EnableDefaultItems></PropertyGroup></Project>",
      "V.razor": "<div/>", "V.razor.js": "export function v(){}"},
     ["wwwroot/V.razor.js"], 1, "cannot state what the bundle owes"),
    ("build residue is never an asset",
     {"Res.csproj": "<Project/>", "V.razor": "<div/>", "V.razor.js": "export function v(){}",
      "obj/Release/Ghost.razor.js": "//", "bin/Release/wwwroot/Ghost.css": "//"},
     ["wwwroot/V.razor.js"], 0, "1 asset(s) implied"),
]


def self_test() -> int:
    import io

    failures = 0
    for index, (title, files, declared, want_exit, needle) in enumerate(_CASES):
        with tempfile.TemporaryDirectory() as raw:
            root = _tree(Path(raw) / "proj", files)
            project = next(root.glob("*.csproj"))
            module = {"Osm": "OpenStreetMap", "Apple": "Apple", "Google": "Google",
                      "Plain": "Plain", "Orphan": "Orphan", "Res": "Res",
                      "NoCss": "NoCss", "NoGlob": "NoGlob"}[project.stem]
            buffer = io.StringIO()
            got = check(module, project, _manifest(Path(raw), module, declared), out=buffer)
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
         0, "0 asset(s) implied"),
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

    total = len(_CASES) + len(reader_cases)
    fires = sum(1 for case in _CASES if case[3] == 1) + sum(1 for case in reader_cases if case[2] == 1)
    print(f"\n{total} case(s) — {len(_CASES)} asset-shape + {len(reader_cases)} manifest-reader: "
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
    return check(args.module, project, manifest)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
