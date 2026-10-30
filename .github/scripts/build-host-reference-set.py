#!/usr/bin/env python3
"""build-host-reference-set.py — the platform's reference set, built on THIS host, so a Mac can verify like CI.

    python3 .github/scripts/build-host-reference-set.py                          # tester closure only
    python3 .github/scripts/build-host-reference-set.py --plugins ~/code/MWP-x   # + the portal, as CI mounts it
    python3 .github/scripts/build-host-reference-set.py --print-path             # where the set for HEAD lives
    python3 .github/scripts/build-host-reference-set.py --self-test

WHY THIS EXISTS
---------------
CI compiles and tests in-mesh C# against the platform IMAGE: the tester image's /app, and in
MeshWeaver.Plugins the portal image's /app with the tester's extra `MeshWeaver.*` assemblies overlaid
(`scripts/platform-mount.py`). On a Mac neither half of that is usable as it stands:

  * an IMAGE extraction (`fetch-refs.py`, `--image`) needs registry access, and its assemblies are
    container-built for linux. `compile-check.py` can compile against them, but `run-node-tests.py`
    must also RUN what it compiles, and it refuses them on a non-Linux host, rightly (#5080: 8 of 8
    sets aborted on load and were reported as BUILD FAILED against clean content);
  * a core SOURCE build (`src/*/bin`) runs here, but a library's bin holds only its own assembly. The
    set has no System.Reactive.dll, so `short_reference_set_refusal` refuses it, also rightly (#1911).

So the in-mesh suites could run in CI and nowhere else. This script publishes the SAME closure the
images carry, on this host and for this host's RID, framework-dependent. The result is one flat
directory, like the image's /app, without the linux shared frameworks: it compiles against the SDK's
reference pack and executes here. It writes `meshweaver-reference-set.json` beside the assemblies.
That manifest is what `compile-check.py` reads to classify the directory as layout `host-build`. The
classification is never inferred: a flat directory without the manifest is still an image extraction,
and an image extraction without its shared frameworks is still refused.

WHAT IT BUILDS, in the order platform-mount.py composes it:

  1. the tester: `tools/MeshWeaver.PluginTester` published (what the tester image carries as /app);
  2. with `--plugins <checkout>`: the portal, `src/Memex.Portal.Distributed` in that checkout, published
     against THIS core (`-p:MeshWeaverRoot=<core>/`). It is the base of the set, and the tester adds
     only the `MeshWeaver.*` assemblies the portal lacks. Two builds of one assembly name in one set are
     the CS0433 family, so the portal's copy always wins, exactly as in the mount.

The registry-served modules (`MeshWeaver.AI`, `.Markdown.Collaboration`, `.Maps`) are not in any
image, and they are not in this set either. Locally `compile-check.py` takes them from the module
repository's own `src/<Module>/bin`, so build them there first. Its refusal names the commands.

STALENESS AND IDENTITY. The manifest records the core commit the set was published from. `compile-check.py` refuses
the set (exit 3, the same code as a stale sibling checkout) once that checkout's `origin/main` has
moved past the commit. Rebuild after you update core. The default output directory is keyed by the
commit, so an old set is never overwritten in place.
"""
from __future__ import annotations

import argparse
import datetime
import hashlib
import json
import os
import platform
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parent
CORE = SCRIPTS.parent.parent
MANIFEST = "meshweaver-reference-set.json"   # compile-check.py's HOST_REFERENCE_SET_MANIFEST
LAYOUT = "host-build"                         # compile-check.py's HOST_REFERENCE_SET_LAYOUT
TESTER = Path("tools/MeshWeaver.PluginTester/MeshWeaver.PluginTester.csproj")
PORTAL = Path("src/Memex.Portal.Distributed/Memex.Portal.Distributed.csproj")
CACHE = Path(os.path.expanduser("~/.cache/meshweaver/refs"))


