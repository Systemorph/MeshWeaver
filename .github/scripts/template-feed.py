#!/usr/bin/env python3
"""The packages the Memex `dotnet new` template restores, derived FROM the template — and checked.

    python3 .github/scripts/template-feed.py plan   --template DIR [--template DIR …] \
                                                     --template-package CSPROJ \
                                                     --core CORE --plugins PLUGINS \
                                                     --version 3.1.<run> --out plan.json
    python3 .github/scripts/template-feed.py slnx   --plan plan.json --line platform --out pack.slnx
    python3 .github/scripts/template-feed.py verify --plan plan.json --nupkgs DIR
    python3 .github/scripts/template-feed.py push-guard --source URL
    python3 .github/scripts/template-feed.py --self-test

WHY (MeshWeaver.Plugins#4467)
-----------------------------
The template MeshWeaver.Plugins generates (`tools/generate-memex-template.cs`) rewrites every project
it does not copy into a PackageReference and pins every `MeshWeaver.*` id at the template's version.
Its generated `nuget.config` maps `MeshWeaver.*` to the org's PRIVATE GitHub Packages feed
(`nuget.pkg.github.com/Systemorph`) and nothing else — nuget.org is not consulted for that prefix —
and no feed carried those ids: library publishing was retired, so a generated portal could not
restore. `.github/workflows/template-feed.yml` closes that by packing, for every SEALED platform set,
exactly what a generated template restores, and pushing it to that private feed.

THE ID LIST IS DERIVED, NEVER LISTED
------------------------------------
`plan` reads the GENERATED template's `Directory.Packages.props` (the lane runs the generator from
the Plugins commit the set was built from). Every `MeshWeaver.*` PackageVersion there is a package
the template restores. Each id is resolved to exactly one project — by `<PackageId>`, else by
project name — in core `src/` or in Plugins `src/`; an id that resolves to none, or to more than one,
is a RED naming it. The plan then adds the transitive closure of each project's ProjectReferences:
`dotnet pack` turns a ProjectReference into a package DEPENDENCY, so packing the 23 pinned ids
alone would publish 23 packages whose dependencies exist nowhere, and restore would still fail.

EVERY SHAPE, AND THE TEMPLATE ITSELF
------------------------------------
The generator has two shapes: the headless default (what the template PACKAGE ships) and
`--with-gui`, which copies the private GUI and pins more ids. `plan` takes one `--template` per shape
and publishes the UNION of their pins, so neither shape pins an id the feed lacks; two shapes that
pin one id at different versions are a RED. `--template-package` adds the `dotnet new` template
package itself (MeshWeaver.MemexTemplate), packed at the set's version, so a template installed from
the feed pins exactly the set it was published with (MeshWeaver#7660).

TWO VERSION LINES
-----------------
A project that declares its OWN `<Version>` in its csproj (today: MeshWeaver.Aspire.Hosting.Memex,
policy `cli-thin-wrapper-own-version`) is packed at that version and is NEVER stamped with the
platform's `3.1.<run>`. Everything else is packed at the set's version. The template must pin each
id at the version the lane packs it at — or at a lower one, since a NuGet pin is a MINIMUM; a pin
ABOVE the packed version cannot resolve, and `plan` refuses it by name.

PRIVATE, ALWAYS
---------------
`push-guard` refuses any push source but `https://nuget.pkg.github.com/Systemorph/index.json`.
nuget.org in particular is refused outright: a nupkg there can never be recalled.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import sys
import tempfile
import zipfile
import xml.etree.ElementTree as ET
from pathlib import Path

FEED = "https://nuget.pkg.github.com/Systemorph/index.json"
PREFIX = "MeshWeaver."


class Red(Exception):
    """A finding that must turn the lane red; the message says what to fix."""


# ─────────────────────────────── template ───────────────────────────────

def template_pins(template_dir: Path) -> dict[str, str]:
    """The MeshWeaver.* ids (and their pinned versions) a generated template restores."""
    props = template_dir / "Directory.Packages.props"
    if not props.is_file():
        raise Red(f"{props} does not exist — the template was not generated, so there is nothing "
                  "to derive the package list from")
    root = ET.parse(props).getroot()
    pins: dict[str, str] = {}
    for el in root.iter():
        if el.tag.split("}")[-1] != "PackageVersion":
            continue
        pid = (el.get("Include") or "").strip()
        ver = (el.get("Version") or "").strip()
        if pid.startswith(PREFIX):
            if not ver:
                raise Red(f"the template pins {pid} with no version")
            pins[pid] = ver
    if not pins:
        raise Red(f"{props} pins no {PREFIX}* package — a template that restores nothing from the "
                  "feed is not what the generator produces; refusing to publish an empty plan")
    return pins


# ─────────────────────────────── projects ───────────────────────────────

def _strip(tag: str) -> str:
    return tag.split("}")[-1]


def _csproj_props(path: Path) -> dict[str, str]:
    props: dict[str, str] = {}
    for el in ET.parse(path).getroot().iter():
        if _strip(el.tag) in ("PackageId", "Version") and (el.text or "").strip():
            props.setdefault(_strip(el.tag), el.text.strip())
    return props


def index_projects(roots: dict[str, Path]) -> dict[str, list[tuple[str, Path]]]:
    """package id → [(origin, csproj)] over each root's src/ tree."""
    idx: dict[str, list[tuple[str, Path]]] = {}
    for origin, root in roots.items():
        src = root / "src"
        if not src.is_dir():
            raise Red(f"{src} does not exist — the {origin} checkout is missing")
        for p in sorted(src.rglob("*.csproj")):
            rel = p.relative_to(src).parts
            if "bin" in rel or "obj" in rel:
                continue
            pid = _csproj_props(p).get("PackageId", p.stem)
            idx.setdefault(pid, []).append((origin, p.resolve()))
    return idx


