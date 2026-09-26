#!/usr/bin/env python3
"""EXECUTE node-repo-compile-check.yml's reference-set step against stub images.

WHY
---
The compile-check lane takes its reference set from ONE of two images: the tester's `/app` (no
`platform-image`), or — with the optional `platform-image` + `platform-image-digest` pair — the
PORTAL's `/app`, the surface content actually compiles against and the one node-repo-gate.yml
composes. With the portal pair the tester still supplies the framework identity and the step
asserts the two images are ONE build. The lane is `workflow_call` only, so nothing on a core pull
request executes this shell: the first run of an edit to it is a satellite's required gate.

HOW IT STAYS HONEST
-------------------
* The step is EXTRACTED FROM THE WORKFLOW by its `id: image`, never copied here.
* `docker` is a stub on PATH that serves synthetic images from a temp directory and RECORDS every
  call, so "which image became the reference set" is read off the bytes that landed in `refs/` and
  "which ref was pulled" off the call log — never off the step's own narration.
* Every refusal is a case with the message it must name, and the positive cases assert the
  DISTINGUISHING bytes (a portal-only assembly present, the tester's CLI absent), not a file count
  either image could satisfy.
* FALSIFICATION ARMS mutate the real step and must turn a case RED: dropping the pair branch's
  switch to the portal, dropping the tester-as-platform refusal, and dropping the one-build
  refusal. A harness whose cases pass against a broken step measures nothing.

usage: test-compile-check-reference-set.py
"""
from __future__ import annotations

import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

import yaml

ROOT = Path(__file__).resolve().parents[2]
LANE = ROOT / ".github" / "workflows" / "node-repo-compile-check.yml"

TESTER = "reg.example/mw-plugin-test"
PORTAL = "reg.example/memex-portal-ai"
T_DIGEST = "sha256:" + "1" * 64
P_DIGEST = "sha256:" + "2" * 64
X_DIGEST = "sha256:" + "3" * 64     # a portal from ANOTHER build (different identity)
N_DIGEST = "sha256:" + "4" * 64     # a portal image with no surface manifest
IDENT = "c0ffee01"

# The stub docker. Images live under $STUB_IMAGES/<ref with / : @ replaced>/{app,shared,identity}.
STUB = r'''#!/usr/bin/env python3
import os, shutil, sys
from pathlib import Path
images = Path(os.environ["STUB_IMAGES"]); state = Path(os.environ["STUB_STATE"])
with open(state / "calls.log", "a") as log: log.write(" ".join(sys.argv[1:]) + "\n")
def key(ref): return ref.replace("/", "_").replace(":", "_").replace("@", "_")
def image(ref):
    d = images / key(ref)
    if not d.is_dir(): sys.stderr.write(f"stub docker: no such image {ref}\n"); sys.exit(1)
    return d
args = sys.argv[1:]
cmd = args[0]
if cmd == "pull":
    image(args[-1]); print(args[-1]); sys.exit(0)
if cmd == "create":
    ref = args[-1]; image(ref)
    cid = f"c{len(list(state.glob('cid-*')))}"; (state / f"cid-{cid}").write_text(ref); print(cid); sys.exit(0)
if cmd == "cp":
    src, dst = args[-2], args[-1]
    cid, path = src.split(":", 1)
    d = image((state / f"cid-{cid}").read_text())
    sub = {"/app/.": "app", "/usr/share/dotnet/shared/.": "shared"}[path]
    Path(dst).mkdir(parents=True, exist_ok=True)
    shutil.copytree(d / sub, dst, dirs_exist_ok=True); sys.exit(0)
if cmd == "rm":
    sys.exit(0)
if cmd == "run":
    ref = next(a for a in args if a.startswith("reg.") and "@" in a)
    d = image(ref)
    if "--print-framework-identity" in args:
        if not (d / "app" / "mw-plugin-test.dll").exists(): sys.exit(127)
        print(f"identity={(d / 'identity').read_text().strip()} provenance=gstub"); sys.exit(0)
    if "framework-identity" in args:
        mount = next(a for a in args if a.endswith(":/portal:ro")).split(":/portal")[0]
        expect = args[args.index("--expect") + 1]
        got = (Path(mount) / "IDENTITY").read_text().strip() if (Path(mount) / "IDENTITY").exists() else "fallback"
        if got != expect:
            print(f"framework-identity: MISMATCH — '/portal' resolves {got}, expected {expect}"); sys.exit(1)
        sys.stderr.write(f"framework-identity: MATCH — '/portal' resolves {got}\n"); print(got); sys.exit(0)
sys.stderr.write(f"stub docker: unhandled {args}\n"); sys.exit(2)
'''


