#!/usr/bin/env python3
"""The two Azure-direct readers of a sealed publication, against a share that HAS a generation.

🚨 WHY THIS HARNESS EXISTS (MeshWeaver#3461, phase 3). Phase 1 routed every reader the PORTAL IMAGE
carries through `ShippedPrebuiltBundles.PublicationDirectoryOf`. Two readers are not in that image
and were therefore not covered: `compose-sealed-modules.sh` on its `--storage-target` (OIDC
fallback) path, and `node-repo-gate.yml`'s `seed` step `download-batch`. Both composed their paths
under the bare `prebuilt-bundles/<identity>/<source>/` prefix.

That is harmless only while nothing writes a generation. The moment a prefix is flipped to
`publication-layout: generation` these two are left on the flat compatibility copy while every
pointer-following reader has moved on — and at phase 5, when the flat copy is dropped, they break
outright. `SealedPublicationGenerations.md` names both by name as a PRECONDITION on flipping any
prefix, and NOTHING executed either of their Azure paths before this file: `test-sealed-module-
compose.py` runs the script with `--registry-url` only, and its gate case runs the `seed` step with
`TARGETS=""`, so the whole `download-batch` block was dead to every harness in the repository.

🚨 And MeshWeaver#4172 turned this from tidiness into load-bearing: a downstream publication now
seals only its OWN modules, so an upstream's module bytes are reachable through the upstream's own
seal and nowhere else. There is no downstream copy left to fall back on.

THE DISCRIMINATING FIXTURE, the same trick `bake-scope.sh --self-test` uses: the flat copy and the
generation `_current` names hold files with the SAME NAMES and DIFFERENT BYTES. A reader that
ignores the pointer composes the flat bytes; one that resolves it composes the generation's. The
verdict is which bytes landed, so neither case can pass by accident.

    python3 .github/scripts/test-publication-pointer-readers.py
"""

from __future__ import annotations

import os
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

try:
    import yaml
except ImportError:  # pragma: no cover — the CI step installs PyYAML
    print("::error::test-publication-pointer-readers.py needs PyYAML to EXTRACT the gate's seed "
          "step (pip install pyyaml). Refusing rather than skipping: a harness that quietly tests "
          "less renders the same green tick as one that tests the lane.")
    raise SystemExit(1) from None

HERE = Path(__file__).resolve().parent
COMPOSE = HERE / "compose-sealed-modules.sh"
GATE = HERE.parent / "workflows" / "node-repo-gate.yml"
SEED_STEP_ID = "seed"

IDENTITY = "s0123456789abcdef0123456789abcdef"
SOURCE = "plugins"
ACCOUNT = "acct"
SHARE = "share"
GENERATION = "Systemorph-MeshWeaver-4242-1"
MODULE = "ai.module.nupkg"
BUNDLE = "Store.zip"
OTHER_SOURCE = "education"
OTHER_MODULE = "maps.module.nupkg"
OTHER_FLAT_BYTES = "EDUCATION-FLAT-BYTES"

FLAT_BYTES = "FLAT-COPY-BYTES"
GEN_BYTES = "GENERATION-BYTES"

