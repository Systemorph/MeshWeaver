#!/usr/bin/env python3
"""Drive `registry-publish-admission.sh` and `mirror-image-to-registry.sh` offline (MeshWeaver#7530).

The CD half of the fleet registry's maintenance lease: a write to a lease-governed registry is
admitted on the control instance first, or it does not happen. This test runs both scripts against
a FAKE `curl` (which plays control's `/api/registry-maintenance/admission` and VERIFIES the HMAC it
is sent, the way control does) and a FAKE `docker` (which records whether the copy ran), so every
answer control can give is exercised without a network:

  * a publish while a cleanup HOLDS the lease (409, forever) is refused — the mirror never copies,
    and the job goes red naming the holder; a 409 that clears is waited out and then admitted;
  * control unreachable, a refused signature, a missing route, a store failure and a missing
    secret are each RED and copy nothing — there is no skip;
  * an admitted mirror copies, then reports completion — also when the copy itself fails;
  * a write that touches no lease-governed registry asks control nothing (negative control);
  * a completion control does not record is a warning, never a red (the admission then holds
    maintenance back until its bound — the safe direction).

    python3 .github/scripts/registry-publish-admission-selftest.py
"""
from __future__ import annotations

import json
import os
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
ADMISSION = HERE / "registry-publish-admission.sh"
MIRROR = HERE / "mirror-image-to-registry.sh"
SECRET = "selftest-secret-not-a-real-one"
CONTROL = json.loads((HERE.parent / "control-instance.json").read_text())["url"].rstrip("/")
ROUTE = "/api/registry-maintenance/admission"

FAKE_CURL = r'''#!/usr/bin/env python3
import hashlib, hmac, json, os, sys
args = sys.argv[1:]
out = fmt = url = data = None
headers = {}
i = 0
while i < len(args):
    a = args[i]
    if a == "-o": out = args[i + 1]; i += 2; continue
    if a == "-w": fmt = args[i + 1]; i += 2; continue
    if a == "-H":
        k, v = args[i + 1].split(":", 1); headers[k.strip().lower()] = v.strip(); i += 2; continue
    if a == "--data": data = args[i + 1]; i += 2; continue
    if a in ("-X", "--connect-timeout", "--max-time"): i += 2; continue
    if a.startswith("-"): i += 1; continue
    url = a; i += 1
plan_path = os.environ["FAKE_CURL_PLAN"]
plan = json.load(open(plan_path))
with open(os.environ["FAKE_CURL_LOG"], "a") as f:
    f.write(json.dumps({"url": url, "body": json.loads(data or "{}")}) + "\n")
code = plan.pop(0) if len(plan) > 1 else plan[0]
json.dump(plan, open(plan_path, "w"))
if code == "unreachable":
    sys.stdout.write("000"); sys.exit(7)
expected = "sha256=" + hmac.new(os.environ["FAKE_CONTROL_SECRET"].encode(), (data or "").encode(), hashlib.sha256).hexdigest()
if headers.get("x-hub-signature-256") != expected:
    code = 401
body = {200: {"admitted": True}, 409: {"admitted": False, "held": True,
        "refusal": "the fleet registry is under maintenance — 'Ops/Actions/fleet-registry-cleanup-x' holds the registry-maintenance lease"},
        401: {"error": "signature"}, 404: {}, 503: {"error": "store"}}.get(int(code), {})
open(out, "w").write(json.dumps(body))
sys.stdout.write(str(code))
'''

FAKE_DOCKER = r'''#!/usr/bin/env python3
import os, sys
args = sys.argv[1:]
with open(os.environ["FAKE_DOCKER_LOG"], "a") as f:
    f.write(" ".join(args) + "\n")
if args[:3] == ["buildx", "imagetools", "inspect"]:
    if "--raw" in args: sys.exit(0)
    print('"sha256:' + "a" * 64 + '"'); sys.exit(0)
if args[:3] == ["buildx", "imagetools", "create"]:
    sys.exit(int(os.environ.get("FAKE_DOCKER_CREATE_EXIT", "0")))
sys.exit(0)
'''

