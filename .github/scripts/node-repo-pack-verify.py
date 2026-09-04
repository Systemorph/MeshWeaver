#!/usr/bin/env python3
"""node-repo-pack-verify.py — assert that EXACTLY the selected module bundles were built.

    "we can estimate the required count and validate it"

A dynamic matrix cannot be a fixed list of required status checks: the per-module contexts appear
and disappear with the diff, so requiring one means requiring a context that will legitimately
never report. That is not a detail — it is the same trap as an `if:` on a gate, and it is how a
gate ends up not required at all and PRs merge past it.

The answer is to make the SELECTION itself checkable: `node-repo-scope.py` computes how many
bundles must be built, every pack job drops a RECEIPT, and this script — one final job, one
STABLE context, present on every run whatever the diff — asserts the two agree and names the
discrepancy. That is CI invariant #4 (a final job that fails iff a real gate skipped for an unsafe
reason) applied to a matrix that is allowed to change size, and it is what makes narrowing safe to
require.

🚨 THE FAILURE IT EXISTS TO CATCH IS A SKIP. GitHub renders a skipped matrix leg with the same
tick as a passed one, so "this module was never packed" and "this module packed fine" look
identical on the checks wall. A receipt is positive evidence; its absence is the finding.

    node-repo-pack-verify.py --expected "MeshWeaver.AI MeshWeaver.Mcp" \\
                             --receipts "$RUNNER_TEMP/receipts" --pack-result success
    node-repo-pack-verify.py --self-test

EXIT 0 only when: every expected module has a well-formed receipt, no unexpected receipt exists,
and the pack job's own result agrees with the expectation (an empty selection is the ONLY case in
which the pack job may be `skipped`).

🚨 ARTIFACTS ARE RUN-WIDE, RECEIPTS ARE PER CALL. A repo may call the module-pack workflow TWICE in
one run (Plugins #932 splits its 31 bundles into the floor the gates compose and the rest), and
both calls' receipts land under the same `module-pack-receipt-*` pattern — so each verifier sees
the other call's receipts and reads them as "built but never selected". `--declared` is the
caller's own matrix (`inputs.modules`): a receipt for a module this call did not declare belongs
to a sibling call and is set aside, NAMED, and never counted. Within the declared set the
accounting is exactly as strict as before.

🚨 …AND A NAME-ONLY SEPARATION IS NOT ENOUGH. `--declared` tells two calls apart only while their
matrices are DISJOINT; the moment both declare the same module — one `always-modules` entry that
also appears in the other call's list is all it takes — one call's evidence answers the other's
question, and the answer a REQUIRED gate composes on (`bundles-built`) becomes truthy from
evidence this call never produced (Plugins#1077). So the producer now STAMPS its lane into every
marker and receipt and `--lane` checks it: evidence this call cannot attribute to itself is set
aside and NAMED, and a SELECTED module left without attributable evidence reads as NOT BUILT.
Fail closed — zero markers, an unstamped marker and a foreign marker are all `false`, never a
silent true.

🚨 AND THE MARKER MUST ASSERT WHAT THE CALLER ACTUALLY NEEDS. `bundles-built` is consumed by gates
that only COMPOSE the bundles, so it has to mean "the set is complete and USABLE". A marker saying
only "an artifact was uploaded" leaves "present but uncomposable" reading as true, so the marker
carries the bundle it attests and the closure evidence its build resolved, and a marker missing
either is refused here rather than trusted because of where its step happened to sit in the job.
🚨 None of this touches the module's TESTS: the marker is still dropped BEFORE them (#2710,
Plugins#937) so a red suite cannot read as "bundle missing" to a gate that only composes.

🚨 …BUT THE SUITE ITSELF IS ACCOUNTED FOR HERE TOO, because it no longer always runs in the pack
job. On a run that PUBLISHES, the suite stays inline in `pack` so a red suite can never publish;
on a run that does not, it runs in a `tests` job beside the pack matrix, so a caller whose gates
consume only the bundle artifact does not wait 12.7 minutes for a verdict they never read
(MeshWeaver.Plugins run 33656010754). Two mutually exclusive conditions can BOTH be false, and a
suite that ran in neither lane renders exactly like one that passed — so each pack receipt records
which lane OWNS its suite (`tests`: `none` | `inline` | `lane`), the tests lane drops a receipt of
its own, and `--test-receipts` / `--tests-result` make the pairing checkable: a delegated module
with no test receipt, a test receipt for a module that delegated nothing, and a tests lane that
did not succeed are each RED, under this job's own unchanged context name. `bundles-built` is
deliberately untouched by all of it.

🚨 AND ONE RUN STATES ONE FRAMEWORK IDENTITY (#3310). Every guard on the identity anchor is
CONFIG-level — it asserts the lane's text or the anchor's location — so none of them can see a run
END UP stating a different identity per module, which is what any per-module property reaching the
anchor build produces, silently and green. The identity names the PLATFORM build and a lane pins
ONE platform, so this job — the only one that sees the whole wave — reads the value each receipt
carries and refuses a lane whose bundles disagree. A receipt that states NO identity was written by
a lane older than this script (a caller pins the LANE and `platform-ref` separately, on purpose)
and is a DISTINCT, NAMED refusal carrying the remedy: "absent" must never read as "agrees", which is
the same mistake as a skipped gate reading as a passed one.
"""
from __future__ import annotations

import argparse
import json
import os
import sys
from pathlib import Path


def read_receipts(directory: Path) -> tuple[dict[str, dict], list[str]]:
    """{module → receipt} plus the files that could not be read as one."""
    found: dict[str, dict] = {}
    broken: list[str] = []
    if not directory.is_dir():
        return found, broken
    for path in sorted(directory.rglob("*.json")):
        try:
            doc = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            broken.append(path.name)
            continue
        module = doc.get("module") if isinstance(doc, dict) else None
        if not isinstance(module, str) or not module:
            broken.append(path.name)
            continue
        # The receipt must be self-describing: a file called X.json claiming to be module Y means
        # the matrix and the artifact names have drifted, and the count would then prove nothing.
        if path.stem != module:
            broken.append(f"{path.name} (claims module '{module}')")
            continue
        found[module] = doc
    return found, broken


def _receipt_stem(broken_entry: str) -> str:
    """The module a broken-receipt entry is FOR — `X.json` or `X.json (claims module 'Y')` ⇒ X."""
    return broken_entry.split(".json", 1)[0]