def host_rid() -> str:
    """This host's RID, as .NET spells it (the same function as run-node-tests.py's)."""
    machine = platform.machine().lower()
    arch = {"x86_64": "x64", "amd64": "x64", "aarch64": "arm64", "arm64": "arm64"}.get(machine, machine)
    os_part = ("linux" if sys.platform.startswith("linux")
               else "osx" if sys.platform == "darwin"
               else "win" if sys.platform.startswith("win")
               else sys.platform)
    return f"{os_part}-{arch}"


def git(repo: Path, *args: str) -> str:
    proc = subprocess.run(["git", "-C", str(repo), *args], capture_output=True, text=True)
    if proc.returncode != 0:
        raise SystemExit(f"✗ git {' '.join(args)} failed in {repo}: {proc.stderr.strip()[:300]}")
    return proc.stdout.strip()


def source_state(repo: Path) -> tuple[str, str | None]:
    """(HEAD, a digest of the uncommitted tracked changes or None when clean). Many worktrees sit at
    the SAME commit, so the commit alone does not say what a set was built from."""
    sha = git(repo, "rev-parse", "HEAD")
    if not git(repo, "status", "--porcelain", "-z", "--untracked-files=no"):
        return sha, None
    proc = subprocess.run(["git", "-C", str(repo), "diff", "HEAD", "--binary"], capture_output=True)
    if proc.returncode != 0:
        raise SystemExit(f"✗ git diff HEAD failed in {repo}: {proc.stderr.decode(errors='replace')[:300]}")
    return sha, hashlib.sha256(proc.stdout).hexdigest()[:12]


def default_out(core: tuple[str, str | None], plugins: tuple[str, str | None] | None, rid: str) -> Path:
    def part(state: tuple[str, str | None]) -> str:
        return state[0][:12] + (f"-dirty-{state[1]}" if state[1] else "")
    return CACHE / (f"host-{rid}-{part(core)}" + (f"-plugins-{part(plugins)}" if plugins else ""))


def install(staging: Path, out: Path, replace: bool) -> bool:
    """Move a finished set to `out`. A default path is keyed by the source state, so a set already
    there is equivalent and may be in use as --refs: it is KEPT (returns False). An explicit --out
    is replaced. The rename is the only step a reader can observe."""
    if out.exists():
        if not replace:
            shutil.rmtree(staging)
            return False
        retired = Path(tempfile.mkdtemp(prefix=out.name + ".retired-", dir=out.parent))
        out.rename(retired / "set")
        staging.rename(out)
        shutil.rmtree(retired, ignore_errors=True)
        return True
    try:
        staging.rename(out)
    except OSError:
        if out.exists() and not replace:   # a concurrent build of the same source state got there first
            shutil.rmtree(staging)
            return False
        raise
    return True


def assemble(out: Path, tester: Path, portal: Path | None) -> dict:
    """Compose the flat set at `out` from published directories. With a portal, it is the base and
    the tester adds only the `MeshWeaver.*` assemblies the portal lacks (platform-mount.py's rule).
    Without one, the tester's publish is the set. Returns what was taken from where. Pure file work."""
    if portal is not None:
        shutil.copytree(portal, out)
        overlaid = []
        for dll in sorted(tester.glob("MeshWeaver.*.dll")):
            if not (out / dll.name).exists():
                shutil.copy2(dll, out / dll.name)
                overlaid.append(dll.name)
        return {"base": "portal", "overlaidFromTester": overlaid}
    shutil.copytree(tester, out)
    return {"base": "tester", "overlaidFromTester": []}


def manifest(core: Path, core_sha: str, core_dirty: bool, plugins: Path | None, plugins_sha: str | None,
             rid: str, composed: dict, out: Path) -> dict:
    return {
        "schema": 1,
        "layout": LAYOUT,
        "producer": ".github/scripts/build-host-reference-set.py",
        "rid": rid,
        "coreRoot": str(core),
        "coreSha": core_sha,
        "coreDirty": core_dirty,
        "pluginsRoot": str(plugins) if plugins else None,
        "pluginsSha": plugins_sha,
        "builtAt": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "assemblies": len(list(out.glob("*.dll"))),
        **composed,
    }