failures: list[str] = []


def check(ok: bool, what: str) -> None:
    if not ok:
        failures.append(what)


def run(root: Path, argv: list[str], plan: list, env_extra: dict | None = None, secret: str | None = SECRET,
        tool: Path = ADMISSION) -> tuple[int, str, list[dict], list[str]]:
    bindir = root / "bin"
    bindir.mkdir(exist_ok=True)
    for name, body in (("curl", FAKE_CURL), ("docker", FAKE_DOCKER)):
        (bindir / name).write_text(body)
        (bindir / name).chmod(0o755)
    (root / "plan.json").write_text(json.dumps(plan))
    for log in ("curl.log", "docker.log"):
        (root / log).write_text("")
    env = {k: v for k, v in os.environ.items() if k not in ("CONTROL_WEBHOOK_SECRET", "GITHUB_JOB")}
    env.update({
        "PATH": f"{bindir}{os.pathsep}{env['PATH']}",
        "FAKE_CURL_PLAN": str(root / "plan.json"), "FAKE_CURL_LOG": str(root / "curl.log"),
        "FAKE_DOCKER_LOG": str(root / "docker.log"), "FAKE_CONTROL_SECRET": SECRET,
        "GITHUB_REPOSITORY": "Systemorph/MeshWeaver", "GITHUB_RUN_ID": "4242", "GITHUB_RUN_ATTEMPT": "1",
        "GITHUB_JOB": "selftest", "RUNNER_TEMP": str(root),
        "MW_REGISTRY_ADMISSION_POLL_SECONDS": "0", "MW_REGISTRY_ADMISSION_WAIT_SECONDS": "2",
    })
    if secret is not None:
        env["CONTROL_WEBHOOK_SECRET"] = secret
    env.update(env_extra or {})
    p = subprocess.run(["bash", str(tool), *argv], capture_output=True, text=True, env=env, timeout=120)
    calls = [json.loads(l) for l in (root / "curl.log").read_text().splitlines() if l.strip()]
    docker = [l for l in (root / "docker.log").read_text().splitlines() if l.strip()]
    return p.returncode, p.stdout + p.stderr, calls, docker


def scenario(name: str):
    def wrap(fn):
        with tempfile.TemporaryDirectory() as d:
            try:
                fn(Path(d))
            except Exception as e:  # noqa: BLE001 — a crashed scenario is a failure, named
                failures.append(f"{name}: crashed: {e!r}")
        return fn
    return wrap


CR = "cr.meshweaver.cloud/memex-portal-ai:staging-abc1234-1"
ACR = "meshweaver.azurecr.io/memex-portal-ai:staging-abc1234-1"


@scenario("governs")
def _(root: Path) -> None:
    code, out, _, _ = run(root, ["governs", CR, ACR, "ghcr.io/systemorph/memex-portal-ai:latest"], [200])
    check(code == 0 and out.strip() == "cr.meshweaver.cloud",
          f"governs: exactly the derived-protected-set host is lease-governed — got {code} {out!r}")
    code, out, _, _ = run(root, ["governs", ACR], [200])
    check(code == 0 and out.strip() == "", f"governs: an ACR-only write is not lease-governed — got {code} {out!r}")
    code, out, _, _ = run(root, ["governs", CR], [200], env_extra={"MW_ACR_RETENTION_DIR": str(root / "missing")})
    check(code == 1, f"governs: an unreadable record is RED, never 'not governed' — got {code} {out!r}")


@scenario("admit: free lease")
def _(root: Path) -> None:
    code, out, calls, _ = run(root, ["admit", "selftest/mirror-x", "cr.meshweaver.cloud"], [200])
    check(code == 0 and "ADMITTED" in out, f"admit: a free lease admits — got {code} {out[-300:]!r}")
    check(len(calls) == 1 and calls[0]["url"] == CONTROL + ROUTE,
          f"admit: one signed POST to control's declared url + route — got {calls}")
    body = calls[0]["body"] if calls else {}
    check(body.get("event") == "registry-publish-admission" and body.get("op") == "admit"
          and body.get("publisher") == "Systemorph/MeshWeaver" and body.get("run") == "4242-1"
          and body.get("step") == "selftest/mirror-x" and body.get("registry") == "cr.meshweaver.cloud" and body.get("at"),
          f"admit: the body carries the documented fields — got {body}")