def verify(expected: list[str], receipts: dict[str, dict], broken: list[str],
           pack_result: str, receipts_dir_exists: bool,
           scope: str = "", declared: set[str] | None = None,
           lane: str = "") -> tuple[int, list[str], list[str]]:
    """(exit code, error lines, note lines). `declared` is the caller's own matrix; when given,
    receipts for modules outside it are a SIBLING call's (artifacts are run-wide) and are set
    aside rather than counted. `None` keeps the pre-#932 behaviour: every receipt is this call's.
    `lane` is this call's own lane key: a receipt stamped with any other lane — or with none —
    is a sibling's STRUCTURALLY, whatever its module is called, and is set aside the same way."""
    errors: list[str] = []
    notes: list[str] = []
    want = set(expected)

    # 🚨 STRUCTURAL ATTRIBUTION FIRST. `--declared` separates two calls in one run by module NAME,
    # which holds only while their matrices are disjoint; the stamp the producer wrote holds
    # always. Applied before anything is counted, so a foreign receipt can neither satisfy a
    # selection nor be reported as "built but never selected".
    if lane:
        foreign = sorted(m for m, r in receipts.items() if r.get("lane") != lane)
        if foreign:
            receipts = {m: r for m, r in receipts.items() if r.get("lane") == lane}
            notes.append(f"{len(foreign)} receipt(s) carry another lane's stamp (this call is "
                         f"'{lane}') and are not this call's to count: " + ", ".join(foreign))

    if declared is not None:
        # 🚨 The selection is computed FROM the declared matrix, so a selected module the caller
        # never declared is not a sibling's — it is the selector and the matrix disagreeing.
        undeclared = sorted(want - declared)
        if undeclared:
            return 1, [f"{len(undeclared)} selected module(s) are not in the caller's own "
                       f"`modules:` list — the selection and the matrix disagree: "
                       + ", ".join(undeclared)], []
        # A sibling's receipt is set aside whether it parsed or not: a corrupt receipt for a module
        # this call never declared is the sibling's finding, not this call's.
        sibling = sorted(m for m in receipts if m not in declared)
        sibling_broken = sorted(b for b in broken if _receipt_stem(b) not in declared)
        if sibling or sibling_broken:
            receipts = {m: r for m, r in receipts.items() if m in declared}
            broken = [b for b in broken if _receipt_stem(b) in declared]
            notes.append(f"{len(sibling) + len(sibling_broken)} receipt(s) belong to a sibling "
                         f"module-pack call in this run (artifacts are run-wide) and are not this "
                         f"call's to count: " + ", ".join(sibling + sibling_broken))
    got = set(receipts)

    if broken:
        errors.append(f"{len(broken)} receipt file(s) could not be read as a receipt: "
                      + ", ".join(broken))

    # 🚨 "BUILD EVERYTHING" AND "EVERYTHING IS NOTHING" CANNOT BOTH BE TRUE. A `full` scope with
    # an empty expectation is an internal contradiction, and the shape it takes is a green tick
    # over a lane that built nothing — a bad --root, an empty matrix input, a tree that could not
    # be listed. The selector refuses it now; this is the second lock, because the two are
    # computed in different jobs and only one of them has to be wrong.
    if scope == "full" and not want:
        return 1, [f"the scope is '{scope}' but the selection is EMPTY — 'build everything' and "
                   "'everything is nothing' cannot both be true. Something upstream (a --root "
                   "that is not the checkout, an empty matrix input) produced a contradiction, "
                   "and nothing was built."], []

    if not want:
        # An empty selection is legitimate (a diff reaching no package and no module project) —
        # GitHub skips a job whose matrix is empty. What must NOT happen is a bundle being built
        # that the selection did not name.
        if pack_result not in ("skipped", "success"):
            errors.append(f"the selection named ZERO module bundles, but the pack job reported "
                          f"'{pack_result}' — a job that should not have run did something.")
        if got:
            errors.append("the selection named ZERO module bundles, yet receipts arrived for: "
                          + ", ".join(sorted(got)))
        if not errors:
            notes.append("no module bundle is reachable from this diff — nothing was packed, and "
                         "nothing was expected to be.")
        return (1 if errors else 0), errors, notes

    # 🚨 The skip. `needs.pack.result == 'skipped'` with a NON-empty selection means the whole
    # fan-out silently did not happen — the exact shape that renders green.
    if pack_result == "skipped":
        errors.append(f"the selection named {len(want)} module bundle(s) and the pack job did NOT "
                      f"RUN AT ALL (result: skipped). A skipped job renders with the same tick as "
                      f"a passed one; nothing was built and nothing would have said so.")
    elif pack_result != "success":
        errors.append(f"the pack job reported '{pack_result}' — read that job for the real "
                      f"failure; the receipt accounting below is a consequence, not the cause.")

    if not receipts_dir_exists:
        errors.append("no receipt directory arrived at all — every pack job failed before its "
                      "receipt, or the artifact names drifted from the matrix.")

    missing = sorted(want - got)
    extra = sorted(got - want)
    if missing:
        errors.append(f"{len(missing)} selected module bundle(s) produced NO receipt — they were "
                      f"never built: " + ", ".join(missing))
    if extra:
        errors.append(f"{len(extra)} receipt(s) arrived for module(s) the selection did not name — "
                      f"the matrix and the selection disagree: " + ", ".join(extra))

    if not errors:
        published = sorted(m for m, r in receipts.items() if r.get("published") is True)
        notes.append(f"{len(want)} of {len(want)} selected module bundle(s) built"
                     + (f"; {len(published)} published to the registry" if published
                        else "; nothing published (not a trunk/release run)"))
    return (1 if errors else 0), errors, notes


MARKER_CLAIMS = ("bundle", "closure")
"""What a built marker must ASSERT, not merely imply from where its step sits in the job.

`bundle` — the bundle file the upload step took; `closure` — how the build resolved this
module's private closure (the `<Module>.closure.txt` manifest on the container path, the
publish `deps.json` on the sdk path). `bundles-built` is what a REQUIRED gate composes bundles
on, so it must mean "complete AND usable": a marker that attests only "an artifact exists"
lets present-but-uncomposable read as true, which is the class Plugins#1077 sits in."""


