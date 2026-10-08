#!/usr/bin/env python3
"""Does this core candidate keep MeshWeaver.Plugins green — or does it DECLARE the break it causes?

    python3 .github/scripts/check-plugins-break-declaration.py \
        --main-verdict main.json [--counterpart-verdict counterpart.json] \
        --pr-body-file body.md [--counterpart-pr counterpart-pr.json] \
        --platform-version-base 3.1.0 --platform-version-head 3.1.0 \
        --epoch-base 1 --epoch-head 1
    python3 .github/scripts/check-plugins-break-declaration.py --self-test

The DECIDING half of the required `Dependent suites (MeshWeaver.Plugins)` gate (policy
`dependent-suites-affected-gate`; Doc/Architecture/CrossRepoPairGate § "The dependent's suites run
against the candidate"). MeshWeaver#2689: a core pull request must PROVE it does not break
MeshWeaver.Plugins before it merges. "Compatible" (the standing assumption, policy
`platform-semver-versioning`: the major moves only on a declared break) is a CLAIM; this script is
where the claim is measured, for exactly the Plugins realms the diff can reach — never all of them.

THE RULE.
  * MeshWeaver.Plugins' reachable suites pass against the candidate (verdict `success`) → GREEN.
  * They do not, and the failure is a MEASURED break in named realms (drift > 0) → GREEN ONLY when
    the PR body DECLARES it, on one line:

        Breaks-plugins: <realm>[, <realm>…] — <what breaks> — counterpart Systemorph/MeshWeaver.Plugins#<n>; semver: <major|minor|ceiling>

    and every one of these holds:
      - the declared realms cover EVERY realm the verdict names as failing (a break in an undeclared
        realm is an undeclared break);
      - the counterpart pull request is OPEN in Systemorph/MeshWeaver.Plugins (never a fork) — once it
        has merged, the default branch carries it and the main verdict itself must be green;
      - the counterpart is the one THIS run requested a measurement for (the body is read again at
        decision time; a declaration added later needs a push or a full re-run);
      - the counterpart's OWN run, against this same candidate and base, with Plugins at the
        counterpart's head, is `success` — the adaptation is proven against the change it adapts to;
      - the semver consequence is IN THE DIFF, not just in the sentence: `major` → `PlatformVersion`'s
        major increases; `minor` → its minor increases (same major); `ceiling` → the compatibility
        epoch in src/MeshWeaver.Compiler/platform-compatibility.json increases (which sets the
        previous epoch's ceiling — Doc/Architecture/PlatformCompatibilityLadder).
  * Anything else is RED: missing evidence, a refused selection, a verdict that never arrived — a
    declaration excuses a MEASURED break, never an absent measurement.

🚨 NO SKIP-TRAPDOOR. There is no "none" form and no label that turns this off. A diff that reaches
no Plugins realm passes because the verdict says "0 selected", with its reason — the count is the
evidence, not the absence of a run.

🔒 Prints only what the verdict already made public (realm names, counts, the run link).
"""
from __future__ import annotations

import argparse
import json
import re
import sys

PLUGINS = "Systemorph/MeshWeaver.Plugins"
SEMVER = ("major", "minor", "ceiling")
LINE = re.compile(
    r"^\s*Breaks-plugins:\s*(?P<realms>[^—\n]+?)\s+(?:—|--?)\s+(?P<what>.+?)\s+(?:—|--?)\s+"
    r"counterpart\s+(?P<repo>[\w.-]+/[\w.-]+)#(?P<pr>\d+)\s*;\s*semver:\s*(?P<semver>[a-z]+)\s*$",
    re.M | re.I)
MENTION = re.compile(r"^\s*Breaks-plugins\s*:", re.M | re.I)