@scenario("admit: a held lease is waited out, then refused by name")
def _(root: Path) -> None:
    code, out, calls, _ = run(root, ["admit", "selftest/mirror-x", "cr.meshweaver.cloud"], [409, 409, 200])
    check(code == 0 and len(calls) == 3, f"admit: a 409 that clears is waited out and then admitted — got {code}, {len(calls)} call(s)")
    code, out, calls, _ = run(root, ["admit", "selftest/mirror-x", "cr.meshweaver.cloud"], [409])
    check(code == 1 and "still holds the maintenance lease" in out and "fleet-registry-cleanup-x" in out,
          f"🚨 admit: a lease still held at the wait bound is RED, naming the holder — got {code} {out[-400:]!r}")


@scenario("admit: every other answer is RED")
def _(root: Path) -> None:
    for plan, needle in (([401], "REFUSED the signature"), ([404], "no /api/registry-maintenance/admission"),
                         ([503], "did not admit"), (["unreachable"], "could not be reached")):
        code, out, _, _ = run(root, ["admit", "s", "cr.meshweaver.cloud"], plan)
        check(code == 1 and needle in out, f"admit: {plan[0]} is RED by name ({needle!r}) — got {code} {out[-300:]!r}")
    code, out, _, _ = run(root, ["admit", "s", "cr.meshweaver.cloud"], [200], secret="wrong-secret")
    check(code == 1 and "REFUSED the signature" in out, f"admit: a secret control does not hold is RED — got {code}")
    code, out, calls, _ = run(root, ["admit", "s", "cr.meshweaver.cloud"], [200], secret=None)
    check(code == 1 and "CONTROL_WEBHOOK_SECRET" in out and not calls,
          f"🚨 admit: no secret is RED and asks control nothing — never a skip — got {code}, {len(calls)} call(s)")


@scenario("complete")
def _(root: Path) -> None:
    code, out, calls, _ = run(root, ["complete", "s", "cr.meshweaver.cloud"], [200])
    check(code == 0 and calls and calls[0]["body"].get("op") == "complete", f"complete: reported — got {code} {calls}")
    code, out, _, _ = run(root, ["complete", "s", "cr.meshweaver.cloud"], [503])
    check(code == 0 and "::warning::" in out, f"complete: an unrecorded completion is a WARNING, never a red — got {code}")


@scenario("mirror: a publish during a held lease copies nothing")
def _(root: Path) -> None:
    code, out, calls, docker = run(root, [ACR, CR], [409], tool=MIRROR)
    check(code == 1 and not any("imagetools create" in d for d in docker),
          f"🚨 mirror: while a cleanup holds the lease the copy never runs and the step is RED — got {code}, docker={docker}")
    check(not any(c["body"].get("op") == "complete" for c in calls), "mirror: a refused admission completes nothing")


@scenario("mirror: an admitted copy runs, then completes")
def _(root: Path) -> None:
    code, out, calls, docker = run(root, [ACR, CR], [200], tool=MIRROR)
    ops = [c["body"].get("op") for c in calls]
    check(code == 0 and any("imagetools create" in d for d in docker) and ops == ["admit", "complete"],
          f"mirror: admitted ⇒ the copy runs and the admission is completed after — got {code}, ops={ops}, docker={docker}")
    code, out, calls, docker = run(root, [ACR, CR], [200], tool=MIRROR, env_extra={"FAKE_DOCKER_CREATE_EXIT": "1"})
    ops = [c["body"].get("op") for c in calls]
    check(code != 0 and ops == ["admit", "complete"], f"mirror: a failed copy still completes its admission — got {code}, ops={ops}")