def publish(project: Path, out: Path, rid: str, cwd: Path, extra: list[str]) -> None:
    cmd = ["dotnet", "publish", str(project), "-c", "Release", "-r", rid, "--self-contained", "false",
           "-o", str(out), *extra]
    print(f"→ {' '.join(cmd)}", flush=True)
    proc = subprocess.run(cmd, cwd=cwd, capture_output=True, text=True)
    if proc.returncode != 0:
        errors = [ln for ln in (proc.stdout + proc.stderr).splitlines() if ": error " in ln]
        print("\n".join(dict.fromkeys(errors).keys() if errors else (proc.stdout + proc.stderr).splitlines()[-40:]))
        raise SystemExit(f"✗ dotnet publish {project} failed (exit {proc.returncode}); no set was written.")


def build(core: Path, plugins: Path | None, out: Path | None, rid: str) -> Path:
    core_state = source_state(core)
    plugins_state = source_state(plugins) if plugins else None
    core_sha, core_dirty = core_state[0], core_state[1] is not None
    plugins_sha = plugins_state[0] if plugins_state else None
    replace = out is not None
    out = out or default_out(core_state, plugins_state, rid)
    if core_dirty:
        print(f"  ⚠ {core} has uncommitted changes; the set carries them and records coreDirty=true.")
    with tempfile.TemporaryDirectory(prefix="host-refs-") as tmp:
        tmp = Path(tmp)
        publish(TESTER, tmp / "tester", rid, core, [])
        portal = None
        if plugins:
            portal = tmp / "portal"
            # The portal's csproj names `PublishProfile=DefaultContainer`, which publishes an IMAGE and
            # accepts only linux RIDs. Cleared here: what this set needs is the same /app, on disk.
            publish(PORTAL, portal, rid, plugins, [f"-p:MeshWeaverRoot={core}/", "-p:PublishProfile="])
        # A staging directory of this build's OWN: a concurrent build of the same path must never
        # delete or fill it. It sits beside `out`, so the final rename stays on one filesystem.
        out.parent.mkdir(parents=True, exist_ok=True)
        staging_root = Path(tempfile.mkdtemp(prefix=out.name + ".staging-", dir=out.parent))
        try:
            staging = staging_root / "set"
            composed = assemble(staging, tmp / "tester", portal)
            (staging / MANIFEST).write_text(
                json.dumps(manifest(core, core_sha, core_dirty, plugins, plugins_sha, rid, composed, staging),
                           indent=2) + "\n", encoding="utf-8")
            # Moved LAST: a half-built set is never at the path a reader uses.
            if not install(staging, out, replace):
                print(f"  {out} already holds a set of this exact source state; kept it (it may be in use).")
        finally:
            shutil.rmtree(staging_root, ignore_errors=True)
    return out