def extract_step() -> str:
    doc = yaml.safe_load(LANE.read_text(encoding="utf-8"))
    steps = [s for s in doc["jobs"]["compile-check"]["steps"] if s.get("id") == "image"]
    if len(steps) != 1:
        raise SystemExit(f"RED: expected exactly one `id: image` step in {LANE.name}, found {len(steps)}")
    body = steps[0]["run"].replace("${{ inputs.allow-unpinned }}", "false")
    if "${{" in body:
        raise SystemExit(f"RED: the `image` step grew a ${{{{ }}}} expression this harness cannot supply "
                         "— teach it, do not skip it")
    return body


def make_image(images: Path, ref: str, *, tester: bool, identity: str, manifest: bool = True) -> None:
    d = images / ref.replace("/", "_").replace(":", "_").replace("@", "_")
    app = d / "app"; shared = d / "shared" / "Microsoft.NETCore.App" / "10.0.0"
    app.mkdir(parents=True); shared.mkdir(parents=True)
    (shared / "System.Private.CoreLib.dll").write_bytes(b"corelib")
    for n in ("MeshWeaver.Mesh.Contract.dll", "MeshWeaver.Compiler.dll"):
        (app / n).write_bytes(b"shared-build")
    if manifest:
        (app / "meshweaver-surface.manifest").write_text("MeshWeaver.Mesh.Contract abc\n")
    if tester:
        (app / "mw-plugin-test.dll").write_bytes(b"tester-cli")
    else:
        # the portal-only surface the whole change is about
        (app / "MeshWeaver.Testing.InMesh.dll").write_bytes(b"portal-only")
        (app / "MeshWeaver.Reactive.Assertions.dll").write_bytes(b"portal-only")
    # Both images of one build resolve the same identity from their own /app — measured: the
    # tester's framework-identity verb on the tester's own /app MATCHES. That is exactly why the
    # tester handed in as the "platform" needs its own refusal: the one-build check passes it.
    (app / "IDENTITY").write_text(identity)
    (d / "identity").write_text(identity)


def run(script: str, env_in: dict, tmp: Path) -> tuple[int, str, Path, str]:
    work = tmp / "work"; state = tmp / "state"
    for p in (work, state):
        shutil.rmtree(p, ignore_errors=True); p.mkdir()
    env = dict(os.environ)
    env.update({"PATH": f"{tmp / 'bin'}{os.pathsep}{env['PATH']}", "STUB_IMAGES": str(tmp / "images"),
                "STUB_STATE": str(state), "GITHUB_OUTPUT": str(state / "out")})
    env.update({"TEST_IMAGE": "", "IMAGE_DIGEST": "", "PLATFORM_IMAGE": "", "PLATFORM_IMAGE_DIGEST": "", "UPSTREAM_SEED": ""})
    env.update(env_in)
    script = script.replace("${{ inputs.allow-unpinned }}", "false")
    r = subprocess.run(["bash", "-c", script], cwd=work, env=env, capture_output=True, text=True)
    calls = (state / "calls.log").read_text() if (state / "calls.log").exists() else ""
    out = (state / "out").read_text() if (state / "out").exists() else ""
    return r.returncode, r.stdout + r.stderr, work / "refs", calls + "\n#OUT\n" + out