def project_refs(csproj: Path, core: Path) -> list[Path]:
    """Unconditional ProjectReferences that become package dependencies."""
    out: list[Path] = []
    root = ET.parse(csproj).getroot()
    for group in root:
        if _strip(group.tag) != "ItemGroup" or group.get("Condition"):
            continue
        for el in group:
            if _strip(el.tag) != "ProjectReference" or el.get("Condition"):
                continue
            meta = {(_strip(c.tag)): (c.text or "").strip() for c in el}
            meta.update({k: v for k, v in el.attrib.items()})
            if meta.get("ReferenceOutputAssembly", "").lower() == "false":
                continue
            if meta.get("PrivateAssets", "").lower() == "all":
                continue
            inc = (meta.get("Include") or "").replace("\\", "/")
            if not inc:
                continue
            inc = inc.replace("$(MeshWeaverRoot)", str(core))
            target = Path(inc) if Path(inc).is_absolute() else (csproj.parent / inc)
            target = target.resolve()
            if not target.is_file():
                raise Red(f"{csproj.name} references {inc}, which does not exist — the closure "
                          "cannot be computed")
            out.append(target)
    return out


def union_pins(template_dirs: list[Path]) -> dict[str, str]:
    """The pins of every template shape, merged; one id pinned at two versions is a RED."""
    if not template_dirs:
        raise Red("no template given — there is nothing to derive the package list from")
    pins: dict[str, str] = {}
    for d in template_dirs:
        for pid, ver in template_pins(d).items():
            if pins.setdefault(pid, ver) != ver:
                raise Red(f"the template shapes disagree on {pid}: {pins[pid]} and {ver} ({d}) — one "
                          "set cannot satisfy both pins")
    return pins


def build_plan(template_dir: Path | list[Path], core: Path, plugins: Path, version: str,
               own_versions: dict[str, str] | None = None,
               template_package: Path | None = None) -> dict:
    """The full plan: every package to pack, its project, origin, version line, and why."""
    if not re.fullmatch(r"\d+\.\d+\.\d+", version):
        raise Red(f"'{version}' is not a platform set version (<major>.<minor>.<run>)")
    pins = union_pins(template_dir if isinstance(template_dir, list) else [template_dir])
    roots = {"core": core.resolve(), "plugins": plugins.resolve()}
    idx = index_projects(roots)
    by_path = {p: (pid, origin) for pid, rows in idx.items() for origin, p in rows}

    def resolve(pid: str) -> tuple[str, Path]:
        rows = idx.get(pid, [])
        if not rows:
            raise Red(f"the template restores {pid}, but no project in core src/ or Plugins src/ "
                      "packs it (no <PackageId> and no project of that name) — the lane cannot "
                      "publish it, so a generated template cannot restore it")
        if len(rows) > 1:
            raise Red(f"{pid} resolves to {len(rows)} projects ({', '.join(str(p) for _, p in rows)}) "
                      "— ambiguous; refusing to guess which one the template means")
        return rows[0]

    plan: dict[str, dict] = {}
    queue = [(pid, "template") for pid in sorted(pins)]
    while queue:
        pid, why = queue.pop(0)
        if pid in plan:
            continue
        origin, path = resolve(pid)
        declared = (own_versions or {}).get(pid) or _csproj_props(path).get("Version")
        own = declared is not None and "$(" not in declared
        if declared is not None and "$(" in declared:
            # A version property (e.g. $(AspireIntegrationVersion)) — read the property literally.
            m = re.fullmatch(r"\$\((\w+)\)", declared)
            text = path.read_text(encoding="utf-8-sig")
            lit = re.search(rf"<{m.group(1)}>([^<]+)</{m.group(1)}>", text) if m else None
            if not lit:
                raise Red(f"{path.name} declares <Version>{declared}</Version>, which this lane cannot "
                          "resolve to a literal — declare the version as a literal property")
            declared, own = lit.group(1).strip(), True
        plan[pid] = {
            "id": pid,
            "origin": origin,
            "project": str(path),
            "line": "own" if own else "platform",
            "version": declared if own else version,
            "pinned": pid in pins,
            "pin": pins.get(pid),
            "reason": why,
        }
        for ref in project_refs(path, roots["core"]):
            hit = by_path.get(ref)
            if hit is None:
                raise Red(f"{pid} references {ref}, which is outside core src/ and Plugins src/ — "
                          "it has no package this lane could publish")
            queue.append((hit[0], f"dependency of {pid}"))

    # The `dotnet new` template package itself rides the set's version: installed from the feed,
    # it then pins exactly the set it was published with (MeshWeaver#7660).
    if template_package is not None:
        tp = template_package.resolve()
        if not tp.is_file():
            raise Red(f"{template_package} does not exist — the template package cannot be packed")
        tid = _csproj_props(tp).get("PackageId")
        if not tid:
            raise Red(f"{tp.name} declares no <PackageId> — the template package needs its published id")
        if tid in plan:
            raise Red(f"{tid} is both the template package and a package the template restores")
        plan[tid] = {"id": tid, "origin": "plugins", "project": str(tp), "line": "platform",
                     "version": version, "pinned": False, "pin": None,
                     "reason": "the dotnet-new template package itself"}

    # A pin above the version packed can never resolve (a NuGet pin is a MINIMUM).
    for pid, pin in pins.items():
        packed = plan[pid]["version"]
        if _vkey(pin) > _vkey(packed):
            raise Red(f"the template pins {pid} at {pin}, but this lane packs it at {packed} "
                      f"({plan[pid]['line']} version line) — a restore of {pin} or higher cannot "
                      "succeed. The template must pin an own-line package at its own version "
                      "(MeshWeaver.Plugins tools/generate-memex-template.cs)")
    return {"version": version, "feed": FEED,
            "pinned": sorted(pins), "packages": [plan[k] for k in sorted(plan)]}


