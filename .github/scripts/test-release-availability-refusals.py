#!/usr/bin/env python3
"""EXECUTE `check-release-availability.sh` and assert its three outcomes are TOLD APART in the log.

WHY
---
This gate is the one that held `MeshWeaver.Reinsurance` 23 times in 24 hours (MeshWeaver#3583).
Every refusal exits 1 — that is correct and is not what this tests. What it tests is that a reader
can tell, from the log alone, WHICH of three different things happened, because they want three
different actions:

  * CANNOT RESOLVE …  no identity at all. Nothing was checked; no upstream may be called absent.
  * CANNOT DETERMINE … an identity, but a probe ERRORED. The absent count is a floor, not a verdict.
  * release availability: N of M … every probe answered NO. The only one that is a statement about
    an upstream, and the only one to act on by waiting for it.

Before this change the middle case was folded into the last one's count, so "I could not ask" was
reported as "the upstream has not published" — a smaller number in place of a refusal.

HOW IT STAYS HONEST
-------------------
* The REAL script is executed, never a copy — a copy passes while the real thing rots.
* `az` is a PATH stub driven by env vars, so no credential and no network are involved.
* Every case asserts on the DISTINGUISHING TEXT and also asserts the OTHER headlines are ABSENT.
  Asserting only "exit 1" would pass even if all three printed the same sentence, which is the
  defect being fixed.
* Case 4 is a CONTROL that must PASS (exit 0). A harness whose every case is a failure cannot tell
  "the gate refuses correctly" from "the gate always refuses".
* Case 5 asserts the resolved identity and its ORIGIN are printed on the success path too — the
  log has to answer "which identity did we ask about" when nothing is wrong, or it cannot be
  compared against a run where something is.
"""

from __future__ import annotations

import os
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent
SCRIPT = ROOT / "check-release-availability.sh"

RESOLVE = "CANNOT RESOLVE"
DETERMINE = "CANNOT DETERMINE"
ABSENT = "are not available for framework identity"

# `az` stub. Behaviour per invocation is chosen by env vars the case sets:
#   AZ_MARKER_FAIL=1   -> `storage file download` fails (no release marker)
#   AZ_EXISTS=<v>      -> `storage file exists` prints <v> ("true"/"false")
#   AZ_EXISTS_FAIL=1   -> `storage file exists` exits non-zero (an ERRORED probe)
#   AZ_EXISTS_MAP      -> "source=true;source2=false" per-source override
AZ_STUB = r"""#!/usr/bin/env bash
args="$*"
case "$args" in
  *"storage file download"*)
    if [ "${AZ_MARKER_FAIL:-0}" = "1" ]; then exit 1; fi
    dest=""; prev=""
    for a in "$@"; do [ "$prev" = "--dest" ] && dest="$a"; prev="$a"; done
    printf '%s' "${AZ_MARKER_VALUE:-sdeadbeefdeadbeefdeadbeefdeadbeef}" > "$dest"
    exit 0 ;;
  *"storage file exists"*)
    path=""; prev=""
    for a in "$@"; do [ "$prev" = "--path" ] && path="$a"; prev="$a"; done
    src=$(printf '%s' "$path" | awk -F/ '{print $(NF-1)}')
    if [ -n "${AZ_EXISTS_MAP:-}" ]; then
      for pair in $(printf '%s' "$AZ_EXISTS_MAP" | tr ';' ' '); do
        k="${pair%%=*}"; v="${pair#*=}"
        if [ "$k" = "$src" ]; then
          [ "$v" = "error" ] && exit 3
          printf '%s\n' "$v"; exit 0
        fi
      done
    fi
    if [ "${AZ_EXISTS_FAIL:-0}" = "1" ]; then exit 3; fi
    printf '%s\n' "${AZ_EXISTS:-true}"; exit 0 ;;
esac
exit 0
"""


def run(args, **env):
    """Execute the real script with the az stub on PATH. Returns (rc, combined output)."""
    with tempfile.TemporaryDirectory() as tmp:
        stub = Path(tmp) / "az"
        stub.write_text(AZ_STUB)
        stub.chmod(0o755)
        e = dict(os.environ)
        e["PATH"] = f"{tmp}:{e['PATH']}"
        e["BAKE_PUBLISH_TARGETS"] = "acct/share/base"
        e.pop("GITHUB_STEP_SUMMARY", None)
        e.update({k: str(v) for k, v in env.items()})
        p = subprocess.run([str(SCRIPT), *args], capture_output=True, text=True, env=e)
        return p.returncode, p.stdout + p.stderr


