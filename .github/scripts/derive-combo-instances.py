#!/usr/bin/env python3
"""The instances combo verification must cover — DERIVED from the deployment overlays, never listed.

WHY THIS EXISTS (MeshWeaver#3848, problem 2)
--------------------------------------------
`combo-verify.yml` used to read its roster out of `vars.COMBO_VERIFY_INSTANCES`: a hand-maintained
JSON array of `{name, baseUrl}` in a repository variable. That is a PIN by another name — the shape
#3842 ruled out in as many words ("we must not pin anything") — and it fails in the direction that
cannot be seen: an installation added to the fleet is simply absent from the list, so the lane goes
green having verified everyone it was told about and nobody it was not.

It was already wrong when it was written. Measured 2026-09-15 against the live fleet, the value the
issue thread proposes provisioning names TWO installations (`memex`, `memex-cloud`); the overlays
declare THREE live ones — `build` (build.meshweaver.cloud, MeshWeaver.Plugins#1784) is an
installation like any other and self-updates like any other.

WHAT IT DERIVES FROM, AND WHY THAT IS THE SAME SOURCE AXIS 3 ALREADY TRUSTS
--------------------------------------------------------------------------
`lock-pinned-digests.py`'s AXIS 3 answers a neighbouring question — *which installations must be
asked what they are running, so their image set can be protected?* — off exactly two keys that every
overlay in the fleet carries:

    config:
      memex_portal:
        Hosting__Deployment: "memex-cloud"   ← the installation id
    ingress:
      host: "memex.meshweaver.cloud"         ← where to reach it

🚨 NO SECOND EXTRACTOR. This script IMPORTS that one (`extract_overlay_instances`, reached through
`scan_overlays_remote` / `scan_overlays_local`) and its roster reader, so the set combo verification
covers and the set the nightly lock protects can never disagree about what an installation IS. A
copy here would be a second rule that drifts silently in whichever direction nobody is reading.

`.github/acr-retention/instances.json` is the one file that may REMOVE an installation from the
roster, and only by declaring it `retired` or `not-installed` with a reason. 🚨 THE OVERLAYS ARE THE
DENOMINATOR AND THAT FILE ONLY EXPLAINS AN ABSENCE: an installation it does not mention is LIVE, so
forgetting an entry makes this stricter (one more instance demanding a credential), never looser.
That is the only direction a hand-maintained file is allowed to fail in.

WHAT IT REFUSES, AND WHY EVERY ONE OF THEM IS A RED RATHER THAN A SHORTER ANSWER
-------------------------------------------------------------------------------
An empty roster is the defect this whole lane exists to refuse: an empty matrix SKIPS the verify
job, and GitHub paints a skipped job the same colour as a passed one. So a derivation that comes
back with nothing — for ANY reason — exits 1 rather than emitting `[]`:

  * a repository whose overlays could not be read (NOBODY LOOKED is not a measured zero);
  * an installation declared by two overlays (which host answers for it is then ambiguous);
  * a roster entry naming an installation no overlay declares (a stale exemption hides the next one);
  * a live installation with no ingress host (it cannot be reached, so it cannot be verified);
  * two installations resolving to the SAME host (one of them would be verified under a name whose
    credentials belong to the other);
  * zero live installations at all.

Usage:
    python3 .github/scripts/derive-combo-instances.py --discover    # GH_TOKEN = installation token
    python3 .github/scripts/derive-combo-instances.py --repos Systemorph/Memex
    python3 .github/scripts/derive-combo-instances.py --root .      # one local checkout
    python3 .github/scripts/derive-combo-instances.py --self-test   # no network
"""

from __future__ import annotations

import argparse
import importlib.util
import json
import os
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent


def _load(name: str, filename: str):
    spec = importlib.util.spec_from_file_location(name, HERE / filename)
    if spec is None or spec.loader is None:      # pragma: no cover - packaging accident
        raise ImportError(f"cannot load {filename} from {HERE}")
    module = importlib.util.module_from_spec(spec)
    # 🚨 REGISTER BEFORE EXECUTING — `@dataclass` resolves its annotations through
    # `sys.modules[cls.__module__]`. Same reason, same comment, as lock-pinned-digests.py's copy.
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


lock = _load("mw_lock_pinned_digests", "lock-pinned-digests.py")