def parse(body: str) -> tuple[dict | None, str | None]:
    """(the declaration, or None; a refusal sentence when a Breaks-plugins line is present but malformed)."""
    m = LINE.search(body or "")
    if not m:
        if MENTION.search(body or ""):
            return None, ("a `Breaks-plugins:` line is present but does not parse. The form is "
                          "`Breaks-plugins: <realm>[, <realm>…] — <what breaks> — counterpart "
                          f"{PLUGINS}#<n>; semver: <major|minor|ceiling>`")
        return None, None
    realms = sorted({r.strip() for r in m.group("realms").split(",") if r.strip()})
    semver = m.group("semver").lower()
    if m.group("repo").lower() != PLUGINS.lower():
        return None, f"the declared counterpart lives in {m.group('repo')}, not {PLUGINS}"
    if semver not in SEMVER:
        return None, f"semver `{semver}` is not one of {', '.join(SEMVER)}"
    if not m.group("what").strip():
        return None, "the declaration does not say WHAT breaks"
    return {"realms": realms, "what": m.group("what").strip(), "pr": int(m.group("pr")), "semver": semver}, None


def _ver(v: str | None) -> tuple[int, int] | None:
    m = re.match(r"^\s*(\d+)\.(\d+)", v or "")
    return (int(m.group(1)), int(m.group(2))) if m else None


def semver_holds(semver: str, pv_base: str | None, pv_head: str | None, ep_base, ep_head) -> tuple[bool, str]:
    b, h = _ver(pv_base), _ver(pv_head)
    if semver in ("major", "minor") and (b is None or h is None):
        return False, f"`PlatformVersion` could not be read at both ends (base {pv_base!r}, head {pv_head!r})"
    if semver == "major":
        return (h[0] > b[0], f"PlatformVersion {pv_base} → {pv_head}"
                + ("" if h[0] > b[0] else " — the declared MAJOR bump is not in the diff"))
    if semver == "minor":
        ok = h[0] == b[0] and h[1] > b[1]
        return ok, f"PlatformVersion {pv_base} → {pv_head}" + ("" if ok else " — the declared MINOR bump is not in the diff")
    try:
        ok = int(ep_head) > int(ep_base)
    except (TypeError, ValueError):
        return False, f"the compatibility epoch could not be read at both ends (base {ep_base!r}, head {ep_head!r})"
    return ok, f"compatibility epoch {ep_base} → {ep_head}" + ("" if ok else " — the declared CEILING (an epoch bump) is not in the diff")


PUBLIC_RUN = f"https://github.com/{PLUGINS}/actions/runs/"


def evidence_gap(verdict: dict) -> str | None:
    """Why this verdict cannot be taken as an answer — the same evidence the waiter demands."""
    counts = verdict.get("counts")
    if not (isinstance(verdict.get("summary"), str) and verdict["summary"].strip()):
        return "it carries no summary sentence"
    if not (isinstance(verdict.get("run"), str) and verdict["run"].startswith(PUBLIC_RUN)):
        return f"its run link {verdict.get('run')!r} is not a {PLUGINS} Actions run"
    if not (isinstance(counts, dict) and all(isinstance(counts.get(k), int) and not isinstance(counts.get(k), bool)
                                             for k in ("selected", "universe", "drift", "missingEvidence"))):
        return f"its counts {counts!r} do not carry the integers selected/universe/drift/missingEvidence"
    return None


def failing_realms(verdict: dict) -> tuple[list[str], list[str]]:
    """(realms with a MEASURED break, realms whose evidence is missing)."""
    realms = (verdict.get("counts") or {}).get("realms") or {}
    broken = sorted(r for r, v in realms.items() if isinstance(v, dict) and v.get("drift"))
    missing = sorted(r for r, v in realms.items() if isinstance(v, dict) and v.get("missingEvidence"))
    return broken, missing


