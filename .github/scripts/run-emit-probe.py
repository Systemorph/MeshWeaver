#!/usr/bin/env python3
"""Bounded native Roslyn controls; a nonzero exit alone is never an emit reproduction."""
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import shutil
import signal
import subprocess
import time

ROOT = Path(__file__).resolve().parents[2]
PROJECT = Path("tools/RoslynEmitProbe")
DLL = PROJECT / "bin/Release/net10.0/EmitProbe.dll"
OUTPUT = Path("artifacts/roslyn-emit-probe")
LEGS = ("shared-nested", "shared-flat", "pristine-nested", "pristine-flat")
ARMS = (("default", 1000, False, "unset"), ("no-pgo", 1000, False, "0"),
        ("invalid-source", 2, True, "unset"))
TIMEOUT = 300
SUMMARY = re.compile(r"SUMMARY expectedAttempts=(\d+) observedAttempts=(\d+) "
                     r"successfulEmits=(\d+) failures=(\d+) elapsedSeconds=([0-9.]+)")
COUNT = re.compile(r"COUNT ([^=]+)=(\d+)")


def native_refusal(env, system, machine):
    if system != "Linux" or machine not in ("x86_64", "AMD64"):
        return "NOT RUN: requires a native Linux X64 runner; this host is not Linux X64"
    if env.get("GITHUB_ACTIONS") == "true" and (
        env.get("RUNNER_OS") != "Linux" or env.get("RUNNER_ARCH") != "X64"
        or env.get("MW_EMIT_NATIVE_RUNNER") != "ubuntu-24.04-hosted"
    ):
        return "NOT RUN: missing native ubuntu-24.04 hosted-runner assertion"
    # Do not silently turn an inherited JIT experiment into the default arm.
    tuned = [k for k in env if k.startswith(("COMPlus_", "DOTNET_Tiered", "DOTNET_TC_",
             "DOTNET_Jit", "DOTNET_AltJit", "DOTNET_ReadyToRun"))]
    if tuned:
        return "NOT RUN: inherited runtime tuning variables: " + ", ".join(sorted(tuned))
    return None


def classify(text, exit_code, timed_out, iterations, negative, pgo):
    """Validate the evidence before distinguishing expected controls from observed failures."""
    report = {"exitCode": exit_code, "timedOut": timed_out}
    environments, summaries, counts = [], [], {}
    errors = []
    for line in text.splitlines():
        if line.startswith("{"):
            try:
                item = json.loads(line)
                if isinstance(item, dict) and item.get("kind") == "environment":
                    environments.append(item)
            except json.JSONDecodeError:
                errors.append("malformed environment JSON")
        elif line.startswith("SUMMARY "):
            match = SUMMARY.fullmatch(line)
            if not match:
                errors.append("malformed summary")
            else:
                summaries.append(tuple(int(v) for v in match.groups()[:4]))
        elif line.startswith("COUNT "):
            match = COUNT.fullmatch(line)
            if not match or match[1] in counts:
                errors.append("malformed or duplicate count")
            else:
                counts[match[1]] = int(match[2])
    report.update(environment=environments[0] if len(environments) == 1 else None, counts=counts,
                  summary=summaries[0] if len(summaries) == 1 else None)
    if timed_out:
        return dict(report, classification="incomplete", passed=False, reason="process deadline expired")
    if exit_code is not None and exit_code < 0:
        return dict(report, classification="runtime-crash", passed=False,
                    reason=f"process terminated by signal {-exit_code}; not an emit exception verdict")
    if len(environments) != 1 or len(summaries) != 1:
        return dict(report, classification="incomplete", passed=False,
                    reason="exactly one environment record and completed summary are required")
    e = environments[0]
    if (e.get("runtimeVersion") != "10.0.12" or e.get("architecture") != "X64"
        or not re.match(r"(?:Linux|Ubuntu)\b", str(e.get("os", "")))
        or not re.match(r"5\.9\.0(?:[+\- ]|$)", str(e.get("roslyn", "")))
        or e.get("iterations") != iterations or e.get("workers") != 1
        or e.get("negative") is not negative or e.get("tieredPgo") != pgo
        or e.get("tieredCompilation") != "unset"
        or type(e.get("processors")) is not int or e["processors"] < 1
        or not all(re.fullmatch(r"[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}",
                               str(e.get(k, ""))) for k in ("roslynMvid", "corelibMvid"))):
        errors.append("runtime/compiler/architecture/arm evidence does not match the pinned experiment")
    totals = dict.fromkeys(LEGS, 0)
    successful = 0
    categories = set()
    for key, count in counts.items():
        leg, separator, verdict = key.partition(":")
        if not separator or leg not in totals or count <= 0:
            errors.append("unknown leg or invalid count")
            continue
        totals[leg] += count
        if verdict == "emits":
            successful += count
            categories.add("emits")
        elif re.fullmatch(r"diagnostics:CS\d+(?:,CS\d+)*", verdict):
            categories.add("diagnostics")
        elif re.fullmatch(r"throws:[A-Za-z_][A-Za-z0-9_]*", verdict):
            categories.add("exceptions")
        elif verdict.startswith("wrong-shape:"):
            categories.add("shape")
        else:
            errors.append("unknown verdict")
    attempts = sum(counts.values())
    failures = attempts - successful
    expected = 4 * iterations
    if any(n != iterations for n in totals.values()):
        errors.append("each of the four legs must account for every iteration")
    if summaries[0] != (expected, attempts, successful, failures) or attempts != expected:
        errors.append("summary totals do not reconcile with all four leg counts")
    if exit_code != (1 if failures else 0):
        errors.append("process exit code contradicts reported failures")
    if errors:
        return dict(report, classification="harness-mismatch", passed=False, reason="; ".join(errors))
    if negative:
        valid = all(counts.get(leg + ":emits", 0) == iterations for leg in LEGS if leg.endswith("flat"))
        valid = valid and all(sum(n for key, n in counts.items()
            if key.startswith(leg + ":diagnostics:")) == iterations
            for leg in LEGS if leg.endswith("nested"))
        return dict(report, classification="expected-diagnostics" if valid else "negative-control-failed",
                    passed=valid, reason="invalid nested source must produce diagnostics while both flat controls emit")
    category = ("emit-exception" if "exceptions" in categories else
                "shape-mismatch" if "shape" in categories else
                "compiler-diagnostics" if "diagnostics" in categories else "no-failure-reproduced")
    return dict(report, classification=category, passed=not failures,
                reason="bounded standalone observation; not attribution to CI #890 or proof of a fix")


