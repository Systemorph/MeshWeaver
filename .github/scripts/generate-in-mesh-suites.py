#!/usr/bin/env python3
"""generate-in-mesh-suites.py — regenerate samples/Graph/Data/Testing/<Suite> from test/<Project>.

    python3 .github/scripts/generate-in-mesh-suites.py            # every suite
    python3 .github/scripts/generate-in-mesh-suites.py Graph Data # some

One suite per xunit test project (the mapping is the suite's own JSON: `name` = "<Project> (in-mesh)").
Every *.cs of the project goes through convert-xunit-to-inmesh.py; a REFUSED file stays on xunit and
is named, with its reason, in the suite's description — the description is the inventory the
maintainer asked for ("pls refactor 100% to this shape": what is not yet in this shape says why).
The two shared sources are laid into every suite from .github/scripts/in-mesh/: InMeshTestBase.cs
(MonolithMeshTestBase's and HubTestBase's protected vocabulary over the live mesh) and XunitShims.cs
(xunit's Assert and the fixture's TestTimeouts, so a converted file compiles unchanged).
A suite whose every file is refused is REMOVED (its JSON and directory) — an empty Tests area is a
skip that renders like a pass. Idempotent; run it after changing the converter, the templates or
the xunit estate, and commit what it wrote.
"""
from __future__ import annotations
import importlib.util, json, pathlib, re, sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
SUITES = ROOT / "samples" / "Graph" / "Data" / "Testing"
TEMPLATES = pathlib.Path(__file__).resolve().parent / "in-mesh"
spec = importlib.util.spec_from_file_location("conv", pathlib.Path(__file__).resolve().parent / "convert-xunit-to-inmesh.py")
conv = importlib.util.module_from_spec(spec); spec.loader.exec_module(conv)  # type: ignore[union-attr]

SKIP_DIRS = {"bin", "obj", "TestResults"}


def project_of(suite_json: dict) -> str:
    m = re.match(r"(.+?) \(in-mesh\)$", suite_json["name"])
    if not m:
        raise SystemExit(f"{suite_json['id']}: name does not end in ' (in-mesh)'")
    return m.group(1)


def sources_of(project: str) -> list[pathlib.Path]:
    base = ROOT / "test" / project
    if not base.is_dir():
        raise SystemExit(f"test/{project} does not exist")
    return sorted(p for p in base.rglob("*.cs") if not (set(p.relative_to(base).parts[:-1]) & SKIP_DIRS))


def generate(suite_id: str) -> tuple[int, int, int]:
    jpath = SUITES / f"{suite_id}.json"
    doc = json.loads(jpath.read_text(encoding="utf-8"))
    project = project_of(doc)
    src_dir = SUITES / suite_id / "Source"
    if src_dir.exists():
        for old in src_dir.glob("*.cs"):
            old.unlink()
    src_dir.mkdir(parents=True, exist_ok=True)
    converted, refused = [], []
    for f in sources_of(project):
        out, why = conv.convert(f.read_text(encoding="utf-8-sig"), f"Testing/{suite_id}/{f.stem}")
        if out is None:
            refused.append((f.name, why))
            continue
        # a helper file with the same name as another (sub-folders) — keep both by qualifying
        dest = src_dir / f.name
        if dest.exists():
            dest = src_dir / f"{f.parent.name}_{f.name}"
        dest.write_text(out, encoding="utf-8")
        converted.append(dest.name)
    total = len(converted) + len(refused)
    cases = sum(len(re.findall(r"\[Mesh(Fact|Theory)\b", (src_dir / n).read_text(encoding="utf-8"))) for n in converted)
    if not cases:
        # helpers alone are not a suite: an empty Tests area is a skip that renders like a pass
        for n in converted:
            (src_dir / n).unlink()
        converted = []
    if not converted:
        for p in sorted(src_dir.parent.rglob("*"), reverse=True):
            p.unlink() if p.is_file() else p.rmdir()
        src_dir.parent.rmdir() if src_dir.parent.exists() else None
        jpath.unlink()
        print(f"  {suite_id}: {total} file(s), no test case survives — suite removed")
        return total, 0, len(refused)
    for t in ("InMeshTestBase.cs", "XunitShims.cs"):
        (src_dir / t).write_text((TEMPLATES / t).read_text(encoding="utf-8"), encoding="utf-8")
    refused_text = ("Refused, still on xunit: " + str(len(refused)) + " — "
                    + "; ".join(f"{n}: {w}" for n, w in refused)) if refused else "Nothing refused."
    doc["description"] = (f"{project} migrated in-mesh (.github/scripts/generate-in-mesh-suites.py, 2026-09-13): "
                          f"{len(converted)} of {total} files, {cases} [MeshFact]/[MeshTheory] cases, run by MeshWeaver.Testing.InMesh.MeshTestRunner "
                          f"(#4184) in the samples gate's mesh; InMeshTestBase and XunitShims laid in from .github/scripts/in-mesh/. "
                          + refused_text)
    jpath.write_text(json.dumps(doc, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"  {suite_id}: {len(converted)} converted ({cases} cases), {len(refused)} refused of {total}")
    return total, len(converted), len(refused)


def main() -> int:
    wanted = set(sys.argv[1:])
    ids = sorted(p.stem for p in SUITES.glob("*.json"))
    if wanted:
        ids = [i for i in ids if i in wanted]
    tot = con = ref = 0
    for i in ids:
        t, c, r = generate(i)
        tot += t; con += c; ref += r
    print(f"suites: {len(ids)}; files: {tot} — converted {con}, refused {ref}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