def _vkey(v: str) -> tuple:
    core = v.split("-", 1)[0]
    return tuple(int(x) for x in core.split(".") if x.isdigit())


# ─────────────────────────────── slnx ───────────────────────────────

def write_slnx(plan: dict, line: str, out: Path) -> int:
    rows = [p for p in plan["packages"] if p["line"] == line]
    lines = ["<Solution>"]
    for p in rows:
        rel = os.path.relpath(p["project"], out.parent.resolve()).replace(os.sep, "/")
        lines.append(f'  <Project Path="{rel}" />')
    lines.append("</Solution>")
    out.write_text("\n".join(lines) + "\n", encoding="utf-8")
    return len(rows)


# ─────────────────────────────── verify ───────────────────────────────

def read_nuspec(nupkg: Path) -> tuple[str, str, list[tuple[str, str]]]:
    with zipfile.ZipFile(nupkg) as z:
        name = next((n for n in z.namelist() if n.endswith(".nuspec") and "/" not in n), None)
        if name is None:
            raise Red(f"{nupkg.name} carries no nuspec")
        root = ET.fromstring(z.read(name))
    meta = next(e for e in root if _strip(e.tag) == "metadata")
    pid = ver = ""
    deps: list[tuple[str, str]] = []
    for e in meta.iter():
        t = _strip(e.tag)
        if t == "id" and not pid:
            pid = (e.text or "").strip()
        elif t == "version" and not ver:
            ver = (e.text or "").strip()
        elif t == "dependency":
            deps.append((e.get("id", ""), e.get("version", "")))
    return pid, ver, deps


def verify(plan: dict, nupkgs: Path) -> list[str]:
    """Every pinned id packed at the planned version; every MeshWeaver dependency in the plan."""
    errors: list[str] = []
    want = {p["id"]: p for p in plan["packages"]}
    got: dict[str, str] = {}
    deps_seen: dict[str, list[tuple[str, str]]] = {}
    for f in sorted(nupkgs.glob("*.nupkg")):
        if f.name.endswith(".symbols.nupkg"):
            continue
        pid, ver, deps = read_nuspec(f)
        got[pid] = ver
        deps_seen[pid] = deps
    for pid in plan["pinned"]:
        if pid not in got:
            errors.append(f"the template restores {pid}, but the lane produced no package for it")
    for pid, row in want.items():
        if pid in got and got[pid] != row["version"]:
            errors.append(f"{pid} was packed at {got[pid]}, not the planned {row['version']}")
        if pid not in got and pid not in plan["pinned"]:
            errors.append(f"{pid} ({row['reason']}) was planned but not packed")
    for pid, deps in deps_seen.items():
        for dep, rng in deps:
            if not dep.startswith(PREFIX):
                continue
            if dep not in want:
                errors.append(f"{pid} depends on {dep}, which the lane does not publish — a restore "
                              "would fail on it")
                continue
            lo = rng.strip("[]() ").split(",")[0]
            if lo and _vkey(lo) > _vkey(want[dep]["version"]):
                errors.append(f"{pid} depends on {dep} {rng}, above the packed {want[dep]['version']}")
    extra = sorted(set(got) - set(want))
    for pid in extra:
        errors.append(f"{pid} was packed but is not in the plan — refusing to publish an id the "
                      "template does not need")
    return errors


def push_guard(source: str) -> None:
    s = source.strip().rstrip("/")
    if "nuget.org" in s.lower():
        raise Red(f"refusing to push to {source}: this lane publishes to the PRIVATE GitHub Packages "
                  "feed only — a nupkg on nuget.org can never be recalled")
    if s != FEED.rstrip("/"):
        raise Red(f"refusing to push to {source}: the only permitted source is {FEED}")



LANE_TOOL = "lane/.github/scripts/template-feed.py"
# The lane's own commit: under workflow_call github.sha is the CALLER's commit in another repository.
LANE_REF = "ref: ${{ steps.lane.outputs.sha }}"
PRIVATE_PUBLISHER_GUARD = 'if [ "$vis" != private ] || [ "$PUBLISHER" != private ]; then'
# The job a private publisher's run records ONLY after push, visibility and restore all succeeded
# (and carries forward on a run that found it). Its run history is the scheduled caller's durable
# "this set is published" marker — never a package's presence on the feed, which an upload that
# failed its later checks also leaves behind (#7660).
MARKER_JOB = "Published to the private feed"