# The registry argument only narrows which image PINS the overlay scan extracts; this script reads
# the `Hosting__Deployment`/`ingress.host` pair, which no registry scopes. Named rather than passed
# as a bare literal so it is obvious it is not a policy choice here.
REGISTRY_FOR_SCAN = lock.REGISTRY_DEFAULT


def _no_probe(host: str):
    """AXIS 3 asks each installation what it is RUNNING; this question does not need to know.

    The seam exists in `build_instances` for its own self-test, and using it here keeps the
    preflight free of an HTTPS call to every portal in the fleet — the verify job contacts them
    immediately afterwards, with a credential, and says so by name when one does not answer."""
    return None, None, ""


def derive(scans, roster) -> tuple[list[dict[str, str]], list[tuple[str, str, str]], list[str]]:
    """(rows, excluded, blockers) — the roster, what was deliberately left out, and why not."""
    instances, blockers = lock.build_instances(scans, roster, probe=_no_probe)

    for scan in scans:
        if scan.unreadable:
            blockers.append(
                f"{scan.gh_repo}: its deployment overlays were NEVER READ ({scan.unreadable}). An "
                "installation declared there would be missing from this roster and nothing would "
                "say so — a repository that could not be read is not a repository with no "
                "installations in it.")

    rows: list[dict[str, str]] = []
    excluded: list[tuple[str, str, str]] = []
    hosts: dict[str, tuple[str, str]] = {}
    names: dict[str, str] = {}
    for instance in sorted(instances, key=lambda i: (i.id, i.gh_repo)):
        if instance.state != "live":
            excluded.append((instance.label, instance.state, instance.reason))
            continue
        if not instance.host:
            blockers.append(
                f"installation `{instance.label}` ({instance.source}) declares no `ingress.host`, "
                "so there is no base URL to verify it at. Combo verification asks the instance "
                "itself what it would roll to and what it runs; an installation that cannot be "
                "reached is left UNVERIFIED, which is the state this lane exists to end.")
            continue
        # 🚨 THE ROSTER NAME IS A CREDENTIAL KEY, AND UPSTREAM IDENTITY IS NOT (#3438, 2026-09-15).
        # `build_instances` qualifies an installation by `gh_repo:id` and DELIBERATELY permits two
        # deployments repositories to each declare a `memex` — both are correct there, because it
        # only ever asks each one what it is running. Here they are not: `COMBO_VERIFY_KEYS` and
        # `COMBO_VERIFY_TOKENS` are keyed by NAME, so two installations sharing one would be handed
        # the SAME `mwi_` key and admin token, and the second's verdict would land on the FIRST's
        # `Admin/UpdatePolicy`. That is the duplicate-host harm arriving through the other door, and
        # it became reachable the moment the fleet gained a second deployments repository — so it is
        # refused HERE rather than assumed away upstream. Qualifying the name instead would be worse:
        # it would silently ask for credentials under a key nobody has provisioned.
        if instance.id in names:
            blockers.append(
                f"two live installations are both named `{instance.id}` ({names[instance.id]} and "
                f"{instance.source}). Identity is qualified by the declaring repository upstream, "
                "but this lane's credential maps (`COMBO_VERIFY_KEYS`, `COMBO_VERIFY_TOKENS`) are "
                "keyed by NAME: both would be handed the same instance key and admin token, and "
                "one's verdict would land on the other's `Admin/UpdatePolicy`. Rename one "
                "installation, or key the maps by the qualified `repo:id` and say so here.")
            continue
        names[instance.id] = instance.source
        # 🚨 CANONICALISE BEFORE COMPARING. A HOSTNAME IS CASE-INSENSITIVE and the overlay extractor
        # accepts upper case, so `portal.example.com` and `PORTAL.EXAMPLE.COM` are ONE portal that a
        # case-sensitive test reads as two — which is not a cosmetic miss: it walks straight through
        # the refusal below, and the second entry would be verified with the first's credentials and
        # land its verdict on the wrong `Admin/UpdatePolicy`. A dedup key that fails to dedup is the
        # shape this whole file is about. The ORIGINAL spelling is what goes in the URL and the
        # report, because the overlay is the record.
        host_key = instance.host.rstrip(".").lower()
        if host_key in hosts:
            # 🚨 AND THE MESSAGE CARRIES BOTH SPELLINGS AS WRITTEN. This is the ONLY output on this
            # path — the run stops here — and whoever has to fix it is looking for a line in a
            # `values*.yaml`, which the canonical form may not match. So name each installation with
            # the host ITS OWN overlay wrote, and the key they collide on separately.
            first_id, first_host = hosts[host_key]
            blockers.append(
                f"installations `{first_id}` (https://{first_host}) and `{instance.label}` "
                f"(https://{instance.host}) both resolve to the same host `{host_key}` — a "
                "hostname is case-insensitive. One of them would be verified under a name whose "
                "instance key and admin token belong to the other, and its verdict would land on "
                "the wrong `Admin/UpdatePolicy`.")
            continue
        hosts[host_key] = (instance.label, instance.host)
        rows.append({"name": instance.id, "baseUrl": f"https://{instance.host}"})

    if not rows and not blockers:
        blockers.append(
            "the fleet's deployment overlays declare ZERO LIVE installations. That is never a "
            "reason to verify nothing: an empty roster yields an empty matrix, an empty matrix "
            "SKIPS the verify job, and GitHub paints a skipped job the same colour as a passed "
            "one. Either the overlay reader stopped finding `Hosting__Deployment` (run "
            "--self-test) or every installation is declared not-live in "
            f".github/acr-retention/{lock.ROSTER_PATH} — and the second would mean the fleet has "
            "no portals.")
    return rows, excluded, blockers