@scenario("mirror: no lease-governed target asks control nothing")
def _(root: Path) -> None:
    code, out, calls, docker = run(root, [ACR, "meshweaver.azurecr.io/memex-portal-ai:3-latest"], [409], tool=MIRROR, secret=None)
    check(code == 0 and not calls and any("imagetools create" in d for d in docker),
          f"NEGATIVE CONTROL: an ACR-only mirror needs no admission and no secret — got {code}, {len(calls)} call(s) {out[-300:]!r}")


# ── THE STATIC HALF: what makes the admission ENFORCED rather than remembered ──────────────────────
# 🚨 The script admits only the writes that go THROUGH it. So the publishing lanes are read here, the
# same way `lock-pinned-digests.py --check-registry-retention` derives their push targets (its own
# helpers, imported, so the two readings cannot drift): (1) no `--tag` push may name a lease-governed
# registry — every write there goes through mirror-image-to-registry.sh, which is admitted; and
# (2) every step that calls the mirror carries the secret it is admitted with, so a governed mirror
# can never meet a missing credential in production (it would be red, but on main, after the merge).
import importlib.util  # noqa: E402
import re  # noqa: E402

_spec = importlib.util.spec_from_file_location("lock_pinned_digests_for_admission", HERE / "lock-pinned-digests.py")
lpd = importlib.util.module_from_spec(_spec)
sys.modules[_spec.name] = lpd
_spec.loader.exec_module(lpd)


def governed_hosts() -> set[str]:
    regs = json.loads((HERE.parent / "acr-retention" / "instances.json").read_text(encoding="utf-8"))["registries"]
    return {h for h, e in regs.items() if isinstance(e, dict) and (e.get("retention") or {}).get("rule") == "derived-protected-set"}


def direct_governed_writes(text: str, governed: set[str]) -> list[str]:
    """Every `--tag` push in `text` whose host is lease-governed — a write that bypasses the admission."""
    env = lpd._workflow_env(text)
    hits = []
    for number, line in lpd._continued_lines(text):
        if line.strip().startswith("#"):
            continue
        for match in lpd._TAG_ARGUMENT.finditer(line):
            ref = next(g for g in match.groups() if g)
            for k, v in env.items():
                ref = ref.replace("${{ env.%s }}" % k, v).replace("${%s}" % k, v).replace("$%s" % k, v)
            if ref.split("/", 1)[0].lower() in governed:
                hits.append(f"line {number}: --tag {ref}")
    return hits


def mirror_steps_without_secret(text: str) -> list[str]:
    """Every step whose run calls mirror-image-to-registry.sh and whose env lacks CONTROL_WEBHOOK_SECRET."""
    lines = text.split("\n")
    starts = [i for i, l in enumerate(lines) if re.match(r"^\s+- ", l) and re.match(r"^(\s+)- (name|uses|id|run|if)\b", l)]
    missing = []
    for i, l in enumerate(lines):
        if "mirror-image-to-registry.sh" not in l or l.strip().startswith("#"):
            continue
        start = max((s for s in starts if s <= i), default=0)
        if not any(re.match(r"^\s+CONTROL_WEBHOOK_SECRET:\s*\$\{\{\s*secrets\.CONTROL_WEBHOOK_SECRET\s*\}\}", x)
                   for x in lines[start:i]):
            missing.append(f"line {i + 1} (step at line {start + 1}: {lines[start].strip()[:80]})")
    return missing


def mirror_jobs_without_record(text: str) -> list[str]:
    """Every job that calls mirror-image-to-registry.sh from a SPARSE checkout lacking the retention record.

    The admission reads .github/acr-retention/instances.json (and .github/control-instance.json) from the repo the
    step runs in; a job that sparse-checks-out only .github/scripts has neither file, so its governed mirror is red
    on main with "the registry record could not be read" (the first main-cd after #8403).
    """
    lines = text.split("\n")
    heads = [(i, m.group(1)) for i, l in enumerate(lines) if (m := re.match(r"^  ([A-Za-z0-9_-]+):\s*$", l))]
    bad = []
    for n, (start, name) in enumerate(heads):
        end = heads[n + 1][0] if n + 1 < len(heads) else len(lines)
        body = lines[start:end]
        if not any("mirror-image-to-registry.sh" in l and not l.strip().startswith("#") for l in body):
            continue
        sparse = [i for i, l in enumerate(body) if re.match(r"^\s+sparse-checkout:", l)]
        for i in sparse:
            block = [body[i]]
            for x in body[i + 1:]:
                if re.match(r"^\s{12,}\S", x) and not re.match(r"^\s+[A-Za-z-]+:", x):
                    block.append(x)
                else:
                    break
            if not any(".github/acr-retention" in x for x in block):
                bad.append(f"job {name} (line {start + i + 1})")
    return bad


