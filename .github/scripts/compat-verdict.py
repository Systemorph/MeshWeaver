#!/usr/bin/env python3
"""compat-verdict.py — did THIS core build break a satellite, or was the satellite already broken?

(The name on this first line is load-bearing, like every script the reusable lanes fetch at a
platform ref and verify by their first bytes.)

WHY THIS EXISTS (maintainer pushback on MeshWeaver#4088, 2026-09-12: "why fail compatibility?")
--------------------------------------------------------------------------------------------
`satellite-compat` compiles every satellite's NodeTypes against the set a core build just
promoted. A red compile alone cannot say WHICH side moved: core changed a surface incompatibly
(core's fault — the red is right), or the satellite merged content that never compiled (core
reddened by a repository it should not know about — precisely the hazard commit c88cd5d5c's
guard exists to keep out of core). One compile is a measurement with no control.

The control is a SECOND compile of the same content against the PREVIOUS image — the set the
fleet is currently on, recorded by `gate` BEFORE this run's promote moved the pointer — with the
module bundles that shipped with it. Two readings, one verdict per NodeType, one verdict per leg:

    previous ok   + new FAIL  → BROKE            core broke it — the leg is RED (the only red)
    previous fail + new fail  → ALREADY-BROKEN   satellite side — GREEN with an advisory line;
                                                 its own daily run / ci-main-red issue own it
    previous ok   + new ok    → COMPATIBLE       green
    no previous image         → NO-BASELINE      a REFUSAL to judge — GREEN with an advisory
                                                 naming why; an unestablished baseline must
                                                 never read as "core broke it" (nor as "fine")

A type that fails on the new image and did not EXIST at the baseline is satellite side too
(nothing core did can have broken content that was never compiled against the fleet's set): it
is listed as already-broken with baseline "absent". A type the new-image ratchet calls "compiles
now — remove it from the allow file" (stale allow) is an advisory in every verdict, never red.

USAGE
  compat-verdict.py --new new.json [--baseline baseline.json] [--baseline-reason TEXT]
                    --new-digest D [--baseline-digest D] --out verdict.json
  compat-verdict.py --self-test

Reads the JSON `compile-check.py --report` writes. Exit 1 iff the verdict is BROKE.
"""
from __future__ import annotations
import argparse
import json
import sys
import tempfile
from pathlib import Path

VERDICTS = ("broke", "already-broken", "compatible", "no-baseline")


def _failing(report: dict) -> dict:
    """type -> first error, for every type that FAILED to compile in a report (gating or allowlisted)."""
    out = {}
    for key in ("new_breaks", "fp_drift", "known_debt"):
        for row in report.get(key, []) or []:
            out.setdefault(row["type"], row.get("error", ""))
    return out


def judge(new: dict, baseline: dict | None, baseline_reason: str = "") -> dict:
    """Pure function: (new report, baseline report or None) -> the classified verdict.

    Only the NEW image's GATING failures (new_breaks + fp_drift) can red anything: known debt
    is allowlisted by the satellite and is its own ratchet's business. The baseline side counts
    ANY compile failure (allowlisted or not) — the question there is only "did it compile?"."""
    new_gating = {}
    for key in ("new_breaks", "fp_drift"):
        for row in new.get(key, []) or []:
            new_gating.setdefault(row["type"], row.get("error", ""))
    stale_allow = list(new.get("stale_allow", []) or [])

    if baseline is None:
        return {
            "verdict": "no-baseline",
            "gate_fail": False,
            "broke": [],
            "already_broken": [],
            "unjudged": [{"type": t, "error": e} for t, e in sorted(new_gating.items())],
            "stale_allow": stale_allow,
            "baseline_reason": baseline_reason or "no baseline report",
        }

    base_ok = set(baseline.get("ok", []) or [])
    base_fail = _failing(baseline)
    broke, already = [], []
    for t, e in sorted(new_gating.items()):
        if t in base_ok:
            broke.append({"type": t, "error": e})
        elif t in base_fail:
            already.append({"type": t, "error": e, "baseline": "failed", "baseline_error": base_fail[t]})
        else:
            already.append({"type": t, "error": e, "baseline": "absent"})
    verdict = "broke" if broke else ("already-broken" if already else "compatible")
    return {
        "verdict": verdict,
        "gate_fail": bool(broke),
        "broke": broke,
        "already_broken": already,
        "unjudged": [],
        "stale_allow": stale_allow,
        "baseline_reason": "",
    }


