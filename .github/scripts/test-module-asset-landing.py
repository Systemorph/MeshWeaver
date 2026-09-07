#!/usr/bin/env python3
"""EXECUTE the reusable lanes' external-module landing against real bundle SHAPES.

WHY
---
`node-repo-gate.yml` and `node-repo-publish-bake.yml` unpack every external module bundle and
compose it into `/ext/<Name>/` for the tester container. Until MeshWeaver#3514 they copied
`meshweaver/modules/` and nothing else — but a bundle carries its STATIC WEB ASSETS separately,
under `meshweaver/moduleassets/`, keeping their module-relative path (`wwwroot/x.js`,
`wwwroot/_content/<Dep>/y.css`). That is the shape `ModuleLandingService` writes verbatim beside
the entry assembly and `MeshModuleStaticAssetExtensions` serves at `_content/<Name>/…`.

So a module landed by the lanes LOADED PERFECTLY and then 404'd every asset it shipped, with a
single `LogDebug` line to say so. Latent for the lanes themselves (the bake is mesh-free, the gate
runs no browser) and invisible in production (which lands through `ServedModuleBytes`, whose asset
handling is correct) — but MeshWeaver.Education copied the block into its mesh e2e, and from
MeshWeaver.Plugins#1268 (which moved the collaboration views into
`MeshWeaver.Markdown.Collaboration`) every exercise page rendered a bare Code node card where the
workbench belongs, 4 x 180 s per course, for three days.

Nothing executed this shell. It runs only inside a satellite's gate/bake, so the first execution of
an edit to it is in THEIR CI, on a mesh nobody points a browser at.

HOW IT STAYS HONEST
-------------------
* The step is EXTRACTED FROM THE WORKFLOW by its `id:`, never copied here — a copy passes while
  the real thing rots. Both lanes are extracted, and finding fewer than both is RED.
* The verdict is read off THE BYTES on disk: every shipped asset must exist at its module-relative
  path under `/ext/<Name>/` with a matching sha256. Not off the step's own log, and not off a file
  count that a landing which copied nothing could also satisfy.
* Every case prints its DENOMINATOR — bundles composed, bundles carrying assets, asset files
  shipped, asset files landed — and a case that finds ZERO shipped assets FAILS. A harness that
  looked in the wrong place must go red, not green.
* TWO falsification arms per lane, and each MUTATES THE REAL STEP rather than asserting about it:
  - `revert`  — the whole asset block removed, i.e. the pre-#3514 lane. The positive assertion must
                go RED. A lane change nobody has watched fail is not a fix.
  - `no-copy` — the `cp` removed, the in-lane fail-closed assertion kept. The STEP must exit
                non-zero. This is what proves the lane's own check is not a no-op: an assertion
                that cannot fail ticks exactly like one that passed.
* THREE control cases must PASS, or the harness cannot tell "the step lands assets" from "the step
  always passes": a bundle with no assets at all still composes; a bundle whose entry DLL is
  missing is refused; and the module CLOSURE (the DLLs) still lands beside the assets.
* The bundle layout is not asserted from memory: `meshweaver/moduleassets` is read out of
  `NuGetPackageWriter.ModuleAssetFolder` in `src/`, so renaming the constant reds this harness
  instead of silently orphaning the lane.

WHAT THIS HARNESS CANNOT PROVE
------------------------------
That the browser gets a 200. It proves the lane lays the bytes down in the exact shape
`MeshModuleStaticAssetExtensions.ModuleWwwrootPath` looks for — `<dir of the loaded assembly>/
wwwroot` — for an assembly the lane passes as `--module /ext/<Name>/<Name>.dll`. The serving half
is the platform's and is exercised in production, where both portals answer 200 for
`_content/MeshWeaver.Markdown.Collaboration/Components/collaborativeMarkdownView.js`.
"""

from __future__ import annotations

import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]

# (workflow, job id, step id) — both lanes carry the same landing block and both are executed.
LANES = [
    (".github/workflows/node-repo-gate.yml", "ext-modules"),
    (".github/workflows/node-repo-publish-bake.yml", "ext-modules"),
]