def decide(main: dict | None, counterpart: dict | None, body: str, counterpart_pr: dict | None,
           pv_base=None, pv_head=None, ep_base=None, ep_head=None, dispatched: int | None = None) -> tuple[bool, list[str]]:
    """(passes, the lines to print). Pure — the self-test drives it."""
    out: list[str] = []
    decl, malformed = parse(body)
    if malformed:
        return False, [f"🚨 {malformed}"]
    if not isinstance(main, dict) or main.get("conclusion") not in ("success", "failure"):
        return False, ["🚨 no readable verdict from MeshWeaver.Plugins for this candidate — the claim is unmeasured"]
    gap = evidence_gap(main)
    if gap:
        return False, [f"🚨 the MeshWeaver.Plugins verdict for this candidate cannot be read as an answer: {gap}"]
    counts = main.get("counts") or {}
    realms = counts.get("realms") or {}
    sel = counts.get("selected")
    out.append(f"MeshWeaver.Plugins against this candidate: {main.get('conclusion')} — {main.get('summary')} ({main.get('run')})")
    out.append(f"selected {sel} suite(s) of {counts.get('universe')} in {len(realms)} realm(s); "
               f"{counts.get('notSelected', '?')} realm(s) not selected; "
               f"{counts.get('meshTestsUnmeasured', 0)} reachable Tests area(s) unmeasured")
    for r, v in sorted(realms.items()):
        out.append(f"  realm {r}: {v.get('conclusion')} ({v.get('suites')} suite(s), drift {v.get('drift')}, "
                   f"missing evidence {v.get('missingEvidence')}, Tests areas unmeasured {v.get('meshTestsUnmeasured')})")
    if main.get("conclusion") == "success":
        if decl:
            out.append(f"ℹ️ a break is declared ({', '.join(decl['realms'])}) but none was measured against Plugins' "
                       "default branch — the counterpart may already have landed. Nothing to excuse.")
        return True, out + ["✅ compatible: every reachable Plugins suite passes against this candidate"]
    broken, missing = failing_realms(main)
    if missing or not broken or counts.get("missingEvidence"):
        return False, out + ["🚨 the verdict is red without a MEASURED break in a named realm (missing evidence, a "
                             "refused selection, or no per-realm detail). A declaration can excuse a measured break, "
                             "never an absent measurement — fix what the linked run names, then re-run."]
    if decl is None:
        return False, out + [
            f"🚨 this change BREAKS MeshWeaver.Plugins realm(s) {', '.join(broken)}, and the PR body declares no break.",
            "   Either fix the behaviour change, or adapt Plugins and declare it on ONE line of the PR body:",
            f"   Breaks-plugins: {', '.join(broken)} — <what breaks> — counterpart {PLUGINS}#<n>; semver: <major|minor|ceiling>"]
    undeclared = sorted(set(broken) - set(decl["realms"]))
    if undeclared:
        return False, out + [f"🚨 the declared break names {', '.join(decl['realms'])}, but the change also breaks "
                             f"{', '.join(undeclared)} — every broken realm must be declared"]
    if dispatched is not None and dispatched != decl["pr"]:
        return False, out + [f"🚨 the body declares {PLUGINS}#{decl['pr']}, but this run requested a measurement for "
                             f"{('#' + str(dispatched)) if dispatched else 'no counterpart'} — the declaration changed after "
                             "the request. Push (or 'Re-run all jobs') so the request job reads the body again and "
                             "measures the declared counterpart; re-running only this job cannot prove it."]
    pr = counterpart_pr if isinstance(counterpart_pr, dict) else {}
    head = pr.get("head") if isinstance(pr.get("head"), dict) else {}
    repo = ((head.get("repo") or {}).get("full_name") or "") if isinstance(head.get("repo"), dict) else ""
    state, merged = pr.get("state"), bool(pr.get("merged"))
    if not pr:
        return False, out + [f"🚨 the declared counterpart {PLUGINS}#{decl['pr']} could not be read"]
    if pr.get("number") != decl["pr"]:
        return False, out + [f"🚨 the counterpart read is #{pr.get('number')}, not the declared #{decl['pr']}"]
    if merged:
        return False, out + [f"🚨 the counterpart {PLUGINS}#{decl['pr']} is already MERGED, so Plugins' default branch "
                             "carries it and a declaration has nothing left to excuse: 'Re-run all jobs' — the main "
                             "measurement then runs against a default branch that includes it, and must be green on its "
                             "own. If it is still red there, the counterpart does not fix this break."]
    if state != "open" or repo.lower() != PLUGINS.lower():
        return False, out + [f"🚨 the counterpart {PLUGINS}#{decl['pr']} is {state} with its head in "
                             f"{repo or '?'} — it must be OPEN in {PLUGINS} (never a fork), or merged"]
    if True:
        if not isinstance(counterpart, dict):
            return False, out + [f"🚨 no verdict for the counterpart's head against this candidate — its adaptation is unproven"]
        if counterpart.get("pluginsSha") != head.get("sha"):
            return False, out + [f"🚨 the counterpart verdict tested Plugins {str(counterpart.get('pluginsSha'))[:9]}, "
                                 f"not the counterpart's head {str(head.get('sha'))[:9]} — re-run this job"]
        cgap = evidence_gap(counterpart)
        if cgap:
            return False, out + [f"🚨 the counterpart verdict cannot be read as an answer: {cgap}"]
        if counterpart.get("conclusion") != "success":
            return False, out + [f"🚨 the counterpart {PLUGINS}#{decl['pr']} is NOT green against this candidate: "
                                 f"{counterpart.get('summary')} ({counterpart.get('run')})"]
        out.append(f"counterpart {PLUGINS}#{decl['pr']} @ {head.get('sha', '')[:9]}: success against this candidate — "
                   f"{counterpart.get('summary')} ({counterpart.get('run')})")
    ok, why = semver_holds(decl["semver"], pv_base, pv_head, ep_base, ep_head)
    if not ok:
        return False, out + [f"🚨 semver `{decl['semver']}` declared: {why}"]
    return True, out + [f"✅ DECLARED BREAK: realm(s) {', '.join(decl['realms'])} — {decl['what']} — counterpart "
                        f"{PLUGINS}#{decl['pr']} green against this candidate; semver {decl['semver']} ({why})"]