def _self_test_identity_and_concurrency() -> list[str]:
    """Many worktrees sit at the same HEAD and share ~/.cache, so the default path must be keyed by
    the SOURCE STATE (not only the commit), two builds of one path must never share a staging
    directory, and a build must never replace a set somebody may already be using as --refs.
    `publish` is replaced by a file copy, so this runs with no dotnet."""
    failures: list[str] = []
    real_publish, real_assemble, real_cache = publish, assemble, CACHE
    with tempfile.TemporaryDirectory() as tmp:
        tmp = Path(tmp)
        repo = tmp / "core"
        repo.mkdir()
        for cmd in (["init", "-q"], ["config", "user.email", "t@example.com"], ["config", "user.name", "t"]):
            git(repo, *cmd)
        (repo / "a.txt").write_text("one\n", encoding="utf-8")
        git(repo, "add", "a.txt")
        git(repo, "commit", "-q", "-m", "c")

        def print_path(*extra: str) -> str:
            proc = subprocess.run([sys.executable, str(Path(__file__).resolve()), "--core", str(repo),
                                   "--print-path", *extra], capture_output=True, text=True)
            return f"exit={proc.returncode} {proc.stdout.strip()}"

        clean = print_path()
        (repo / "a.txt").write_text("two\n", encoding="utf-8")
        dirty_two = print_path()
        (repo / "a.txt").write_text("three\n", encoding="utf-8")
        dirty_three = print_path()
        if len({clean, dirty_two, dirty_three}) != 3:
            failures.append("a clean checkout and two different uncommitted states at ONE commit must get three "
                            f"different default paths; got {clean!r}, {dirty_two!r}, {dirty_three!r}")
        (repo / "a.txt").write_text("one\n", encoding="utf-8")

        other = "linux-x64" if host_rid() != "linux-x64" else "osx-arm64"
        refused = print_path("--rid", other)
        if refused.startswith("exit=0"):
            failures.append(f"a set for another RID ({other}) must be refused: it would still be declared "
                            f"'{LAYOUT}' and pass the execution guard; got {refused!r}")

        def fake_publish(project: Path, out: Path, rid: str, cwd: Path, extra: list[str]) -> None:
            out.mkdir(parents=True)
            (out / "MeshWeaver.Graph.dll").write_text(str(project), encoding="utf-8")

        state = {"nested": False}

        def racing_assemble(out: Path, tester: Path, portal: Path | None) -> dict:
            got = real_assemble(out, tester, portal)
            if not state["nested"]:
                # A second build of the SAME path starts and finishes while this one is mid-staging.
                state["nested"] = True
                built = build(repo, None, None, host_rid())
                (built / "reader-holds-this").write_text("in use as --refs", encoding="utf-8")
            return got

        globals().update(publish=fake_publish, assemble=racing_assemble, CACHE=tmp / "cache")
        try:
            try:
                first = build(repo, None, None, host_rid())
            except Exception as exc:   # noqa: BLE001 — the race surfacing as a crash IS the finding
                failures.append(f"two concurrent builds of one path must not share a staging directory; "
                                f"the first one failed: {type(exc).__name__}: {exc}")
            else:
                if not (first / "reader-holds-this").exists():
                    failures.append("a build must not replace a set of the same source state that a reader "
                                    "may already be using as --refs")
                if sorted(p.name for p in first.parent.iterdir()) != [first.name]:
                    failures.append(f"no staging directory may be left behind; found "
                                    f"{sorted(p.name for p in first.parent.iterdir())}")
        finally:
            globals().update(publish=real_publish, assemble=real_assemble, CACHE=real_cache)
    return failures


