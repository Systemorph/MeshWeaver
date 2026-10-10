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
    python3 .github/scripts/derive-combo-instances.py --discover --slot 2 \
        --expect-count 6 --expect-digest <digest>     # ONE verify job's row → $GITHUB_ENV
    python3 .github/scripts/derive-combo-instances.py --root .      # one local checkout
    python3 .github/scripts/derive-combo-instances.py --self-test   # no network
"""

from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
import re
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
        # 🚨 THE ROSTER NAME IS THE LANE'S KEY, AND UPSTREAM IDENTITY IS NOT (#3438, 2026-09-15).
        # `build_instances` qualifies an installation by `gh_repo:id` and DELIBERATELY permits two
        # deployments repositories to each declare a `memex` — both are correct there, because it
        # only ever asks each one what it is running. Here they are not: each verify job, its
        # work-root artifact and its step summary are keyed by NAME (and, until #3848 moved the lane
        # onto the run's own OIDC identity, so were the credential maps), so two installations
        # sharing one produce verdicts nobody can tell apart. That became reachable the moment the
        # fleet gained a second deployments repository — so it is refused HERE rather than assumed
        # away upstream.
        if instance.id in names:
            blockers.append(
                f"two live installations are both named `{instance.id}` ({names[instance.id]} and "
                f"{instance.source}). Identity is qualified by the declaring repository upstream, "
                "but this lane keys each verify job, its work-root artifact and its step summary by "
                "NAME, so the two verdicts would be indistinguishable in the run that produced "
                "them. TWO ways out exist "
                "TODAY, and they do not carry the same cost. (1) Rename one installation — its "
                "`Hosting__Deployment` is its inventory identity, so this moves whichever estate "
                "owns the one that changes; this is the answer taken the first time it happened "
                "(#3848: a client's `memex` became `globex-test`, see "
                "Doc/Architecture/ComboGateWiring). (2) Key the roster by the qualified `repo:id` and "
                "say so here — this removes the collision and then demands a build-principal grant "
                "on every installation named, including any in an estate this fleet does not run. "
                "🚨 A THIRD ANSWER IS THE RIGHT ONE IN PRINCIPLE AND IS NOT IMPLEMENTED: scoping "
                "the DENOMINATOR, so that an installation which can never receive this candidate "
                "is not in it — one whose overlay pins a registry declared `out-of-estate`, since "
                "the image this lane verifies is then not the image that installation runs. "
                "Nothing here expresses that, and NEITHER existing flag can stand in for it: "
                "`.github/acr-retention/instances.json`'s instance states are LIVENESS only "
                "(`live` / `not-installed` / `retired`), so declaring a LIVE installation "
                "`not-installed` would record a falsehood in order to obtain an exclusion; and the "
                "`out_of_scope` flag the retention table derives belongs to the LOCK lane's "
                "registry scoping, which this derivation never reads — a live out-of-scope "
                "installation is still returned here. Building it means an explicit combo-scope "
                "declaration this script CONSUMES, with its own self-test arm. 🚨 And when it is "
                "built it is still the LOOSER DIRECTION, the only one of the three that can be "
                "silently wrong: it SHRINKS the denominator, so an installation excluded by "
                "mistake is one this lane then reports nothing about while reading green — the "
                "exact failure the derived roster replaced a hand-maintained list to prevent.")
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


def derive_sources(scans) -> tuple[str, list[str]]:
    """(space-separated --source pairs, blockers) from deployment-record registry mounts."""
    # Match ComboAssemblyOptions.SourceRepositories, whose comparer is OrdinalIgnoreCase. The
    # verifier would otherwise collapse differently-cased names after this check and could silently
    # keep whichever URL happened to be passed last.
    sources: dict[str, tuple[str, str, str]] = {}
    blockers: list[str] = []
    for scan in scans:
        for path, error in scan.source_errors:
            blockers.append(
                f"{scan.gh_repo}/{path}: registry sources could not be read ({error}); refusing "
                "to run combo verification with a partial source map.")
        for name, url, path in scan.sources:
            key = name.casefold()
            previous = sources.get(key)
            if previous is not None and previous[1] != url:
                blockers.append(
                    f"case-insensitive registry source `{name}` conflicts with "
                    f"`{previous[0]}`: {previous[1]} ({previous[2]}) and {url} "
                    f"({scan.gh_repo}/{path}).")
            else:
                provenance = f"{scan.gh_repo}/{path}"
                if previous is None or name < previous[0]:
                    # Keep one deterministic spelling while preserving the actual name expected by
                    # installations; the assembler resolves it with OrdinalIgnoreCase too.
                    sources[key] = (name, url, provenance)
    if not sources and not blockers:
        blockers.append(
            "the deployment records declare ZERO registry-source plugin repositories. The source "
            "map is derived from DeploymentContent.PluginRepos; an empty map cannot verify the "
            "installed modules and is not treated as a successful empty set.")
    ordered = sorted(sources.values(), key=lambda source: source[0].casefold())
    return " ".join(f"{name}={url}" for name, url, _ in ordered), blockers


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


# 2**256 has 78 decimal digits; the digest is zero-padded to that, so its length is a constant.
DIGEST_DIGITS = 78


def roster_digest(rows: list[dict[str, str]], sources: str) -> str:
    """One opaque value that says "the same roster and the same source map" across a job boundary.

    🚨 WHY A DIGEST AND NOT THE ROSTER (#3848). The roster names installations a client estate keeps
    private, the private roster registers those names as log masks, and the runner refuses to pass
    ANY job output that contains a masked value. So the preflight hands the verify matrix opaque
    slot numbers, each verify job derives the roster again and takes its own row — and this value is
    how that second derivation is held to the first: an overlay that changed between the two jobs
    is a RED naming the drift, never a verdict landed on a different installation than the one the
    preflight counted.

    🚨 SALTED WITH THE PRIVATE ROSTER, so the public value confirms nothing to a reader who can only
    guess at a private name: without the secret document the digest cannot be recomputed. Absent
    (a fork, a local run) there is nothing private in the roster to protect.

    🚨 ONE DIGIT, ONE SEPARATOR, ALTERNATING — for the very reason this function exists. A job
    output is dropped when it CONTAINS a masked value, and no plain alphabet is safe from that: hex
    spells words (`cafe`, `beef`), and decimal digits collide with an all-digit installation id or
    a numeric secret such as a GitHub App id (a 4-digit value sits inside a 78-digit number about
    once in 130 runs). So the digest is written `d_d_d_…`: every two adjacent characters are one
    digit and one underscore, which means a value can only occur inside it if that value ITSELF
    alternates single digits with underscores. No identifier and no credential has that shape, and
    `digest_can_contain` states the rule so the self-test can hold it."""
    salt = os.environ.get(lock.PRIVATE_ROSTER_ENV, "").strip()
    body = json.dumps({"instances": rows, "sources": sources}, separators=(",", ":"), sort_keys=True)
    digest = hashlib.sha256(f"{salt}\n{body}".encode("utf-8")).hexdigest()
    return "_".join(f"{int(digest, 16):0{DIGEST_DIGITS}d}")


def is_digest(value: str) -> bool:
    """Whether `value` has the digest's exact shape: 78 single digits joined by underscores."""
    return re.fullmatch(r"[0-9](?:_[0-9]){%d}" % (DIGEST_DIGITS - 1), value or "") is not None


def digest_can_contain(value: str) -> bool:
    """Whether `value` COULD be a substring of some digest — i.e. could get the output dropped.

    True only for a value of two or more characters that alternates single digits with
    underscores (`4_2`, `_7_`), or a single digit or underscore. Everything else — every word,
    every number of two or more digits, every host, every token — can never occur."""
    if len(value) < 2:
        return value.isdigit() or value == "_"
    return all((a.isdigit() and b == "_") or (a == "_" and b.isdigit())
               for a, b in zip(value, value[1:]))


def _is_private(name: str, host: str, declared_in: str) -> bool:
    """Whether the private roster names this installation — by id, by host or by repository.

    🚨 EXACT MATCH ON THE COMPLETE SET FIRST (`lock.private_roster_entries`, no length floor), so a
    one-character private id is as private as a long one. Then the mask rule: anything the runner
    would mask a part of (a private value of four or more characters occurring inside the name,
    host or repository) is private too. Both directions fail CLOSED — more rows private, never
    fewer. Case-insensitive, because a host is."""
    name, host, declared_in = name.lower(), host.rstrip(".").lower(), (declared_in or "").lower()
    entries = lock.private_roster_entries()
    if name in {v.lower() for v in entries["ids"]}:
        return True
    if host and host in {v.rstrip(".").lower() for v in entries["hosts"]}:
        return True
    if declared_in and declared_in in {v.lower() for v in entries["repos"]}:
        return True
    haystack = [name, host, declared_in]
    for value in lock.private_roster_values():
        needles = {value.lower()}
        if "/" in value:
            needles.add(value.split("/", 1)[1].lower())
        if any(needle in text for needle in needles for text in haystack):
            return True
    return False


def is_private_row(row: dict[str, str], declared_in: str) -> bool:
    """Whether a roster row belongs to a client estate — i.e. the private roster names it.

    🚨 THIS REPOSITORY IS PUBLIC, and a verify job's log, step summary and artifacts are too. For a
    private row the preflight prints no name or host, and the lander prints the verdict and counts
    only and uploads nothing: an installation's module list is the client's inventory."""
    return _is_private(row["name"], row["baseUrl"].split("//", 1)[-1], declared_in)


def row_owners(instances) -> dict[tuple[str, str], str]:
    """(name, baseUrl) → the repository whose overlay declares that LIVE installation.

    🚨 KEYED BY NAME **AND** URL, NEVER BY NAME ALONE. Identity upstream is `gh_repo:id`, so two
    repositories may declare one id; `derive` refuses that only while BOTH are live. With one of
    them retired, a name-keyed lookup would be answered by whichever was iterated last — and a
    live private row could be handed the PUBLIC repository's name and be classified public."""
    return {(instance.id, f"https://{instance.host}"): instance.gh_repo
            for instance in instances if instance.state == "live" and instance.host}


def mask_private_instances(instances) -> int:
    """Register every DERIVED identifier of a private installation as a log mask.

    The private roster's own strings are masked by `lock.mask_private_roster`. That is not enough:
    a row can be private through its declaring repository alone, and then its installation id and
    host appear nowhere in the secret — they are derived from that repository's overlays. Called
    before anything is printed about any installation, live or not. Returns how many it masked."""
    if os.environ.get("GITHUB_ACTIONS") != "true":
        return 0
    masked = 0
    for instance in instances:
        if not _is_private(instance.id, instance.host or "", instance.gh_repo):
            continue
        values = [instance.host or "", f"https://{instance.host}" if instance.host else ""]
        if len(instance.id) >= 4:       # a shorter mask would shred the log; private rows print
            values.append(instance.id)  # no name at all (see report / resolve_slot)
        for value in values:
            if value:
                print(f"::add-mask::{value}")
                masked += 1
    return masked


def resolve_slot(rows: list[dict[str, str]], sources: str, slot: str,
                 expect_count: str, expect_digest: str,
                 owners: dict[tuple[str, str], str] | None = None) -> int:
    """ONE verify job's row of the roster, re-derived inside that job and held to the preflight's.

    Writes INSTANCE_NAME / BASE_URL / SOURCES to `$GITHUB_ENV` — within one job, where a masked
    value is masked rather than dropped. Every refusal exits 1: a verify job that cannot say WHICH
    installation it is verifying must not verify one."""
    def refuse(message: str) -> int:
        print(f"::error::slot {slot!r}: {message}")
        return 1

    if not (expect_count or "").isdigit() or int(expect_count) < 1:
        return refuse(
            f"the preflight's instance count arrived as {expect_count!r}, not a positive integer. "
            "The job output that carries the denominator was lost between the jobs; refusing to "
            "verify against a roster nobody counted.")
    if not is_digest(expect_digest):
        return refuse(
            "the preflight's roster digest did not arrive (the runner drops a job output that "
            "contains a masked value, #3848). Without it this job cannot show that the roster it "
            "derived is the one the preflight counted.")
    if not (slot or "").isdigit():
        return refuse("the matrix slot is not a non-negative integer.")
    if len(rows) != int(expect_count):
        return refuse(
            f"this job derived {len(rows)} live installation(s) and the preflight counted "
            f"{expect_count}. The fleet's deployment overlays changed between the two jobs; re-run "
            "the workflow so both read the same fleet.")
    if roster_digest(rows, sources) != expect_digest:
        return refuse(
            "this job derived the same NUMBER of installations as the preflight but not the same "
            "roster or source map (digest mismatch). The fleet's deployment overlays or records "
            "changed between the two jobs; re-run the workflow so both read the same fleet.")
    index = int(slot)
    if index >= len(rows):
        return refuse(f"the roster has {len(rows)} row(s); there is no slot {index}.")
    row = rows[index]
    private = is_private_row(row, (owners or {}).get((row["name"], row["baseUrl"]), ""))
    if private:
        # No name, no host: the masks would cover them, but a private row's line says nothing a
        # reader of a public log needs beyond its slot.
        print(f"slot {index} of {len(rows)}: a PRIVATE roster row ({len(sources.split())} registry "
              "source(s)); roster digest matches the preflight's. Its job prints the verdict and "
              "counts only and uploads no artifact.")
    else:
        print(f"slot {index} of {len(rows)}: {row['name']}: {row['baseUrl']} "
              f"({len(sources.split())} registry source(s)); roster digest matches the preflight's.")
    env_file = os.environ.get("GITHUB_ENV")
    if env_file:
        with open(env_file, "a", encoding="utf-8") as handle:
            handle.write(f"INSTANCE_NAME={row['name']}\n")
            handle.write(f"BASE_URL={row['baseUrl']}\n")
            handle.write(f"SOURCES={sources}\n")
            handle.write(f"INSTANCE_PRIVATE={'true' if private else 'false'}\n")
    return 0


def report(rows, excluded, blockers, repos: list[str], sources: str,
           private_names: frozenset[str] = frozenset()) -> int:
    """Print the derivation. `private_names` are roster rows the private roster names: they are
    COUNTED here and never named — not in the log and not in the step summary, neither of which a
    public repository keeps private."""
    for identifier, state, reason in excluded:
        print(f"excluded  {identifier}: declared {state} — {reason[:160]}")
    if blockers:
        print("::error::the combo-verification roster and sources could not be derived:")
        for blocker in blockers:
            print(f"  • {blocker}")
        print(f"  Scanned {len(repos)} repository(ies): {', '.join(repos)}")
        return 1
    for index, row in enumerate(rows):
        if row["name"] in private_names:
            print(f"derived   slot {index}: a private roster row (name and host withheld)")
        else:
            print(f"derived   {row['name']}: {row['baseUrl']}")
    for source in sources.split():
        print(f"source    {source}")
    payload = json.dumps(rows, separators=(",", ":"))
    print(f"{len(rows)} live installation(s) and {len(sources.split())} registry source(s) derived "
          f"from {len(repos)} repository(ies); "
          f"{len(excluded)} declared not-live.")
    output = os.environ.get("GITHUB_OUTPUT")
    if output:
        # 🚨 STEP outputs, and they must never become JOB outputs (#3848). `instances` and `sources`
        # carry names the private roster masks, and the runner DROPS any job output containing a
        # masked value ("Skip output 'instances' since it may contain secret") — silently, as a
        # warning in a green job. Only `count` and `digest` are safe to hand to another job; each
        # verify job re-derives its own row through --slot below.
        with open(output, "a", encoding="utf-8") as handle:
            handle.write(f"instances={payload}\n")
            handle.write(f"count={len(rows)}\n")
            handle.write(f"sources={sources}\n")
            handle.write(f"digest={roster_digest(rows, sources)}\n")
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as handle:
            handle.write("## Combo-verification roster (derived)\n\n")
            for index, row in enumerate(rows):
                if row["name"] in private_names:
                    handle.write(f"- slot {index}: a private roster row (name and host withheld)\n")
                else:
                    handle.write(f"- `{row['name']}` → {row['baseUrl']}\n")
            handle.write("\n## Registry sources (derived from DeploymentContent.PluginRepos)\n\n")
            for source in sources.split():
                handle.write(f"- `{source}`\n")
            for identifier, state, _ in excluded:
                handle.write(f"- ~~`{identifier}`~~ — declared `{state}`\n")
    return 0


# ── Falsification ──────────────────────────────────────────────────────────────────────────────


def _scan(gh_repo: str, instances, unreadable: str | None = None,
          sources=None, source_errors=None):
    scan = lock.OverlayScan(gh_repo=gh_repo, files=len(instances))
    scan.instances = list(instances)
    scan.unreadable = unreadable
    scan.sources = list(sources or [])
    scan.source_errors = list(source_errors or [])
    return scan


TWO_LIVE = [_scan("Systemorph/Memex", [
    ("memex", "memex.systemorph.com", "deployments/aks/memex/values.memex.public.yaml"),
    ("memex-cloud", "memex.meshweaver.cloud",
     "deployments/aks/memex-cloud/values.memexcloud.public.yaml"),
])]
SOURCE_SCANS = [
    _scan("Systemorph/Memex", [], sources=[
        ("Plugins", "https://github.com/Systemorph/MeshWeaver.Plugins",
         "mesh/Deployments/memex-cloud.json"),
        ("FundReporting", "https://github.com/Systemorph/MeshWeaver.FundReporting",
         "mesh/Deployments/memex.json"),
    ]),
    _scan("Systemorph/Umbrella.Memex", [], sources=[
        ("Plugins", "https://github.com/Systemorph/MeshWeaver.Plugins",
         "mesh/Deployments/globex-test.json"),
        ("Umbrella", "https://github.com/Systemorph/Umbrella.Memex",
         "mesh/Deployments/globex-test.json"),
    ]),
]


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

    sources, source_blockers = derive_sources(SOURCE_SCANS)
    check(source_blockers == [] and sources == (
        "FundReporting=https://github.com/Systemorph/MeshWeaver.FundReporting "
        "Plugins=https://github.com/Systemorph/MeshWeaver.Plugins "
        "Umbrella=https://github.com/Systemorph/Umbrella.Memex"),
        "registry sources union across deployment repositories, deduplicate identical mappings, "
        "and sort deterministically")
    _, source_blockers = derive_sources([_scan("Systemorph/Memex", [])])
    check(any("ZERO registry-source" in blocker for blocker in source_blockers),
          "an empty registry-source map is a RED, not a successful empty set")
    same_source, source_blockers = derive_sources([
        _scan("Systemorph/Memex", [], sources=[
            ("Plugins", "https://github.com/Systemorph/MeshWeaver.Plugins", "Deployments/memex.json")]),
        _scan("Systemorph/Umbrella.Memex", [], sources=[
            ("plugins", "https://github.com/Systemorph/MeshWeaver.Plugins", "Deployments/globex-test.json")]),
    ])
    check(not source_blockers and same_source ==
          "Plugins=https://github.com/Systemorph/MeshWeaver.Plugins",
          "source names differing only by case, with one URL, deduplicate like the verifier")
    _, source_blockers = derive_sources([
        _scan("Systemorph/Memex", [], sources=[
            ("Plugins", "https://github.com/Systemorph/MeshWeaver.Plugins", "Deployments/memex.json")]),
        _scan("Systemorph/Umbrella.Memex", [], sources=[
            ("plugins", "https://github.com/Systemorph/Other.Plugins", "Deployments/memex.json")]),
    ])
    check(any("case-insensitive registry source" in blocker and "Plugins" in blocker
              for blocker in source_blockers),
          "source names differing only by case and pointing at different repositories are a RED")
    _, source_blockers = derive_sources([_scan(
        "Systemorph/Memex", [], source_errors=[("mesh/Deployments/memex.json", "bad pluginRepos")])])
    check(any("partial source map" in blocker and "bad pluginRepos" in blocker
              for blocker in source_blockers),
          "an unreadable registry source record blocks a partial source map")

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
        _scan("Systemorph/Umbrella.Memex", [
            ("memex", "globex.example.com", "deployments/aks/memex/values.memex.yaml")]),
    ], {})
    check(rows == [{"name": "memex", "baseUrl": "https://memex.systemorph.com"}]
          and any("both named `memex`" in b and "Systemorph/Umbrella.Memex" in b
                  and "Systemorph/Memex" in b for b in blockers),
          "one NAME declared by two repositories is a RED naming both, though upstream allows it")

    # 🚨 The refusal names the two ways out that EXIST and the third that does NOT, and every one of
    # those claims is load-bearing. A refusal naming only the two sends the reader to move another
    # estate's inventory identity or to ask for credentials nobody holds, with no hint that the right
    # answer is a missing mechanism. A refusal offering the third as if it were available is WORSE
    # than either: the only exclusion lever `instances.json` has is a LIVENESS state, so an operator
    # following that advice about a LIVE installation records a falsehood to obtain an exclusion —
    # and the `out_of_scope` flag that would be the honest input is the lock lane's and is never read
    # here. That is exactly what a review caught in the first version of this message, so the arm
    # asserts each claim rather than the shape of the paragraph: a message is the only part of this
    # refusal anybody acts on, and nothing else in the suite reads it.
    check(any("NOT IMPLEMENTED" in b and "LIVENESS" in b and "LOOSER DIRECTION" in b
              and "out_of_scope" in b for b in blockers),
          "the duplicate-name refusal names denominator-scoping as NOT IMPLEMENTED, says why neither "
          "existing flag expresses it, and still flags it as the looser direction")

    # …and two repositories declaring DIFFERENT names is the ordinary multi-repo fleet: no blocker.
    # Spelled as the fleet resolved #3848: the client's installation keeps its `memex` NAMESPACE and
    # overlay directory and changes only its `Hosting__Deployment`, which is all this lane keys on.
    rows, _, blockers = derive([
        _scan("Systemorph/Memex", [
            ("memex", "memex.systemorph.com", "deployments/aks/memex/values.memex.public.yaml")]),
        _scan("Systemorph/Umbrella.Memex", [
            ("globex-test", "globex.example.com",
             "deployments/aks/memex/values.memex.yaml")]),
    ], {})
    check(blockers == [] and sorted(r["name"] for r in rows) == ["globex-test", "memex"],
          "two repositories declaring different names derive both, with no blocker — even from "
          "overlays in same-named directories")

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
            derived_sources, source_blockers = derive_sources(SOURCE_SCANS)
            rows, excluded, blockers = derive(TWO_LIVE, {})
            code = report(rows, excluded, blockers + source_blockers,
                          ["Systemorph/Memex"], derived_sources)
        finally:
            del os.environ["GITHUB_OUTPUT"]
        text = out.read_text(encoding="utf-8")
    check(code == 0 and "instances=[{" in text and "count=2" in text
          and "sources=FundReporting=https://github.com/Systemorph/MeshWeaver.FundReporting" in text
          and f"digest={roster_digest(rows, derived_sources)}" in text,
          "a passing derivation emits the roster, denominator, registry sources and roster digest")

    # ── --slot: one verify job's row, re-derived and held to the preflight's (#3848) ─────────────
    # The roster cannot cross a job boundary (a job output containing a masked name is DROPPED), so
    # each verify job derives it again. These arms are what keeps "derived again" from meaning
    # "derived something else".
    digest = roster_digest(rows, derived_sources)

    def slot_run(slot, count, expected, slot_rows=rows, slot_sources=derived_sources,
                 owners=None):
        with tempfile.TemporaryDirectory() as tmp:
            env_file = Path(tmp) / "env"
            env_file.touch()
            os.environ["GITHUB_ENV"] = str(env_file)
            try:
                code = resolve_slot(slot_rows, slot_sources, slot, count, expected, owners)
            finally:
                del os.environ["GITHUB_ENV"]
            return code, env_file.read_text(encoding="utf-8")

    code, env_text = slot_run("1", "2", digest)
    check(code == 0 and f"INSTANCE_NAME={rows[1]['name']}\n" in env_text
          and f"BASE_URL={rows[1]['baseUrl']}\n" in env_text
          and f"SOURCES={derived_sources}\n" in env_text,
          "a slot resolves to ITS row of the re-derived roster, with the source map")
    code, env_text = slot_run("0", "2", digest)
    check(code == 0 and f"INSTANCE_NAME={rows[0]['name']}\n" in env_text
          and "INSTANCE_PRIVATE=false\n" in env_text,
          "slot 0 resolves to the first row — slots are positions in the sorted roster — and a "
          "row the private roster does not name is NOT private")

    # ── A PRIVATE row: its module list must not leave the run (this repository is public) ───────
    private_rows = [{"name": "globex-test", "baseUrl": "https://portal.globex.example"},
                    {"name": "memex", "baseUrl": "https://memex.systemorph.com"}]
    owners = {("globex-test", "https://portal.globex.example"): "Systemorph/Umbrella.Memex",
              ("memex", "https://memex.systemorph.com"): "Systemorph/Memex"}
    for label, document in (
        ("its instance id", '{"instances":[{"id":"globex-test"}]}'),
        ("its host", '{"instances":[{"host":"PORTAL.globex.example"}]}'),
        ("its declaring repository", '{"repositories":{"Systemorph/Umbrella.Memex":{}}}'),
    ):
        os.environ[lock.PRIVATE_ROSTER_ENV] = document
        try:
            private_digest = roster_digest(private_rows, derived_sources)
            code0, env0 = slot_run("0", "2", private_digest, slot_rows=private_rows, owners=owners)
            code1, env1 = slot_run("1", "2", private_digest, slot_rows=private_rows, owners=owners)
        finally:
            del os.environ[lock.PRIVATE_ROSTER_ENV]
        check(code0 == 0 and "INSTANCE_PRIVATE=true\n" in env0
              and code1 == 0 and "INSTANCE_PRIVATE=false\n" in env1,
              f"a row the private roster names by {label} is PRIVATE, and its public neighbour is not")

    # 🚨 A SHORT private id is still private. The log-mask set drops values under four characters
    # (a one-character mask shreds the log); the classification set must not.
    short_rows = [{"name": "memex", "baseUrl": "https://memex.systemorph.com"},
                  {"name": "x9", "baseUrl": "https://portal.short.example"}]
    os.environ[lock.PRIVATE_ROSTER_ENV] = '{"instances":[{"id":"x9"}]}'
    try:
        short_private = [is_private_row(row, "Systemorph/Memex") for row in short_rows]
        mask_set = lock.private_roster_values()
    finally:
        del os.environ[lock.PRIVATE_ROSTER_ENV]
    check(short_private == [False, True] and mask_set == [],
          "a two-character private id is PRIVATE although it is too short to be a log mask, and "
          "it does not make its neighbour private")

    # 🚨 ONE id, TWO repositories, one of them retired. A lookup keyed by the bare id would hand
    # the LIVE private row whichever repository was iterated last — here the public one.
    collision = [
        _scan("Systemorph/Umbrella.Memex", [
            ("memex", "portal.globex.example", "deployments/aks/memex/values.memex.yaml")]),
        _scan("Systemorph/Memex", [
            ("memex", "memex.systemorph.com", "deployments/aks/memex/values.memex.public.yaml")]),
    ]
    collision_roster = {"Systemorph/Memex:memex": ("retired", "moved", "Systemorph/Memex")}
    collided, collision_blockers = lock.build_instances(collision, collision_roster, probe=_no_probe)
    collision_rows, _, derive_blockers = derive(collision, collision_roster)
    collision_owners = row_owners(collided)
    os.environ[lock.PRIVATE_ROSTER_ENV] = '{"repositories":{"Systemorph/Umbrella.Memex":{}}}'
    try:
        verdicts = [is_private_row(row, collision_owners.get((row["name"], row["baseUrl"]), ""))
                    for row in collision_rows]
        naive = {instance.id: instance.gh_repo for instance in collided}
        naive_verdicts = [is_private_row(row, naive.get(row["name"], "")) for row in collision_rows]
    finally:
        del os.environ[lock.PRIVATE_ROSTER_ENV]
    check(not collision_blockers and not derive_blockers
          and collision_rows == [{"name": "memex", "baseUrl": "https://portal.globex.example"}]
          and verdicts == [True],
          "a live private row sharing its id with a retired public installation is PRIVATE")
    check(naive_verdicts == [False],
          "…and the control: the name-keyed lookup this replaced classifies that same row PUBLIC")

    # 🚨 A row private ONLY through its repository: its name and host are in no secret, so nothing
    # masks them — the preflight's report must not print them, in the log or the summary.
    import contextlib
    import io
    with tempfile.TemporaryDirectory() as tmp:
        summary_file = Path(tmp) / "summary"
        os.environ["GITHUB_STEP_SUMMARY"] = str(summary_file)
        os.environ.pop("GITHUB_OUTPUT", None)
        printed = io.StringIO()
        try:
            with contextlib.redirect_stdout(printed):
                code = report(private_rows, [], [], ["a"], derived_sources,
                              frozenset({"globex-test"}))
        finally:
            del os.environ["GITHUB_STEP_SUMMARY"]
        shown = printed.getvalue() + summary_file.read_text(encoding="utf-8")
    check(code == 0 and "globex" not in shown and "slot 0: a private roster row" in shown
          and "memex.systemorph.com" in shown,
          "the report names a public row and only COUNTS a private one, in log and summary")
    os.environ["GITHUB_ACTIONS"] = "true"
    os.environ[lock.PRIVATE_ROSTER_ENV] = '{"repositories":{"Systemorph/Umbrella.Memex":{}}}'
    printed = io.StringIO()
    try:
        with contextlib.redirect_stdout(printed):
            masked_count = mask_private_instances(collided)
    finally:
        del os.environ["GITHUB_ACTIONS"]
        del os.environ[lock.PRIVATE_ROSTER_ENV]
    check(masked_count == 3 and "::add-mask::portal.globex.example" in printed.getvalue()
          and "::add-mask::memex\n" in printed.getvalue()
          and "memex.systemorph.com" not in printed.getvalue(),
          "the derived host and id of a repository-private installation are masked; a public "
          "installation's are not")

    for label, args in (
        ("a slot beyond the roster", ("2", "2", digest)),
        ("a non-numeric slot", ("memex", "2", digest)),
        ("a count the preflight never delivered", ("0", "", digest)),
        ("a zero count", ("0", "0", digest)),
        ("a digest the preflight never delivered (the dropped-output shape)", ("0", "2", "")),
        ("a roster that changed size between the jobs", ("0", "3", digest)),
        ("a roster of the same size and different content", ("0", "2", "_".join("0" * DIGEST_DIGITS))),
    ):
        code, env_text = slot_run(*args)
        check(code == 1 and env_text == "",
              f"{label} is a RED that names no installation to verify")
    moved = [dict(rows[0]), {"name": rows[1]["name"], "baseUrl": "https://moved.example.com"}]
    code, env_text = slot_run("1", "2", digest, slot_rows=moved)
    check(code == 1 and env_text == "",
          "an installation whose host moved between the jobs is a RED, not a verdict landed elsewhere")
    code, env_text = slot_run("0", "2", digest,
                              slot_sources=derived_sources + " Extra=https://x.example")
    check(code == 1 and env_text == "",
          "a source map that changed between the jobs is a RED")
    os.environ[lock.PRIVATE_ROSTER_ENV] = '{"instances":[{"id":"globex-test"}]}'
    try:
        salted = roster_digest(rows, derived_sources)
    finally:
        del os.environ[lock.PRIVATE_ROSTER_ENV]
    check(salted != digest and is_digest(salted) and is_digest(digest),
          "the digest is salted with the private roster — the public value cannot confirm a "
          "guessed name — and has the digest's exact shape")

    # 🚨 THE DIGEST CANNOT CONTAIN A MASKED VALUE, whatever the value is. A plain decimal digest
    # could: an all-digit installation id or a numeric secret (a GitHub App id) is a substring of
    # a 78-digit number often enough to matter, and a job output containing a masked value is
    # dropped. Hold the property two ways: the rule, and a brute-force search for a counterexample.
    hostile = ["1234", "0000", "4242", "12", "987654", "cafe", "beef", "memex", "globex-test",
               "portal.globex.example", "Systemorph/Umbrella.Memex", "1.2.3.4", "a_b", "12_34"]
    check(not any(digest_can_contain(value) for value in hostile),
          "no identifier, host, repository, word or multi-digit number can occur inside a digest")
    check(digest_can_contain("4_2") and digest_can_contain("_7_") and not digest_can_contain("42"),
          "the containment rule is not vacuous: only a digit/underscore alternation can occur")
    found = 0
    for variant in range(400):
        candidate = roster_digest(rows, f"{derived_sources} Probe{variant}=https://x.example")
        if not is_digest(candidate):
            found += 1
        found += sum(1 for width in (2, 3, 4)
                     for number in ("12", "123", "1234", "00", "000", "0000", "99", "4242")
                     if len(number) == width and number in candidate)
    check(found == 0,
          "400 different digests: none contains any 2-, 3- or 4-digit number (a plain decimal "
          "digest contains `1234` within a few hundred)")
    plain = sum(1 for variant in range(400)
                if "1234" in roster_digest(rows, f"{derived_sources} P{variant}=https://x.example")
                .replace("_", ""))
    check(plain > 0,
          "…and the control: the SAME 400 digests with the separators removed do contain `1234`, "
          "so the search above can find what it is looking for")

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
    parser.add_argument("--slot", help="resolve ONE row of the roster (a verify job's matrix slot) "
                                       "into $GITHUB_ENV instead of reporting the whole roster")
    parser.add_argument("--expect-count", default="",
                        help="with --slot: the instance count the preflight derived")
    parser.add_argument("--expect-digest", default="",
                        help="with --slot: the roster digest the preflight derived")
    args = parser.parse_args()

    if args.self_test:
        return self_test()

    # 🚨 MASK FIRST, before ANY output. GitHub applies `::add-mask::` only to lines emitted AFTER
    # it, and the very next print lists every discovered repository — a client estate's among them
    # once the private roster is merged. Registered after that print, the mask covered nothing the
    # print had already put in the public log. Not under --self-test (its fixtures are not secrets).
    lock.mask_private_roster()      # public log: the private roster's identifiers stay private

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
    scans = read_scans(repos, args.root)
    # 🚨 PRIVACY BEFORE ANY LINE ABOUT AN INSTALLATION. Which rows are private is decided here, and
    # every derived identifier of a private installation is masked here — before `derive` builds a
    # blocker naming one, and before `report` or `resolve_slot` prints anything.
    instances = lock.build_instances(scans, roster, probe=_no_probe)[0]
    mask_private_instances(instances)
    owners = row_owners(instances)
    rows, excluded, blockers = derive(scans, roster)
    sources, source_blockers = derive_sources(scans)
    all_blockers = roster_problems + blockers + source_blockers
    if args.slot is not None and not all_blockers:
        return resolve_slot(rows, sources, args.slot, args.expect_count, args.expect_digest, owners)
    private_names = frozenset(
        row["name"] for row in rows
        if is_private_row(row, owners.get((row["name"], row["baseUrl"]), "")))
    # With --slot AND blockers this falls through on purpose: report() prints them and exits 1.
    return report(rows, excluded, all_blockers, repos, sources, private_names)


if __name__ == "__main__":
    sys.exit(main())