# Where the constant that decides the bundle layout actually lives. Read, never remembered.
PACKAGING_SOURCE = "src/MeshWeaver.Plugin.Packaging/NuGetPackageWriter.cs"

FAILURES: list[str] = []


def fail(message: str) -> None:
    FAILURES.append(message)
    print(f"  FAIL  {message}")


def ok(message: str) -> None:
    print(f"  ok    {message}")


def die(message: str) -> None:
    print(f"error: {message}", file=sys.stderr)
    sys.exit(1)


# ── what the bundle layout IS, read out of src/ ─────────────────────────────────────────────
def asset_folder() -> str:
    """`NuGetPackageWriter.ModuleAssetFolder`, read from source.

    The lane hard-codes this path. If the constant is renamed and the lane is not, every module
    silently stops landing its assets again — so the coupling is asserted here rather than assumed.
    """
    text = (ROOT / PACKAGING_SOURCE).read_text(encoding="utf-8")
    match = re.search(
        r'public\s+const\s+string\s+ModuleAssetFolder\s*=\s*"([^"]+)"', text)
    if not match:
        die(f"cannot read ModuleAssetFolder from {PACKAGING_SOURCE} — the harness would be "
            "asserting a path nothing in src/ defines")
    return match.group(1)


def module_folder() -> str:
    text = (ROOT / PACKAGING_SOURCE).read_text(encoding="utf-8")
    match = re.search(
        r'public\s+const\s+string\s+ModuleFolder\s*=\s*"([^"]+)"', text)
    if not match:
        die(f"cannot read ModuleFolder from {PACKAGING_SOURCE}")
    return match.group(1)


# ── extracting the real step ────────────────────────────────────────────────────────────────
def extract_step(workflow: str, step_id: str) -> str:
    import yaml

    doc = yaml.safe_load((ROOT / workflow).read_text(encoding="utf-8"))
    for job in (doc.get("jobs") or {}).values():
        for step in job.get("steps") or []:
            if step.get("id") == step_id:
                body = step.get("run")
                if not body:
                    die(f"{workflow}: step '{step_id}' has no `run:` body")
                if "${{" in body:
                    die(f"{workflow}: step '{step_id}' grew a ${{{{ }}}} expression this harness "
                        "cannot supply — teach it, do not skip it")
                return body
    die(f"{workflow}: no step with id '{step_id}'. The harness EXTRACTS the lane's own shell by "
        "id; if the step was renamed or the id dropped, this harness would test nothing.")
    raise AssertionError("unreachable")


def strip_asset_landing(body: str, lane: str) -> str:
    """The PRE-#3514 body: the whole static-asset block removed.

    Reconstructed by deleting the real block, so the arm tracks the lane rather than freezing a
    copy of what it used to say.
    """
    lines = body.splitlines()
    try:
        start = next(i for i, line in enumerate(lines)
                     if "A module bundle carries its STATIC WEB ASSETS" in line)
        opener = next(i for i, line in enumerate(lines)
                      if 'if [ -d "$u/meshweaver/moduleassets" ]' in line)
    except StopIteration:
        die(f"{lane}: the asset-landing block is not in the step — nothing to revert, so the "
            "falsification arm would pass having removed nothing")
    indent = len(lines[opener]) - len(lines[opener].lstrip())
    closer = next(
        (i for i in range(opener + 1, len(lines))
         if lines[i].strip() == "fi" and len(lines[i]) - len(lines[i].lstrip()) == indent),
        None)
    if closer is None:
        die(f"{lane}: cannot find the `fi` closing the asset-landing block")
    kept = lines[:start] + lines[closer + 1:]
    out = "\n".join(kept)
    # `assets` is defined inside the removed region; keep the rest of the step runnable under -u.
    out = out.replace(" — $assets static asset file(s)", "")
    out = out.replace("ASSET_FILES=$((ASSET_FILES + assets))", "ASSET_FILES=$((ASSET_FILES + 0))")
    if "moduleassets" in out:
        die(f"{lane}: the revert arm still mentions moduleassets — it removed the wrong region")
    return out


