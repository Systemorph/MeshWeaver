#!/usr/bin/env python3
"""build-host-announcement.py — core's CD hands the promoted set to the standing build host of a channel.

Rule 2 of the standing build hosts (MeshWeaver.Plugins `Hosting/StandingBuildHosts` §2, *How the
platform layer follows*; platform reference Doc/Architecture/BuildHosts): when core's CD ARMS a set,
the same run rolls `build-latest` onto it. It does so the way CD hands control its next image —
a SIGNED event into the control plane's inbox (`Hosting/PlatformBuilds`), which the operations
instance turns into a change of ITS OWN record (`buildHosts.<channel>.testerDigest`, `.portalDigest`,
`.platformVersion`) and the record's Reconcile. Never `kubectl set image`, never a hand-edited
Deployment: the record is the only source of truth for what the host runs.

    build-host-announcement.py decide --declaration .github/build-hosts.json \
        --control-instance .github/control-instance.json --channel latest
        → prints `enabled=true|false`, `deployment=<operations record>`, `why=<sentence>`
          (GITHUB_OUTPUT lines)
    build-host-announcement.py body --deployment control --channel latest --version 3.1.10412 \
        --tester-image cr.example.com/mw-plugin-test --tester-digest sha256:… \
        --portal-image cr.example.com/memex-portal-ai --portal-digest sha256:… \
        --core-sha <40 hex> [--plugins-sha <40 hex>] --instance <run url>
        → the event JSON, one line (the bytes that are signed are the bytes that are sent)
    build-host-announcement.py --self-test

🚨 A NO-OP UNTIL DECLARED. `.github/build-hosts.json` says whether the platform's estate runs
standing build hosts at all (`enabled`) and which channels CD rolls (`channels`). While it says
`false` the CD job writes one summary line and sends nothing — and the RECEIVER guards the same
fact a second time: it changes a record only when that record's `buildHosts.enabled` is true, so
an event cannot switch the hosts on by itself. Both halves default to off.

🚨 NEVER BACKWARDS. The job sends only for the newest ARMED set (the same reading
`notify-platform-update` makes), and the receiver refuses a `platformVersion` older than the one
the record names.
"""
from __future__ import annotations

import argparse
import datetime as _dt
import json
import re
import sys
from pathlib import Path

EVENT = "build-host-update-available"
POLICY = "build-hosts-follow-latest"
REPORTER = "main-cd build-host-latest"
DIGEST = re.compile(r"^sha256:[0-9a-f]{64}$")
SHA = re.compile(r"^[0-9a-f]{40}$")
CHANNELS = ("latest", "stable")
# Only `latest` is rolled by the run that arms a set; `stable` moves with its own pointer (§2).
CD_CHANNELS = ("latest",)
SET_NAME = re.compile(r"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.]+)?$")
REPOSITORY = re.compile(r"^[A-Za-z0-9][A-Za-z0-9./_-]*$")


def load_declaration(path: Path) -> dict:
    d = json.loads(path.read_text())
    if not isinstance(d, dict) or type(d.get("enabled")) is not bool:
        raise ValueError(f"{path}: `enabled` must be true or false")
    ch = d.get("channels")
    if not isinstance(ch, list) or not all(isinstance(c, str) and c in CHANNELS for c in ch):
        raise ValueError(f"{path}: `channels` must be a list drawn from {list(CHANNELS)}")
    return d


def decide(declaration: dict, control: dict, channel: str) -> tuple[bool, str, str]:
    """(send?, deployment, why). Pure."""
    dep = str(control.get("deployment") or "").strip()
    if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._/-]*", dep):
        raise ValueError(f"control-instance.json names no plain `deployment` ({dep!r}) — the operations record "
                         f"the build hosts live on")
    if channel not in CD_CHANNELS:
        raise ValueError(f"channel {channel!r} is not rolled by the run that arms a set (only {list(CD_CHANNELS)})")
    if not declaration["enabled"]:
        return False, dep, ("standing build hosts are not enabled for this estate (.github/build-hosts.json "
                            "enabled: false) — nothing is sent")
    if channel not in declaration["channels"]:
        return False, dep, f"channel {channel} is not in .github/build-hosts.json channels — nothing is sent"
    return True, dep, f"build-{channel} follows the armed set on record {dep}"


def body(*, deployment: str, channel: str, version: str, tester_image: str, tester_digest: str,
         portal_image: str, portal_digest: str, core_sha: str, plugins_sha: str, instance: str,
         detected_at: str) -> dict:
    """The signed event. Pure; every field validated — a fact naming something malformed is worse
    than no fact."""
    if channel not in CHANNELS:
        raise ValueError(f"channel {channel!r} is not one of {list(CHANNELS)}")
    if not SET_NAME.match(version or ""):
        raise ValueError(f"version {version!r} is not a platform set name")
    for name, d in (("tester", tester_digest), ("portal", portal_digest)):
        if not DIGEST.match(d or ""):
            raise ValueError(f"{name} digest {d!r} is not sha256:<64 hex>")
    for name, r in (("tester", tester_image), ("portal", portal_image)):
        if not REPOSITORY.match(r or "") or ":" in r.rsplit("/", 1)[-1] or "@" in r:
            raise ValueError(f"{name} image {r!r} is not a plain repository (no tag, no digest)")
    if not SHA.match(core_sha or ""):
        raise ValueError(f"core sha {core_sha!r} is not a full commit")
    if plugins_sha and not SHA.match(plugins_sha):
        raise ValueError(f"plugins sha {plugins_sha!r} is not a full commit")
    prefix = f"buildHosts.{channel}"
    # Key order is the wire order: `event` first.
    return {
        "event": EVENT,
        "deployment": deployment,
        "channel": channel,
        "platformVersion": version,
        "testerImage": tester_image,
        "testerDigest": tester_digest,
        "portalImage": portal_image,
        "portalDigest": portal_digest,
        "coreSha": core_sha,
        "pluginsSha": plugins_sha or None,
        # What the receiver writes on the record, spelled as the chart reads it.
        "recordFields": {
            f"{prefix}.platformVersion": version,
            f"{prefix}.testerDigest": tester_digest,
            f"{prefix}.portalDigest": portal_digest,
        },
        "policy": POLICY,
        "trigger": "build-host-latest",
        "reporter": REPORTER,
        "instance": instance,
        "detectedAt": detected_at,
    }