def read_markers(directory: Path, lane: str = "") -> tuple[set[str], list[str]]:
    """(modules this call can attribute a complete built marker to, the markers it REFUSED and why).

    🚨 EVERY REFUSAL IS FAIL-CLOSED. A marker that cannot be parsed, does not name its own file,
    carries another call's lane stamp (or none at all), or does not record what it attests, is not
    a marker — the module then reads as NOT built. A truthy answer derived from absent or foreign
    evidence is the whole defect, so the refusals are returned and NAMED rather than dropped."""
    found: set[str] = set()
    refused: list[str] = []
    if not directory.is_dir():
        return found, refused
    for path in sorted(directory.rglob("*.json")):
        try:
            doc = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            refused.append(f"{path.name} (unreadable as JSON)")
            continue
        if not isinstance(doc, dict):
            refused.append(f"{path.name} (not a marker object)")
            continue
        module = doc.get("module")
        if not isinstance(module, str) or not module or path.stem != module:
            refused.append(f"{path.name} (claims module {doc.get('module')!r})")
            continue
        stamp = doc.get("lane")
        if lane and stamp != lane:
            refused.append(f"{module} (lane {stamp!r}, this call is {lane!r})")
            continue
        absent = [claim for claim in MARKER_CLAIMS
                  if not isinstance(doc.get(claim), str) or not doc.get(claim)]
        if absent:
            refused.append(f"{module} (records no " + ", no ".join(absent) + ")")
            continue
        found.add(module)
    return found, refused


def bundles_built(expected: list[str], built: set[str], declared: set[str] | None,
                  refused: list[str] | None = None) -> tuple[bool, str]:
    """(every selected bundle exists as a COMPOSABLE artifact of THIS call, one line saying so).

    Independent of the receipts and of the pack job's result on purpose: this is the claim a
    caller's gate depends on when it only COMPOSES the bundles, and a red suite or a failed
    hand-over must not turn it false (#2710, Plugins#937). A sibling call's markers are set aside
    the same way as its receipts — by lane stamp in `read_markers`, and by name here."""
    want = set(expected)
    if declared is not None:
        built = {m for m in built if m in declared}
    refused = list(refused or [])
    aside = (f" ({len(refused)} marker(s) refused: " + "; ".join(refused) + ")") if refused else ""
    missing = sorted(want - built)
    if missing:
        return False, (f"bundles-built: false — {len(missing)} of {len(want)} selected bundle(s) "
                       f"have no built marker this call can attribute to itself: "
                       + ", ".join(missing) + aside)
    return True, (f"bundles-built: true — {len(want)} of {len(want)} selected bundle(s) exist as "
                  f"this call's artifacts, each recording the bundle and the closure it was "
                  f"composed from" + aside)


def own_receipts(receipts: dict[str, dict], lane: str,
                 declared: set[str] | None) -> dict[str, dict]:
    """The receipts THIS call produced — the same two filters `verify` applies internally, exposed
    so the test-lane accounting below sees exactly the set the count gate counted."""
    out = receipts
    if lane:
        out = {m: r for m, r in out.items() if r.get("lane") == lane}
    if declared is not None:
        out = {m: r for m, r in out.items() if m in declared}
    return out


def test_lane(receipts: dict[str, dict], test_receipts: dict[str, dict], broken: list[str],
              tests_result: str) -> tuple[list[str], list[str]]:
    """(errors, notes) — did every suite this call DELEGATED actually run?

    🚨 THE FAILURE THIS EXISTS TO CATCH IS "NEITHER LANE RAN IT". The suite runs inline in `pack`
    when the call publishes and in the `tests` job when it does not; those two `if:` conditions are
    complements today and one edit away from both being false, at which point nothing is red and
    nothing is missing — a suite simply stops existing. So the answer is not read off a condition:
    the pack leg WRITES which lane owns its suite and the tests lane WRITES that it ran one, and
    this pairs them.

    `tests_result` is `needs.tests.result`. A lane that FAILED is red here as well as on its own
    job, so the lane's one stable context still goes red on a red suite — which is the whole
    reason moving the suite off the critical path does not move it out of the gate. An empty
    string means the caller did not ask for this accounting (an older workflow against a newer
    script) and the check stands down, saying so."""
    errors: list[str] = []
    notes: list[str] = []
    if not tests_result:
        return errors, notes

    delegated = sorted(m for m, r in receipts.items() if r.get("tests") == "lane")
    ran = sorted(test_receipts)

    if tests_result not in ("success", "skipped"):
        errors.append(f"the module TEST lane reported '{tests_result}' — the suites run beside the "
                      f"pack matrix on a non-publishing run, and a red suite must red this context "
                      f"exactly as it did when it ran inside the pack job. Read that job.")
    if broken:
        errors.append(f"{len(broken)} test receipt file(s) could not be read as a receipt: "
                      + ", ".join(broken))
    if delegated and tests_result == "skipped":
        errors.append(f"{len(delegated)} module(s) delegated their suite to the tests lane and that "
                      f"lane did NOT RUN AT ALL (result: skipped) — the suite ran in neither lane, "
                      f"which renders exactly like one that passed: " + ", ".join(delegated))
    missing = sorted(set(delegated) - set(ran))
    if missing and tests_result != "skipped":
        errors.append(f"{len(missing)} module(s) delegated their suite to the tests lane but no "
                      f"test receipt arrived for them — the suite did not get to the end: "
                      + ", ".join(missing))
    # A test receipt for a module whose OWN pack receipt says the suite belongs elsewhere: the two
    # halves of the switch disagree. Modules with no pack receipt at all are not judged here — that
    # is already the count gate's finding, and blaming the suite for it would misname the cause.
    disagree = sorted(m for m in ran
                      if m in receipts and receipts[m].get("tests") != "lane")
    if disagree:
        errors.append(f"{len(disagree)} module(s) ran a suite in the tests lane while their own "
                      f"pack receipt says the suite was "
                      + ", ".join(f"{m} ({receipts[m].get('tests')!r})" for m in disagree)
                      + " — the two halves of the switch disagree, so one of them is wrong about "
                        "where the verdict came from.")
    if not errors:
        inline = sorted(m for m, r in receipts.items() if r.get("tests") == "inline")
        none = sorted(m for m, r in receipts.items() if r.get("tests") == "none")
        notes.append(f"suites: {len(delegated)} ran in the tests lane beside the pack matrix, "
                     f"{len(inline)} inline in the pack job (this call publishes, so a red suite "
                     f"cannot publish), {len(none)} not owed a run by the selection")
    return errors, notes


IDENTITY_FIELD = "frameworkIdentity"
"""The framework identity the receipt's bundle STATES, read off the packed manifest's
`frameworkMvid` by the leg that produced the bytes (build or reuse alike)."""