def strip_asset_copy(body: str, lane: str) -> str:
    """The `cp` removed, the in-lane assertion kept: does the lane's own check actually fire?"""
    needle = 'cp -R "$u"/meshweaver/moduleassets/. "$EXT/$name/"'
    if needle not in body:
        die(f"{lane}: the asset copy is not in the step — the no-copy arm has nothing to remove")
    return body.replace(needle, ': "the copy this arm deliberately removes"')


# ── fixtures ────────────────────────────────────────────────────────────────────────────────
def make_bundle(path: Path, module: str, assets: dict[str, bytes],
                *, entry_dll: bool = True, closure: tuple[str, ...] = ()) -> dict[str, bytes]:
    """Write one module bundle in the shape `ModulePackCommand` produces.

    Returns the asset map actually written, keyed by module-relative path — the DENOMINATOR every
    assertion below is measured against.
    """
    mf, af = module_folder(), asset_folder()
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as archive:
        archive.writestr("meshweaver/manifest.json", json.dumps({
            "plugin": module,
            "version": "1.2.3",
            "module": {"assemblyName": module},
        }))
        if entry_dll:
            archive.writestr(f"{mf}/{module}.dll", b"MZ-entry-" + module.encode())
        for dep in closure:
            archive.writestr(f"{mf}/{dep}.dll", b"MZ-dep-" + dep.encode())
        for relative, content in assets.items():
            archive.writestr(f"{af}/{relative}", content)
    return dict(assets)


def realistic_assets(module: str) -> dict[str, bytes]:
    """The two shapes a standalone RCL publish emits, plus what makes them awkward.

    * the pack's OWN assets at the wwwroot root — including the collocated `.razor.js` whose 404
      is #3514's actual symptom, and the scoped-CSS aggregate the host links;
    * its DEPENDENCIES' assets already namespaced under `wwwroot/_content/<Dep>/`;
    * precompressed `.br`/`.gz` siblings, which the host negotiates and which must ride along;
    * a nested directory, so a flat copy cannot pass.
    """
    return {
        "wwwroot/Components/collaborativeMarkdownView.js":
            b"export function attach(){/* " + module.encode() + b" */}\n",
        "wwwroot/Components/collaborativeMarkdownView.js.br": b"\x1b\x2f\x00brotli-bytes",
        "wwwroot/Components/collaborativeMarkdownView.js.gz": b"\x1f\x8b\x08gzip-bytes",
        f"wwwroot/{module}.styles.css": b"@import '_content/MeshWeaver.Blazor/x.bundle.scp.css';\n",
        "wwwroot/_content/MeshWeaver.Blazor/nested/deep/leaflet.css": b".leaflet{}\n",
        "wwwroot/_content/Radzen.Blazor/fonts/material.woff2": b"\x77\x4f\x46\x32font",
    }


# ── running the real step ───────────────────────────────────────────────────────────────────
def run_step(body: str, workdir: Path, bundles: list[Path]) -> subprocess.CompletedProcess[str]:
    runner_temp = workdir / "runner-temp"
    (runner_temp / "ext-bundles").mkdir(parents=True)
    for bundle in bundles:
        shutil.copy2(bundle, runner_temp / "ext-bundles" / bundle.name)
    github_env = workdir / "github-env"
    github_env.touch()
    script = workdir / "step.sh"
    script.write_text(body, encoding="utf-8")

    env = dict(os.environ)
    env.update({
        "RUNNER_TEMP": str(runner_temp),
        "GITHUB_ENV": str(github_env),
        # Every input the step reads. Empty registry inputs take the artifact path — the one the
        # satellites' gate lanes actually use for their own modules.
        "REGISTRY_MODULES": "",
        "REGISTRY_URL": "",
        "REGISTRY_KEY": "",
        "SEAL_UPSTREAMS": "",
        "IDENTITY": "test-identity",
        "TARGETS": "",
        "PLATFORM_REF": "",
        "GH_TOKEN": "",
    })
    return subprocess.run(["bash", str(script)], capture_output=True, text=True, env=env,
                          cwd=workdir)