def main(argv: list[str]) -> int:
    if argv[:1] == ["--self-test"]:
        return self_test()
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    sub = ap.add_subparsers(dest="cmd", required=True)
    d = sub.add_parser("decide")
    d.add_argument("--declaration", type=Path, default=Path(".github/build-hosts.json"))
    d.add_argument("--control-instance", type=Path, default=Path(".github/control-instance.json"))
    d.add_argument("--channel", default="latest")
    b = sub.add_parser("body")
    for k in ("deployment", "channel", "version", "tester-image", "tester-digest", "portal-image",
              "portal-digest", "core-sha", "instance"):
        b.add_argument(f"--{k}", required=True)
    b.add_argument("--plugins-sha", default="")
    a = ap.parse_args(argv)
    try:
        if a.cmd == "decide":
            send, dep, why = decide(load_declaration(a.declaration),
                                    json.loads(a.control_instance.read_text()), a.channel)
            print(f"enabled={'true' if send else 'false'}")
            print(f"deployment={dep}")
            print(f"why={why}")
            return 0
        ev = body(deployment=a.deployment, channel=a.channel, version=a.version,
                  tester_image=a.tester_image, tester_digest=a.tester_digest,
                  portal_image=a.portal_image, portal_digest=a.portal_digest,
                  core_sha=a.core_sha, plugins_sha=a.plugins_sha, instance=a.instance,
                  detected_at=_dt.datetime.now(_dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"))
        print(json.dumps(ev, separators=(",", ":")))
        return 0
    except (ValueError, OSError) as e:
        print(f"::error::{e}", file=sys.stderr)
        return 1


def self_test() -> int:
    failures: list[str] = []

    def check(name: str, cond: bool, detail: object = "") -> None:
        if not cond:
            failures.append(f"{name}: {detail}")

    control = {"deployment": "control", "url": "https://control.example.com", "platformLagBoundMinutes": 180}
    off = {"enabled": False, "channels": ["latest"]}
    on = {"enabled": True, "channels": ["latest"]}
    send, dep, why = decide(off, control, "latest")
    check("decide/off is a no-op", send is False and dep == "control" and "not enabled" in why, (send, why))
    send, _, _ = decide(on, control, "latest")
    check("decide/on sends", send is True)
    send, _, why = decide({"enabled": True, "channels": []}, control, "latest")
    check("decide/channel not declared", send is False and "not in" in why, why)
    for bad_ctl, bad_ch in (({"deployment": ""}, "latest"), (control, "stable")):
        try:
            decide(on, bad_ctl, bad_ch)
            check(f"decide/refuses {bad_ctl} {bad_ch}", False, "accepted")
        except ValueError:
            pass

    ok = dict(deployment="control", channel="latest", version="3.1.10412",
              tester_image="cr.example.com/mw-plugin-test", tester_digest="sha256:" + "1" * 64,
              portal_image="cr.example.com/memex-portal-ai", portal_digest="sha256:" + "2" * 64,
              core_sha="a" * 40, plugins_sha="b" * 40, instance="https://github.com/x/y/actions/runs/1",
              detected_at="2026-01-01T00:00:00Z")
    ev = body(**ok)
    check("body/event first", list(ev)[0] == "event" and ev["event"] == EVENT, list(ev))
    check("body/record fields as the chart reads them", ev["recordFields"] == {
        "buildHosts.latest.platformVersion": "3.1.10412",
        "buildHosts.latest.testerDigest": "sha256:" + "1" * 64,
        "buildHosts.latest.portalDigest": "sha256:" + "2" * 64}, ev["recordFields"])
    check("body/ci set name accepted", body(**dict(ok, version="3.0.0-ci.10052"))["platformVersion"] == "3.0.0-ci.10052")
    for k, v in (("tester_digest", "3.1.10412"), ("portal_digest", "sha256:abc"),
                 ("tester_image", "cr.example.com/mw-plugin-test:3.1.1"),
                 ("portal_image", "cr.example.com/memex-portal-ai@sha256:" + "2" * 64),
                 ("core_sha", "abc1234"), ("version", "latest"), ("channel", "nightly")):
        try:
            body(**dict(ok, **{k: v}))
            check(f"body/refuses {k}={v}", False, "accepted")
        except ValueError:
            pass
    shipped = Path(__file__).resolve().parents[1] / "build-hosts.json"
    try:
        load_declaration(shipped)
    except (ValueError, OSError) as e:
        check("the shipped .github/build-hosts.json validates", False, str(e))
    if failures:
        for f in failures:
            print(f"SELF-TEST FAILED: {f}")
        return 1
    print("build-host-announcement self-test: all cases passed (no-op while off, channel declaration, "
          "record fields, digest/repository/sha/version refusals, the shipped declaration)")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
