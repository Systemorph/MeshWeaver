#!/usr/bin/env python3
"""publication-platform.py — the gate takes its platform FROM the upstream publication (MeshWeaver#7337).

    python3 publication-platform.py choose --declaration platform-compatibility.json \\
        --upstreams "plugins" --run-set 3.1.11069 --run-tester sha256:… --run-portal sha256:… \\
        --pinned false [--ceiling 3.1.11069] (--registry-url https://… | --targets acct/share[/base])
    python3 publication-platform.py verify --source plugins --mode publication \\
        --composed-set 3.1.11076 --composed-tester sha256:… --composed-portal sha256:… \\
        --identity c003e001 (--index index.json | --record platform-set.json | --no-record)
    python3 publication-platform.py key --set 3.1.11069 --declaration platform-compatibility.json
    python3 publication-platform.py --self-test

THE RACE THIS CLOSES
--------------------
A satellite gate (`node-repo-gate.yml`) had TWO inputs resolved INDEPENDENTLY: the platform set its
host is composed on (the caller's preflight, or the lane's own resolution) and the seed — the
CURRENT sealed publication of each upstream for the framework identity, whose bundles carry a
platform FLOOR (`producerPlatformVersion`) equal to whatever set the upstream last baked on. When the
upstream resealed on a NEWER set between the two reads, every bundle was DECLINED ("its platform
floor X … is NEWER than the running platform Y") and the gate failed `bake consumption` on bytes no
diff touched (MeshWeaver.Reinsurance PR #264, run 39025974143).

THE DESIGN (policy: one set identity for host and seed)
------------------------------------------------------
The publication RECORDS the platform it was sealed on — `platform-set.json`, written by
`publish-bake-bundles.sh`, served by the registry index as `platform` — and the gate reads it FIRST
and composes its host on exactly that set. One set identity for both inputs, so there is nothing to
race. What the caller passes changes what the record is checked against, never whether:

  * NOTHING PINNED (both digests empty): the host is the publication's set. With `--ceiling` (a pull
    request follows the set its `main` has passed) a publication sealed on a NEWER set than the
    ceiling is REFUSED BY NAME — the gate never silently runs a pull request on a platform `main`
    has not passed, and never silently drops back to a set the seed's floors exceed.
  * PINNED (the caller's digests): a pin that IS the publication's set passes. A pin OLDER than the
    publication's set is RED naming both — that is exactly the DECLINED case, now named before
    anything is composed. A pin NEWER than the publication's set is, for now, composed as pinned with
    a ::warning naming both (TRANSITIONAL — every upstream-seed caller pins its preflight's set
    today, and that pin is always at or above the publication's floors, so a red here would turn the
    fleet red on bytes that adopt fine); it becomes red once those callers have dropped their pins.
  * NO RECORD (a publication sealed by a lane that predates this): the gate keeps its own platform
    and says so — the race stays open for that publication until its upstream republishes.

The framework identity that ADDRESSES the publication is computed BEFORE the host exists: it is the
compatibility key `c<major:D3>e<epoch:D3>` (MeshWeaver.Compiler.PlatformCompatibility.KeyOf), with
the major read off the set name and the epoch off core's declaration
(`src/MeshWeaver.Compiler/platform-compatibility.json`: the current epoch, and each declared break's
`previousEpochCeiling` for the sets before it). The gate asserts afterwards that the composed host
resolves the same key, so a disagreement is RED by name, never a seed read for another identity.

`verify` is the second half: the seed step re-reads each publication's index and refuses one that was
resealed on another platform after the host was composed (a minutes-wide window, now RED by name —
a re-run reads the publication that then applies — instead of a wall of DECLINED bundles).

Everything is printed; every refusal names both sets and what to do. Exit 0 = decided, 1 = refused.
Doc/Architecture/PublicationNamesItsPlatform.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
from dataclasses import dataclass, field
from typing import Callable

RECORD_FILE = "platform-set.json"          # publish-bake-bundles.sh PLATFORM_RECORD_FILE
SENTINEL = "_complete"
POINTER = "_current"
DIGEST = re.compile(r"^sha256:[0-9a-f]{64}$")
LEGACY_SET = re.compile(r"^(\d+)\.(\d+)\.(\d+)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?[.-]ci\.(\d+)$")
SEMVER_SET = re.compile(r"^(\d+)\.(\d+)\.(\d+)$")
SEMVER_ERA_START = (3, 1)                  # resolve-platform.py SEMVER_ERA_START
TRANSIENT_DELAYS = (15, 30, 60, 90)        # registry_get in node-repo-gate.yml, same policy
TRANSITIONAL = "MeshWeaver#7337"


class Refusal(Exception):
    """A RED verdict, worded for the log: what was found, and what to do."""


# ───────────────────────────────── sets and the compatibility key ─────────────────────────────────

def parse_set(name: str | None) -> tuple[int, int] | None:
    """(major, run) for a set name in either notation, or None when it is no orderable set.
    `3.0.0-ci.9985` / `3.0.0-rc9.ci.7534` → (3, 9985/7534); `3.1.11069` → (3, 11069)."""
    text = (name or "").strip()
    m = LEGACY_SET.fullmatch(text)
    if m:
        return int(m.group(1)), int(m.group(4))
    m = SEMVER_SET.fullmatch(text)
    if m and (int(m.group(1)), int(m.group(2))) >= SEMVER_ERA_START and int(m.group(3)) > 0:
        return int(m.group(1)), int(m.group(3))
    return None


def compare_sets(a: str | None, b: str | None) -> int | None:
    """-1/0/1 by the core-CD run number both notations share, or None when either cannot be ordered."""
    pa, pb = parse_set(a), parse_set(b)
    if pa is None or pb is None:
        return None
    return (pa[1] > pb[1]) - (pa[1] < pb[1])


def epoch_for(set_name: str, declaration: dict) -> int:
    """The compatibility epoch a set was built in, from core's DECLARATION (never a guess): the
    oldest declared break whose `previousEpochCeiling` the set does not exceed names the epoch
    before it; a set past every ceiling is in the current epoch."""
    if not isinstance(declaration, dict) or not isinstance(declaration.get("epoch"), int):
        raise Refusal("the platform-compatibility declaration carries no integer `epoch` — the "
                      "framework identity that addresses the publication cannot be computed")
    parsed = parse_set(set_name)
    if parsed is None:
        raise Refusal(f"'{set_name}' is not a platform set name, so its compatibility epoch — and the "
                      "framework identity that addresses the publication — cannot be computed")
    breaks = declaration.get("breaks") or []
    if not isinstance(breaks, list):
        raise Refusal("the declaration's `breaks` is not a list")
    for brk in sorted(breaks, key=lambda b: b.get("epoch", 0) if isinstance(b, dict) else 0):
        if not isinstance(brk, dict) or not isinstance(brk.get("epoch"), int):
            raise Refusal("a declared break names no integer `epoch`")
        ceiling = parse_set(brk.get("previousEpochCeiling"))
        if ceiling is None:
            raise Refusal(f"the break to epoch {brk['epoch']} names no orderable previousEpochCeiling "
                          f"({brk.get('previousEpochCeiling')!r})")
        if parsed[1] <= ceiling[1]:
            return brk["epoch"] - 1
    return declaration["epoch"]


def key_for(set_name: str, declaration: dict) -> str:
    """PlatformCompatibility.KeyOf(major, epoch) = c<major:D3>e<epoch:D3>."""
    major = parse_set(set_name)
    if major is None:
        raise Refusal(f"'{set_name}' is not a platform set name")
    return f"c{major[0]:03d}e{epoch_for(set_name, declaration):03d}"


# ───────────────────────────────── records ─────────────────────────────────

@dataclass(frozen=True)
class Platform:
    set: str
    tester: str
    portal: str

    def same_build(self, other: "Platform") -> bool:
        return self.tester == other.tester and self.portal == other.portal

    def text(self) -> str:
        return f"set {self.set or '<unnamed>'} (tester {self.tester or '<tag>'}, portal {self.portal or '<tag>'})"


@dataclass(frozen=True)
class Reading:
    """What one upstream's publication says about its platform: `record`, `no-record` (sealed by a
    lane predating the record), or `absent` (nothing sealed — the seed step names that itself)."""
    kind: str
    record: Platform | None = None
    where: str = ""


def record_of(raw: object, source: str, identity: str, where: str) -> Platform:
    if not isinstance(raw, dict):
        raise Refusal(f"the '{source}' publication at {where} carries a platform record that is not an "
                      f"object ({str(raw)[:80]!r}) — refusing to compose a host on it")
    tester, portal, name = (str(raw.get(k) or "").strip() for k in ("testerDigest", "portalDigest", "set"))
    if not (DIGEST.fullmatch(tester) and DIGEST.fullmatch(portal) and name):
        raise Refusal(f"the '{source}' publication at {where} carries a malformed platform record "
                      f"({json.dumps(raw)[:200]}) — set, testerDigest and portalDigest (sha256) are all required")
    recorded_identity = str(raw.get("identity") or "").strip()
    if recorded_identity and identity and recorded_identity != identity:
        raise Refusal(f"the '{source}' publication addressed by framework identity {identity} records that "
                      f"it was baked for {recorded_identity} — the address and the bytes disagree")
    return Platform(name, tester, portal)


# ───────────────────────────────── the decision ─────────────────────────────────

@dataclass
class Decision:
    platform: Platform
    mode: str                     # publication | pin | legacy
    notes: list[str] = field(default_factory=list)
    warnings: list[str] = field(default_factory=list)
    # The upstreams whose publication carried a platform record WHEN THE HOST WAS CHOSEN. `verify`
    # refuses a record that vanished only for these: an upstream that never had one (a lane
    # predating the record, beside a recorded one) is the documented fallback, not a reseal.
    recorded: list[str] = field(default_factory=list)


def decide(run: Platform, pinned: bool, ceiling: str | None, readings: dict[str, Reading],
           frozen: str | None = None) -> Decision:
    """The platform this gate composes on. Pure; every branch is in the self-test.

    `frozen` — the caller's MW_PLATFORM_REF freeze, when the run's platform was resolved through it.
    A freeze is an INSTRUCTION (an incident), so it binds like a pin: the host stays the frozen set,
    and a publication the frozen host cannot adopt is refused by name — never silently replaced by
    the publication's set, and never measured against the ceiling, which the freeze overrides."""
    if frozen:
        pinned, ceiling = True, None
    who = (f"the MW_PLATFORM_REF freeze '{frozen}' FROZE the host at" if frozen else "the caller PINNED")
    with_record = {src: r.record for src, r in readings.items() if r.kind == "record" and r.record}
    without = [src for src, r in readings.items() if r.kind == "no-record"]
    absent = [src for src, r in readings.items() if r.kind == "absent"]
    warnings = [f"the '{src}' publication predates the platform record ({readings[src].where}) — its "
                f"bundles' floors are not named, so the race {TRANSITIONAL} closes stays open for it until "
                f"'{src}' republishes" for src in without]
    notes = [f"'{src}' has no sealed publication for this identity — the seed step names that" for src in absent]
    if not with_record:
        return Decision(run, "legacy", notes + [f"no upstream publication records its platform — "
                                                f"composing on this run's {run.text()}"], warnings)
    distinct = {(p.tester, p.portal) for p in with_record.values()}
    if len(distinct) > 1:
        raise Refusal("the upstream publications were sealed on DIFFERENT platforms — "
                      + "; ".join(f"'{s}' on {p.text()}" for s, p in sorted(with_record.items()))
                      + ". One gate host cannot be both; the upstreams must reseal on one set.")
    src, rec = sorted(with_record.items())[0]
    names = "', '".join(sorted(with_record))
    recorded = sorted(with_record)
    if pinned:
        if rec.same_build(run):
            return Decision(run, "pin", notes + [f"{who} the platform '{names}' was sealed on — {run.text()}"], warnings, recorded)
        order = compare_sets(rec.set, run.set)
        if order is None or order >= 0:
            what = ("NEWER than" if order and order > 0 else
                    "the same set NAME as, but a different build from" if order == 0 else
                    "not orderable against")
            raise Refusal(
                f"{who} {run.text()}, and the '{names}' publication was sealed on {rec.text()} — "
                f"{what} the {'freeze' if frozen else 'pin'}. Composing on it would DECLINE every bundle whose floor exceeds it "
                f"(the bake-consumption failure {TRANSITIONAL} names). "
                + (f"Lift or move the freeze (repository variable MW_PLATFORM_REF) to the publication's set "
                   f"{rec.set}, or wait for '{names}' to publish on the frozen set." if frozen else
                   f"Either pin exactly the publication's set, or pass NO digests (and, on a pull request, "
                   f"platform-ceiling) so this lane composes on the set the publication names."))
        if frozen:
            warnings.append(
                f"the MW_PLATFORM_REF freeze '{frozen}' holds the host at {run.text()}, NEWER than the set the "
                f"'{names}' publication was sealed on ({rec.text()}). Composing on the freeze — every floor is "
                f"at or below it, so nothing is declined — but not on the publication's set.")
            return Decision(run, "pin", notes, warnings, recorded)
        warnings.append(
            f"TRANSITIONAL ({TRANSITIONAL}): the caller pinned {run.text()}, NEWER than the set the "
            f"'{names}' publication was sealed on ({rec.text()}). Composing on the pin as before — every "
            f"floor is at or below it, so nothing is declined — but the gate is NOT on the publication's "
            f"set. Pass no digests (and platform-ceiling on a pull request) to compose on it; a disagreeing "
            f"pin becomes red once every upstream-seed caller has.")
        return Decision(run, "pin", notes, warnings, recorded)
    if ceiling:
        order = compare_sets(rec.set, ceiling)
        if order is None:
            raise Refusal(f"the '{names}' publication was sealed on {rec.text()}, which cannot be ordered "
                          f"against this run's ceiling {ceiling} — refusing rather than guessing which is newer")
        if order > 0:
            raise Refusal(
                f"the '{names}' publication was sealed on {rec.text()}, NEWER than this run's ceiling "
                f"{ceiling} (the set this repository's main has passed). A pull request does not run on a "
                f"platform main has not passed, and composing on the ceiling would DECLINE every bundle "
                f"floored above it. Re-run once main has passed {rec.set}, or label the pull request to "
                f"follow the newest sealed set.")
    moved = "" if rec.same_build(run) else f" (this run had resolved {run.text()} — the publication decides)"
    return Decision(rec, "publication", notes + [f"composing on the set '{names}' was sealed on: {rec.text()}{moved}"],
                    warnings, recorded)