# ── self-test ─────────────────────────────────────────────────────────────────────────────────────
def self_test() -> int:
    failures = 0

    def check(name: str, ok: bool, detail: object = "") -> None:
        nonlocal failures
        print(("  ok   " if ok else "  FAIL ") + name + ("" if ok else f" — {detail}"))
        failures += 0 if ok else 1

    H = "f" * 40
    run = "https://github.com/Systemorph/MeshWeaver.Plugins/actions/runs/1"

    def verdict(conclusion, realms, plugins="a" * 40):
        return {"conclusion": conclusion, "summary": "s", "run": run, "pluginsSha": plugins,
                "counts": {"selected": 3, "universe": 84, "drift": sum(v.get("drift", 0) for v in realms.values()),
                           "missingEvidence": sum(v.get("missingEvidence", 0) for v in realms.values()),
                           "notSelected": 2, "meshTestsUnmeasured": 1, "realms": realms}}

    green = verdict("success", {"hosts": {"conclusion": "success", "suites": 3, "drift": 0, "missingEvidence": 0}})
    broke = verdict("failure", {"hosts": {"conclusion": "failure", "suites": 3, "drift": 2, "missingEvidence": 0},
                                "AI": {"conclusion": "success", "suites": 1, "drift": 0, "missingEvidence": 0}})
    gap = verdict("failure", {"hosts": {"conclusion": "failure", "suites": 3, "drift": 0, "missingEvidence": 1}})
    refused = {"conclusion": "failure", "summary": "the selection could not be computed", "run": run,
               "counts": {"selected": 0, "universe": 84, "drift": 0, "missingEvidence": 0}}
    body = (f"fix\n\nBreaks-plugins: hosts — synced partitions refuse non-system writes — counterpart "
            f"{PLUGINS}#3164; semver: minor\n")
    open_pr = {"number": 3164, "state": "open", "merged": False, "head": {"sha": H, "repo": {"full_name": PLUGINS}}}
    cp_green = verdict("success", {"hosts": {"conclusion": "success", "suites": 3, "drift": 0, "missingEvidence": 0}}, plugins=H)
    sv = dict(pv_base="3.1.0", pv_head="3.2.0", ep_base=1, ep_head=1)

    ok, _ = decide(green, None, "", None)
    check("a green verdict with no declaration passes", ok)
    ok, lines = decide(broke, None, "plain body", None)
    check("a MEASURED break with no declaration is RED, naming the realm and the line to write",
          not ok and any("hosts" in l and "Breaks-plugins:" in l for l in lines), lines)
    ok, lines = decide(broke, cp_green, body, open_pr, **sv)
    check("THE ESCAPE: declared realms ⊇ broken, counterpart open + green at its head, minor bump in the diff → green",
          ok, lines)
    ok, lines = decide(broke, cp_green, body.replace("hosts —", "AI —"), open_pr, **sv)
    check("a declaration that does not cover a broken realm is RED, naming it", not ok and any("hosts" in l for l in lines[-1:]), lines)
    ok, lines = decide(broke, verdict("failure", {}, plugins=H), body, open_pr, **sv)
    check("a counterpart that is NOT green against the candidate is RED", not ok, lines)
    ok, lines = decide(broke, cp_green | {"pluginsSha": "b" * 40}, body, open_pr, **sv)
    check("a counterpart verdict about a different Plugins head is RED", not ok, lines)
    ok, lines = decide(broke, None, body, open_pr, **sv)
    check("no counterpart verdict at all is RED (the adaptation is unproven)", not ok, lines)
    ok, lines = decide(broke, cp_green, body, {"number": 3164, "state": "open", "merged": False, "head": {"sha": H, "repo": {"full_name": "x/fork"}}}, **sv)
    check("a FORK counterpart is RED", not ok, lines)
    ok, lines = decide(broke, cp_green, body, {"number": 3164, "state": "closed", "merged": False, "head": {"sha": H, "repo": {"full_name": PLUGINS}}}, **sv)
    check("a CLOSED, unmerged counterpart is RED", not ok, lines)
    ok, lines = decide(broke, cp_green, body, open_pr, pv_base="3.1.0", pv_head="3.1.0", ep_base=1, ep_head=1)
    check("a declared minor bump that is NOT in the diff is RED", not ok and "MINOR" in lines[-1], lines)
    ok, _ = decide(broke, cp_green, body.replace("semver: minor", "semver: major"), open_pr, pv_base="3.1.0", pv_head="4.0.0")
    check("semver: major holds only when the major moved", ok)
    ok, _ = decide(broke, cp_green, body.replace("semver: minor", "semver: ceiling"), open_pr, pv_base="3.1.0", pv_head="3.1.0", ep_base=1, ep_head=2)
    check("semver: ceiling holds when the compatibility epoch moved", ok)
    ok, _ = decide(broke, cp_green, body.replace("semver: minor", "semver: ceiling"), open_pr, ep_base=1, ep_head=1)
    check("semver: ceiling without an epoch bump is RED", not ok)
    ok, lines = decide(gap, cp_green, body, open_pr, **sv)
    check("a declaration NEVER excuses missing evidence", not ok, lines)
    ok, lines = decide(refused, cp_green, body, open_pr, **sv)
    check("a declaration NEVER excuses a refused selection", not ok, lines)
    ok, lines = decide(None, None, body, None)
    check("no verdict at all is RED", not ok, lines)
    ok, lines = decide(green, None, "Breaks-plugins: hosts and some prose", None)
    check("a malformed Breaks-plugins line is RED with the form, even over a green verdict", not ok and "form" in lines[0], lines)
    ok, lines = decide(green, None, body.replace("semver: minor", "semver: patch"), None)
    check("semver must be major, minor or ceiling", not ok, lines)
    ok, lines = decide(green, None, body, None)
    check("a declared break that did not reproduce passes, and says so", ok and any("declared" in l for l in lines), lines)
    d, _ = parse("Breaks-plugins: AI, hosts -- x -- counterpart Systemorph/MeshWeaver.Plugins#12; semver: ceiling")
    check("ASCII dashes and several realms parse", d == {"realms": ["AI", "hosts"], "what": "x", "pr": 12, "semver": "ceiling"}, d)
    merged = {"number": 3164, "state": "closed", "merged": True, "head": {"sha": H, "repo": {"full_name": PLUGINS}},
              "base": {"ref": "main", "repo": {"default_branch": "main"}}}
    ok, lines = decide(broke, cp_green, body, merged, **sv)
    check("a MERGED counterpart excuses nothing: the default branch carries it and is still red", not ok, lines)
    ok, lines = decide(green | {"summary": ""}, None, "", None)
    check("a 'success' without its summary is RED (the waiter rejected it; the decision must too)", not ok, lines)
    ok, lines = decide(green | {"run": "https://example.com/x"}, None, "", None)
    check("a 'success' citing a non-Plugins run is RED", not ok, lines)
    ok, lines = decide(green | {"counts": {"realms": {}}}, None, "", None)
    check("a 'success' without integer counts is RED", not ok, lines)
    both = broke | {"counts": broke["counts"] | {"missingEvidence": 1}}
    ok, lines = decide(both, cp_green, body, open_pr, **sv)
    check("TOP-LEVEL missing evidence beside a measured drift is never excused", not ok, lines)
    ok, lines = decide(broke, cp_green, body, open_pr | {"number": 9}, **sv)
    check("a counterpart PR that is not the declared number is RED", not ok, lines)
    ok, lines = decide(broke, cp_green, body, open_pr, dispatched=0, **sv)
    check("a declaration added AFTER the request (nothing measured for it) is RED, saying push or re-run all",
          not ok and "Re-run all jobs" in lines[-1], lines)
    ok, _ = decide(broke, cp_green, body, open_pr, dispatched=3164, **sv)
    check("the declared counterpart the request measured passes", ok)
    ok, lines = decide(broke, cp_green | {"summary": ""}, body, open_pr, **sv)
    check("a counterpart verdict without its evidence is RED", not ok, lines)
    print(f"check-plugins-break-declaration self-test: {failures} failure(s)")
    return 1 if failures else 0


