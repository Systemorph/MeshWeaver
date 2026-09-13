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

# Files the converter accepts but the in-mesh compile still refuses for a reason no rule expresses well —
# each a HAND PORT, named with what it needs. Shrink this list, never grow it silently.
HAND_PORT = {
    "TaskSchedulerInvariantTest.cs": "reads a typed Message off hub.Observe(request), which is untyped in-mesh — needs the typed Observe",
    "QuiescingHubRefusesNewWorkTest.cs": "builds Task<IMessageDelivery<T>> from the untyped hub.Observe — needs the typed Observe",
}


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


def content_surface() -> set[str]:
    """FrameworkBuildIdentity.ContentSurfaceAssemblies — the assemblies an in-mesh source can bind."""
    text = (ROOT / "src" / "MeshWeaver.Compiler" / "FrameworkBuildIdentity.cs").read_text(encoding="utf-8")
    body = text[text.index("ContentSurfaceAssemblies ="):]
    body = body[:body.index("];")]
    return set(re.findall(r'"(MeshWeaver\.[\w.]+)"', body))


def remove_suite(suite_id: str) -> None:
    jpath = SUITES / f"{suite_id}.json"
    d = SUITES / suite_id
    if d.exists():
        for p in sorted(d.rglob("*"), reverse=True):
            p.unlink() if p.is_file() else p.rmdir()
        d.rmdir()
    if jpath.exists():
        jpath.unlink()


def generate(suite_id: str) -> tuple[int, int, int]:
    jpath = SUITES / f"{suite_id}.json"
    doc = json.loads(jpath.read_text(encoding="utf-8"))
    project = project_of(doc)
    under_test = project[:-len(".Test")] if project.endswith(".Test") else project
    if under_test not in content_surface():
        # a suite that binds an assembly the framework identity does not carry (build tooling, a host,
        # the Orleans silo, the doc content project) cannot compile in-mesh — it stays on xunit whole
        n = len(sources_of(project))
        remove_suite(suite_id)
        print(f"  {suite_id}: {n} file(s) — {under_test} is not a content-surface assembly (FrameworkBuildIdentity); suite stays on xunit")
        return n, 0, n
    src_dir = SUITES / suite_id / "Source"
    if src_dir.exists():
        for old in src_dir.glob("*.cs"):
            old.unlink()
    src_dir.mkdir(parents=True, exist_ok=True)
    converted, refused = [], []
    for f in sources_of(project):
        if f.name in HAND_PORT:
            refused.append((f.name, HAND_PORT[f.name]))
            continue
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
    # a type declared in a REFUSED file is gone for the suite: every converted file that names it is
    # refused too, until the set is stable (declarations only — never the word in prose)
    decl = re.compile(r"^\s*(?:public|internal|private|protected|static|sealed|abstract|partial|file|\s)*\s*(?:class|record|struct|interface|enum)\s+(\w+)", re.M)
    refused_by_name = {n for n, _ in refused}
    while True:
        gone: set[str] = set()
        for f in sources_of(project):
            if f.name in refused_by_name:
                gone |= set(decl.findall(f.read_text(encoding="utf-8-sig")))
        gone -= {"InMeshTestBase", "Assert", "Record", "TestTimeouts"}
        newly = []
        for n in list(converted):
            text = (src_dir / n).read_text(encoding="utf-8")
            mine = set(decl.findall(text))
            # a TYPE reference: not a member access (`Assert.Empty(`, `Task.Run(`), not part of a longer name
            hit = sorted(t for t in gone - mine if re.search(r"(?<![\w.])" + re.escape(t) + r"\b", text))
            if hit:
                newly.append((n, f"uses {', '.join(hit[:3])} declared in a refused file"))
        if not newly:
            break
        for n, why in newly:
            (src_dir / n).unlink(); converted.remove(n); refused.append((n, why)); refused_by_name.add(n)
    total = len(converted) + len(refused)
    cases = sum(len(re.findall(r"\[Mesh(Fact|Theory)\b", (src_dir / n).read_text(encoding="utf-8"))) for n in converted)
    if not cases:
        # helpers alone are not a suite: an empty Tests area is a skip that renders like a pass
        for n in converted:
            (src_dir / n).unlink()
        converted = []
    if not converted:
        remove_suite(suite_id)
        print(f"  {suite_id}: {total} file(s), no test case survives — suite removed")
        return total, 0, len(refused)
    for t in ("InMeshTestBase.cs", "XunitShims.cs"):
        (src_dir / t).write_text((TEMPLATES / t).read_text(encoding="utf-8"), encoding="utf-8")
    # the Tests area lambda names the suite assembly through typeof(<a class>): it must be one that survived
    anchor = None
    for n in converted:
        m = re.search(r"^\s*public\s+(?:sealed\s+|partial\s+)*class\s+(\w+)", (src_dir / n).read_text(encoding="utf-8"), flags=re.M)
        if m and "[MeshFact" in (src_dir / n).read_text(encoding="utf-8"):
            anchor = m.group(1); break
    if anchor:
        doc["content"]["configuration"] = re.sub(r"typeof\(\w+\)\.Assembly", f"typeof({anchor}).Assembly", doc["content"]["configuration"])
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