def cases(script: str, tmp: Path) -> list[tuple[str, bool, str]]:
    base = {"TEST_IMAGE": f"{TESTER}:latest", "IMAGE_DIGEST": T_DIGEST}
    results = []

    def case(name, env, expect_ok, check):
        code, log, refs, calls = run(script, {**base, **env}, tmp)
        ok = (code == 0) == expect_ok
        why = ""
        if ok:
            why = check(log, refs, calls) or ""
            ok = not why
        results.append((name, ok, why or f"exit={code}\n{log[-1500:]}"))

    def has(refs, n): return (refs / n).exists()

    case("no pair: the tester's /app is the reference set (unchanged path)",
         {"UPSTREAM_SEED": "plugins"}, True,
         lambda log, refs, calls: None if has(refs, "mw-plugin-test.dll") and not has(refs, "MeshWeaver.Testing.InMesh.dll")
         and f"identity={IDENT}" in calls and has(refs, "shared-frameworks/Microsoft.NETCore.App/10.0.0/System.Private.CoreLib.dll")
         else "tester refs / identity / shared frameworks missing")
    case("portal pair: the PORTAL's /app is the reference set, one identity asserted",
         {"PLATFORM_IMAGE": PORTAL, "PLATFORM_IMAGE_DIGEST": P_DIGEST, "UPSTREAM_SEED": "plugins"}, True,
         lambda log, refs, calls: None if has(refs, "MeshWeaver.Testing.InMesh.dll") and not has(refs, "mw-plugin-test.dll")
         and f"identity={IDENT}" in calls and f"pull -q {PORTAL}@{P_DIGEST}" in calls and "--expect" in calls
         else "portal-only assembly absent, tester CLI present, identity not asserted, or wrong pull")
    case("portal pair without upstream-seed still asserts one build",
         {"PLATFORM_IMAGE": PORTAL, "PLATFORM_IMAGE_DIGEST": P_DIGEST}, True,
         lambda log, refs, calls: None if "--expect" in calls and has(refs, "MeshWeaver.Testing.InMesh.dll")
         else "the one-build assertion did not run")
    case("platform-image without a digest is RED",
         {"PLATFORM_IMAGE": PORTAL}, False, lambda *_: None)
    case("platform-image-digest without an image is RED",
         {"PLATFORM_IMAGE_DIGEST": P_DIGEST}, False, lambda *_: None)
    case("the TESTER handed in as the platform is RED",
         {"PLATFORM_IMAGE": TESTER, "PLATFORM_IMAGE_DIGEST": T_DIGEST}, False, lambda *_: None)
    case("a portal of ANOTHER build (identity mismatch) is RED",
         {"PLATFORM_IMAGE": PORTAL, "PLATFORM_IMAGE_DIGEST": X_DIGEST}, False, lambda *_: None)
    case("a platform image without a surface manifest is RED",
         {"PLATFORM_IMAGE": PORTAL, "PLATFORM_IMAGE_DIGEST": N_DIGEST}, False, lambda *_: None)
    case("a platform image on another registry is RED",
         {"PLATFORM_IMAGE": "other.example/memex-portal-ai", "PLATFORM_IMAGE_DIGEST": P_DIGEST}, False, lambda *_: None)
    case("a tag is stripped from the LAST component only (registry port survives)",
         {"TEST_IMAGE": "reg.example:5000/mw-plugin-test:latest",
          "PLATFORM_IMAGE": "reg.example:5000/memex-portal-ai:latest", "PLATFORM_IMAGE_DIGEST": P_DIGEST}, True,
         lambda log, refs, calls: None if f"pull -q reg.example:5000/memex-portal-ai@{P_DIGEST}" in calls
         else f"wrong portal ref pulled:\n{calls}")
    return results


def main() -> int:
    script = extract_step()
    with tempfile.TemporaryDirectory() as t:
        tmp = Path(t)
        (tmp / "bin").mkdir()
        stub = tmp / "bin" / "docker"; stub.write_text(STUB); stub.chmod(0o755)
        images = tmp / "images"
        make_image(images, f"{TESTER}@{T_DIGEST}", tester=True, identity=IDENT)
        make_image(images, f"{PORTAL}@{P_DIGEST}", tester=False, identity=IDENT)
        make_image(images, f"{PORTAL}@{X_DIGEST}", tester=False, identity="deadbeef")
        make_image(images, f"{PORTAL}@{N_DIGEST}", tester=False, identity=IDENT, manifest=False)
        make_image(images, f"reg.example:5000/mw-plugin-test@{T_DIGEST}", tester=True, identity=IDENT)
        make_image(images, f"reg.example:5000/memex-portal-ai@{P_DIGEST}", tester=False, identity=IDENT)

        failed = 0
        for name, ok, why in cases(script, tmp):
            print(f"  {'ok  ' if ok else 'FAIL'} {name}")
            if not ok:
                failed += 1; print("       " + why.replace("\n", "\n       "))

        # Falsification arms: each MUTATES the real step and names the case that must go RED.
        arms = [
            ("the pair no longer switches to the portal", "; PORTAL_REFS=1\n", "; PORTAL_REFS=0\n",
             "portal pair: the PORTAL's /app is the reference set, one identity asserted"),
            ("the tester-as-platform refusal removed", "[ ! -f refs/mw-plugin-test.dll ] ||", "true ||",
             "the TESTER handed in as the platform is RED"),
            ("the one-build refusal removed", 'if [ "$ok" -ne 0 ]; then', 'if false; then',
             "a portal of ANOTHER build (identity mismatch) is RED"),
        ]
        for label, old, new, target in arms:
            if script.count(old) != 1:
                print(f"  FAIL arm '{label}': the anchor {old!r} is not in the step exactly once — the arm cannot mutate it")
                failed += 1; continue
            mutated = script.replace(old, new)
            verdict = dict((n, ok) for n, ok, _ in cases(mutated, tmp))
            if verdict.get(target, True):
                print(f"  FAIL arm '{label}': case '{target}' stayed GREEN against the mutated step — it measures nothing")
                failed += 1
            else:
                print(f"  ok   arm '{label}' turns '{target}' RED")
    if failed:
        print(f"::error title=compile-check reference set::{failed} case(s)/arm(s) failed")
        return 1
    print("compile-check reference-set step: every case and every falsification arm behaved")
    return 0


if __name__ == "__main__":
    sys.exit(main())
