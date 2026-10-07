#!/usr/bin/env python3
"""test-release-promotes-both-notations.py — EXECUTE release.yml's version steps in both notations.

WHY. Policy `platform-semver-versioning` (Doc/Architecture/PlatformVersioning §6, decision 2): a
release keeps PROMOTING and renames nothing. Below line 3.1 `v3.0.0` promotes the newest
`3.0.0-ci.<run>` set of its commit; from line 3.1 on, `v<major>.<minor>.0` promotes the newest
`<major>.<minor>.<run>` set. The steps that decide this are bash inside a workflow, which no build
type-checks, and the shape they replaced read only `ci.<n>` — it refused every new-notation set and
accepted the withdrawn slip `3.1.0-ci.7841` under `v3.1.0`. So this script reads release.yml, cuts
out the real step text and RUNS it against fake registry listings (an `az` stub), asserting the
verdict and the message.

A negative control: run it against a release.yml that predates the change — the new-notation and
era cases fail.

Usage: test-release-promotes-both-notations.py [path/to/release.yml]
"""
from __future__ import annotations

import json
import os
import subprocess
import sys
import tempfile
from pathlib import Path

import yaml

ROOT = Path(__file__).resolve().parents[2]


def main(argv: list[str]) -> int:
    workflow = Path(argv[1]) if len(argv) > 1 else ROOT / ".github" / "workflows" / "release.yml"
    steps = yaml.safe_load(workflow.read_text())["jobs"]["release"]["steps"]

    def step(prefix: str) -> str:
        return next(s for s in steps if s.get("name", "").startswith(prefix))["run"]

    rel = step("Resolve the release")
    set_run = step("Resolve the promoted continuous set")
    # The era fragment: from reading the version's parts to the end of the SEMVER decision.
    try:
        start = rel.index("IFS=. read -r V_MAJOR")
        end = rel.index("fi\n", rel.index("SEMVER=false")) + 3
        era = rel[start:end]
    except ValueError:
        print("FAIL release.yml decides no SemVer era (no `IFS=. read -r V_MAJOR … SEMVER=false` block)")
        return 1

    failures: list[str] = []
    tmp = Path(tempfile.mkdtemp(prefix="release-notations-"))

    def run(script: str, env: dict[str, str]) -> tuple[int, str]:
        path = tmp / "step.sh"
        path.write_text("set -euo pipefail\n" + script + '\necho "SEMVER=${SEMVER:-}"\n')
        p = subprocess.run(["bash", str(path)], cwd=ROOT, env={**os.environ, **env},
                           capture_output=True, text=True)
        return p.returncode, p.stdout + p.stderr

    def check(name: str, rc: int, out: str, want_rc: int, want: str) -> None:
        ok = rc == want_rc and want in out
        print(f"  {'ok  ' if ok else 'FAIL'} {name}")
        if not ok:
            failures.append(f"{name}: rc={rc} (want {want_rc}), want text {want!r}\n{out}")

    for version, want_rc, want in [
        ("3.0.0", 0, "SEMVER=false"),
        ("3.0.5", 0, "SEMVER=false"),
        ("3.1.0", 0, "SEMVER=true"),
        ("3.2.0", 0, "SEMVER=true"),
        ("4.0.0", 0, "SEMVER=true"),
        ("3.1.5", 1, "with a non-zero patch is a continuous build"),
    ]:
        rc, out = run(f"VERSION={version}\nTAG=v{version}\n" + era, {})
        check(f"era of v{version}", rc, out, want_rc, want)

    cases = [
        ("old notation: v3.0.0 promotes 3.0.0-ci.<run>", "3.0.0", "false",
         ["1234567", "3.0.0-ci.9000", "3.0.0-latest"], 0, "continuous set: 3.0.0-ci.9000"),
        ("new notation: v3.1.0 promotes 3.1.<run>", "3.1.0", "true",
         ["1234567", "3.1.10050", "3-latest", "3.1-latest"], 0, "continuous set: 3.1.10050"),
        ("new notation: the newest run on the digest wins", "3.1.0", "true",
         ["1234567", "3.1.10050", "3.1.10060"], 0, "continuous set: 3.1.10060"),
        ("a release re-run: the clean 3.1.0 already on the digest is no set", "3.1.0", "true",
         ["1234567", "3.1.0", "3.1.10050"], 0, "continuous set: 3.1.10050"),
        ("v3.1.0 on an old-notation build is refused", "3.1.0", "true",
         ["1234567", "3.0.0-ci.9000"], 1, "not a 3.1.<run> build"),
        ("v3.1.0 on the withdrawn slip 3.1.0-ci.7841 is refused", "3.1.0", "true",
         ["1234567", "3.1.0-ci.7841"], 1, "not a 3.1.<run> build"),
        ("v3.1.0 on a 3.2 set is refused", "3.1.0", "true",
         ["1234567", "3.2.10400"], 1, "not a 3.1.<run> build"),
        ("v3.0.0 on a new-notation build is refused", "3.0.0", "false",
         ["1234567", "3.1.10050"], 1, "not a 3.0.0-ci.<n> build"),
        ("a digest with no set tag is refused", "3.1.0", "true",
         ["1234567", "3-latest"], 1, "carries no continuous version tag"),
    ]
    for name, version, semver, tags, want_rc, want in cases:
        listing = tmp / "manifests.json"
        listing.write_text(json.dumps([
            {"digest": "sha256:aa", "tags": tags},
            {"digest": "sha256:bb", "tags": ["abcdefg", "3.1.9999"]},  # another commit's set: never read
        ]))
        stub = f'az() {{ cat "{listing}"; }}\nexport -f az\n'
        rc, out = run(stub + set_run, {"SHORT": "1234567", "VERSION": version, "SEMVER": semver,
                                       "RUNNER_TEMP": str(tmp), "GITHUB_OUTPUT": str(tmp / "out")})
        check(name, rc, out, want_rc, want)

    print(f"release notations self-test: {len(failures)} failure(s)")
    for f in failures:
        print(f"  - {f}")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