GOVERNED = governed_hosts()
check(GOVERNED == {"cr.meshweaver.cloud"}, f"static: the record's lease-governed registries are {sorted(GOVERNED)}, not the one host this lane knows")
lanes = {name: (HERE.parent / "workflows" / name).read_text(encoding="utf-8") for name in lpd.PUBLISHING_WORKFLOWS}
mirror_sites = 0
for name, text in lanes.items():
    for hit in direct_governed_writes(text, GOVERNED):
        failures.append(f"🚨 static: {name} {hit} writes a lease-governed registry OUTSIDE mirror-image-to-registry.sh, so no admission guards it")
    for gap in mirror_steps_without_secret(text):
        failures.append(f"🚨 static: {name} {gap} calls the mirror without `CONTROL_WEBHOOK_SECRET: ${{{{ secrets.CONTROL_WEBHOOK_SECRET }}}}` in its env — a governed mirror there would be RED on main")
    for gap in mirror_jobs_without_record(text):
        failures.append(f"🚨 static: {name} {gap} calls the mirror from a sparse checkout without .github/acr-retention — the admission cannot read the registry record and the mirror is RED on main")
    mirror_sites += sum(1 for l in text.split("\n") if "mirror-image-to-registry.sh" in l and not l.strip().startswith("#"))
check(mirror_sites >= 10, f"static: only {mirror_sites} mirror call site(s) found in the publishing lanes — the reader stopped seeing them, so the secret arm checked nothing")
# Negative controls: each static arm fires on the shape it exists for.
check(direct_governed_writes('env:\n  REGISTRY: cr.meshweaver.cloud\njobs:\n  x:\n    steps:\n      - run: docker buildx imagetools create --tag "${{ env.REGISTRY }}/a:b" src\n', GOVERNED),
      "NEGATIVE CONTROL: a direct --tag push to the governed registry was NOT flagged")
check(mirror_steps_without_secret("jobs:\n  x:\n    steps:\n      - name: m\n        run: .github/scripts/mirror-image-to-registry.sh a b\n"),
      "NEGATIVE CONTROL: a mirror step without the secret was NOT flagged")
check(mirror_jobs_without_record("jobs:\n  x:\n    steps:\n      - uses: actions/checkout@v7\n        with:\n          sparse-checkout: .github/scripts\n      - run: .github/scripts/mirror-image-to-registry.sh a b\n"),
      "NEGATIVE CONTROL: a mirror job with a scripts-only sparse checkout was NOT flagged")
check(not mirror_jobs_without_record("jobs:\n  x:\n    steps:\n      - uses: actions/checkout@v7\n        with:\n          sparse-checkout: |\n            .github/scripts\n            .github/acr-retention\n      - run: .github/scripts/mirror-image-to-registry.sh a b\n"),
      "CONTROL: a mirror job whose sparse checkout carries the record was flagged")

if failures:
    print("registry-publish-admission self-test FAILED:")
    for f in failures:
        print("  ✗ " + f)
    sys.exit(1)
print("registry-publish-admission self-test: 8 scenarios held (governs reads the record; a free lease admits with the "
      "documented signed body; a held lease is waited out, and red naming the holder at the bound; 401/404/503/"
      "unreachable/no-secret are each red and the last asks nothing; a completion is reported and an unrecorded one "
      "is a warning; a mirror during a held lease copies nothing; an admitted mirror copies then completes, also on a "
      "failed copy; an ACR-only mirror asks control nothing) — offline, fake curl verifies the HMAC")