# A stub `az` whose remote is a directory tree: <root>/<account>/<share>/<path>. Modelled on the
# one in bake-scope.sh --self-test, plus the `file download-batch` verb the gate's seed uses.
#
# 🚨 The batch RECURSES and FLATTENS, exactly as the real CLI does. That is not decoration: it is
# the property that makes reading a prefix holding generations a MIX, and the reason the gate
# refuses an unfollowed pointer rather than taking the reader's fall-back there.
STUB_AZ = r"""#!/usr/bin/env bash
group="$2"
verb=""
for a in "$@"; do case "$a" in exists|download-batch|download|list) verb="$a"; break;; esac; done
account=""; share=""; path=""; dest=""; name=""; source=""; destination=""; pattern=""
while [ $# -gt 0 ]; do
  case "$1" in
    --account-name) account="$2"; shift 2;;
    --share-name)   share="$2";   shift 2;;
    --path)         path="$2";    shift 2;;
    --dest)         dest="$2";    shift 2;;
    --name)         name="$2";    shift 2;;
    --source)       source="$2";  shift 2;;
    --destination)  destination="$2"; shift 2;;
    --pattern)      pattern="$2"; shift 2;;
    *) shift;;
  esac
done
file="$MOCK_AZ_ROOT/$account/$share/$path"
dir="$MOCK_AZ_ROOT/$account/$share/$name"
case "$group/$verb" in
  directory/exists) { [ -d "$dir" ] && echo true; } || echo false;;
  directory/list)
    # Only the one --query the reader issues: [?type=='dir'].name, -o tsv.
    [ -d "$dir" ] || exit 1
    find "$dir" -mindepth 1 -maxdepth 1 -type d -exec basename {} \;
    ;;
  file/exists)      { [ -f "$file" ] && echo true; } || echo false;;
  file/download)    [ -f "$file" ] || exit 1; mkdir -p "$(dirname "$dest")"; cp "$file" "$dest";;
  file/download-batch)
    # --source is "<share>/<dir>", and the tree already starts at <root>/<account>/<share>.
    src="$MOCK_AZ_ROOT/$account/$source"
    [ -d "$src" ] || exit 1
    mkdir -p "$destination"
    find "$src" -type f -name "${pattern:-*}" -exec cp {} "$destination"/ \;
    ;;
  *) echo "stub-az: unmodelled '$group $verb' — teach the stub rather than letting the script fall back" >&2; exit 64;;
esac
"""


def stage(root: Path, *, pointer: str | None, generation: bool = True,
          source: str = SOURCE, gen_name: str = GENERATION,
          flat: str = FLAT_BYTES, gen: str = GEN_BYTES) -> None:
    """One publication prefix: a flat copy, and optionally a generation with the same names and
    different bytes.

    🚨 `generation` is a SEPARATE knob from `pointer`, and conflating them would have made the
    no-pointer control meaningless: with a generation present and no pointer, the gate's recursive
    batch flattens BOTH publications and which one wins is `find` order — a control that passes by
    coincidence. The true flat layout is what every prefix on every share is today: a flat copy and
    no generation at all.
    """
    prefix = root / ACCOUNT / SHARE / "prebuilt-bundles" / IDENTITY / source
    staged = [(prefix, flat)]
    if generation:
        staged.append((prefix / gen_name, gen))
    for directory, payload in staged:
        (directory / "modules").mkdir(parents=True, exist_ok=True)
        (directory / "_complete").write_text(f"{BUNDLE}\n", encoding="utf-8")
        (directory / "source-commit.txt").write_text("4b97be19\n", encoding="utf-8")
        (directory / "repository.txt").write_text("Systemorph/MeshWeaver.Plugins\n", encoding="utf-8")
        (directory / BUNDLE).write_text(payload, encoding="utf-8")
        module = MODULE if source == SOURCE else OTHER_MODULE
        (directory / "modules" / "_index").write_text(f"{module}\n", encoding="utf-8")
        (directory / "modules" / module).write_text(payload, encoding="utf-8")
    # 🚨 One bundle the FLAT copy carries and the generation does not. Same-named files silently
    # OVERWRITE each other when a recursive batch flattens them, so a mix of two publications can
    # be invisible to a count as well as to a reader — this asymmetric name is what makes the union
    # COUNTABLE, and it is the shape a real supersession has (a publication that dropped a bundle).
    (prefix / "Legacy.zip").write_text(flat, encoding="utf-8")
    if pointer is not None:
        (prefix / "_current").write_text(pointer + "\n", encoding="utf-8")


def environment(workdir: Path, root: Path) -> dict[str, str]:
    binary = workdir / "bin"
    binary.mkdir(parents=True, exist_ok=True)
    az = binary / "az"
    az.write_text(STUB_AZ, encoding="utf-8")
    az.chmod(0o755)
    env = dict(os.environ)
    env["PATH"] = f"{binary}:{env['PATH']}"
    env["MOCK_AZ_ROOT"] = str(root)
    return env


def run_compose(workdir: Path, root: Path, *, upstreams: str = SOURCE, packages: str = "AI",
                generations: str | None = None) -> tuple[subprocess.CompletedProcess[str], Path]:
    out = workdir / "ext"
    out.mkdir(parents=True, exist_ok=True)
    argv = [str(COMPOSE), "--identity", IDENTITY, "--packages", packages,
            "--upstreams", upstreams, "--out", str(out),
            "--storage-target", f"{ACCOUNT}/{SHARE}"]
    if generations is not None:
        argv += ["--generations", generations]
    proc = subprocess.run(argv, capture_output=True, text=True, timeout=180, check=False,
                          env=environment(workdir, root))
    return proc, out / MODULE