def verify(composed: Platform, mode: str, source: str, reading: Reading,
           recorded: frozenset[str] | None = None) -> list[str]:
    """After composition: the publication the seed is ABOUT to read must still be one this host can
    adopt. Returns warnings; raises Refusal when it cannot.

    `recorded` — the upstreams that carried a record when the host was chosen (`choose`'s
    `recorded-sources`). A missing record is a reseal for one of THOSE in EVERY mode — a pinned or
    frozen host recorded the upstream too, and a pre-record reseal under it carries floors nobody
    named — while an upstream that had none then (beside a recorded one) is the documented
    fallback. None = not told: only then does the mode decide (`publication` presumes every
    upstream was recorded, the strict reading)."""
    if reading.kind == "absent":
        return []                                   # the seed step's own refusal names it
    if reading.kind == "no-record":
        lost = (source in recorded) if recorded is not None else (mode == "publication")
        if lost:
            raise Refusal(f"the '{source}' publication this shard composed from carried a platform record, and "
                          f"the one it now reads ({reading.where}) carries none — it was resealed by a lane that "
                          f"predates the record, whose floors are unknown. Re-run: the gate then reads the "
                          f"publication that applies.")
        return []
    rec = reading.record
    assert rec is not None
    if rec.same_build(composed):
        return []
    if mode == "publication":
        raise Refusal(f"the '{source}' publication was RESEALED on {rec.text()} after this shard composed its host "
                      f"on {composed.text()} — the seed and the host would be two sets. Re-run: the gate then "
                      f"composes on the set the publication names ({TRANSITIONAL}).")
    order = compare_sets(rec.set, composed.set)
    if order == 0:
        # The SAME set name over a different build is the pair `decide()` refuses — a reseal under
        # the same name must not slip past the image-pair check by being re-read here.
        raise Refusal(f"the '{source}' publication is sealed on {rec.text()} — the same set NAME as, but a different "
                      f"build from, the host this shard composed ({composed.text()}). One name, two builds: the "
                      f"floors cannot be ordered against the host. Re-run: the gate then reads the publication "
                      f"that applies ({TRANSITIONAL}).")
    if order is not None and order > 0:
        raise Refusal(f"the '{source}' publication is sealed on {rec.text()}, NEWER than the host this shard composed "
                      f"({composed.text()}) — every bundle floored above it would be DECLINED. Re-run with no pinned "
                      f"digests so the gate composes on the publication's set ({TRANSITIONAL}).")
    if order is None:
        return [f"the '{source}' publication is sealed on {rec.text()}, which cannot be ordered against this "
                f"shard's host {composed.text()} — the bake-consumption postcondition decides"]
    return []


