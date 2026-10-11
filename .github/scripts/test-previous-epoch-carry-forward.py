#!/usr/bin/env python3
"""EXECUTE the publish-bake lane's previous-epoch CARRY-FORWARD (MeshWeaver#3583) — the real step.

WHY
---
Across a declared compatibility break the `epoch-leg: previous` call of `node-repo-publish-bake.yml`
must seal the previous epoch's OWN sealed module set again, verified — never the module bundles
this run built, which are current-epoch builds. Two pieces of shell carry that decision, and both
run only inside a satellite's publish-bake, so nothing in core would otherwise execute them before
the first declared break does:

* the `carry-forward` step — reads the seal (`compose-sealed-modules.sh`), has the ceiling's tester
  verify every bundle's identity (`mw-plugin-test module-identity --expect`), and exports the
  sha256 of every carried bundle as `CARRIED_FORWARD_DIGESTS`;
* the digest refusal inside the `ext-modules` step — an own bundle whose bytes are not one of the
  carried ones is refused, never sealed.

`test-sealed-module-compose.py` covers the compose script, and `ModuleIdentityCheckTest` covers the
verb's identity RULE. This harness covers the PLUMBING between them: what the step passes where,
what it exports, and every refusal.

HOW IT STAYS HONEST
-------------------
* Both steps are EXTRACTED from the workflow by `id:` (shared helpers with
  `test-module-asset-landing.py`), never copied.
* `compose-sealed-modules.sh` and `docker` are stubs that RECORD their arguments; the cases assert
  the storage target, identity, source and `--expect` the step actually passed. The docker stub
  answers `module-identity` by comparing each mounted bundle's stated `frameworkMvid` with
  `--expect` — the plumbing is under test here, the scheme-aware rule is the verb's own test's.
* The exported digests are compared with sha256 computed HERE over the bytes the seal supplied.
* A falsification arm removes the digest refusal from the real `ext-modules` step and asserts the
  foreign bundle is then sealed — proving the refusal case is red because of that check.
"""

from __future__ import annotations

import hashlib
import importlib.util
import json
import os
import shutil
import subprocess
import sys
import tempfile
import zipfile
from pathlib import Path

WORKFLOW = ".github/workflows/node-repo-publish-bake.yml"

_spec = importlib.util.spec_from_file_location(
    "module_asset_landing_harness", Path(__file__).with_name("test-module-asset-landing.py"))
_harness = importlib.util.module_from_spec(_spec)
assert _spec.loader is not None
_spec.loader.exec_module(_harness)

extract_step = _harness.extract_step
run_step = _harness.run_step
seal_dir = _harness.seal_dir
module_folder = _harness.module_folder

IDENTITY = "k3.1-previous-epoch"
OTHER = "k3.2-current-epoch"
SOURCE = "Plugins"
TARGETS = "store-a store-b"

FAILURES: list[str] = []
CASES = 0


def fail(message: str) -> None:
    FAILURES.append(message)
    print(f"  FAIL  {message}")


def ok(message: str) -> None:
    print(f"  ok    {message}")


def die(message: str) -> None:
    print(f"error: {message}", file=sys.stderr)
    sys.exit(1)