def execute(command, env, log, timeout=TIMEOUT):
    """One fresh process; preserve output even on timeout and kill its process group once."""
    started = time.monotonic()
    with log.open("wb") as output:
        try:
            process = subprocess.Popen(command, stdout=output, stderr=subprocess.STDOUT,
                                       env=env, start_new_session=True)
        except OSError as error:
            output.write(f"NOT RUN: {type(error).__name__}: {error}\n".encode())
            return None, False, time.monotonic() - started
        expired = False
        try:
            code = process.wait(timeout=timeout)
        except subprocess.TimeoutExpired:
            expired = True
            try:
                os.killpg(process.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
            code = process.wait()
    return code, expired, time.monotonic() - started


def run(root=ROOT, env=None, system=None, machine=None, runner=execute):
    env = dict(os.environ if env is None else env)
    output = root / OUTPUT
    output.mkdir(parents=True, exist_ok=True)
    report = {"arms": [], "hashes": {}, "passed": False,
              "nativeEvidence": {"system": system or platform.system(), "machine": machine or platform.machine(),
                  "runnerOS": env.get("RUNNER_OS"), "runnerArch": env.get("RUNNER_ARCH"),
                  "assertion": env.get("MW_EMIT_NATIVE_RUNNER")},
              "limitation": "OS/architecture cannot universally detect emulation. CI's hosted ubuntu-24.04 job without a container supplies the native-runner premise."}
    try:
        refusal = native_refusal(env, report["nativeEvidence"]["system"], report["nativeEvidence"]["machine"])
        if ((env.get("GITHUB_ACTIONS") == "true" or "MW_EMIT_BUILD_OUTCOME" in env)
            and env.get("MW_EMIT_BUILD_OUTCOME") != "success"):
            refusal = refusal or "NOT RUN: this run did not build the probe successfully"
        for path in (PROJECT / "Program.cs", PROJECT / "EmitProbe.csproj", DLL):
            if (root / path).is_file():
                report["hashes"][str(path)] = hashlib.sha256((root / path).read_bytes()).hexdigest()
            else:
                refusal = refusal or f"NOT RUN: missing build/source input {path}"
        dotnet = shutil.which("dotnet", path=env.get("PATH"))
        if not dotnet:
            refusal = refusal or "NOT RUN: dotnet executable is missing"
        for name, iterations, negative, pgo in ARMS:
            log = output / f"{name}.log"
            if refusal:
                log.write_text(refusal + "\n")
                verdict = {"classification": "not-run", "passed": False, "reason": refusal, "exitCode": None}
            else:
                arm_env = dict(env)
                if pgo != "unset":
                    arm_env["DOTNET_TieredPGO"] = pgo
                command = [dotnet, str(root / DLL), "--iterations", str(iterations), "--workers", "1"]
                if negative:
                    command.append("--invalid-source")
                try:
                    code, expired, elapsed = runner(command, arm_env, log)
                    verdict = classify(log.read_text(errors="replace"), code, expired, iterations, negative, pgo)
                    verdict.update(elapsedSeconds=elapsed, command=command)
                except Exception as error:
                    verdict = {"classification": "harness-mismatch", "passed": False,
                               "reason": f"driver failure: {type(error).__name__}: {error}", "exitCode": None}
            report["arms"].append(dict(verdict, name=name, log=str(log)))
        report["passed"] = all(arm["passed"] for arm in report["arms"])
    except Exception as error:
        report["driverError"] = f"{type(error).__name__}: {error}"
    finally:
        (output / "verdict.json").write_text(json.dumps(report, indent=2) + "\n")
        print(json.dumps(report))
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(run())