def ext_dir(workdir: Path) -> Path:
    return workdir / "runner-temp" / "ext-modules"


def sha(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


# ── cases ───────────────────────────────────────────────────────────────────────────────────
def case_lands_assets(lane: str, body: str, tmp: Path) -> None:
    """POSITIVE: every shipped asset lands, byte-identical, at its module-relative path."""
    before = len(FAILURES)
    work = tmp / "lands"
    work.mkdir()
    fixtures = tmp / "fixtures-lands"
    fixtures.mkdir()

    shipped: dict[str, dict[str, bytes]] = {}
    bundles = []
    for module, closure in (
        ("MeshWeaver.Markdown.Collaboration", ("MeshWeaver.Yjs",)),
        ("MeshWeaver.Blazor.EntityViews", ()),
    ):
        path = fixtures / f"{module}.module.nupkg"
        shipped[module] = make_bundle(path, module, realistic_assets(module), closure=closure)
        bundles.append(path)
    # A module that ships no assets at all: composing it must stay green (control).
    plain = fixtures / "MeshWeaver.Testing.module.nupkg"
    make_bundle(plain, "MeshWeaver.Testing", {})
    bundles.append(plain)

    result = run_step(body, work, bundles)
    if result.returncode != 0:
        fail(f"{lane}: the step failed on a well-formed bundle set\n{result.stdout}\n{result.stderr}")
        return

    total_shipped = sum(len(a) for a in shipped.values())
    if total_shipped == 0:
        fail(f"{lane}: the fixtures ship ZERO assets — this case would pass having checked nothing")
        return

    landed = 0
    for module, assets in shipped.items():
        module_dir = ext_dir(work) / module
        for relative, content in assets.items():
            target = module_dir / relative
            if not target.is_file():
                fail(f"{lane}: {module}: '{relative}' did not land at {target}")
                continue
            if sha(target.read_bytes()) != sha(content):
                fail(f"{lane}: {module}: '{relative}' landed with different bytes")
                continue
            landed += 1

    # The closure must still land beside the assets — the pre-existing behaviour, not collateral.
    for module, closure in (("MeshWeaver.Markdown.Collaboration", ("MeshWeaver.Yjs",)),
                            ("MeshWeaver.Blazor.EntityViews", ())):
        for dll in (module,) + closure:
            if not (ext_dir(work) / module / f"{dll}.dll").is_file():
                fail(f"{lane}: {module}: closure file {dll}.dll did not land")

    # The module with no assets composed anyway (control).
    if not (ext_dir(work) / "MeshWeaver.Testing" / "MeshWeaver.Testing.dll").is_file():
        fail(f"{lane}: a bundle carrying no assets failed to compose its closure")

    env_text = (work / "github-env").read_text(encoding="utf-8")
    for module in list(shipped) + ["MeshWeaver.Testing"]:
        if f"--module /ext/{module}/{module}.dll" not in env_text:
            fail(f"{lane}: EXT_MODULE_ARGS does not name {module}")

    print(f"        denominator: 3 bundle(s) composed, 2 carrying static assets, "
          f"{total_shipped} asset file(s) shipped, {landed} landed byte-identical")
    if landed == total_shipped and len(FAILURES) == before:
        ok(f"{lane}: {landed}/{total_shipped} shipped assets landed under /ext/<module>/wwwroot")


def case_missing_entry_is_refused(lane: str, body: str, tmp: Path) -> None:
    """CONTROL: the step can still fail. A harness whose cases all pass proves nothing."""
    work = tmp / "refused"
    work.mkdir()
    fixtures = tmp / "fixtures-refused"
    fixtures.mkdir()
    path = fixtures / "MeshWeaver.Broken.module.nupkg"
    make_bundle(path, "MeshWeaver.Broken", realistic_assets("MeshWeaver.Broken"), entry_dll=False)

    result = run_step(body, work, [path])
    if result.returncode == 0:
        fail(f"{lane}: a bundle with no entry assembly composed GREEN")
    else:
        ok(f"{lane}: a bundle with no entry assembly is refused (exit {result.returncode})")


def case_revert_goes_red(lane: str, body: str, tmp: Path) -> None:
    """FALSIFICATION: with the fix reverted, the positive assertion must go RED."""
    work = tmp / "revert"
    work.mkdir()
    fixtures = tmp / "fixtures-revert"
    fixtures.mkdir()
    module = "MeshWeaver.Markdown.Collaboration"
    path = fixtures / f"{module}.module.nupkg"
    shipped = make_bundle(path, module, realistic_assets(module))

    result = run_step(strip_asset_landing(body, lane), work, [path])
    if result.returncode != 0:
        fail(f"{lane}: the PRE-FIX step failed for an unrelated reason — the arm proves nothing\n"
             f"{result.stdout}\n{result.stderr}")
        return
    module_dir = ext_dir(work) / module
    if not (module_dir / f"{module}.dll").is_file():
        fail(f"{lane}: the PRE-FIX step did not even land the closure — wrong arm")
        return
    survivors = [r for r in shipped if (module_dir / r).is_file()]
    if survivors:
        fail(f"{lane}: the PRE-FIX step landed {len(survivors)} asset(s) — the fix is not what "
             "makes the positive case pass, so that case measures nothing")
    else:
        ok(f"{lane}: reverted, 0/{len(shipped)} assets land — the positive case goes RED without "
           "the fix")


def case_no_copy_fails_closed(lane: str, body: str, tmp: Path) -> None:
    """FALSIFICATION: the lane's OWN assertion must fail when the copy is removed."""
    work = tmp / "nocopy"
    work.mkdir()
    fixtures = tmp / "fixtures-nocopy"
    fixtures.mkdir()
    module = "MeshWeaver.Markdown.Collaboration"
    path = fixtures / f"{module}.module.nupkg"
    shipped = make_bundle(path, module, realistic_assets(module))

    result = run_step(strip_asset_copy(body, lane), work, [path])
    if result.returncode == 0:
        fail(f"{lane}: with the asset copy removed the step still passed — its fail-closed check "
             "is a no-op, which ticks exactly like a check that passed")
        return
    if "did not land" not in result.stdout:
        fail(f"{lane}: the step failed without naming the missing asset\n{result.stdout}")
        return
    ok(f"{lane}: copy removed ⇒ the step's own check fails closed, naming the "
       f"{len(shipped)} shipped asset(s)")


def main() -> int:
    try:
        import yaml  # noqa: F401
    except ImportError as exc:
        die(f"this harness cannot run — {exc}. pip install pyyaml.")

    for tool in ("unzip", "jq"):
        if shutil.which(tool) is None:
            die(f"this harness needs `{tool}` — the lane's own step calls it")

    print(f"bundle layout read from {PACKAGING_SOURCE}: "
          f"modules={module_folder()!r} assets={asset_folder()!r}")

    lanes = [(workflow, extract_step(workflow, step_id)) for workflow, step_id in LANES]
    if len(lanes) != len(LANES):
        die("not every lane was extracted")
    print(f"lanes examined: {len(lanes)} — {', '.join(Path(w).name for w, _ in lanes)}")

    for workflow, body in lanes:
        lane = Path(workflow).name
        print(f"\n{lane}")
        with tempfile.TemporaryDirectory() as raw:
            tmp = Path(raw)
            case_lands_assets(lane, body, tmp)
            case_missing_entry_is_refused(lane, body, tmp)
            case_revert_goes_red(lane, body, tmp)
            case_no_copy_fails_closed(lane, body, tmp)

    print()
    if FAILURES:
        print(f"FAILED — {len(FAILURES)} finding(s):")
        for finding in FAILURES:
            print(f"  - {finding}")
        return 1
    print(f"PASS — {len(lanes)} lane(s), 4 case(s) each: assets land byte-identical, the lane "
          "refuses a broken bundle, and BOTH falsification arms go red.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