def identity_agreement(receipts: dict[str, dict]) -> tuple[list[str], list[str]]:
    """(errors, notes) — does every bundle this lane packed state the SAME framework identity?

    🚨 THIS IS THE ONLY OUTCOME-LEVEL CHECK ON THE IDENTITY, and the failure it catches is SILENT
    (#3310). The identity names the PLATFORM build, and a lane pins ONE platform — so a lane whose
    bundles state two identities has produced bundles no consumer can compare against a single
    platform, and it did so while every existing guard stayed green. Those guards are CONFIG-level:
    `ModuleIdentityPublishGuard` asserts the lane's TEXT (the anchor is built from the pinned
    platform source and that command line carries no `-p:Version`) and `ModulePackCommand` asserts
    the anchor's LOCATION. Neither can see the run END UP stating more than one value — a second
    per-module property reaching the anchor build (`-p:InformationalVersion`, `-p:SourceRevisionId`,
    a `Directory.Build.props` edit keyed on the module), a future arm deriving the anchor a third
    way, or a caller passing `--framework-mvid` per module would leave them all passing while the
    bundles pack, the manifests carry well-formed 32-hex values, and every non-blank assertion
    downstream succeeds. Measured twice already: #3211's `(unrecorded)` fleet, then run 33874892203
    packing TWO identities for one platform.

    🚨 AND AN ABSENT IDENTITY IS A NAMED REFUSAL, NEVER "AGREES". A receipt carrying no identity
    was written by a module-pack lane older than this verifier — a caller may pin the LANE
    (`uses: …/node-repo-module-pack.yml@<sha>`) and `platform-ref` (which is where this script is
    read from) separately and on purpose. Reading that silence as agreement would make this gate
    answer "one identity" from a set that stated none, which is exactly the class of mistake the
    gate exists to remove, so it is refused and NAMED with the remedy.

    Scoped to THIS call's receipts (lane stamp + declared matrix, applied by `own_receipts`): a repo
    calling the lane twice pins one platform PER CALL, so two calls may legitimately differ."""
    errors: list[str] = []
    notes: list[str] = []
    if not receipts:
        return errors, notes

    stated: dict[str, str] = {}
    silent: list[str] = []
    for module in sorted(receipts):
        value = receipts[module].get(IDENTITY_FIELD)
        value = value.strip() if isinstance(value, str) else ""
        if value:
            stated[module] = value
        else:
            silent.append(module)

    if silent:
        errors.append(
            f"{len(silent)} receipt(s) state NO framework identity, so this lane cannot say whether "
            f"its bundles agree — and an unstated identity must never read as agreement (#3310): "
            + ", ".join(silent)
            + ". The receipts were written by a module-pack lane older than this verifier: move the "
              "caller's `uses: Systemorph/MeshWeaver/.github/workflows/node-repo-module-pack.yml@<sha>` "
              "to a commit that records `frameworkIdentity` (the same commit as its `platform-ref` "
              "is the safe choice — the two are pinned separately).")

    distinct = sorted(set(stated.values()))
    if len(distinct) > 1:
        by_identity = {
            identity: sorted(m for m, v in stated.items() if v == identity)
            for identity in distinct
        }
        errors.append(
            f"this lane's bundles state {len(distinct)} DIFFERENT framework identities, and a lane "
            f"pins ONE platform — so at least one of them names a platform build that does not "
            f"exist, which every consumer then fails to match on every reconcile, forever (#3310, "
            f"#3154): "
            + "; ".join(f"{identity} ← " + ", ".join(by_identity[identity]) for identity in distinct)
            + ". Something per-module is reaching the identity anchor (a `-p:Version`-shaped "
              "property on the anchor build, an arm deriving the anchor from the module's own "
              "output, or a caller passing --framework-mvid per module).")

    if not errors:
        notes.append(f"all {len(stated)} bundle(s) this call packed state ONE framework identity: "
                     f"{distinct[0]}")
    return errors, notes


def parse_declared(text: str) -> set[str] | None:
    """The caller's `modules:` input as it reaches the workflow — a JSON list of entries carrying
    `module`, a JSON list of names, or a whitespace-separated list. Empty ⇒ None (no filter)."""
    text = (text or "").strip()
    if not text:
        return None
    try:
        doc = json.loads(text)
    except json.JSONDecodeError:
        return {m for m in text.split() if m}
    if isinstance(doc, list):
        out: set[str] = set()
        for entry in doc:
            if isinstance(entry, dict) and isinstance(entry.get("module"), str):
                out.add(entry["module"])
            elif isinstance(entry, str) and entry:
                out.add(entry)
        return out
    return None


# ── self-test ────────────────────────────────────────────────────────────────────────────────
# 🚨 A COUNT GATE THAT CANNOT FAIL IS A TICK, NOT A GATE. Every case below is a MUTATION of a
# green run — remove a receipt, add one, skip the job, corrupt a file — and each must turn red and
# NAME what it found. Without these, "the count matched" would be an assertion nobody has ever
# seen fail.