def marker_seen(runs: list, version: str) -> bool:
    """True when one of the caller's runs holds a SUCCESSFUL `MARKER_JOB: <version>` job.

    `runs` is a list of job lists (`[{"name", "conclusion"}]`) from the caller's own successful
    runs. A job of a called workflow is reported as `<caller job> / <name>`, so the leaf is compared,
    and EXACTLY: `3.1.12` is not `3.1.123`.
    """
    want = f"{MARKER_JOB}: {version}"
    for jobs in runs:
        for job in jobs or []:
            leaf = str(job.get("name") or "").rsplit(" / ", 1)[-1].strip()
            if leaf == want and job.get("conclusion") == "success":
                return True
    return False


def feed_verdict(pre: str, candidate: str, already: str, publish: str, dry: str,
                 publisher: str) -> tuple[bool, str]:
    """The lane's verdict: (published — closes the ledger, the sentence to print), or Red.

    `already` (an earlier fully green run's marker for this exact set) IS a publication proof: a
    run that found it after a transient red must close that red, or the ledger stays open for as
    long as the set is unchanged (#7660 review). A genuine "no sealed set" closes nothing.
    """
    if pre != "success":
        raise Red(f"preflight did not succeed ({pre})")
    if candidate != "true":
        if already == "true":
            return True, ("an earlier fully green run published this set (its marker is in the run "
                          "history) — publication proven, nothing to push")
        return False, "no sealed set in that CD run — nothing to publish"
    if publish != "success":
        raise Red(f"a sealed set exists and publish did not succeed ({publish}) — generated templates "
                  "cannot restore it")
    # A public publisher's run is dry BY CONSTRUCTION (#7660): the pack and its closure proof are all
    # it can owe, so they close ITS ledger; the publication is the private caller's.
    if publisher == "public":
        return True, "packed and verified; publication is the private caller's"
    # An operator's dry run on the private publisher proves the pack, not the feed.
    if dry == "true":
        return False, "dry run: packed and verified, nothing published"
    return True, "published, every package private, and the template restores and builds"


_JOB_HEADER = re.compile(r"^  ([A-Za-z_][\w-]*):\s*$", re.M)


def workflow_jobs(text: str) -> list[tuple[str, str]]:
    """(job id, its text) for each job under `jobs:`; a fragment with no `jobs:` block is ONE job."""
    at = re.search(r"^jobs:\s*$", text, re.M)
    if not at:
        return [("publish", text)]
    rest = text[at.end():]
    heads = list(_JOB_HEADER.finditer(rest))
    return [(m.group(1), rest[m.end():heads[i + 1].start() if i + 1 < len(heads) else len(rest)])
            for i, m in enumerate(heads)]


def workflow_findings(text: str) -> list[str]:
    """Shapes of template-feed.yml that would read a sealed set as 'nothing to publish'.

    A CD run seals its set and can still CONCLUDE `failure` (an independent job such as
    control-image red), so whether to publish must come from the Promote / Verify / Bake JOB
    results alone — never from the run's conclusion, and never from a `status=success` run filter.
    """
    out: list[str] = []
    if "workflow_run.conclusion" in text:
        out.append("the lane reads the CD run's CONCLUSION — a sealed set in a run that concluded "
                   "`failure` would be skipped and read green; decide on the job results only")
    if "status=success" in text:
        out.append("the dispatch path selects CD runs by `status=success` — a sealed set in a "
                   "failed run is invisible to it; select completed runs and let the job results decide")
    # The publish job checks out the SET's core at core_sha, which is usually older than the lane.
    # Running this script out of that tree runs whatever copy the set happens to hold — none at all
    # for a set sealed before the lane existed (MeshWeaver#7660: `can't open file`).
    if re.search(r"python3\s+\.github/scripts/template-feed\.py", text):
        out.append("the lane runs template-feed.py out of the SET's core checkout (core_sha) — a set "
                   "older than the lane has no such script, or an older one; run the lane's own copy "
                   "(the `lane` checkout at github.sha)")
    # The calls go through $FEED_TOOL, so the regex above cannot see where it points: the binding
    # itself must name the lane's copy, or the set-tree regression returns through one env line.
    bindings = re.findall(r"^\s*FEED_TOOL:\s*['\"]?([^'\"\s]+)", text, re.M)
    if "$FEED_TOOL" in text and (not bindings or any(b != LANE_TOOL for b in bindings)):
        out.append(f"FEED_TOOL is bound to {bindings or 'nothing'}, not {LANE_TOOL} — the calls through it "
                   "would run the set's copy (or none)")
    # PER JOB: every job runs in its own workspace, so a job that runs $FEED_TOOL needs its OWN lane
    # checkout — another job's (preflight's) satisfies nothing (#7660 review).
    for name, body in workflow_jobs(text):
        if "$FEED_TOOL" in body and (LANE_REF not in body or "path: lane" not in body):
            out.append(f"the {name} job does not check out the lane's own tooling at the commit that defines "
                       "the workflow into `lane` — the script it runs would come from the set's tree")
    # WHO PUBLISHES decides every new id's visibility: a package created with a workflow's GITHUB_TOKEN
    # takes the publishing repository's visibility, and a public package can never be made private
    # again (MeshWeaver#7660 — the public core repository created 40 of 43 ids public).
    if "nuget push" in text and PRIVATE_PUBLISHER_GUARD not in text:
        out.append("the lane pushes without refusing a non-private publisher — a package created from a "
                   "public repository is public, irreversibly")
    if "packages/nuget/MeshWeaver.MemexTemplate/versions" in text:
        out.append("the lane decides 'already published' from the template package's PRESENCE on the feed — "
                   "a push whose visibility or restore check failed leaves it there too; skip only on the "
                   f"`{MARKER_JOB}` job a fully green run records")
    if re.search(r"RepositoryUrl=https://github\.com/", text):
        out.append("RepositoryUrl names a literal repository — it must name the PUBLISHING repository "
                   "($GITHUB_SERVER_URL/$GITHUB_REPOSITORY), the one whose visibility a new package takes")
    return out