# ───────────────────────────────── reading publications ─────────────────────────────────

Reader = Callable[[str, str], Reading]       # (identity, source) → Reading


def reading_from_index(index: dict, source: str, identity: str, where: str) -> Reading:
    if not isinstance(index, dict) or not isinstance(index.get("bundles"), list):
        raise Refusal(f"{where} answered, but not with a publication index")
    raw = index.get("platform")
    if raw is None:
        return Reading("no-record", None, where)
    return Reading("record", record_of(raw, source, identity, where), where)


class _RefuseRedirect(urllib.request.HTTPRedirectHandler):
    """A redirect is never followed: urllib would replay ordinary headers — the registry key — to
    whatever origin the Location names. The curl seed reader does not follow redirects either, so a
    3xx surfaces as an HTTPError and the reader refuses it by name."""

    def redirect_request(self, req, fp, code, msg, headers, newurl):  # noqa: ANN001, D102
        return None


_REGISTRY_OPENER = urllib.request.build_opener(_RefuseRedirect)


def _open_registry(request: urllib.request.Request, timeout: float):  # noqa: ANN202
    return _REGISTRY_OPENER.open(request, timeout=timeout)


def registry_reader(url: str, key: str, delays=TRANSIENT_DELAYS,
                    sleep: Callable[[float], None] = time.sleep) -> Reader:
    def read(identity: str, source: str) -> Reading:
        where = f"{url.rstrip('/')}/api/plugins/bundles/prebuilt/{identity}/{source}"
        attempt = 0
        while True:
            request = urllib.request.Request(where)
            # UNREDIRECTED as well as never redirected (`_RefuseRedirect`): the key reaches this
            # registry URL and no other host, whatever a later change does to the opener.
            request.add_unredirected_header("Authorization", f"Bearer {key}")
            try:
                with _open_registry(request, timeout=60) as response:
                    body = response.read()
                code = 200
            except urllib.error.HTTPError as error:
                code, body = error.code, b""
            except (urllib.error.URLError, OSError) as error:
                code, body = 0, str(error).encode()
            if code == 200:
                try:
                    return reading_from_index(json.loads(body), source, identity, where)
                except ValueError:
                    raise Refusal(f"{where} answered 200 but not JSON (starts {body[:60]!r}) — the registry "
                                  "does not serve the publication index")
            if code == 404:
                return Reading("absent", None, where)
            if 300 <= code <= 399:
                raise Refusal(f"the registry answered {code} (a redirect) for {where} — refusing: a redirect is "
                              "never followed, so the registry key is never sent to another host")
            if code in (401, 403):
                raise Refusal(f"the registry refused registry-key for '{source}' ({code}) — the instance needs "
                              f"a whole-source grant '{source}/*'")
            if code in (0, 408, 429) or 500 <= code <= 599:
                if attempt < len(delays):
                    print(f"registry answered {code or 'no connection'} for {where} — transient; re-asking "
                          f"in {delays[attempt]}s", file=sys.stderr)
                    sleep(delays[attempt])
                    attempt += 1
                    continue
            raise Refusal(f"the registry answered {code or 'no connection'} for {where} — refusing")
    return read