WORKFLOW = ROOT.parent / "workflows" / "node-repo-publish-bake.yml"
STEP_ID = "upstream-gate"

# The `${{ }}` expressions the harness knows how to supply. Anything else in the step is a REFUSAL,
# not a silent substitution: a step that grew a new input must fail this harness loudly rather than
# be executed with a guess.
KNOWN_EXPRESSIONS = {
    "steps.identity.outputs.identity": "sfeedface",
    "inputs.upstream-sources": "crm",
    "inputs.bake-publish-targets": "acct/share/base",
    "steps.platform.outputs.ref": "portal:resolved-by-this-run",
    "github.event.action": "meshweaver-framework-released",
}

FAILURES: list[str] = []


def check(case, cond, detail):
    if cond:
        print(f"  ok   {case}: {detail}")
    else:
        print(f"  FAIL {case}: {detail}")
        FAILURES.append(f"{case}: {detail}")


def expect_only(case, out, present, absent_list):
    check(case, present in out, f"says {present!r}")
    for other in absent_list:
        check(case, other not in out, f"does NOT also say {other!r}")


def extract_step():
    """The step's `run:` and `env:`, EXTRACTED from the workflow by id — never a copy."""
    import re as _re

    import yaml  # noqa: PLC0415

    doc = yaml.safe_load(WORKFLOW.read_text())
    for job in doc.get("jobs", {}).values():
        for step in job.get("steps", []) or []:
            if step.get("id") == STEP_ID:
                run, env = step["run"], step.get("env", {}) or {}

                def fill(text):
                    def sub(m):
                        expr = m.group(1).strip()
                        if expr not in KNOWN_EXPRESSIONS:
                            raise SystemExit(
                                f"REFUSING to execute: step '{STEP_ID}' uses an expression this "
                                f"harness cannot supply: ${{{{ {expr} }}}}. Add it to "
                                f"KNOWN_EXPRESSIONS deliberately — never guess."
                            )
                        return KNOWN_EXPRESSIONS[expr]
                    return _re.sub(r"\$\{\{([^}]*)\}\}", sub, text)

                return fill(run), {k: fill(str(v)) for k, v in env.items()}
    raise SystemExit(f"REFUSING: no step with id '{STEP_ID}' in {WORKFLOW}")


def run_step(headline: str):
    """Run the extracted step with the availability script stubbed to print `headline` and fail."""
    run, env = extract_step()
    with tempfile.TemporaryDirectory() as tmp:
        gate = Path(tmp) / "mw-platform-gate" / ".github" / "scripts"
        gate.mkdir(parents=True)
        stub = gate / "check-release-availability.sh"
        stub.write_text("#!/usr/bin/env bash\n"
                        f"echo {headline!r}\n"
                        "echo 'identity resolved: sfeedface — from ...'\n"
                        "exit 1\n" if headline else
                        "#!/usr/bin/env bash\necho ok\nexit 0\n")
        stub.chmod(0o755)
        summary = Path(tmp) / "summary.md"
        summary.write_text("")
        e = dict(os.environ)
        e.update(env)
        e.update({"GITHUB_STEP_SUMMARY": str(summary), "RUNNER_TEMP": tmp,
                  "GITHUB_EVENT_NAME": "repository_dispatch"})
        p = subprocess.run(["bash", "-c", run], cwd=tmp, capture_output=True, text=True, env=e)
        return p.returncode, p.stdout + p.stderr, summary.read_text()