def self_test() -> int:
    """Offline, no dotnet: the composition rule and the manifest compile-check.py classifies by."""
    failures: list[str] = []
    with tempfile.TemporaryDirectory() as tmp:
        tmp = Path(tmp)
        tester, portal = tmp / "tester", tmp / "portal"
        for d, names in ((tester, ["MeshWeaver.Hosting.Monolith.dll", "MeshWeaver.Graph.dll", "System.Reactive.dll",
                                   "mw-plugin-test.dll"]),
                         (portal, ["MeshWeaver.Graph.dll", "Memex.Portal.Shared.dll", "System.Reactive.dll"])):
            d.mkdir()
            for n in names:
                (d / n).write_text(f"{d.name}:{n}", encoding="utf-8")

        with_portal = tmp / "set-portal"
        got = assemble(with_portal, tester, portal)
        if got["overlaidFromTester"] != ["MeshWeaver.Hosting.Monolith.dll"]:
            failures.append(f"with a portal, the tester must add ONLY the MeshWeaver.* the portal lacks; got {got}")
        if (with_portal / "MeshWeaver.Graph.dll").read_text(encoding="utf-8") != "portal:MeshWeaver.Graph.dll":
            failures.append("with a portal, the portal's copy of a shared MeshWeaver.* assembly must win")
        if (with_portal / "mw-plugin-test.dll").exists():
            failures.append("with a portal, a non-MeshWeaver.* tester assembly must not be overlaid")

        tester_only = tmp / "set-tester"
        assemble(tester_only, tester, None)
        if sorted(p.name for p in tester_only.iterdir()) != sorted(p.name for p in tester.iterdir()):
            failures.append("without a portal, the tester's publish must be the set, whole")

        m = manifest(Path("/c"), "a" * 40, False, None, None, "osx-arm64", {"base": "tester"}, tester_only)
        if m["layout"] != LAYOUT or m["coreSha"] != "a" * 40 or m["rid"] != "osx-arm64":
            failures.append(f"the manifest must declare layout/coreSha/rid; got {m}")
        if "plugins" in default_out(("a" * 40, None), None, "osx-arm64").name:
            failures.append("a tester-only set must not be keyed as a plugins set")
        if default_out(("a" * 40, None), ("b" * 40, None), "osx-arm64") == default_out(("c" * 40, None), ("b" * 40, None), "osx-arm64"):
            failures.append("two core commits must never share an output directory")

    failures += _self_test_identity_and_concurrency()

    # The manifest name and layout are a contract with compile-check.py: read them from there, so a
    # rename on one side reds here instead of turning every host-built set back into a refused image.
    try:
        import importlib.util
        spec = importlib.util.spec_from_file_location("compile_check", SCRIPTS / "compile-check.py")
        cc = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(cc)
        if (cc.HOST_REFERENCE_SET_MANIFEST, cc.HOST_REFERENCE_SET_LAYOUT) != (MANIFEST, LAYOUT):
            failures.append(f"compile-check.py reads {cc.HOST_REFERENCE_SET_MANIFEST}/{cc.HOST_REFERENCE_SET_LAYOUT}, "
                            f"this script writes {MANIFEST}/{LAYOUT}")
    except AttributeError as exc:
        failures.append(f"compile-check.py no longer declares the host-build contract: {exc}")

    if failures:
        print("✗ build-host-reference-set self-test FAILED:\n  " + "\n  ".join(failures))
        return 1
    print("✓ build-host-reference-set self-test: the portal is the base and the tester adds only the "
          "MeshWeaver.* it lacks; a tester-only set is the tester whole; the manifest declares the layout "
          "compile-check.py classifies by, and both sides agree on its name.")
    return 0


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("--core", type=Path, default=CORE, help="the core checkout (default: this script's)")
    ap.add_argument("--plugins", type=Path, help="a MeshWeaver.Plugins checkout: add the portal, as CI mounts it")
    ap.add_argument("--out", type=Path, help="output directory (default: ~/.cache/meshweaver/refs/host-<rid>-<sha>)")
    ap.add_argument("--rid", default=None,
                    help="runtime identifier; only this host's is accepted (the set is declared host-built)")
    ap.add_argument("--print-path", action="store_true",
                    help="print where the set for this source state lives, build nothing")
    ap.add_argument("--self-test", action="store_true")
    args = ap.parse_args(argv)
    if args.self_test:
        return self_test()
    core = args.core.resolve()
    plugins = args.plugins.resolve() if args.plugins else None
    rid = args.rid or host_rid()
    if rid != host_rid():
        # The manifest declares layout `host-build`, which is what lets run-node-tests.py EXECUTE the
        # set on this host. A set published for another RID would carry that declaration falsely.
        print(f"✗ --rid {rid}: this script builds for this host only ({host_rid()}); a set for another "
              f"RID would be declared '{LAYOUT}' and pass the execution guard. Use an image extraction instead.")
        return 2
    if args.print_path:
        print(args.out or default_out(source_state(core), source_state(plugins) if plugins else None, rid))
        return 0
    out = build(core, plugins, args.out.resolve() if args.out else None, rid)
    m = json.loads((out / MANIFEST).read_text(encoding="utf-8"))
    print(f"\n✓ host-built reference set: {out}\n"
          f"    {m['assemblies']} assemblies, {rid}, core {m['coreSha'][:12]}"
          + (f", plugins {m['pluginsSha'][:12]}" if m.get("pluginsSha") else "") + "\n"
          "  Use it as --refs (export MW_PLATFORM_SCRIPTS so the launchers run THIS core's scripts):\n"
          f"    export MW_PLATFORM_SCRIPTS={core}/.github/scripts\n"
          f"    python3 scripts/canonical-compile-check.py --refs {out}\n"
          f"    python3 devtools/run-node-tests.py <Module> --refs {out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