def seed_step() -> str:
    """The gate's `seed` step, EXTRACTED by its `id:` — never copied.

    Fails RED rather than skipping when the step is gone, has grown an expression this harness
    cannot supply, or has stopped issuing the `download-batch` this case exists to exercise.
    """
    if not GATE.is_file():
        raise SystemExit(f"FAIL: {GATE} is missing — this harness runs the REAL lane, never a copy.")
    workflow = yaml.safe_load(GATE.read_text(encoding="utf-8"))
    for job in (workflow.get("jobs") or {}).values():
        for step in job.get("steps") or []:
            if step.get("id") != SEED_STEP_ID:
                continue
            run = step["run"]
            if re.search(r"\$\{\{", run):
                raise SystemExit(
                    f"FAIL: the '{SEED_STEP_ID}' step now carries a workflow expression this "
                    "harness cannot supply — teach it, never skip it.")
            if "download-batch" not in run:
                raise SystemExit(
                    f"FAIL: the '{SEED_STEP_ID}' step no longer issues the `download-batch` this "
                    "harness drives — it would pass having exercised nothing.")
            return run
    raise SystemExit(f"FAIL: no step with id '{SEED_STEP_ID}' in {GATE}.")


def run_seed(workdir: Path, root: Path, body: str, *, upstreams: str = SOURCE
             ) -> tuple[subprocess.CompletedProcess[str], Path]:
    workdir.mkdir(parents=True, exist_ok=True)
    script = workdir / "seed.sh"
    script.write_text(body, encoding="utf-8")
    runner_temp = workdir / "runner-temp"
    runner_temp.mkdir(parents=True, exist_ok=True)
    env = environment(workdir, root)
    env.update(
        UPSTREAMS=upstreams, IDENTITY=IDENTITY, TARGETS=f"{ACCOUNT}/{SHARE}",
        REGISTRY_URL="", REGISTRY_KEY="",
        RUNNER_TEMP=str(runner_temp), GITHUB_OUTPUT=str(runner_temp / "outputs"),
        GITHUB_ENV=str(runner_temp / "github-env"))
    proc = subprocess.run(["bash", str(script)], capture_output=True, text=True,
                          timeout=180, check=False, env=env)
    return proc, runner_temp / "upstream-seed" / BUNDLE


FAILURES: list[str] = []
EXECUTED: list[str] = []


def check(case: str, condition: bool, why: str,
          proc: subprocess.CompletedProcess[str] | None = None) -> None:
    # 🚨 COUNTED, never asserted from a literal. The success line used to carry a hand-written
    # number, and a hand-written number is a claim about the harness rather than a reading of it —
    # it said 13 while 12 cases ran. A count that cannot drift from what executed is the only kind
    # worth printing beside a green tick.
    EXECUTED.append(case)
    if condition:
        print(f"  OK   {case}")
        return
    FAILURES.append(case)
    print(f"  FAIL {case}: {why}")
    if proc is not None:
        print(f"    exit={proc.returncode}\n    stdout:\n{proc.stdout}\n    stderr:\n{proc.stderr}")


def read(path: Path) -> str:
    try:
        return path.read_text(encoding="utf-8")
    except OSError as error:
        return f"<unreadable: {error}>"