def _load(path: str | None):
    if not path:
        return None
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    except (OSError, ValueError):
        return None


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0])
    ap.add_argument("--self-test", action="store_true")
    ap.add_argument("--main-verdict")
    ap.add_argument("--counterpart-verdict")
    ap.add_argument("--pr-body-file")
    ap.add_argument("--counterpart-pr")
    ap.add_argument("--platform-version-base")
    ap.add_argument("--platform-version-head")
    ap.add_argument("--epoch-base")
    ap.add_argument("--epoch-head")
    ap.add_argument("--dispatched-counterpart", default=None,
                    help="the counterpart PR number the request job measured ('' = none)")
    ap.add_argument("--print-counterpart", action="store_true",
                    help="print the declared counterpart PR number (empty when none) and exit")
    a = ap.parse_args()
    if a.self_test:
        return self_test()
    body = ""
    if a.pr_body_file:
        with open(a.pr_body_file, encoding="utf-8") as f:
            body = f.read()
    if a.print_counterpart:
        d, bad = parse(body)
        if bad:
            print(f"::error::{bad}")
            return 1
        print(d["pr"] if d else "")
        return 0
    disp = None if a.dispatched_counterpart is None else (int(a.dispatched_counterpart) if a.dispatched_counterpart.strip() else 0)
    ok, lines = decide(_load(a.main_verdict), _load(a.counterpart_verdict), body, _load(a.counterpart_pr),
                       a.platform_version_base, a.platform_version_head, a.epoch_base, a.epoch_head, dispatched=disp)
    for line in lines:
        print(line if ok or not line.startswith("🚨") else f"::error::{line}")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