def extractor_control() -> list[str]:
    """🚨 THE INSTRUMENT BEFORE THE MEASUREMENT.

    Every "the overlays declare N installations" answer below is worth exactly the extractor that
    produced it, and a broken one answers ZERO in the same words as a fleet with no portals. Drive
    it over two known fixtures — the plain shape and the real `build` shape — on every run, so a
    matcher that stopped matching reds HERE, naming itself, instead of one arm later wearing "the
    fleet declares no installations"."""
    problems: list[str] = []
    for label, text, want in (
        ("the plain overlay shape", lock.FIXTURE_OVERLAY, ("memex-cloud", "memex.example.cloud")),
        ("the real `build` shape", lock.FIXTURE_OVERLAY_FOREIGN, ("build", "build.example.cloud")),
    ):
        found = lock.extract_overlay_instances(text)
        if found != [want]:
            problems.append(
                f"the overlay installation extractor no longer reads {label}: expected {[want]}, "
                f"got {found}. Every roster this script derives would be wrong or empty.")
    return problems


def read_scans(repos: list[str], root: str | None):
    if root:
        return [lock.scan_overlays_local(root, repos[0], REGISTRY_FOR_SCAN)]
    return [lock.scan_overlays_remote(repo, REGISTRY_FOR_SCAN) for repo in repos]


def report(rows, excluded, blockers, repos: list[str]) -> int:
    for identifier, state, reason in excluded:
        print(f"excluded  {identifier}: declared {state} — {reason[:160]}")
    if blockers:
        print("::error::the combo-verification roster could not be derived:")
        for blocker in blockers:
            print(f"  • {blocker}")
        print(f"  Scanned {len(repos)} repository(ies): {', '.join(repos)}")
        return 1
    for row in rows:
        print(f"derived   {row['name']}: {row['baseUrl']}")
    payload = json.dumps(rows, separators=(",", ":"))
    print(f"{len(rows)} live installation(s) derived from {len(repos)} repository(ies); "
          f"{len(excluded)} declared not-live.")
    output = os.environ.get("GITHUB_OUTPUT")
    if output:
        with open(output, "a", encoding="utf-8") as handle:
            handle.write(f"instances={payload}\n")
            handle.write(f"count={len(rows)}\n")
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as handle:
            handle.write("## Combo-verification roster (derived)\n\n")
            for row in rows:
                handle.write(f"- `{row['name']}` → {row['baseUrl']}\n")
            for identifier, state, _ in excluded:
                handle.write(f"- ~~`{identifier}`~~ — declared `{state}`\n")
    return 0


# ── Falsification ──────────────────────────────────────────────────────────────────────────────


def _scan(gh_repo: str, instances, unreadable: str | None = None):
    scan = lock.OverlayScan(gh_repo=gh_repo, files=len(instances))
    scan.instances = list(instances)
    scan.unreadable = unreadable
    return scan