def self_test() -> int:
    import tempfile
    failures: list[str] = []

    def check(name: str, ok: bool, detail: str = "") -> None:
        print(f"  {'✓' if ok else '✗'} {name}{'' if ok else ': ' + detail}")
        if not ok:
            failures.append(name)

    def run(directory: Path, expected: list[str], result: str = "success", scope: str = "",
            declared: set[str] | None = None, lane: str = ""):
        receipts, broken = read_receipts(directory)
        return verify(expected, receipts, broken, result, directory.is_dir(), scope, declared, lane)

    def marker(module: str, lane: str = "floor-abc123", **overrides) -> str:
        doc = {"lane": lane, "package": module.split(".")[-1], "module": module,
               "version": "1.2.3", "compiler": "container",
               "bundle": f"{module}.1.2.3.module.nupkg",
               "closure": f"{module}.closure.txt (2 entries)"}
        doc.update(overrides)
        return json.dumps({k: v for k, v in doc.items() if v is not None})

    with tempfile.TemporaryDirectory() as tmp:
        d = Path(tmp) / "receipts"
        d.mkdir()
        three = ["MeshWeaver.AI", "MeshWeaver.Mcp", "MeshWeaver.Teams"]
        for m in three:
            (d / f"{m}.json").write_text(json.dumps(
                {"package": m.split(".")[-1], "module": m, "version": "1.2.3",
                 "published": False}), encoding="utf-8")

        print("the green run this gate is a mutation of:")
        code, errors, notes = run(d, three)
        check("every selected bundle has a receipt ⇒ pass", code == 0, f"{errors}")
        check("and it SAYS what it verified", bool(notes), f"{notes}")

        print("mutations — each must turn RED and name what it found:")
        removed = d / "MeshWeaver.Mcp.json"
        keep = removed.read_text(encoding="utf-8")
        removed.unlink()
        code, errors, _ = run(d, three)
        check("a pack job that SKIPPED (its receipt is gone) fails, naming the module",
              code == 1 and any("MeshWeaver.Mcp" in e and "NO receipt" in e for e in errors),
              f"{errors}")
        removed.write_text(keep, encoding="utf-8")

        (d / "MeshWeaver.Ghost.json").write_text(json.dumps(
            {"package": "Ghost", "module": "MeshWeaver.Ghost"}), encoding="utf-8")
        code, errors, _ = run(d, three)
        check("a bundle built that the selection did NOT name fails, naming it",
              code == 1 and any("MeshWeaver.Ghost" in e and "did not name" in e for e in errors),
              f"{errors}")
        # 🚨 Two module-pack calls in ONE run share the artifact namespace (Plugins #932), so the
        # same stray receipt is a SIBLING's when this call never declared the module — set aside
        # and named, never counted…
        code, errors, notes = run(d, three, declared=set(three))
        check("…but a receipt for a module this call did NOT declare is a sibling call's ⇒ pass, named",
              code == 0 and any("sibling" in n and "MeshWeaver.Ghost" in n for n in notes),
              f"{errors} {notes}")
        # …while a DECLARED module that was built without being selected stays red.
        code, errors, _ = run(d, three, declared=set(three) | {"MeshWeaver.Ghost"})
        check("a declared module built without being selected is still the matrix disagreeing",
              code == 1 and any("MeshWeaver.Ghost" in e and "did not name" in e for e in errors),
              f"{errors}")
        code, errors, _ = run(d, three + ["MeshWeaver.Ghost"], declared=set(three))
        check("a SELECTED module the caller never declared is refused (selector ≠ matrix)",
              code == 1 and any("not in the caller" in e and "MeshWeaver.Ghost" in e for e in errors),
              f"{errors}")
        (d / "MeshWeaver.Ghost.json").write_text("{ not json", encoding="utf-8")
        code, errors, notes = run(d, three, declared=set(three))
        check("a CORRUPT receipt of a sibling call is the sibling's finding ⇒ pass, named",
              code == 0 and any("sibling" in n and "MeshWeaver.Ghost" in n for n in notes),
              f"{errors} {notes}")
        code, errors, _ = run(d, three, declared=set(three) | {"MeshWeaver.Ghost"})
        check("…while a corrupt receipt of a DECLARED module still fails",
              code == 1 and any("could not be read" in e for e in errors), f"{errors}")
        (d / "MeshWeaver.Ghost.json").unlink()
        check("--declared accepts the workflow's JSON entries, plain names, and nothing",
              parse_declared('[{"package": "AI", "module": "MeshWeaver.AI"}, "MeshWeaver.Mcp"]')
              == {"MeshWeaver.AI", "MeshWeaver.Mcp"}
              and parse_declared("MeshWeaver.AI MeshWeaver.Mcp") == {"MeshWeaver.AI", "MeshWeaver.Mcp"}
              and parse_declared("  ") is None)

        code, errors, _ = run(d, three, "skipped")
        check("a NON-EMPTY selection whose pack job never ran fails loudly",
              code == 1 and any("did NOT" in e and "RUN AT ALL" in e for e in errors), f"{errors}")
        code, errors, _ = run(d, three, "failure")
        check("a failed pack job is reported as the CAUSE, not as a count discrepancy",
              code == 1 and any("read that job" in e for e in errors), f"{errors}")

        (d / "MeshWeaver.AI.json").write_text("{ not json", encoding="utf-8")
        code, errors, _ = run(d, three)
        check("a corrupt receipt is not a receipt (and the module then reads as unbuilt)",
              code == 1 and any("could not be read" in e for e in errors), f"{errors}")
        (d / "MeshWeaver.AI.json").write_text(json.dumps(
            {"module": "MeshWeaver.SomethingElse"}), encoding="utf-8")
        code, errors, _ = run(d, three)
        check("a receipt whose filename and module disagree is refused",
              code == 1 and any("claims module" in e for e in errors), f"{errors}")
        (d / "MeshWeaver.AI.json").write_text(json.dumps(
            {"package": "AI", "module": "MeshWeaver.AI", "published": True}), encoding="utf-8")

        code, errors, _ = run(Path(tmp) / "nowhere", three)
        check("no receipts at all is a FAILURE, never a pass",
              code == 1 and any("no receipt directory" in e for e in errors), f"{errors}")

        # 🚨 The receipts' lane half: `--declared` separates two calls by NAME, which holds only
        # while their matrices are disjoint. The stamp holds always (Plugins#1077).
        print("the receipts' LANE stamp — the separation that survives two calls declaring the "
              "same module:")
        (d / "MeshWeaver.AI.json").write_text(json.dumps(
            {"lane": "floor-abc123", "package": "AI", "module": "MeshWeaver.AI",
             "published": True}), encoding="utf-8")
        for m in three[1:]:
            (d / f"{m}.json").write_text(json.dumps(
                {"lane": "floor-abc123", "package": m.split(".")[-1], "module": m,
                 "published": False}), encoding="utf-8")
        code, errors, notes = run(d, three, lane="floor-abc123")
        check("this call's own stamped receipts still add up", code == 0, f"{errors}")
        # A module BOTH calls declare — the case --declared cannot see — must not be donated.
        (d / "MeshWeaver.Mcp.json").write_text(json.dumps(
            {"lane": "rest-def456", "package": "Mcp", "module": "MeshWeaver.Mcp"}),
            encoding="utf-8")
        code, errors, notes = run(d, three, declared=set(three), lane="floor-abc123")
        check("a receipt this call DECLARED but a SIBLING LANE produced is set aside and the "
              "module then reads as never built",
              code == 1
              and any("another lane's stamp" in n and "MeshWeaver.Mcp" in n for n in notes)
              and any("NO receipt" in e and "MeshWeaver.Mcp" in e for e in errors),
              f"{errors} {notes}")
        (d / "MeshWeaver.Mcp.json").write_text(json.dumps(
            {"lane": "floor-abc123", "package": "Mcp", "module": "MeshWeaver.Mcp"}),
            encoding="utf-8")

        print("the built markers — 'complete AND composable, from THIS lane', still surviving a "
              "red suite:")
        b = Path(tmp) / "built"
        b.mkdir()
        for m in three:
            (b / f"{m}.json").write_text(marker(m), encoding="utf-8")
        found, refused = read_markers(b, "floor-abc123")
        ok, line = bundles_built(three, found, None, refused)
        check("every selected bundle has a complete marker ⇒ bundles-built true",
              ok and "true" in line and not refused, line)
        code, errors, _ = run(d, three, "failure", lane="floor-abc123")
        found, refused = read_markers(b, "floor-abc123")
        ok, _ = bundles_built(three, found, None, refused)
        check("…and a FAILED pack job (its suite went red) keeps bundles-built TRUE while the "
              "lane itself still fails — #2710 is untouched", ok and code == 1, f"{errors}")

        print("marker mutations — each must make bundles-built FALSE and name what it refused:")
        (b / "MeshWeaver.Mcp.json").unlink()
        found, refused = read_markers(b, "floor-abc123")
        ok, line = bundles_built(three, found, None, refused)
        check("a selected bundle with NO marker ⇒ false, naming it",
              not ok and "MeshWeaver.Mcp" in line, line)
        # 🚨 THE DEFECT ITSELF: a sibling call's marker for a module THIS call also declared.
        (b / "MeshWeaver.Mcp.json").write_text(marker("MeshWeaver.Mcp", lane="rest-def456"),
                                               encoding="utf-8")
        found, refused = read_markers(b, "floor-abc123")
        ok, line = bundles_built(three, found, None, refused)
        check("a marker from a FOREIGN LANE does not satisfy this one — false, and it says whose",
              not ok and "MeshWeaver.Mcp" in line and "rest-def456" in line, line)
        (b / "MeshWeaver.Mcp.json").write_text(marker("MeshWeaver.Mcp", lane=None),
                                               encoding="utf-8")
        found, refused = read_markers(b, "floor-abc123")
        ok, line = bundles_built(three, found, None, refused)
        check("an UNSTAMPED marker is not this call's either ⇒ false (fail closed, never a "
              "silent true)", not ok and "MeshWeaver.Mcp" in line, line)
        for claim in ("closure", "bundle"):
            (b / "MeshWeaver.Mcp.json").write_text(
                marker("MeshWeaver.Mcp", **{claim: None}), encoding="utf-8")
            found, refused = read_markers(b, "floor-abc123")
            ok, line = bundles_built(three, found, None, refused)
            check(f"a marker recording no {claim} ⇒ false — 'the artifact exists' is not the "
                  f"claim `bundles-built` makes", not ok and claim in line, line)
        (b / "MeshWeaver.Mcp.json").write_text(marker("MeshWeaver.Mcp"), encoding="utf-8")
        (b / "MeshWeaver.Mcp.json").write_text("{ not json", encoding="utf-8")
        found, refused = read_markers(b, "floor-abc123")
        ok, line = bundles_built(three, found, None, refused)
        check("a CORRUPT marker is not a marker ⇒ false, naming the file",
              not ok and "unreadable" in line, line)
        (b / "MeshWeaver.Mcp.json").write_text(marker("MeshWeaver.Mcp"), encoding="utf-8")

        found, refused = read_markers(Path(tmp) / "no-markers-at-all", "floor-abc123")
        ok, line = bundles_built(three, found, None, refused)
        check("ZERO markers with a non-empty selection ⇒ false, never a silent true",
              not ok and not found, line)

        (b / "MeshWeaver.Ghost.json").write_text(marker("MeshWeaver.Ghost"), encoding="utf-8")
        found, refused = read_markers(b, "floor-abc123")
        ok, _ = bundles_built(["MeshWeaver.AI", "MeshWeaver.Teams"], found, set(three), refused)
        check("a sibling call's marker for an UNDECLARED module is set aside like its receipt", ok)
        (b / "MeshWeaver.Ghost.json").unlink()
        found, refused = read_markers(b)
        ok, _ = bundles_built(three, found, None, refused)
        check("no --lane ⇒ the pre-#1077 name-only behaviour still adds up", ok)
        ok, _ = bundles_built([], set(), None)
        check("an empty selection has every bundle it needs ⇒ true", ok)

        print("the legitimate empty selection — and its own mutation:")
        empty = Path(tmp) / "empty"
        empty.mkdir()
        code, errors, notes = run(empty, [], "skipped")
        check("zero selected + zero receipts + a skipped job ⇒ pass, and says so",
              code == 0 and bool(notes), f"{errors}")
        code, errors, notes = run(empty, [], "success")
        check("zero selected + zero receipts + a job GitHub reported success ⇒ pass",
              code == 0 and bool(notes), f"{errors}")
        # 🚨 The contradiction lock. Without the scope, this is indistinguishable from the case
        # above — which is why the count alone was not enough.
        code, errors, _ = run(empty, [], "skipped", scope="full")
        check("scope=FULL with an empty selection ⇒ fail (a contradiction, not a scope)",
              code == 1 and any("cannot both be true" in e for e in errors), f"{errors}")
        code, errors, _ = run(empty, [], "skipped", scope="narrowed")
        check("…while scope=narrowed with an empty selection stays legitimate",
              code == 0, f"{errors}")
        (empty / "MeshWeaver.AI.json").write_text(json.dumps({"module": "MeshWeaver.AI"}),
                                                  encoding="utf-8")
        code, errors, _ = run(empty, [], "skipped")
        check("zero selected but a bundle WAS built ⇒ fail",
              code == 1 and any("ZERO" in e and "receipts arrived" in e for e in errors),
              f"{errors}")

        # ── THE TESTS LANE ────────────────────────────────────────────────────────────────
        # 🚨 Every case here is a mutation of a green run in which the suites ran BESIDE the pack
        # matrix. The one that matters most is the SKIP: two mutually exclusive `if:`s can both be
        # false, and "the suite ran nowhere" is otherwise indistinguishable from "the suite passed".
        print("the tests lane — the suite may move off the critical path, never out of the gate:")

        def packr(mode: str) -> dict[str, dict]:
            return {m: {"lane": "floor-abc123", "module": m, "tests": mode} for m in three}

        def testr(modules) -> dict[str, dict]:
            return {m: {"lane": "floor-abc123", "module": m, "tests": "lane",
                        "suite": "present", "result": "passed"} for m in modules}

        delegated, ran = packr("lane"), testr(three)
        errs, nts = test_lane(delegated, ran, [], "success")
        check("every delegated suite has a receipt and the lane is green ⇒ pass",
              not errs and bool(nts), f"{errs}")
        for bad in ("failure", "cancelled"):
            errs, _ = test_lane(delegated, ran, [], bad)
            check(f"a {bad.upper()} tests lane reds this context — the gate MOVED, it did not "
                  f"vanish", any(f"reported '{bad}'" in e for e in errs), f"{errs}")
        errs, _ = test_lane(delegated, {}, [], "skipped")
        check("delegated suites + a SKIPPED lane ⇒ red: the suite ran in NEITHER lane, which "
              "renders exactly like one that passed",
              any("did NOT RUN AT ALL" in e for e in errs), f"{errs}")
        errs, _ = test_lane(delegated, testr(m for m in three if m != "MeshWeaver.Mcp"), [],
                            "success")
        check("a delegated module whose suite never reached its receipt ⇒ red, naming it",
              any("MeshWeaver.Mcp" in e and "no test receipt" in e for e in errs), f"{errs}")
        errs, _ = test_lane(packr("inline"), ran, [], "success")
        check("a test receipt for a module whose pack receipt says the suite ran INLINE ⇒ red "
              "(the two halves of the switch disagree)",
              any("disagree" in e for e in errs), f"{errs}")
        errs, _ = test_lane(delegated, ran, ["MeshWeaver.Ghost.json"], "success")
        check("a corrupt test receipt is not a receipt ⇒ red, naming the file",
              any("could not be read" in e for e in errs), f"{errs}")

        print("…and the shapes that must stay GREEN, so the gate is not merely loud:")
        errs, nts = test_lane(packr("inline"), {}, [], "skipped")
        check("a PUBLISHING run — every suite inline, the lane deliberately skipped ⇒ pass",
              not errs and bool(nts), f"{errs}")
        errs, nts = test_lane(packr("none"), {}, [], "skipped")
        check("a selection that owes no test run at all + a skipped lane ⇒ pass",
              not errs and bool(nts), f"{errs}")
        errs, nts = test_lane(delegated, {}, [], "")
        check("no --tests-result ⇒ the accounting stands down (an older caller against a newer "
              "script), silently adding nothing", not errs and not nts, f"{errs}")
        errs, _ = test_lane({}, ran, [], "success")
        check("test receipts whose pack legs produced NO receipt are the COUNT gate's finding, "
              "not this one's — it must not misname the cause", not errs, f"{errs}")

        # ── ONE RUN, ONE PLATFORM, ONE IDENTITY ───────────────────────────────────────────
        # 🚨 The mutations here are the ONLY place the identity defect can be made to fail. Every
        # other guard on it reads the lane's TEXT, and the failure they cannot see is silent: the
        # bundles pack, the manifests carry well-formed 32-hex values, and a run states one
        # identity PER MODULE. Reinstating `-p:Version="$VERSION"` on the anchor build produces
        # EXACTLY the first mutation below (measured on that command: -p:Version=1.3.18 →
        # 34337c31…, -p:Version=1.0.24 → 7c1c4f70…, no override → 71cc81ba… twice).
        print("one run, one platform, ONE identity — the outcome the config-level guards cannot "
              "see (#3310):")
        ONE = "71cc81badb364c5d8558ac5e7db6a44e"
        OTHER = "34337c31960d47f5b5251d32ef923fc6"

        def idr(identity: str) -> dict[str, dict]:
            return {m: {"lane": "floor-abc123", "module": m, IDENTITY_FIELD: identity}
                    for m in three}

        agreed = idr(ONE)
        errs, nts = identity_agreement(agreed)
        check("every bundle states the same identity ⇒ pass, and the note SAYS which",
              not errs and any(ONE in n for n in nts), f"{errs} {nts}")

        disagreeing = dict(agreed)
        disagreeing["MeshWeaver.Mcp"] = {"lane": "floor-abc123", "module": "MeshWeaver.Mcp",
                                         IDENTITY_FIELD: OTHER}
        errs, _ = identity_agreement(disagreeing)
        check("a per-module identity (what reinstating -p:Version on the anchor build produces) "
              "⇒ RED, naming the modules AND their identities",
              any("DIFFERENT framework identities" in e and ONE in e and OTHER in e
                  and "MeshWeaver.Mcp" in e and "MeshWeaver.AI" in e for e in errs), f"{errs}")

        for value, label in ((None, "an absent"), ("", "an empty"), ("   ", "a whitespace")):
            silent = dict(agreed)
            silent["MeshWeaver.Teams"] = {"lane": "floor-abc123", "module": "MeshWeaver.Teams"}
            if value is not None:
                silent["MeshWeaver.Teams"][IDENTITY_FIELD] = value
            errs, nts = identity_agreement(silent)
            check(f"{label} identity is a NAMED refusal, never a silent 'agrees' — and the "
                  f"remedy is in the message",
                  any("state NO framework identity" in e and "MeshWeaver.Teams" in e
                      and "node-repo-module-pack.yml@" in e for e in errs)
                  and not nts, f"{errs} {nts}")

        # Both findings at once: a lane may be BOTH stale and disagreeing, and reporting only the
        # first would send the reader after the wrong one.
        both = dict(disagreeing)
        both["MeshWeaver.Teams"] = {"lane": "floor-abc123", "module": "MeshWeaver.Teams"}
        errs, _ = identity_agreement(both)
        check("a lane that is BOTH stale and disagreeing reports both findings",
              len(errs) == 2, f"{errs}")

        # 🚨 PER LANE, because a repo calling this workflow twice pins one platform PER CALL. The
        # filter is `own_receipts` — the same one the count gate and the tests lane apply — so a
        # sibling call's identity can neither red this one nor satisfy it.
        with_sibling = dict(agreed)
        with_sibling["MeshWeaver.Ghost"] = {"lane": "rest-def456", "module": "MeshWeaver.Ghost",
                                            IDENTITY_FIELD: OTHER}
        errs, _ = identity_agreement(own_receipts(with_sibling, "floor-abc123", set(three)))
        check("a SIBLING call's differing identity is not this call's to judge (two calls, two "
              "pins) ⇒ pass", not errs, f"{errs}")
        errs, _ = identity_agreement(own_receipts(with_sibling, "", None))
        check("…and without the lane filter that same set IS the disagreement — the scope is what "
              "makes the pass legitimate, not the absence of a finding",
              any("DIFFERENT framework identities" in e for e in errs), f"{errs}")

        check("an empty selection has nothing to disagree about ⇒ pass, silently",
              identity_agreement({}) == ([], []))

        # And end-to-end, off files on disk, through the same reader the gate uses — a check that
        # only ever ran on hand-built dicts would not prove the FIELD NAME the lane writes.
        idd = Path(tmp) / "identity"
        idd.mkdir()
        for m in three:
            (idd / f"{m}.json").write_text(json.dumps(
                {"lane": "floor-abc123", "package": m.split(".")[-1], "module": m,
                 "version": "1.2.3", IDENTITY_FIELD: ONE}), encoding="utf-8")
        parsed, _ = read_receipts(idd)
        errs, nts = identity_agreement(own_receipts(parsed, "floor-abc123", set(three)))
        check("read off real receipt FILES, the field the lane writes is the field this reads",
              not errs and any(ONE in n for n in nts), f"{errs} {nts}")
        (idd / "MeshWeaver.Teams.json").write_text(json.dumps(
            {"lane": "floor-abc123", "package": "Teams", "module": "MeshWeaver.Teams",
             "version": "1.2.3", IDENTITY_FIELD: OTHER}), encoding="utf-8")
        parsed, _ = read_receipts(idd)
        errs, _ = identity_agreement(own_receipts(parsed, "floor-abc123", set(three)))
        check("…and one file mutated to a second identity turns it RED",
              any("DIFFERENT framework identities" in e for e in errs), f"{errs}")

    if failures:
        print(f"\n::error title=node-repo-pack-verify self-test failed::{len(failures)} case(s) — "
              "this gate is the only thing standing between a narrowed matrix and a silently "
              "skipped module bundle.")
        return 1
    print("\n✓ node-repo-pack-verify self-test: 2 green-run assertions, 10 mutations that must go "
          "red, the two sibling-call receipts (well-formed and corrupt) that must NOT, the two "
          "receipt LANE cases (own stamp counts, a sibling lane's does not — even for a module "
          "both calls declared), 12 built-marker cases (foreign lane, unstamped, no closure, no "
          "bundle, corrupt, zero markers — every one fail-closed — plus #2710's red-suite "
          "survival), 5 empty-selection cases including the scope=full contradiction lock, 11 "
          "TESTS-LANE cases (a failed or cancelled lane is red, a skipped lane with delegated "
          "suites is red because the suite then ran NOWHERE, a missing or corrupt test receipt is "
          "red, the inline/lane halves disagreeing is red — and the publishing, nothing-owed, "
          "older-caller and pack-failed shapes must stay green), and 10 IDENTITY cases (two "
          "identities in one lane is red naming both, an absent/empty/whitespace identity is a "
          "distinct NAMED refusal rather than 'agrees', both findings are reported together, the "
          "check is scoped per LANE, and the field is read end-to-end off real receipt files) — "
          "all green.")
    return 0


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0])
    p.add_argument("--expected", default="",
                   help="space-separated module names the selection named")
    p.add_argument("--receipts", default="", help="directory the receipt artifacts were merged into")
    p.add_argument("--pack-result", default="", dest="pack_result",
                   help="the pack job's aggregated result (needs.pack.result)")
    p.add_argument("--scope", default="",
                   help="the selection's own scope (full|narrowed). A 'full' scope with an empty "
                        "selection is a contradiction and is refused.")
    p.add_argument("--declared", default="",
                   help="the caller's own `modules:` input (JSON entries or names). Receipts for "
                        "modules outside it belong to a sibling call in the same run and are set "
                        "aside, not counted. Empty ⇒ every receipt is this call's.")
    p.add_argument("--lane", default="",
                   help="this call's lane key (select.outputs.lane). A marker or receipt stamped "
                        "with any other lane — or with none — belongs to a sibling call in the "
                        "same run and is set aside, NAMED, never counted. Empty ⇒ no lane check "
                        "(name-based --declared separation only).")
    p.add_argument("--built", default="",
                   help="directory the module-built-* markers were merged into; with --github-output, "
                        "writes bundles_built=true|false — the receipt-independent 'this call built "
                        "a complete, composable bundle' claim")
    p.add_argument("--test-receipts", default="", dest="test_receipts",
                   help="directory the module-tests-receipt-* artifacts were merged into — the "
                        "positive evidence that a DELEGATED suite ran. Paired with --tests-result.")
    p.add_argument("--tests-result", default="", dest="tests_result",
                   help="the tests lane's aggregated result (needs.tests.result). A lane that did "
                        "not succeed is RED here too, so a red suite still reds this context; a "
                        "SKIPPED lane with delegated suites is red because the suite then ran "
                        "nowhere. Empty ⇒ the caller does not run a tests lane and the check "
                        "stands down.")
    p.add_argument("--github-output", default="", dest="github_output",
                   help="the $GITHUB_OUTPUT file to append bundles_built to")
    p.add_argument("--self-test", action="store_true", dest="self_test")
    args = p.parse_args()
    if args.self_test:
        return self_test()
    if not args.receipts or not args.pack_result:
        p.error("--receipts and --pack-result are required")

    directory = Path(args.receipts)
    expected = [m for m in args.expected.split() if m]
    receipts, broken = read_receipts(directory)
    code, errors, notes = verify(expected, receipts, broken, args.pack_result,
                                 directory.is_dir(), args.scope, parse_declared(args.declared),
                                 args.lane)

    markers, refused = read_markers(Path(args.built), args.lane) if args.built else (set(), [])
    ok, line = bundles_built(expected, markers, parse_declared(args.declared), refused)
    if args.built:
        notes.append(line)
        if args.github_output:
            with open(args.github_output, "a", encoding="utf-8") as fh:
                fh.write(f"bundles_built={'true' if ok else 'false'}\n")

    # 🚨 AFTER the bundles-built answer and deliberately unable to change it: a red suite must red
    # THIS context and must NOT read as "bundle missing" to a gate that only composes the bundle
    # (#2710, Plugins#937). Those are two different questions and this is the second one.
    declared_set = parse_declared(args.declared)
    if args.test_receipts or args.tests_result:
        t_receipts, t_broken = (read_receipts(Path(args.test_receipts))
                                if args.test_receipts else ({}, []))
        t_errors, t_notes = test_lane(own_receipts(receipts, args.lane, declared_set),
                                      own_receipts(t_receipts, args.lane, declared_set),
                                      t_broken, args.tests_result)
        notes.extend(t_notes)
        errors.extend(t_errors)
        if t_errors:
            code = 1

    # 🚨 THE THIRD QUESTION, and the only one asked of the OUTCOME: did this lane's bundles end up
    # stating ONE framework identity? Unconditional — there is no flag to forget and no input to
    # test, because the evidence is the receipts this job already downloaded. Like the tests-lane
    # accounting it runs after `bundles_built` and cannot change it: bundles that disagree are
    # complete and composable, and mis-stating them as missing would misname the cause (#2710).
    i_errors, i_notes = identity_agreement(own_receipts(receipts, args.lane, declared_set))
    notes.extend(i_notes)
    errors.extend(i_errors)
    if i_errors:
        code = 1
    for note in notes:
        print(f"✓ {note}")
    for error in errors:
        print(f"::error title=Module bundles incomplete::{error}")
    if os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as fh:
            fh.write(f"### {'✅' if code == 0 else '❌'} Module bundles complete\n\n")
            fh.write(f"selection named **{len(expected)}**, receipts arrived for "
                     f"**{len(receipts)}** (pack job: `{args.pack_result}`)\n\n")
            for line in notes + errors:
                fh.write(f"- {line}\n")
    return code


if __name__ == "__main__":
    sys.exit(main())