def _load(path: str | None) -> dict | None:
    if not path:
        return None
    p = Path(path)
    if not p.is_file():
        return None
    return json.loads(p.read_text(encoding="utf-8"))


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--new", help="compile-check --report of the NEW image")
    ap.add_argument("--baseline", help="compile-check --report of the PREVIOUS image (may be absent)")
    ap.add_argument("--baseline-reason", default="", help="why there is no baseline, when there is none")
    ap.add_argument("--new-digest", default="")
    ap.add_argument("--baseline-digest", default="")
    ap.add_argument("--out", help="where to write the verdict JSON")
    ap.add_argument("--self-test", action="store_true")
    args = ap.parse_args()
    if args.self_test:
        return _self_test()
    if not args.new or not args.out:
        print("error: --new and --out are required (or --self-test)")
        return 2

    new = _load(args.new)
    if new is None:
        print(f"::error::the new-image compile produced no report at {args.new} — the gate died before "
              "judging anything; that is a red run, not a verdict")
        return 2
    baseline = _load(args.baseline)
    reason = args.baseline_reason
    if baseline is None and args.baseline and not reason:
        reason = f"the baseline compile produced no report ({args.baseline} is absent)"
    if baseline is None and not args.baseline and not reason:
        reason = "no baseline image was supplied"

    v = judge(new, baseline, reason)
    out = {
        "schema": "satellite-compat/1",
        "content": new.get("content", {}),
        "images": {"new": args.new_digest, "baseline": args.baseline_digest if baseline is not None else "",
                   "baseline_reason": v["baseline_reason"]},
        "types": new.get("types", 0),
        "clean": new.get("clean", 0),
        "known_debt": new.get("known_debt", []),
        **v,
    }
    Path(args.out).parent.mkdir(parents=True, exist_ok=True)
    Path(args.out).write_text(json.dumps(out, indent=2) + "\n", encoding="utf-8")

    sat = out["content"].get("repository") or "this content"
    label = {
        "broke": f"🔴 BROKE — this core build broke {sat}: {len(v['broke'])} NodeType(s) compiled against the "
                 f"previous image ({args.baseline_digest[:19]}) and do not against the new one ({args.new_digest[:19]})",
        "already-broken": f"🟡 ALREADY BROKEN — {len(v['already_broken'])} NodeType(s) of {sat} fail on the new image "
                          f"AND on the previous one (or did not exist there): satellite side, not this build's",
        "compatible": f"✅ COMPATIBLE — every NodeType of {sat} compiles against the new image "
                      f"({len(v['already_broken'])} advisory)",
        "no-baseline": f"⚪ NO BASELINE — refusing to judge {sat}: {v['baseline_reason']}. "
                       f"{len(v['unjudged'])} new-image failure(s) are listed UNJUDGED, not attributed",
    }[v["verdict"]]
    print(label)
    for row in v["broke"]:
        print(f"  BROKE            {row['type']}: {row['error']}")
    for row in v["already_broken"]:
        print(f"  already-broken   {row['type']} (baseline: {row['baseline']}): {row['error']}")
    for row in v["unjudged"]:
        print(f"  unjudged         {row['type']}: {row['error']}")
    for t in v["stale_allow"]:
        print(f"  advisory         {t}: compiles now — remove it from scripts/compile-check.allow")
    if v["gate_fail"]:
        print(f"::error::{label}")
        return 1
    if v["verdict"] != "compatible":
        print(f"::warning::{label}")
    return 0