# ─────────────────────────────── self-test ───────────────────────────────

def _proj(path: Path, refs: list[str] = (), version: str | None = None,
          package_id: str | None = None, extra: str = "") -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    props = ""
    if package_id:
        props += f"<PackageId>{package_id}</PackageId>"
    if version:
        props += version
    items = "".join(f'<ProjectReference Include="{r}" />' for r in refs)
    path.write_text(f'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>{props}</PropertyGroup>'
                    f"<ItemGroup>{items}</ItemGroup>{extra}</Project>", encoding="utf-8")


def _template(d: Path, pins: dict[str, str]) -> None:
    d.mkdir(parents=True, exist_ok=True)
    body = "".join(f'<PackageVersion Include="{k}" Version="{v}" />' for k, v in pins.items())
    d.joinpath("Directory.Packages.props").write_text(
        f"<Project><ItemGroup>{body}"
        '<PackageVersion Include="Newtonsoft.Json" Version="13.0.3" /></ItemGroup></Project>',
        encoding="utf-8")


def _nupkg(d: Path, pid: str, ver: str, deps: list[tuple[str, str]] = ()) -> None:
    d.mkdir(parents=True, exist_ok=True)
    dx = "".join(f'<dependency id="{i}" version="{v}" />' for i, v in deps)
    nuspec = (f'<?xml version="1.0"?><package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">'
              f"<metadata><id>{pid}</id><version>{ver}</version><dependencies><group>{dx}</group>"
              "</dependencies></metadata></package>")
    with zipfile.ZipFile(d / f"{pid}.{ver}.nupkg", "w") as z:
        z.writestr(f"{pid}.nuspec", nuspec)