def share_reader(targets: str, run: Callable[[list[str]], tuple[int, str]] | None = None) -> Reader:
    """The FIRST target, by the reader's pointer rules (ShippedPrebuiltBundles.PublicationDirectoryOf
    and its shell copies in node-repo-gate.yml — change one, change all): an absent, blank,
    unreadable, escaping or dangling `_current` means the source directory."""
    target = (targets.split() or [""])[0]
    parts = target.split("/", 2)
    if len(parts) < 2 or not parts[0] or not parts[1]:
        raise Refusal(f"bake-publish-targets '{targets}' names no <account>/<share>")
    account, share, base = parts[0], parts[1], (parts[2] if len(parts) > 2 else "")

    def az(args: list[str]) -> tuple[int, str]:
        proc = subprocess.run(["az", "storage", *args, "--account-name", account, "--share-name", share,
                               "--auth-mode", "login", "--backup-intent", "--only-show-errors"],
                              capture_output=True, text=True)
        return proc.returncode, proc.stdout
    call = run or az

    def exists(kind: str, path: str) -> str:
        flag = "--path" if kind == "file" else "--name"
        rc, out = call([kind, "exists", flag, path, "--query", "exists", "-o", "tsv"])
        return out.strip() if rc == 0 and out.strip() in ("true", "false") else "unknown"

    def download(path: str) -> str | None:
        with tempfile.TemporaryDirectory() as tmp:
            dest = os.path.join(tmp, "f")
            rc, _ = call(["file", "download", "--path", path, "--dest", dest])
            if rc != 0 or not os.path.isfile(dest):
                return None
            with open(dest, encoding="utf-8") as handle:
                return handle.read()

    def read(identity: str, source: str) -> Reading:
        prefix = f"{base + '/' if base else ''}prebuilt-bundles/{identity}/{source}"
        directory = prefix
        if exists("file", f"{prefix}/{POINTER}") == "true":
            named = (download(f"{prefix}/{POINTER}") or "").strip().splitlines()
            name = named[0].strip() if named else ""
            if name and name not in (".", "..") and "/" not in name and "\\" not in name \
                    and exists("directory", f"{prefix}/{name}") == "true":
                directory = f"{prefix}/{name}"
        where = f"{account}/{share}/{directory}"
        sealed = exists("file", f"{directory}/{SENTINEL}")
        if sealed == "false":
            return Reading("absent", None, where)
        if sealed != "true":
            raise Refusal(f"could not read {where}/{SENTINEL} — availability cannot be determined (a hold, not an absence)")
        present = exists("file", f"{directory}/{RECORD_FILE}")
        if present == "false":
            return Reading("no-record", None, where)
        text = download(f"{directory}/{RECORD_FILE}") if present == "true" else None
        if text is None:
            raise Refusal(f"could not read {where}/{RECORD_FILE} (exists={present}) — an unreadable record is not an absent one")
        try:
            raw = json.loads(text)
        except ValueError:
            raise Refusal(f"{where}/{RECORD_FILE} is not JSON (starts {text[:60]!r})")
        return Reading("record", record_of(raw, source, identity, where), where)
    return read