def sha(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def make_bundle(path: Path, module: str, framework_mvid: str | None) -> Path:
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as archive:
        archive.writestr("meshweaver/manifest.json", json.dumps({
            "plugin": module.replace("MeshWeaver.", ""),
            "version": "1.2.3",
            "frameworkMvid": framework_mvid,
            "module": {"assemblyName": module},
        }))
        archive.writestr(f"{module_folder()}/{module}.dll", b"MZ-" + module.encode())
    return path


# ── stubs ───────────────────────────────────────────────────────────────────────────────────
COMPOSE_STUB = r"""#!/usr/bin/env bash
# Records its arguments, then copies the "sealed" bundles of $STUB_SEAL into --out.
set -euo pipefail
printf '%s\n' "$*" >> "$STUB_LOG/compose.args"
out=""
while [ $# -gt 0 ]; do case "$1" in --out) out="$2"; shift 2 ;; *) shift ;; esac; done
[ -n "$out" ] || { echo "stub compose: no --out" >&2; exit 2; }
mkdir -p "$out"
shopt -s nullglob
for f in "$STUB_SEAL"/*; do cp "$f" "$out/"; done
"""

DOCKER_STUB = r"""#!/usr/bin/env bash
# Records its arguments. `--help` answers the verb's usage unless STUB_NO_VERB is set;
# `module-identity <b>... --expect X` compares each mounted bundle's stated frameworkMvid with X.
set -uo pipefail
printf '%s\n' "$*" >> "$STUB_LOG/docker.args"
mount=""; verb=0; expect=""; bundles=(); help=0
args=("$@"); i=0
while [ $i -lt ${#args[@]} ]; do
  a="${args[$i]}"
  case "$a" in
    -v) mount="${args[$((i+1))]}"; i=$((i+2)); continue ;;
    module-identity) verb=1 ;;
    --help) help=1 ;;
    --expect) expect="${args[$((i+1))]}"; i=$((i+2)); continue ;;
    /carry/*) bundles+=("$a") ;;
  esac
  i=$((i+1))
done
if [ $verb -eq 1 ] && [ $help -eq 1 ]; then
  if [ -n "${STUB_NO_VERB:-}" ]; then echo "unknown verb 'module-identity'"; exit 2; fi
  echo "usage: mw-plugin-test module-identity <bundle.module.nupkg>... --expect <identity>"; exit 0
fi
[ $verb -eq 1 ] || { echo "stub docker: unexpected call" >&2; exit 3; }
host="${mount%%:*}"; bad=0
for b in "${bundles[@]}"; do
  stated=$(unzip -p "$host/${b#/carry/}" meshweaver/manifest.json | jq -r '.frameworkMvid // empty')
  if [ "$stated" = "$expect" ]; then echo "module-identity: MATCH ${b#/carry/}"
  else echo "module-identity: REFUSED ${b#/carry/} — states '${stated}'" >&2; bad=1; fi
done
exit $bad
"""


def run_carry(body: str, work: Path, *, sealed: list[Path], ceiling: str = "3.1.0",
              module_artifacts: str = "module-*", identity: str = IDENTITY,
              preexisting: list[Path] | None = None, no_verb: bool = False
              ) -> tuple[subprocess.CompletedProcess[str], Path]:
    runner_temp = work / "runner-temp"
    runner_temp.mkdir(parents=True)
    if preexisting:
        (runner_temp / "ext-bundles").mkdir()
        for p in preexisting:
            shutil.copy2(p, runner_temp / "ext-bundles" / p.name)
    seal = work / "seal"
    seal.mkdir()
    for p in sealed:
        shutil.copy2(p, seal / p.name)
    logs = work / "logs"
    logs.mkdir()
    scripts = work / "mw-platform-gate" / ".github" / "scripts"
    scripts.mkdir(parents=True)
    (scripts / "compose-sealed-modules.sh").write_text(COMPOSE_STUB, encoding="utf-8")
    (scripts / "compose-sealed-modules.sh").chmod(0o755)
    bindir = work / "bin"
    bindir.mkdir()
    (bindir / "docker").write_text(DOCKER_STUB, encoding="utf-8")
    (bindir / "docker").chmod(0o755)
    github_env = work / "github-env"
    github_env.touch()
    summary = work / "summary"
    summary.touch()
    script = work / "step.sh"
    script.write_text(body, encoding="utf-8")
    env = dict(os.environ)
    env.update({
        "PATH": f"{bindir}{os.pathsep}{env.get('PATH', '')}",
        "RUNNER_TEMP": str(runner_temp),
        "GITHUB_ENV": str(github_env),
        "GITHUB_STEP_SUMMARY": str(summary),
        "CEILING": ceiling,
        "MODULE_ARTIFACTS": module_artifacts,
        "BAKE_SOURCE": SOURCE,
        "BAKE_PUBLISH_TARGETS": TARGETS,
        "IDENTITY": identity,
        "TESTER_REF": "registry.example/tester:ceiling",
        "STUB_LOG": str(logs),
        "STUB_SEAL": str(seal),
    })
    if no_verb:
        env["STUB_NO_VERB"] = "1"
    result = subprocess.run(["bash", str(script)], capture_output=True, text=True, env=env, cwd=work)
    return result, logs


def exported_digests(work: Path) -> Path | None:
    for line in (work / "github-env").read_text(encoding="utf-8").splitlines():
        if line.startswith("CARRIED_FORWARD_DIGESTS="):
            return Path(line.split("=", 1)[1])
    return None


def case(name: str):
    def wrap(fn):
        def run(*args):
            global CASES
            CASES += 1
            before = len(FAILURES)
            fn(*args)
            if len(FAILURES) == before:
                ok(name)
        return run
    return wrap


# ── carry-forward cases ─────────────────────────────────────────────────────────────────────
@case("current leg: nothing carried, compose never called, no digests exported")
def case_current_leg(body: str, tmp: Path) -> None:
    work = tmp / "current"
    result, logs = run_carry(body, work, sealed=[], ceiling="")
    if result.returncode != 0 or "epoch leg: current" not in result.stdout:
        fail(f"current leg did not pass with its sentence\n{result.stdout}{result.stderr}")
    if (logs / "compose.args").exists() or exported_digests(work):
        fail("current leg read the seal or exported carried digests")


@case("previous leg, no own modules: says so, nothing carried")
def case_no_modules(body: str, tmp: Path) -> None:
    work = tmp / "nomodules"
    result, logs = run_carry(body, work, sealed=[], module_artifacts="")
    if result.returncode != 0 or "builds no module of its own" not in result.stdout:
        fail(f"module-less repo was not a stated pass\n{result.stdout}{result.stderr}")
    if (logs / "compose.args").exists():
        fail("module-less repo read the seal")


@case("previous leg, sealed set built for the ceiling: carried, verified, digests exported")
def case_accepted(body: str, tmp: Path, fixtures: Path) -> None:
    work = tmp / "accepted"
    sealed = [make_bundle(fixtures / "ai.module.nupkg", "MeshWeaver.AI", IDENTITY),
              make_bundle(fixtures / "maps.module.nupkg", "MeshWeaver.Maps", IDENTITY)]
    result, logs = run_carry(body, work, sealed=sealed)
    if result.returncode != 0:
        fail(f"an accepted carried set failed\n{result.stdout}{result.stderr}")
        return
    compose = (logs / "compose.args").read_text(encoding="utf-8")
    for expected in (f"--identity {IDENTITY}", "--packages *", f"--upstreams {SOURCE}",
                     "--storage-target store-a"):
        if expected not in compose:
            fail(f"compose was not called with '{expected}': {compose!r}")
    docker = (logs / "docker.args").read_text(encoding="utf-8")
    verify = [l for l in docker.splitlines() if "module-identity" in l and "--help" not in l]
    if len(verify) != 1:
        fail(f"the tester verified {len(verify)} time(s), expected once: {docker!r}")
    else:
        for expected in ("/carry/ai.module.nupkg", "/carry/maps.module.nupkg", f"--expect {IDENTITY}"):
            if expected not in verify[0]:
                fail(f"the tester call does not carry '{expected}': {verify[0]!r}")
    digests = exported_digests(work)
    if digests is None or not digests.is_file():
        fail("CARRIED_FORWARD_DIGESTS was not exported")
        return
    recorded = {l.split(" ", 1)[0] for l in digests.read_text(encoding="utf-8").splitlines() if l}
    wanted = {sha(p) for p in sealed}
    if recorded != wanted:
        fail(f"exported digests {sorted(recorded)} != the sealed bytes' {sorted(wanted)}")


@case("previous leg, a bundle built for another identity: REFUSED, no digests exported")
def case_wrong_identity(body: str, tmp: Path, fixtures: Path) -> None:
    work = tmp / "wrongid"
    sealed = [make_bundle(fixtures / "ai2.module.nupkg", "MeshWeaver.AI", IDENTITY),
              make_bundle(fixtures / "maps2.module.nupkg", "MeshWeaver.Maps", OTHER)]
    result, _ = run_carry(body, work, sealed=sealed)
    if result.returncode == 0:
        fail("a carried set holding a current-epoch build passed")
    if exported_digests(work):
        fail("digests were exported for a refused set")


@case("previous leg, this run's own builds already present: REFUSED before the seal is read")
def case_preexisting(body: str, tmp: Path, fixtures: Path) -> None:
    work = tmp / "preexisting"
    own = make_bundle(fixtures / "own.module.nupkg", "MeshWeaver.AI", OTHER)
    result, logs = run_carry(body, work, sealed=[], preexisting=[own])
    if result.returncode == 0 or "3583" not in result.stdout + result.stderr:
        fail(f"own builds on the previous leg were not refused by name\n{result.stdout}{result.stderr}")
    if (logs / "compose.args").exists():
        fail("the seal was read although own builds were present")


@case("previous leg, the seal holds no module: REFUSED")
def case_empty_seal(body: str, tmp: Path) -> None:
    result, _ = run_carry(body, tmp / "emptyseal", sealed=[])
    if result.returncode == 0:
        fail("an empty sealed module set passed on a repository that builds modules")


@case("previous leg, the ceiling's tester has no verb: REFUSED")
def case_no_verb(body: str, tmp: Path, fixtures: Path) -> None:
    sealed = [make_bundle(fixtures / "ai3.module.nupkg", "MeshWeaver.AI", IDENTITY)]
    result, _ = run_carry(body, tmp / "noverb", sealed=sealed, no_verb=True)
    if result.returncode == 0:
        fail("a tester without module-identity was trusted")


@case("previous leg, no identity resolved: REFUSED")
def case_no_identity(body: str, tmp: Path) -> None:
    result, _ = run_carry(body, tmp / "noid", sealed=[], identity="")
    if result.returncode == 0:
        fail("an unknown identity passed")


# ── ext-modules digest refusal ──────────────────────────────────────────────────────────────
def run_ext(body: str, work: Path, bundle: Path, digests: list[str]) -> subprocess.CompletedProcess[str]:
    work.mkdir(parents=True)
    listing = work / "carried.sha256"
    listing.write_text("".join(f"{d} {bundle.name}\n" for d in digests), encoding="utf-8")
    saved = os.environ.get("CARRIED_FORWARD_DIGESTS")
    os.environ["CARRIED_FORWARD_DIGESTS"] = str(listing)
    try:
        return run_step(body, work, [bundle])
    finally:
        if saved is None:
            os.environ.pop("CARRIED_FORWARD_DIGESTS", None)
        else:
            os.environ["CARRIED_FORWARD_DIGESTS"] = saved


def sealed_names(work: Path) -> list[str]:
    folder = seal_dir(work)
    return sorted(p.name for p in folder.glob("*.module.nupkg")) if folder.is_dir() else []


@case("ext-modules: a carried bundle is sealed")
def case_ext_carried(body: str, tmp: Path, fixtures: Path) -> None:
    bundle = make_bundle(fixtures / "MeshWeaver.AI.module.nupkg", "MeshWeaver.AI", IDENTITY)
    work = tmp / "ext-carried"
    result = run_ext(body, work, bundle, [sha(bundle)])
    if result.returncode != 0:
        fail(f"a carried own bundle was refused\n{result.stdout}{result.stderr}")
    elif not sealed_names(work):
        fail("a carried own bundle was not staged for the seal")


@case("ext-modules: an own bundle NOT among the carried digests is REFUSED, nothing sealed")
def case_ext_foreign(body: str, tmp: Path, fixtures: Path) -> None:
    bundle = make_bundle(fixtures / "MeshWeaver.Maps.module.nupkg", "MeshWeaver.Maps", OTHER)
    work = tmp / "ext-foreign"
    result = run_ext(body, work, bundle, ["0" * 64])
    if result.returncode == 0:
        fail("an own bundle outside the carried set passed the publish-bake ext-modules step")
    elif "carried-forward" not in result.stdout + result.stderr:
        fail(f"the refusal does not name the carried-forward set\n{result.stdout}{result.stderr}")
    if sealed_names(work):
        fail(f"a refused bundle was staged for the seal: {sealed_names(work)}")


@case("falsification: without the digest check the foreign bundle IS sealed")
def case_ext_falsified(body: str, tmp: Path, fixtures: Path) -> None:
    start = body.find('if [ "$origin" = "own" ] && [ -n "${CARRIED_FORWARD_DIGESTS:-}" ]; then')
    end = body.find("fi\n", start)
    if start < 0 or end < 0:
        fail("the digest check was not found in the real ext-modules step — this harness tests nothing")
        return
    mutated = body[:start] + body[end + 3:]
    bundle = make_bundle(fixtures / "MeshWeaver.Geo.module.nupkg", "MeshWeaver.Geo", OTHER)
    work = tmp / "ext-falsified"
    result = run_ext(mutated, work, bundle, ["0" * 64])
    if result.returncode != 0 or not sealed_names(work):
        fail("with the check removed the foreign bundle was still refused — the refusal case is "
             f"red for some other reason\n{result.stdout}{result.stderr}")


def main() -> int:
    try:
        import yaml  # noqa: F401
    except ImportError as exc:
        die(f"this harness cannot run — {exc}. pip install pyyaml.")
    for tool in ("unzip", "jq"):
        if shutil.which(tool) is None:
            die(f"this harness needs `{tool}`")

    carry = extract_step(WORKFLOW, "carry-forward")
    ext = extract_step(WORKFLOW, "ext-modules")
    with tempfile.TemporaryDirectory() as raw:
        tmp = Path(raw)
        fixtures = tmp / "fixtures"
        fixtures.mkdir()
        case_current_leg(carry, tmp)
        case_no_modules(carry, tmp)
        case_accepted(carry, tmp, fixtures)
        case_wrong_identity(carry, tmp, fixtures)
        case_preexisting(carry, tmp, fixtures)
        case_empty_seal(carry, tmp)
        case_no_verb(carry, tmp, fixtures)
        case_no_identity(carry, tmp)
        case_ext_carried(ext, tmp, fixtures)
        case_ext_foreign(ext, tmp, fixtures)
        case_ext_falsified(ext, tmp, fixtures)

    print()
    if FAILURES:
        print(f"FAILED — {len(FAILURES)} finding(s) over {CASES} case(s):")
        for finding in FAILURES:
            print(f"  - {finding}")
        return 1
    print(f"PASS — {CASES} case(s): the carry-forward step reads the seal, verifies every bundle "
          "against the ceiling's identity, exports exactly the sealed bytes' digests and refuses "
          "every other shape; ext-modules seals only carried bytes, and its falsification arm goes red.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