def self_test() -> int:
    failures: list[str] = []

    def expect_red(label: str, fn, needle: str) -> None:
        try:
            fn()
        except Red as e:
            if needle not in str(e):
                failures.append(f"{label}: red, but for the wrong reason: {e}")
            return
        failures.append(f"{label}: expected RED, got a pass")

    with tempfile.TemporaryDirectory() as t:
        root = Path(t)
        core, plugins = root / "core", root / "plugins"
        _proj(core / "src/MeshWeaver.Mesh.Contract/MeshWeaver.Mesh.Contract.csproj")
        _proj(core / "src/MeshWeaver.Graph/MeshWeaver.Graph.csproj",
              ["../MeshWeaver.Mesh.Contract/MeshWeaver.Mesh.Contract.csproj",
               "../MeshWeaver.Analyzer/MeshWeaver.Analyzer.csproj\" PrivateAssets=\"all"])
        _proj(core / "src/MeshWeaver.Analyzer/MeshWeaver.Analyzer.csproj")
        _proj(plugins / "src/MeshWeaver.Hosting.PostgreSql/MeshWeaver.Hosting.PostgreSql.csproj",
              ["$(MeshWeaverRoot)/src/MeshWeaver.Graph/MeshWeaver.Graph.csproj"])
        _proj(plugins / "src/Memex.Aspire.Hosting/Memex.Aspire.Hosting.csproj",
              version="<AspireIntegrationVersion>3.1.0</AspireIntegrationVersion><Version>$(AspireIntegrationVersion)</Version>",
              package_id="MeshWeaver.Aspire.Hosting.Memex")

        good_pins = {"MeshWeaver.Graph": "3.1.500", "MeshWeaver.Hosting.PostgreSql": "3.1.500",
                     "MeshWeaver.Aspire.Hosting.Memex": "3.1.0"}
        _template(root / "t-good", good_pins)

        # 1. The derived plan is exactly the template's pins plus their closure.
        plan = build_plan(root / "t-good", core, plugins, "3.1.500")
        ids = [p["id"] for p in plan["packages"]]
        if plan["pinned"] != sorted(good_pins):
            failures.append(f"derived pinned list {plan['pinned']} != template pins {sorted(good_pins)}")
        if sorted(ids) != sorted([*good_pins, "MeshWeaver.Mesh.Contract"]):
            failures.append(f"closure wrong: {ids}")
        if "MeshWeaver.Analyzer" in ids:
            failures.append("a PrivateAssets=all reference entered the closure")
        aspire = next(p for p in plan["packages"] if p["id"] == "MeshWeaver.Aspire.Hosting.Memex")
        if (aspire["line"], aspire["version"]) != ("own", "3.1.0"):
            failures.append(f"the Aspire integration lost its own version line: {aspire}")
        graph = next(p for p in plan["packages"] if p["id"] == "MeshWeaver.Graph")
        if (graph["line"], graph["version"], graph["origin"]) != ("platform", "3.1.500", "core"):
            failures.append(f"a platform id is not on the set's version: {graph}")

        # 2. The lane is RED when the template pins an id it cannot pack.
        _template(root / "t-unknown", {**good_pins, "MeshWeaver.Nobody.Packs.This": "3.1.500"})
        expect_red("template pins an unpackable id",
                   lambda: build_plan(root / "t-unknown", core, plugins, "3.1.500"),
                   "MeshWeaver.Nobody.Packs.This")

        # 3. RED when the template pins an own-line package ABOVE its own version.
        _template(root / "t-aspire-high", {**good_pins, "MeshWeaver.Aspire.Hosting.Memex": "3.1.500"})
        expect_red("own-line pin above its version",
                   lambda: build_plan(root / "t-aspire-high", core, plugins, "3.1.500"),
                   "MeshWeaver.Aspire.Hosting.Memex at 3.1.500")

        # 4. RED on an empty template and on a non-set version.
        _template(root / "t-empty", {})
        expect_red("empty template", lambda: build_plan(root / "t-empty", core, plugins, "3.1.500"),
                   "pins no")
        expect_red("non-set version", lambda: build_plan(root / "t-good", core, plugins, "3.1.0-dev"),
                   "not a platform set version")

        # 5. verify: a complete, consistent pack passes …
        out = root / "nupkgs"
        _nupkg(out, "MeshWeaver.Mesh.Contract", "3.1.500")
        _nupkg(out, "MeshWeaver.Graph", "3.1.500", [("MeshWeaver.Mesh.Contract", "3.1.500"),
                                                   ("System.Reactive", "6.0.0")])
        _nupkg(out, "MeshWeaver.Hosting.PostgreSql", "3.1.500", [("MeshWeaver.Graph", "3.1.500")])
        _nupkg(out, "MeshWeaver.Aspire.Hosting.Memex", "3.1.0")
        errs = verify(plan, out)
        if errs:
            failures.append(f"a complete pack was refused: {errs}")
        # … and a pack missing a pinned id is RED (the template-pins-what-the-lane-doesn't case) …
        (out / "MeshWeaver.Hosting.PostgreSql.3.1.500.nupkg").unlink()
        if not any("MeshWeaver.Hosting.PostgreSql" in e for e in verify(plan, out)):
            failures.append("a pack missing a pinned id passed verify")
        _nupkg(out, "MeshWeaver.Hosting.PostgreSql", "3.1.500", [("MeshWeaver.Ghost", "3.1.500")])
        if not any("MeshWeaver.Ghost" in e for e in verify(plan, out)):
            failures.append("a dependency outside the plan passed verify")
        _nupkg(out, "MeshWeaver.Hosting.PostgreSql", "3.1.500", [("MeshWeaver.Graph", "3.1.500")])
        _nupkg(out, "MeshWeaver.Graph", "3.1.499", [])
        (out / "MeshWeaver.Graph.3.1.500.nupkg").unlink()
        if not any("packed at 3.1.499" in e for e in verify(plan, out)):
            failures.append("a package at the wrong version passed verify")

        # 6. The push guard refuses nuget.org and anything that is not the org feed.
        push_guard(FEED)
        expect_red("push to nuget.org", lambda: push_guard("https://api.nuget.org/v3/index.json"),
                   "nuget.org")
        expect_red("push elsewhere", lambda: push_guard("https://nuget.pkg.github.com/Someone/index.json"),
                   "only permitted source")

        # 7. slnx lists one line only.
        plan_good = build_plan(root / "t-good", core, plugins, "3.1.500")
        n = write_slnx(plan_good, "platform", root / "pack.slnx")
        text = (root / "pack.slnx").read_text()
        if n != 3 or "Memex.Aspire.Hosting" in text:
            failures.append(f"platform slnx wrong ({n} projects): {text}")

        # 9. Every shape is published: the union of two templates' pins, and the template package
        #    itself at the set's version; two shapes pinning one id differently are RED.
        _proj(plugins / "src/MeshWeaver.Blazor.Portal/MeshWeaver.Blazor.Portal.csproj",
              ["$(MeshWeaverRoot)/src/MeshWeaver.Mesh.Contract/MeshWeaver.Mesh.Contract.csproj"])
        _template(root / "t-gui", {**good_pins, "MeshWeaver.Blazor.Portal": "3.1.500"})
        _proj(plugins / "tools/MeshWeaver.MemexTemplate.Pack/MeshWeaver.MemexTemplate.Pack.csproj",
              package_id="MeshWeaver.MemexTemplate")
        tpkg = plugins / "tools/MeshWeaver.MemexTemplate.Pack/MeshWeaver.MemexTemplate.Pack.csproj"
        both = build_plan([root / "t-good", root / "t-gui"], core, plugins, "3.1.500",
                          template_package=tpkg)
        both_ids = {p["id"]: p for p in both["packages"]}
        if "MeshWeaver.Blazor.Portal" not in both["pinned"]:
            failures.append(f"the second shape's pins were dropped: {both['pinned']}")
        tmpl = both_ids.get("MeshWeaver.MemexTemplate")
        if not tmpl or (tmpl["line"], tmpl["version"], tmpl["pinned"]) != ("platform", "3.1.500", False):
            failures.append(f"the template package is not planned at the set's version: {tmpl}")
        headless = build_plan(root / "t-good", core, plugins, "3.1.500")
        if "MeshWeaver.Blazor.Portal" in {p["id"] for p in headless["packages"]}:
            failures.append("the negative control is meaningless: one shape already pins the GUI id")
        _template(root / "t-gui-skew", {**good_pins, "MeshWeaver.Graph": "3.1.499"})
        expect_red("two shapes pin one id differently",
                   lambda: build_plan([root / "t-good", root / "t-gui-skew"], core, plugins, "3.1.500"),
                   "disagree on MeshWeaver.Graph")
        expect_red("template package missing",
                   lambda: build_plan(root / "t-good", core, plugins, "3.1.500",
                                      template_package=root / "nope.csproj"),
                   "cannot be packed")

    # 8. The workflow decides 'sealed' from job results, never from the run's conclusion. Every
    #    fixture starts from the CORRECT lane shape, so each control is red only for its own rule.
    lane_ok = ("      FEED_TOOL: lane/.github/scripts/template-feed.py\n"
               "          ref: ${{ steps.lane.outputs.sha }}\n          path: lane\n"
               '        run: python3 "$FEED_TOOL" --self-test\n'
               '          -p:RepositoryUrl="$GITHUB_SERVER_URL/$GITHUB_REPOSITORY"\n'
               '          if [ "$vis" != private ] || [ "$PUBLISHER" != private ]; then exit 1; fi\n'
               '            dotnet nuget push "$f" --source "$FEED"\n')
    if workflow_findings(lane_ok):
        failures.append(f"workflow_findings refused the lane-tool shape: {workflow_findings(lane_ok)}")

    def only(fixture: str, needle: str, label: str) -> None:
        got = workflow_findings(fixture)
        if len(got) != 1 or needle not in got[0]:
            failures.append(f"workflow_findings, {label}: expected exactly the `{needle}` finding, got {got}")

    only(lane_ok + "    if: github.event.workflow_run.conclusion == 'success'\n", "CONCLUSION",
         "a conclusion-gated publish")
    only(lane_ok + "gh api actions/workflows/main-cd.yml/runs?branch=main&status=success\n",
         "status=success", "a status=success run filter")
    # 8b. … and runs its own tool from the lane's commit, never from the set's tree (#7660).
    only(lane_ok.replace('python3 "$FEED_TOOL"', "python3 .github/scripts/template-feed.py"),
         "SET's core checkout", "the script run out of the set's tree")
    only(lane_ok.replace("FEED_TOOL: lane/.github/scripts/template-feed.py",
                         "FEED_TOOL: .github/scripts/template-feed.py"),
         "FEED_TOOL", "FEED_TOOL re-bound to the set's tree")
    only(lane_ok.replace("          path: lane\n", ""), "does not check out the lane's own tooling",
         "a publish job with no lane checkout")
    only(lane_ok.replace("${{ steps.lane.outputs.sha }}", "${{ github.sha }}"),
         "does not check out the lane's own tooling", "the lane checked out at github.sha (the caller's commit under workflow_call)")
    # 8c. … and pushes only from a private repository, linked to it (#7660).
    only(lane_ok.replace('          if [ "$vis" != private ] || [ "$PUBLISHER" != private ]; then exit 1; fi\n', ""),
         "non-private publisher", "a push with no private-publisher refusal")
    only(lane_ok.replace('"$GITHUB_SERVER_URL/$GITHUB_REPOSITORY"', "https://github.com/Systemorph/MeshWeaver"),
         "RepositoryUrl names a literal", "a RepositoryUrl hard-coded to the public repository")
    only(lane_ok + 'gh api "orgs/Systemorph/packages/nuget/MeshWeaver.MemexTemplate/versions"\n',
         "PRESENCE on the feed", "a skip decided by the template package's presence on the feed")
    # 8d. The durable marker: only a SUCCESSFUL marker job for EXACTLY this version counts.
    ok_job = {"name": f"feed / {MARKER_JOB}: 3.1.12", "conclusion": "success"}
    for label, runs, want in [
            ("a successful marker for the version", [[ok_job]], True),
            ("a marker job that did not succeed", [[{**ok_job, "conclusion": "failure"}]], False),
            ("a skipped marker job", [[{**ok_job, "conclusion": "skipped"}]], False),
            ("a marker for a longer version", [[{**ok_job, "name": f"feed / {MARKER_JOB}: 3.1.123"}]], False),
            ("another job naming the version", [[{"name": "feed / Template feed verdict 3.1.12", "conclusion": "success"}]], False),
            ("no runs at all", [], False),
            ("the marker in an older run", [[{"name": "x", "conclusion": "success"}], [ok_job]], True)]:
        if marker_seen(runs, "3.1.12") != want:
            failures.append(f"marker_seen, {label}: expected {want}")
    # 8f. The verdict: a found marker closes the ledger; a genuine no-candidate leaves it alone.
    for label, args, want in [
            ("marker found after a transient red", ("success", "false", "true", "skipped", "false", "private"), True),
            ("no sealed set", ("success", "false", "", "skipped", "false", "private"), False),
            ("no sealed set, public publisher", ("success", "false", "", "skipped", "true", "public"), False),
            ("private publication", ("success", "true", "", "success", "false", "private"), True),
            ("operator dry run on the private publisher", ("success", "true", "", "success", "true", "private"), False),
            ("public publisher's forced dry run", ("success", "true", "", "success", "true", "public"), True)]:
        if feed_verdict(*args)[0] != want:
            failures.append(f"feed_verdict, {label}: expected published={want}")
    for label, args in [("preflight failed", ("failure", "", "", "skipped", "", "")),
                        ("publish failed", ("success", "true", "", "failure", "false", "private")),
                        ("publish failed beside a stale marker", ("success", "true", "true", "failure", "false", "private"))]:
        try:
            feed_verdict(*args)
            failures.append(f"feed_verdict, {label}: expected RED")
        except Red:
            pass

    wf = Path(__file__).resolve().parents[1] / "workflows" / "template-feed.yml"
    if wf.is_file():
        real = wf.read_text(encoding="utf-8")
        for f in workflow_findings(real):
            failures.append(f"template-feed.yml: {f}")
        # 8e. The lane checkout is judged PER JOB: break ONLY publish's checkout in the REAL workflow,
        #     keeping preflight's, and the check must name publish (#7660 review).
        jobs = dict(workflow_jobs(real))
        if "$FEED_TOOL" not in jobs.get("preflight", "") or LANE_REF not in jobs.get("preflight", ""):
            failures.append("8e's control is vacuous: the real preflight job no longer runs $FEED_TOOL off its own lane checkout")
        pub = jobs.get("publish", "")
        for label, broken in [("publish's lane checkout removed", pub.replace("          path: lane\n", "", 1)),
                              ("publish's lane checkout at github.sha", pub.replace(LANE_REF, "ref: ${{ github.sha }}", 1))]:
            if broken == pub:
                failures.append(f"8e, {label}: the real publish job has nothing to break — the control checked nothing")
                continue
            got = workflow_findings(real.replace(pub, broken, 1))
            if len(got) != 1 or "the publish job does not check out" not in got[0]:
                failures.append(f"8e, {label} (preflight's kept): expected exactly the publish finding, got {got}")
    else:
        failures.append(f"{wf} not found — the workflow-shape check checked nothing")

    if failures:
        print("template-feed self-test FAILED:")
        for f in failures:
            print(f"  ✗ {f}")
        return 1
    print("template-feed self-test: 9 scenarios, all held (derive, unknown id RED, own-line pin RED, "
          "empty/odd version RED, verify both ways, push guard, slnx, sealed-by-job-results, lane tool from the lane's commit, private publisher, durable marker, verdict, "
          "every shape + the template package)")
    return 0