# ───────────────────────────────── CLI ─────────────────────────────────

def emit(rows: dict[str, str]) -> None:
    output = os.environ.get("GITHUB_OUTPUT")
    for k, v in rows.items():
        print(f"{k}={v}")
    if output:
        with open(output, "a", encoding="utf-8") as handle:
            for k, v in rows.items():
                handle.write(f"{k}={v}\n")


def load_json(path: str, what: str) -> object:
    try:
        with open(path, encoding="utf-8") as handle:
            return json.load(handle)
    except (OSError, ValueError) as error:
        raise Refusal(f"could not read {what} at {path}: {error}")


def cmd_choose(args: argparse.Namespace, reader: Reader | None = None) -> int:
    declaration = load_json(args.declaration, "the platform-compatibility declaration")
    run = Platform(args.run_set.strip(), args.run_tester.strip(), args.run_portal.strip())
    pinned = args.pinned == "true"
    ceiling = (args.ceiling or "").strip() or None
    # 🚨 A FREEZE WINS (the incident instruction): when the run's platform was resolved through
    # MW_PLATFORM_REF, the host is the frozen set — addressed by it, held to it, and the ceiling
    # (which the freeze already overrode when the set was resolved) is not consulted.
    frozen = (args.freeze or "").strip() or None
    if frozen and pinned:
        frozen = None                               # the caller's digests are the pin; plan resolved nothing
    if frozen and ceiling:
        print(f"::notice title=Platform from the publication::the MW_PLATFORM_REF freeze '{frozen}' overrides "
              f"platform-ceiling {ceiling} — the host stays the frozen set")
        ceiling = None
    upstreams = args.upstreams.split()
    if not upstreams:
        raise Refusal("--upstreams is empty — there is no publication to take a platform from")
    address_set = ceiling if (ceiling and not pinned and not frozen) else run.set
    if not address_set:
        print(f"::warning title=Platform not taken from the publication::this run names no platform SET "
              f"(the caller pinned digests without platform-set, or follows tags), so the framework identity "
              f"that addresses the publication cannot be computed before the host exists. Composing on "
              f"{run.text()} — the race {TRANSITIONAL} closes stays open for this caller; pass platform-set "
              f"with pins, or no pins at all.")
        emit({"image-digest": run.tester, "platform-image-digest": run.portal, "platform-set": run.set,
              "identity": "", "mode": "legacy", "recorded-sources": ""})
        return 0
    identity = key_for(address_set, declaration)
    if reader is None:
        if args.registry_url:
            key = os.environ.get("REGISTRY_KEY", "")
            if not key:
                raise Refusal("registry-url is set but registry-key is empty — the publication cannot be read")
            reader = registry_reader(args.registry_url, key)
        elif args.targets:
            reader = share_reader(args.targets)
        else:
            raise Refusal("neither registry-url nor bake-publish-targets is set — nowhere to read the publication from")
    readings = {src: reader(identity, src) for src in upstreams}
    for src, r in readings.items():
        rec = f": {r.record.text()}" if r.record else ""
        print(f"publication '{src}' @ {identity}: {r.kind}{rec} ({r.where})")
    decision = decide(run, pinned, ceiling, readings, frozen)
    for line in decision.notes:
        print(f"::notice title=Platform from the publication::{line}")
    for line in decision.warnings:
        print(f"::warning title=Platform from the publication::{line}")
    emit({"image-digest": decision.platform.tester, "platform-image-digest": decision.platform.portal,
          "platform-set": decision.platform.set, "identity": identity, "mode": decision.mode,
          "recorded-sources": " ".join(decision.recorded)})
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as handle:
            handle.write(f"### Platform — taken from the upstream publication ({TRANSITIONAL})\n\n")
            handle.write(f"- framework identity: `{identity}` (computed from `{address_set}`)\n")
            for src, r in readings.items():
                handle.write(f"- `{src}`: {r.kind}{(' — ' + r.record.text()) if r.record else ''}\n")
            handle.write(f"- **composing on** {decision.platform.text()} — mode `{decision.mode}`\n")
    return 0


def cmd_verify(args: argparse.Namespace) -> int:
    composed = Platform(args.composed_set.strip(), args.composed_tester.strip(), args.composed_portal.strip())
    if args.index:
        reading = reading_from_index(load_json(args.index, "the publication index"), args.source, args.identity, args.index)
    elif args.record:
        reading = Reading("record", record_of(load_json(args.record, "the platform record"), args.source,
                                               args.identity, args.record), args.record)
    else:
        reading = Reading("no-record", None, "the publication")
    recorded = None if args.recorded_sources is None else frozenset(args.recorded_sources.split())
    for line in verify(composed, args.mode, args.source, reading, recorded):
        print(f"::warning title=Seed vs host::{line}")
    print(f"seed '{args.source}': {reading.kind}{(' — ' + reading.record.text()) if reading.record else ''}; "
          f"host {composed.text()} (mode {args.mode}) — consistent")
    return 0


# ───────────────────────────────── self-test ─────────────────────────────────