def workflow_cases():
    upstream_absent = "release availability: 1 of 1 source(s) are not available for framework identity sfeedface."
    for headline, label, must_say, must_not_say in [
        (upstream_absent, "upstream absent", "Upstreams not ready",
         ["Could not resolve", "Could not query"]),
        ("::error::CANNOT RESOLVE a framework identity: ...", "cannot resolve",
         "Could not resolve a framework identity", ["Upstreams not ready", "Could not query"]),
        ("::error::CANNOT DETERMINE release availability ...", "cannot determine",
         "Could not query the artifact store", ["Upstreams not ready", "Could not resolve"]),
    ]:
        rc, out, summary = run_step(headline)
        both = out + summary
        check(f"case 7 ({label})", rc != 0, f"the step still fails (rc={rc})")
        check(f"case 7 ({label})", must_say in both, f"reports {must_say!r}")
        for other in must_not_say:
            check(f"case 7 ({label})", other not in both,
                  f"does NOT also report {other!r}")

    # 🚨 The claim that is FALSE on the refusal paths, asserted directly: a refusal must never say an
    # upstream has no sealed publication, because nothing was checked.
    for headline, label in [("::error::CANNOT RESOLVE x", "cannot resolve"),
                            ("::error::CANNOT DETERMINE x", "cannot determine")]:
        _, out, summary = run_step(headline)
        check(f"case 7 ({label})", "has no sealed" not in (out + summary),
              "never claims an upstream has no sealed publication")


def main() -> int:
    print("case 1 — no release marker: CANNOT RESOLVE, and nothing is called absent")
    rc, out = run(["3.0.0-ci.9999", "crm"], AZ_MARKER_FAIL=1)
    check("case 1", rc != 0, f"exits non-zero (got {rc})")
    expect_only("case 1", out, RESOLVE, [ABSENT, DETERMINE])
    check("case 1", "crm" not in out.split(RESOLVE)[-1].split("\n")[0],
          "the refusal line does not name an upstream as absent")

    print("case 2 — identity resolves, upstream absent: the availability verdict, not a refusal")
    rc, out = run(["--identity", "sabc", "crm"], AZ_EXISTS="false")
    check("case 2", rc != 0, f"exits non-zero (got {rc})")
    expect_only("case 2", out, ABSENT, [RESOLVE, DETERMINE])
    check("case 2", "crm" in out, "names the upstream that has not published")

    print("case 3 — identity resolves, probe ERRORS: CANNOT DETERMINE, never an absent count")
    rc, out = run(["--identity", "sabc", "crm"], AZ_EXISTS_FAIL=1)
    check("case 3", rc != 0, f"exits non-zero (got {rc})")
    expect_only("case 3", out, DETERMINE, [RESOLVE, ABSENT])

    print("case 3b — one errored probe + one genuine NO: the NO is a FLOOR under the refusal")
    rc, out = run(["--identity", "sabc", "crm", "plugins"],
                  AZ_EXISTS_MAP="crm=error;plugins=false")
    check("case 3b", rc != 0, f"exits non-zero (got {rc})")
    expect_only("case 3b", out, DETERMINE, [RESOLVE, ABSENT])
    check("case 3b", "floor, not the number" in out,
          "says the absent count is a floor while probes are erroring")

    print("case 4 — CONTROL: everything sealed must PASS")
    rc, out = run(["--identity", "sabc", "crm", "plugins"], AZ_EXISTS="true")
    check("case 4", rc == 0, f"exits 0 (got {rc})")
    for h in (RESOLVE, DETERMINE, ABSENT):
        check("case 4", h not in out, f"prints no {h!r}")

    print("case 5 — the identity and its ORIGIN are logged, including on the success path")
    rc, out = run(["--identity", "sabc", "--identity-origin", "the portal image 'x:1' (event=schedule)",
                   "crm"], AZ_EXISTS="true")
    check("case 5", rc == 0, f"exits 0 (got {rc})")
    check("case 5", "identity resolved: sabc" in out, "names the identity it asked about")
    check("case 5", "event=schedule" in out, "names WHY that identity — the origin the caller gave")

    print("case 6 — origin is stated as unknown rather than silently omitted")
    rc, out = run(["--identity", "sabc", "crm"], AZ_EXISTS="true")
    check("case 6", "identity resolved: sabc" in out, "still names the identity")
    check("case 6", "origin not stated" in out, "says the origin was not supplied")

    print("case 7 — the WORKFLOW step must not re-collapse what the script distinguished")
    workflow_cases()

    print()
    if FAILURES:
        print(f"FAILED — {len(FAILURES)} assertion(s):")
        for f in FAILURES:
            print(f"  • {f}")
        return 1
    print("PASSED — all three outcomes are distinguishable, and the control still passes.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