def _self_test() -> int:
    """Every row of the truth table, both directions, plus the two edge rows."""
    failures = []
    def rep(ok=(), breaks=(), drift=(), debt=(), stale=()):
        return {"ok": list(ok),
                "new_breaks": [{"type": t, "error": f"CS0246: {t}"} for t in breaks],
                "fp_drift": [{"type": t, "error": f"CS0103: {t}"} for t in drift],
                "known_debt": [{"type": t, "error": f"CS1061: {t}"} for t in debt],
                "stale_allow": list(stale), "types": 3, "clean": len(ok)}
    # row 1: previous ok + new FAIL → BROKE, red
    v = judge(rep(breaks=["P/A"], ok=["P/B"]), rep(ok=["P/A", "P/B"]))
    if v["verdict"] != "broke" or not v["gate_fail"] or [r["type"] for r in v["broke"]] != ["P/A"]:
        failures.append(f"row1 broke: {v!r}")
    # row 2: previous fail + new fail → ALREADY-BROKEN, green
    v = judge(rep(breaks=["P/A"], ok=["P/B"]), rep(ok=["P/B"], breaks=["P/A"]))
    if v["verdict"] != "already-broken" or v["gate_fail"] or v["broke"] or v["already_broken"][0]["baseline"] != "failed":
        failures.append(f"row2 already-broken: {v!r}")
    # row 2b: the baseline failure was ALLOWLISTED debt there — still "did not compile" → already-broken
    v = judge(rep(breaks=["P/A"]), rep(debt=["P/A"]))
    if v["verdict"] != "already-broken" or v["gate_fail"]:
        failures.append(f"row2b debt-at-baseline: {v!r}")
    # row 3: ok + ok → COMPATIBLE
    v = judge(rep(ok=["P/A", "P/B"]), rep(ok=["P/A", "P/B"]))
    if v["verdict"] != "compatible" or v["gate_fail"] or v["broke"] or v["already_broken"]:
        failures.append(f"row3 compatible: {v!r}")
    # row 4: no baseline → NO-BASELINE, green, failures UNJUDGED (never attributed)
    v = judge(rep(breaks=["P/A"]), None, "could not resolve the previous image")
    if v["verdict"] != "no-baseline" or v["gate_fail"] or v["broke"] or [r["type"] for r in v["unjudged"]] != ["P/A"] \
       or "previous image" not in v["baseline_reason"]:
        failures.append(f"row4 no-baseline: {v!r}")
    # edge: the type did not exist at the baseline → satellite side, never BROKE
    v = judge(rep(breaks=["P/New"], ok=["P/A"]), rep(ok=["P/A"]))
    if v["verdict"] != "already-broken" or v["gate_fail"] or v["already_broken"][0]["baseline"] != "absent":
        failures.append(f"edge absent-at-baseline: {v!r}")
    # edge: fingerprint drift on the new image counts as a new-image failure; stale allow is advisory only
    v = judge(rep(drift=["P/A"], stale=["P/S"], ok=["P/B", "P/S"]), rep(ok=["P/A", "P/B", "P/S"]))
    if v["verdict"] != "broke" or [r["type"] for r in v["broke"]] != ["P/A"] or v["stale_allow"] != ["P/S"]:
        failures.append(f"edge drift+stale: {v!r}")
    v = judge(rep(stale=["P/S"], ok=["P/S"]), rep(ok=["P/S"]))
    if v["verdict"] != "compatible" or v["gate_fail"]:
        failures.append(f"edge stale-only must be compatible: {v!r}")
    # edge: known debt on the NEW image alone never reds (it is the satellite's ratchet)
    v = judge(rep(debt=["P/D"], ok=["P/A"]), rep(ok=["P/A", "P/D"]))
    if v["verdict"] != "compatible" or v["gate_fail"]:
        failures.append(f"edge known-debt-new: {v!r}")
    # the CLI end to end: exit codes and the written file
    import subprocess
    with tempfile.TemporaryDirectory() as tmp:
        t = Path(tmp)
        (t / "new.json").write_text(json.dumps(rep(breaks=["P/A"], ok=["P/B"]) | {"content": {"repository": "Systemorph/X", "sha": "abc"}}))
        (t / "base.json").write_text(json.dumps(rep(ok=["P/A", "P/B"])))
        rc = subprocess.run([sys.executable, __file__, "--new", str(t / "new.json"), "--baseline", str(t / "base.json"),
                             "--new-digest", "sha256:n", "--baseline-digest", "sha256:b", "--out", str(t / "v.json")],
                            capture_output=True, text=True).returncode
        got = json.loads((t / "v.json").read_text())
        if rc != 1 or got["verdict"] != "broke" or got["images"] != {"new": "sha256:n", "baseline": "sha256:b", "baseline_reason": ""} \
           or got["content"]["sha"] != "abc":
            failures.append(f"cli broke: rc={rc} {got!r}")
        rc = subprocess.run([sys.executable, __file__, "--new", str(t / "new.json"), "--baseline", str(t / "missing.json"),
                             "--new-digest", "sha256:n", "--out", str(t / "v2.json")], capture_output=True, text=True).returncode
        got = json.loads((t / "v2.json").read_text())
        if rc != 0 or got["verdict"] != "no-baseline" or got["images"]["baseline"] != "" or "absent" not in got["images"]["baseline_reason"]:
            failures.append(f"cli no-baseline: rc={rc} {got!r}")
        rc = subprocess.run([sys.executable, __file__, "--new", str(t / "nope.json"), "--out", str(t / "v3.json")],
                            capture_output=True, text=True).returncode
        if rc != 2:
            failures.append(f"cli missing new report must be rc=2, got {rc}")
    if failures:
        print("✗ compat-verdict self-test FAILED:\n  " + "\n  ".join(failures))
        return 1
    print("✓ compat-verdict: broke (red) · already-broken (green, advisory) · compatible · no-baseline (green, "
          "unjudged) — all four rows, both directions, absent-at-baseline, drift, stale-allow and known-debt edges, CLI exit codes")
    return 0


if __name__ == "__main__":
    sys.exit(main())