def self_test() -> int:
    failures: list[str] = []
    passes = 0

    def check(name: str, ok: bool, detail: object = "") -> None:
        nonlocal passes
        if ok:
            passes += 1
            print(f"  OK   {name}")
        else:
            failures.append(name)
            print(f"  FAIL {name} — {detail}")

    def refused(fn: Callable[[], object]) -> str:
        try:
            fn()
        except Refusal as error:
            return str(error)
        return ""

    T = {n: "sha256:" + str(n) * 64 for n in range(1, 8)}
    decl = {"epoch": 1, "breaks": []}
    old = Platform("3.1.11069", T[1], T[2])          # what the caller's preflight resolved at T0
    new = Platform("3.1.11076", T[3], T[4])          # what the upstream resealed on at T0+Δ
    rec = lambda p: Reading("record", p, "registry")  # noqa: E731

    print("the compatibility key (the publication's ADDRESS, computed before the host exists):")
    check("3.1.11069 under epoch 1 → c003e001 (PlatformCompatibility.KeyOf)", key_for("3.1.11069", decl) == "c003e001")
    check("the legacy notation reads the same major", key_for("3.0.0-ci.9985", decl) == "c003e001")
    brk = {"epoch": 2, "breaks": [{"epoch": 2, "previousEpochCeiling": "3.1.11000"}]}
    check("a set at or below a declared break's ceiling is in the PREVIOUS epoch",
          key_for("3.1.11000", brk) == "c003e001" and key_for("3.1.10999", brk) == "c003e001")
    check("a set past the ceiling is in the current epoch", key_for("3.1.11001", brk) == "c003e002")
    check("a break without a ceiling is refused, never guessed",
          "previousEpochCeiling" in refused(lambda: key_for("3.1.5", {"epoch": 2, "breaks": [{"epoch": 2}]})))
    check("a non-set is refused", "not a platform set" in refused(lambda: key_for("main", decl)))
    check("ordering: SemVer and legacy share one run lineage",
          compare_sets("3.1.11076", "3.1.11069") == 1 and compare_sets("3.0.0-ci.9985", "3.1.11069") == -1
          and compare_sets("3.1.0", "3.1.11069") is None)

    print("\nthe decision — the REPRODUCTION first (Reinsurance #264, run 39025974143):")
    # 🚨 NEGATIVE CONTROL. The behaviour before this script: the host is whatever the run resolved,
    # the seed is whatever the publication now is. Its result is checked by the SAME predicate the
    # fix must satisfy — "every seed floor is at or below the host" — and must FAIL it, or the
    # predicate proves nothing.
    def old_behaviour(run: Platform, readings: dict[str, Reading]) -> Platform:
        return run
    def adoptable(host: Platform, readings: dict[str, Reading]) -> bool:
        return all(r.record is None or (compare_sets(r.record.set, host.set) or 0) <= 0 for r in readings.values())
    race = {"plugins": rec(new)}
    check("NEGATIVE CONTROL: the old behaviour composes on 3.1.11069 under a seed floored at 3.1.11076 — not adoptable",
          not adoptable(old_behaviour(old, race), race))
    d = decide(old, pinned=False, ceiling=None, readings=race)
    check("nothing pinned: the host IS the publication's set — adoptable by construction",
          d.platform == new and d.mode == "publication" and adoptable(d.platform, race), d)
    d = decide(old, pinned=False, ceiling="3.1.11080", readings=race)
    check("…and under a ceiling the publication does not exceed, the same", d.platform == new, d)
    why = refused(lambda: decide(old, pinned=False, ceiling="3.1.11069", readings=race))
    check("(b) a publication NEWER than the pull request's ceiling is REFUSED BY NAME, never dropped",
          "NEWER than this run's ceiling 3.1.11069" in why and "3.1.11076" in why, why)
    why = refused(lambda: decide(old, pinned=True, ceiling=None, readings=race))
    check("(c) a PIN older than the publication's set is RED naming both (the re-run-failed-jobs case)",
          "PINNED set 3.1.11069" in why and "3.1.11076" in why, why)
    d = decide(new, pinned=True, ceiling=None, readings=race)
    check("a pin that IS the publication's set passes", d.platform == new and d.mode == "pin" and not d.warnings, d)
    d = decide(Platform("3.1.11080", T[5], T[6]), pinned=True, ceiling=None, readings=race)
    check("a pin NEWER than the publication's set composes as pinned, WARNING naming both (transitional)",
          d.mode == "pin" and d.platform.set == "3.1.11080" and any("TRANSITIONAL" in w and "3.1.11076" in w for w in d.warnings), d)
    why = refused(lambda: decide(Platform("3.1.11076", T[5], T[6]), pinned=True, ceiling=None, readings=race))
    check("a pin with the publication's set NAME and another build is RED", "different build" in why, why)

    print("\nthe edges:")
    d = decide(old, pinned=False, ceiling=None, readings={"plugins": Reading("no-record", None, "share")})
    check("a publication that predates the record: own platform kept, and SAID",
          d.mode == "legacy" and d.platform == old and any("predates the platform record" in w for w in d.warnings), d)
    d = decide(old, pinned=False, ceiling=None, readings={"plugins": Reading("absent", None, "registry")})
    check("no publication at all: own platform, the seed step names the absence", d.mode == "legacy" and d.notes, d)
    why = refused(lambda: decide(old, False, None, {"plugins": rec(new), "crm": rec(Platform("3.1.11070", T[5], T[6]))}))
    check("two upstreams sealed on different platforms are RED naming each", "'crm' on" in why and "'plugins' on" in why, why)
    d = decide(old, False, None, {"plugins": rec(new), "crm": Reading("no-record", None, "x")})
    check("a recorded upstream beside an unrecorded one: the record decides, the other is warned",
          d.platform == new and any("'crm'" in w for w in d.warnings) and d.recorded == ["plugins"], d)
    # …and the seed re-read accepts the unrecorded one unchanged (it never had a record), while a
    # RECORDED upstream that lost its record is still a reseal. NEGATIVE CONTROL: without the
    # per-upstream status (recorded=None, the shard-wide mode alone) the legacy one is refused —
    # every re-run of a mixed seed failed that way.
    mixed = frozenset(d.recorded)
    check("mixed seed: the legacy upstream re-read with no record passes verify",
          verify(new, d.mode, "crm", Reading("no-record", None, "x"), mixed) == [])
    check("mixed seed NEGATIVE CONTROL: the shard-wide mode alone refuses the unchanged legacy upstream",
          "predates the record" in refused(lambda: verify(new, d.mode, "crm", Reading("no-record", None, "x"))))
    why = refused(lambda: verify(new, d.mode, "plugins", Reading("no-record", None, "x"), mixed))
    check("mixed seed: the RECORDED upstream losing its record is still RED", "predates the record" in why, why)

    print("\nthe MW_PLATFORM_REF freeze wins over the publication (and the ceiling):")
    check("NEGATIVE CONTROL: without the freeze the same inputs replace the frozen set by the publication's",
          decide(old, pinned=False, ceiling="3.1.11080", readings=race).platform == new)
    why = refused(lambda: decide(old, pinned=False, ceiling="3.1.11080", readings=race, frozen="3.1.11069"))
    check("a publication NEWER than the frozen host is RED naming the freeze and both sets",
          "MW_PLATFORM_REF freeze '3.1.11069'" in why and "3.1.11069" in why and "3.1.11076" in why, why)
    d = decide(Platform("3.1.11080", T[5], T[6]), pinned=False, ceiling="3.1.11070", readings=race, frozen="3.1.11080")
    check("a frozen host NEWER than the publication keeps the freeze (ceiling not consulted), warning naming both",
          d.platform.set == "3.1.11080" and d.mode == "pin"
          and any("freeze" in w and "3.1.11076" in w for w in d.warnings), d)
    d = decide(new, pinned=False, ceiling=None, readings=race, frozen="3.1.11076")
    check("a frozen host that IS the publication's set passes", d.platform == new and d.mode == "pin" and not d.warnings, d)
    why = refused(lambda: decide(old, False, "3.1.11080", {"plugins": rec(Platform("3.1.0-edge.5", T[3], T[4]))}))
    check("an unorderable record under a ceiling is refused, not guessed", "cannot be ordered" in why, why)
    why = refused(lambda: record_of({"set": "3.1.5", "testerDigest": "sha256:x", "portalDigest": T[1]}, "plugins", "c003e001", "w"))
    check("a malformed record is refused", "malformed" in why, why)
    why = refused(lambda: record_of({"set": "3.1.5", "testerDigest": T[1], "portalDigest": T[2], "identity": "c003e002"},
                                    "plugins", "c003e001", "w"))
    check("a record whose identity disagrees with its address is refused", "disagree" in why, why)

    print("\nverify — the seed re-read after the host is composed:")
    check("same build: consistent", verify(new, "publication", "plugins", rec(new)) == [])
    why = refused(lambda: verify(new, "publication", "plugins", rec(Platform("3.1.11080", T[5], T[6]))))
    check("resealed after composition (mode publication): RED naming both", "RESEALED" in why and "3.1.11080" in why, why)
    why = refused(lambda: verify(new, "publication", "plugins", Reading("no-record", None, "x")))
    check("resealed by a lane that predates the record: RED", "predates the record" in why, why)
    why = refused(lambda: verify(old, "pin", "plugins", rec(new)))
    check("a pinned host under a NEWER seed: RED before the DECLINE wall", "NEWER than the host" in why, why)
    check("a pinned host above an older seed: fine", verify(Platform("3.1.11080", T[5], T[6]), "pin", "plugins", rec(new)) == [])
    check("legacy host, no record: fine", verify(old, "legacy", "plugins", Reading("no-record", None, "x")) == [])
    # A PINNED or FROZEN host recorded the upstream too (decide() returns its `recorded`), so a
    # pre-record reseal under it is a reseal. NEGATIVE CONTROL: the mode-only reading accepted it.
    d = decide(new, pinned=True, ceiling=None, readings=race)
    pinned_recorded = frozenset(d.recorded)
    check("pin mode records the upstream (the input the next two cases rely on)", d.mode == "pin" and pinned_recorded == {"plugins"}, d)
    why = refused(lambda: verify(new, d.mode, "plugins", Reading("no-record", None, "x"), pinned_recorded))
    check("pin mode: a RECORDED upstream resealed by a pre-record lane is RED", "predates the record" in why, why)
    d = decide(new, pinned=False, ceiling=None, readings=race, frozen="3.1.11076")
    why = refused(lambda: verify(new, d.mode, "plugins", Reading("no-record", None, "x"), frozenset(d.recorded)))
    check("freeze: a RECORDED upstream resealed by a pre-record lane is RED", d.mode == "pin" and "predates the record" in why, why)
    check("pin mode NEGATIVE CONTROL: with no per-source list the mode default still applies (no list = not told)",
          verify(new, "pin", "plugins", Reading("no-record", None, "x")) == [])
    check("pin mode: an upstream that never had a record (beside a recorded one) stays the fallback",
          verify(new, "pin", "crm", Reading("no-record", None, "x"), pinned_recorded) == [])
    # Same set NAME, different build after a pinned/frozen host was chosen: decide() refuses that
    # pair, so verify must too. NEGATIVE CONTROL: a genuinely OLDER set under the pin still passes.
    twin = Platform(new.set, T[5], T[6])
    why = refused(lambda: verify(new, "pin", "plugins", rec(twin)))
    check("pin mode: a reseal with the host's set NAME and another build is RED", "different build" in why and new.set in why, why)
    why = refused(lambda: verify(new, "pin", "plugins", rec(twin), pinned_recorded))
    check("…and under a freeze (mode pin, recorded) the same", "different build" in why, why)
    check("pin mode NEGATIVE CONTROL: an OLDER set under the pin still passes",
          verify(new, "pin", "plugins", rec(old), pinned_recorded) == [])

    print("\nreaders, against fakes (no network):")
    idx = {"identity": "c003e001", "source": "plugins", "bundles": ["a.zip"], "generation": "g1",
           "platform": {"set": "3.1.11076", "testerDigest": T[3], "portalDigest": T[4], "identity": "c003e001"}}
    r = reading_from_index(idx, "plugins", "c003e001", "x")
    check("an index carrying `platform` reads as a record", r.kind == "record" and r.record == new, r)
    r = reading_from_index({"bundles": []}, "plugins", "c003e001", "x")
    check("an index without `platform` reads as no-record (a registry or publication predating it)", r.kind == "no-record", r)
    check("a non-index is refused", "not with a publication index" in refused(lambda: reading_from_index({"x": 1}, "p", "c", "w")))
    files = {"pfx/prebuilt-bundles/c003e001/plugins/_current": "gen-7\n",
             "pfx/prebuilt-bundles/c003e001/plugins/gen-7/_complete": "a.zip\n",
             "pfx/prebuilt-bundles/c003e001/plugins/gen-7/platform-set.json": json.dumps(idx["platform"])}
    dirs = {"pfx/prebuilt-bundles/c003e001/plugins/gen-7"}

    def fake_az(args: list[str]) -> tuple[int, str]:
        kind, verb = args[0], args[1]
        path = args[3]
        if verb == "exists":
            return 0, ("true" if (path in files if kind == "file" else path in dirs) else "false") + "\n"
        if verb == "download":
            if path not in files:
                return 1, ""
            with open(args[args.index("--dest") + 1], "w", encoding="utf-8") as handle:
                handle.write(files[path])
            return 0, ""
        return 2, ""
    r = share_reader("acct/share/pfx", fake_az)("c003e001", "plugins")
    check("the share reader follows _current to the generation and reads its record",
          r.kind == "record" and r.record == new and r.where.endswith("gen-7"), r)
    del files["pfx/prebuilt-bundles/c003e001/plugins/gen-7/platform-set.json"]
    r = share_reader("acct/share/pfx", fake_az)("c003e001", "plugins")
    check("…a sealed generation without one is no-record", r.kind == "no-record", r)
    r = share_reader("acct/share/pfx", fake_az)("c003e001", "crm")
    check("…an unsealed source is absent", r.kind == "absent", r)
    calls = {"n": 0}

    def flaky(url, timeout=60):  # noqa: ANN001
        calls["n"] += 1
        raise urllib.error.HTTPError(url.full_url, 503 if calls["n"] < 3 else 404, "x", {}, None)
    global _open_registry
    real = _open_registry
    _open_registry = flaky  # type: ignore[assignment]
    try:
        r = registry_reader("https://r", "k", delays=(0, 0, 0, 0), sleep=lambda s: None)("c003e001", "plugins")
    finally:
        _open_registry = real  # type: ignore[assignment]
    check("the registry reader waits out a 503 (republish window) and reads the definite answer", r.kind == "absent" and calls["n"] == 3, (r, calls))

    # A REAL redirect over loopback: the registry answers 302 to a second origin; the reader must
    # refuse it, and the second origin must never see a request — let alone the key.
    import http.server
    import threading
    seen: list[str | None] = []

    class Elsewhere(http.server.BaseHTTPRequestHandler):
        def do_GET(self):  # noqa: N802
            seen.append(self.headers.get("Authorization"))
            self.send_response(200)
            self.end_headers()
            self.wfile.write(b'{"bundles": []}')

        def log_message(self, *a):  # noqa: ANN002
            pass

    other = http.server.HTTPServer(("127.0.0.1", 0), Elsewhere)

    class Redirecting(http.server.BaseHTTPRequestHandler):
        def do_GET(self):  # noqa: N802
            self.send_response(302)
            self.send_header("Location", f"http://127.0.0.1:{other.server_port}/index")
            self.end_headers()

        def log_message(self, *a):  # noqa: ANN002
            pass

    registry = http.server.HTTPServer(("127.0.0.1", 0), Redirecting)
    for server in (other, registry):
        threading.Thread(target=server.serve_forever, daemon=True).start()
    try:
        why = refused(lambda: registry_reader(f"http://127.0.0.1:{registry.server_port}", "secret-key",
                                              delays=(), sleep=lambda s: None)("c003e001", "plugins"))
    finally:
        for server in (other, registry):
            server.shutdown()
            server.server_close()
    check("a registry redirect is REFUSED by name, never followed", "redirect" in why, why)
    check("…and the redirect target never receives a request, so never the key", seen == [], seen)

    total = passes + len(failures)
    print(f"\npublication-platform.py self-test: {passes} passed, {len(failures)} failed, {total} assertion(s)")
    if total == 0 or failures:
        print("::error::publication-platform.py self-test FAILED: " + "; ".join(failures or ["zero assertions"]))
        return 1
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--self-test", action="store_true")
    sub = parser.add_subparsers(dest="command")
    c = sub.add_parser("choose")
    c.add_argument("--declaration", required=True)
    c.add_argument("--upstreams", required=True)
    c.add_argument("--run-set", default="")
    c.add_argument("--run-tester", default="")
    c.add_argument("--run-portal", default="")
    c.add_argument("--pinned", choices=("true", "false"), required=True)
    c.add_argument("--ceiling", default="")
    c.add_argument("--freeze", default="")
    c.add_argument("--registry-url", default="")
    c.add_argument("--targets", default="")
    v = sub.add_parser("verify")
    v.add_argument("--source", required=True)
    v.add_argument("--mode", choices=("publication", "pin", "legacy"), required=True)
    v.add_argument("--composed-set", default="")
    v.add_argument("--composed-tester", default="")
    v.add_argument("--composed-portal", default="")
    v.add_argument("--identity", default="")
    v.add_argument("--recorded-sources", default=None)
    g = v.add_mutually_exclusive_group(required=True)
    g.add_argument("--index")
    g.add_argument("--record")
    g.add_argument("--no-record", action="store_true")
    k = sub.add_parser("key")
    k.add_argument("--set", required=True)
    k.add_argument("--declaration", required=True)
    args = parser.parse_args()
    if args.self_test:
        return self_test()
    try:
        if args.command == "choose":
            return cmd_choose(args)
        if args.command == "verify":
            return cmd_verify(args)
        if args.command == "key":
            print(key_for(args.set, load_json(args.declaration, "the declaration")))
            return 0
    except Refusal as error:
        print(f"::error title=Platform from the publication ({TRANSITIONAL})::{error}")
        return 1
    parser.print_help()
    return 2


if __name__ == "__main__":
    sys.exit(main())