def main() -> int:
    body = seed_step()

    with tempfile.TemporaryDirectory() as temporary:
        base = Path(temporary)

        # ── 1. THE PROPERTY: a pointed-at generation is what both readers read ────────────
        print("a prefix whose _current names a generation:")
        root = base / "pointed" / "remote"
        stage(root, pointer=GENERATION)

        proc, module = run_compose(base / "pointed" / "compose", root)
        check("compose-sealed-modules.sh --storage-target composes the GENERATION's module bytes",
              proc.returncode == 0 and read(module) == GEN_BYTES,
              f"composed {read(module)!r}, wanted {GEN_BYTES!r}. A reader that ignores the pointer "
              "takes the flat compatibility copy — which at phase 4 is a DIFFERENT publication and "
              "at phase 5 is not there at all", proc)

        proc, bundle = run_seed(base / "pointed" / "seed", root, body)
        check("node-repo-gate's seed download-batch seeds the GENERATION's bundle bytes",
              proc.returncode == 0 and read(bundle) == GEN_BYTES,
              f"seeded {read(bundle)!r}, wanted {GEN_BYTES!r}", proc)
        seeded = sorted(p.name for p in bundle.parent.glob("*.zip")) if bundle.parent.is_dir() else []
        check("…and it did NOT also flatten the flat copy's bundles into the same seed directory",
              proc.returncode == 0 and seeded == [BUNDLE],
              f"the seed holds {seeded}, wanted exactly ['{BUNDLE}']. `download-batch` recurses and "
              "the CLI flattens, so a prefix read flat lands EVERY generation's *.zip in one "
              "directory — and same-named files overwrite each other, so the mix is invisible to "
              "the step's own count as well as to the compiler", proc)

        # ── 2. THE CONTROL: no pointer ⇒ byte-identical to the flat behaviour ─────────────
        print("the same prefix with NO pointer (today's layout, and the one that must not change):")
        root = base / "flat" / "remote"
        stage(root, pointer=None, generation=False)

        proc, module = run_compose(base / "flat" / "compose", root)
        check("compose-sealed-modules.sh reads the prefix itself when there is no pointer",
              proc.returncode == 0 and read(module) == FLAT_BYTES,
              f"composed {read(module)!r}, wanted {FLAT_BYTES!r}. Resolution is opt-in BY THE "
              "WRITER; a reader that needed a pointer would break every prefix in the fleet", proc)

        proc, bundle = run_seed(base / "flat" / "seed", root, body)
        check("the gate's seed reads the prefix itself when there is no pointer",
              proc.returncode == 0 and read(bundle) == FLAT_BYTES,
              f"seeded {read(bundle)!r}, wanted {FLAT_BYTES!r}", proc)

        # ── 3. A DANGLING pointer: the reader contract falls back; the BATCH may not ──────
        print("a pointer naming a generation that is not on the share:")
        root = base / "dangling" / "remote"
        stage(root, pointer="Systemorph-MeshWeaver-9999-1", generation=False)

        proc, module = run_compose(base / "dangling" / "compose", root)
        check("compose-sealed-modules.sh falls back to the prefix, exactly as the reader contract says",
              proc.returncode == 0 and read(module) == FLAT_BYTES,
              f"composed {read(module)!r}, wanted {FLAT_BYTES!r}", proc)
        check("…and it names the dangling pointer instead of falling back in silence",
              "is not on the share" in proc.stdout + proc.stderr,
              "a silent fallback is indistinguishable from a resolution", proc)

        proc, bundle = run_seed(base / "dangling" / "seed", root, body)
        check("the gate's seed takes the SAME fall-back when nothing else is under the prefix",
              proc.returncode == 0 and read(bundle) == FLAT_BYTES,
              f"seeded {read(bundle)!r}, wanted {FLAT_BYTES!r}. A dangling pointer over a prefix "
              "that holds no generation is the ordinary fall-back — refusing here would red every "
              "gate over a swept-away pointer for no safety gained", proc)

        # ── 4. THE ORPHAN: a generation on the share with NO pointer ──────────────────────
        # A generation publish seals its directory and THEN moves the pointer, so a failed or
        # interrupted pointer upload leaves exactly this. The pointer file says nothing, and a
        # reader that asked only about `_current` would flatten both publications.
        print("a generation on the share with NO pointer at all (an interrupted pointer move):")
        root = base / "orphan" / "remote"
        stage(root, pointer=None)

        proc, module = run_compose(base / "orphan" / "compose", root)
        check("compose-sealed-modules.sh reads the flat copy — a POINT read of a prefix is safe",
              proc.returncode == 0 and read(module) == FLAT_BYTES,
              f"composed {read(module)!r}, wanted {FLAT_BYTES!r}", proc)

        proc, _ = run_seed(base / "orphan" / "seed", root, body)
        check("the gate's seed REFUSES, because its BATCH would flatten both publications",
              proc.returncode != 0 and "3461" in proc.stdout + proc.stderr,
              "the condition has to be a property of the BYTES, not of the pointer file: checking "
              "only `_current` misses the orphan entirely, and the recursive batch then lands two "
              "publications' *.zip in one seed directory where same-named files overwrite each "
              "other — invisible to the step's own count", proc)

        # ── 5. ONE PUBLICATION PER GATE: the seed's generation reaches the composition ────
        print("the generation the seed read is what the module composition uses:")
        root = base / "carried" / "remote"
        stage(root, pointer=GENERATION)
        work = base / "carried" / "seed"
        proc, bundle = run_seed(work, root, body)
        check("the seed exports the generation it read",
              proc.returncode == 0
              and f"SEED_GENERATIONS={SOURCE}={GENERATION}"
              in read(work / "runner-temp" / "github-env"),
              "without it the composition resolves the pointer a SECOND time, so a pointer that "
              "advances between the two steps hands one gate one generation's bundles and "
              f"another's module bytes. GITHUB_ENV held: {read(work / 'runner-temp' / 'github-env')!r}",
              proc)

        # …and the script honours it even when the pointer has since moved elsewhere.
        moved = base / "carried" / "remote" / ACCOUNT / SHARE / "prebuilt-bundles" / IDENTITY / SOURCE
        (moved / "_current").write_text("Systemorph-MeshWeaver-9999-1\n", encoding="utf-8")
        proc, module = run_compose(base / "carried" / "compose", root,
                                   generations=f"{SOURCE}={GENERATION}")
        check("…and --generations wins over a pointer that has moved since",
              proc.returncode == 0 and read(module) == GEN_BYTES,
              f"composed {read(module)!r}, wanted {GEN_BYTES!r}. A caller that has already resolved "
              "the publication is stating a fact; re-resolving it would be the cross-step mix", proc)

        # ── 6. TWO UPSTREAMS, and the pin is PER SOURCE ───────────────────────────────────
        # 🚨 `upstream-seed` is a LIST. One scalar generation would be applied to every upstream,
        # so a gate for `plugins education` would read education's modules out of a directory named
        # after plugins' generation — and a flat source following a generation source would inherit
        # the previous one's value. The fixture makes the two sources' generation names DIFFERENT
        # and the second one FLAT, so a scalar cannot produce this result by any route.
        print("two upstreams, one on a generation and one flat:")
        root = base / "two" / "remote"
        stage(root, pointer=GENERATION)
        stage(root, pointer=None, generation=False, source=OTHER_SOURCE,
              flat=OTHER_FLAT_BYTES, gen=OTHER_FLAT_BYTES)
        work = base / "two" / "seed"
        proc, _ = run_seed(work, root, body, upstreams=f"{SOURCE} {OTHER_SOURCE}")
        exported = read(work / "runner-temp" / "github-env")
        check("the seed exports ONE entry per source, and only for the source that has a generation",
              proc.returncode == 0
              and f"SEED_GENERATIONS={SOURCE}={GENERATION}" in exported
              and f"{OTHER_SOURCE}=" not in exported,
              "a scalar export applies one source's generation to every upstream, and leaves a "
              f"flat source carrying the previous one's value. GITHUB_ENV held: {exported!r}", proc)

        proc, module = run_compose(base / "two" / "compose", root,
                                   upstreams=f"{SOURCE} {OTHER_SOURCE}", packages="Maps",
                                   generations=f"{SOURCE}={GENERATION}")
        maps = module.parent / OTHER_MODULE
        check("…and the FLAT upstream's module is read from its own prefix, not from that generation",
              proc.returncode == 0 and read(maps) == OTHER_FLAT_BYTES,
              f"composed {read(maps)!r}, wanted {OTHER_FLAT_BYTES!r}. A pin applied to every "
              "upstream would look for this module under a directory named after the OTHER "
              "source's generation — which is not there, so the gate reds on a publication that "
              "is perfectly fine", proc)

    print("")
    if FAILURES:
        print(f"::error title=publication-pointer readers::{len(FAILURES)} case(s) failed — "
              "an Azure-direct reader that ignores `_current` serves a publication nobody points "
              "at, silently (MeshWeaver#3461).")
        return 1
    print(f"test-publication-pointer-readers: {len(EXECUTED)} case(s) executed over 2 readers — all green.")
    return 0


if __name__ == "__main__":
    if shutil.which("bash") is None:  # pragma: no cover
        raise SystemExit("FAIL: bash is required.")
    sys.exit(main())