TWO_LIVE = [_scan("Systemorph/Memex", [
    ("memex", "memex.systemorph.com", "deployments/aks/memex/values.memex.public.yaml"),
    ("memex-cloud", "memex.meshweaver.cloud",
     "deployments/aks/memex-cloud/values.memexcloud.public.yaml"),
])]


def self_test() -> int:
    failures = 0

    def check(condition: bool, message: str) -> None:
        nonlocal failures
        print(f"[{'PASS' if condition else 'FAIL'}] {message}")
        if not condition:
            failures += 1
            print(f"::error::--self-test: {message}")

    # The instrument, first — and PROVEN TO BE ABLE TO FAIL, not merely run.
    check(extractor_control() == [], "the overlay installation extractor reads both fixtures")
    saved = lock.extract_overlay_instances
    try:
        lock.extract_overlay_instances = lambda text: []
        check(len(extractor_control()) == 2,
              "a blinded extractor is caught by its own control, rather than answering ZERO")
    finally:
        lock.extract_overlay_instances = saved

    rows, excluded, blockers = derive(TWO_LIVE, {})
    check(blockers == [] and rows == [
        {"name": "memex", "baseUrl": "https://memex.systemorph.com"},
        {"name": "memex-cloud", "baseUrl": "https://memex.meshweaver.cloud"},
    ] and excluded == [], "two live overlays derive two instances, sorted, with no blocker")

    # A not-installed declaration REMOVES an instance — and is printed, never silently dropped.
    rows, excluded, blockers = derive(
        TWO_LIVE, {"memex-cloud": ("not-installed", "no DNS record", "")})
    check(blockers == [] and [r["name"] for r in rows] == ["memex"]
          and [e[0] for e in excluded] == ["memex-cloud"],
          "a not-installed declaration excludes that instance and names it")

    # …and a declaration cannot empty the roster silently.
    rows, _, blockers = derive(TWO_LIVE, {"memex": ("retired", "gone", ""),
                                          "memex-cloud": ("retired", "gone", "")})
    check(rows == [] and any("ZERO LIVE" in b for b in blockers),
          "an all-retired fleet is a RED, never an empty matrix")

    # A repository that could not be read is NOT a repository with no installations.
    rows, _, blockers = derive(
        TWO_LIVE + [_scan("Systemorph/Private", [], unreadable="HTTP 404")], {})
    check(any("NEVER READ" in b for b in blockers),
          "an unreadable repository blocks, rather than shortening the roster")

    # A live installation with no ingress host cannot be reached, so it cannot be verified.
    rows, _, blockers = derive([_scan("Systemorph/Memex", [
        ("memex", None, "deployments/aks/memex/values.memex.public.yaml")])], {})
    check(rows == [] and any("no `ingress.host`" in b for b in blockers),
          "a live installation with no ingress host is a RED")

    # Two overlays, one id: which host answers for it is ambiguous (this arm lives in
    # build_instances, and is driven here so a change there cannot silently stop covering us).
    rows, _, blockers = derive([_scan("Systemorph/Memex", [
        ("memex", "a.example.com", "deployments/aks/memex/values.a.yaml"),
        ("memex", "b.example.com", "deployments/aks/memex/values.b.yaml")])], {})
    check(any("declared by two overlays" in b for b in blockers),
          "one installation declared by two overlays is a RED")

    # 🚨 ONE NAME, TWO REPOSITORIES. Upstream this is LEGAL — identity there is `gh_repo:id`, and
    # two deployments repositories may each declare a `memex`. Here it is fatal, because the
    # credential maps are keyed by NAME. It must red rather than emit two rows.
    rows, _, blockers = derive([
        _scan("Systemorph/Memex", [
            ("memex", "memex.systemorph.com", "deployments/aks/memex/values.memex.public.yaml")]),
        _scan("Systemorph/PartnerRe.Memex", [
            ("memex", "partnerre.meshweaver.cloud", "deployments/aks/memex/values.memex.yaml")]),
    ], {})
    check(rows == [{"name": "memex", "baseUrl": "https://memex.systemorph.com"}]
          and any("both named `memex`" in b and "Systemorph/PartnerRe.Memex" in b
                  and "Systemorph/Memex" in b for b in blockers),
          "one NAME declared by two repositories is a RED naming both, though upstream allows it")

    # …and two repositories declaring DIFFERENT names is the ordinary multi-repo fleet: no blocker.
    rows, _, blockers = derive([
        _scan("Systemorph/Memex", [
            ("memex", "memex.systemorph.com", "deployments/aks/memex/values.memex.public.yaml")]),
        _scan("Systemorph/PartnerRe.Memex", [
            ("partnerre", "partnerre.meshweaver.cloud", "deployments/aks/pr/values.pr.yaml")]),
    ], {})
    check(blockers == [] and [r["name"] for r in rows] == ["memex", "partnerre"],
          "two repositories declaring different names derive both, with no blocker")

    # Two ids, one host: the second would be verified with the first's credentials.
    rows, _, blockers = derive([_scan("Systemorph/Memex", [
        ("memex", "same.example.com", "deployments/aks/memex/values.memex.yaml"),
        ("twin", "same.example.com", "deployments/aks/twin/values.twin.yaml")])], {})
    check(any("resolve to" in b for b in blockers),
          "two installations on one host is a RED")

    # …and the same host SPELLED DIFFERENTLY is the same host. A case-sensitive dedup key walks
    # straight through the arm above and hands one portal two names (caught in review on #4390).
    rows, _, blockers = derive([_scan("Systemorph/Memex", [
        ("memex", "Same.Example.COM.", "deployments/aks/memex/values.memex.yaml"),
        ("twin", "same.example.com", "deployments/aks/twin/values.twin.yaml")])], {})
    check(rows == [{"name": "memex", "baseUrl": "https://Same.Example.COM."}]
          and any("resolve to the same host" in b
                  and "https://Same.Example.COM." in b and "https://same.example.com" in b
                  for b in blockers),
          "one host in two spellings is a RED naming BOTH spellings as written, and the "
          "overlay's own spelling survives")

    # A roster entry naming nothing exempts nothing and hides the next one.
    rows, _, blockers = derive(TWO_LIVE, {"ghost": ("retired", "long gone", "")})
    check(any("`ghost`" in b for b in blockers),
          "a stale instances.json entry is a RED")

    # The emission itself: a passing derivation must write BOTH keys, because a matrix that is
    # never emitted skips the verify job just as an empty one does.
    with tempfile.TemporaryDirectory() as tmp:
        out = Path(tmp) / "out"
        out.touch()
        os.environ["GITHUB_OUTPUT"] = str(out)
        try:
            code = report(*derive(TWO_LIVE, {}), ["Systemorph/Memex"])
        finally:
            del os.environ["GITHUB_OUTPUT"]
        text = out.read_text(encoding="utf-8")
    check(code == 0 and "instances=[{" in text and "count=2" in text,
          "a passing derivation emits both the matrix and its denominator")

    if failures:
        print(f"::error::--self-test: {failures} arm(s) did not behave as documented.")
        return 1
    print("derive-combo-instances --self-test: every arm fires and stays silent.")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description="Derive the combo-verification instance roster.")
    parser.add_argument("--repos", help="comma-separated owner/name list to scan")
    parser.add_argument("--discover", action="store_true",
                        help="enumerate the fleet from the App installation this token belongs to")
    parser.add_argument("--root", help="read one repository from a local checkout instead")
    parser.add_argument("--self-test", action="store_true",
                        help="prove every refusal fires; no network")
    args = parser.parse_args()

    if args.self_test:
        return self_test()

    if args.root:
        repos = [args.repos or "local"]
    elif bool(args.repos) == bool(args.discover):
        parser.error("give exactly one of --repos, --discover or --root")
    else:
        repos = (lock.consistency.discover_repos() if args.discover
                 else [r.strip() for r in args.repos.split(",") if r.strip()])

    # 🚨 The instrument before the measurement, on EVERY run and not only under --self-test.
    problems = extractor_control()
    if problems:
        print("::error::the overlay installation extractor failed its own control:")
        for problem in problems:
            print(f"  • {problem}")
        return 1

    print(f"deriving the combo-verification roster from {len(repos)} repository(ies): "
          + ", ".join(repos))
    roster, roster_problems = lock.read_instance_roster(args.root or ".")
    rows, excluded, blockers = derive(read_scans(repos, args.root), roster)
    return report(rows, excluded, roster_problems + blockers, repos)


if __name__ == "__main__":
    sys.exit(main())