# ─────────────────────────────── main ───────────────────────────────

def main(argv: list[str]) -> int:
    if argv[:1] == ["--self-test"]:
        return self_test()
    ap = argparse.ArgumentParser()
    sub = ap.add_subparsers(dest="cmd", required=True)
    p = sub.add_parser("plan")
    p.add_argument("--template", required=True, type=Path, action="append")
    p.add_argument("--template-package", type=Path)
    p.add_argument("--core", required=True, type=Path)
    p.add_argument("--plugins", required=True, type=Path)
    p.add_argument("--version", required=True)
    p.add_argument("--out", required=True, type=Path)
    s = sub.add_parser("slnx")
    s.add_argument("--plan", required=True, type=Path)
    s.add_argument("--line", required=True, choices=["platform", "own"])
    s.add_argument("--out", required=True, type=Path)
    v = sub.add_parser("verify")
    v.add_argument("--plan", required=True, type=Path)
    v.add_argument("--nupkgs", required=True, type=Path)
    g = sub.add_parser("push-guard")
    g.add_argument("--source", required=True)
    fv = sub.add_parser("verdict")
    for k in ("pre", "candidate", "already", "publish", "dry", "publisher"):
        fv.add_argument(f"--{k}", default="")
    fv.add_argument("--out", type=Path)
    m = sub.add_parser("marker")
    m.add_argument("--runs", required=True, type=Path)
    m.add_argument("--version", required=True)
    a = ap.parse_args(argv)
    try:
        if a.cmd == "plan":
            plan = build_plan(a.template, a.core, a.plugins, a.version,
                              template_package=a.template_package)
            a.out.write_text(json.dumps(plan, indent=2) + "\n", encoding="utf-8")
            own = [p for p in plan["packages"] if p["line"] == "own"]
            print(f"plan: {len(plan['pinned'])} ids pinned by the template, "
                  f"{len(plan['packages'])} packages with their closure "
                  f"({len(plan['packages']) - len(own)} at {plan['version']}, "
                  f"{len(own)} on their own line: "
                  + ", ".join(f"{p['id']} {p['version']}" for p in own) + ")")
            for row in plan["packages"]:
                print(f"  {row['id']:48} {row['version']:12} {row['origin']:8} {row['reason']}")
        elif a.cmd == "slnx":
            n = write_slnx(json.loads(a.plan.read_text()), a.line, a.out)
            print(f"{a.out}: {n} {a.line}-line project(s)")
        elif a.cmd == "verify":
            errors = verify(json.loads(a.plan.read_text()), a.nupkgs)
            if errors:
                for e in errors:
                    print(f"::error::{e}")
                return 1
            print(f"verify: every planned package is in {a.nupkgs} at its planned version, and every "
                  f"{PREFIX}* dependency is published by this lane")
        elif a.cmd == "verdict":
            published, why = feed_verdict(a.pre, a.candidate, a.already, a.publish, a.dry, a.publisher)
            print(why)
            if a.out:
                with a.out.open("a", encoding="utf-8") as fh:
                    fh.write(f"published={'true' if published else 'false'}\n")
        elif a.cmd == "marker":
            print("seen" if marker_seen(json.loads(a.runs.read_text()), a.version) else "absent")
        elif a.cmd == "push-guard":
            push_guard(a.source)
            print(f"push source {a.source} is the private org feed")
    except Red as e:
        print(f"::error::{e}")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
