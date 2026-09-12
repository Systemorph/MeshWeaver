#!/usr/bin/env python3
"""module-pack-batch.py — the module-pack lane's BATCHING of sub-minute matrix legs, and the
per-module bookkeeping that keeps a batch from becoming a coupling.

(The name on this first line is load-bearing: the module-pack lane fetches this file at the
caller's `build-logic-ref` (falling back to the exact loaded workflow SHA) and its `select` job
self-tests it on every run. The framework checkout remains independently pinned by `platform-ref`.)

WHY THIS EXISTS (measured 2026-09-05..12)
------------------------------------------
GitHub bills every job rounded UP to a whole minute. `node-repo-module-pack.yml` fanned out ONE
job per module, and most of those jobs were shorter than the unit they were billed in:

    MeshWeaver.Plugins  Module bundles / Module bundle   4,975 sampled legs, MEDIAN 1.1 min (p90 8.4)
    MeshWeaver.Plugins  Module bundles (parent legs)     1,385 legs,          MEDIAN 1.1 min
    MeshWeaver.Plugins  Module bundles / Module tests    2,935 legs,          MEDIAN 4.0 min (p90 8.2)

Whole-minute rounding on the sub-minute legs was ≈12 % of Plugins' bill (~4,700 billed minutes a
day) and 25–40 % in Crm / Reinsurance / SocialMedia, whose modules are smaller still. Every leg
also pays its own checkout, SDK install and cache restores before it does a second of module work.

So the lane now packs (and tests) N modules per leg — `batch-size`, default 6, hard cap 10 — as a
RUNNER-SHARING DEVICE and nothing more. The observable per-module semantics are unchanged, and
this script is what makes that structural rather than hoped-for:

  * `chunk`   — a DETERMINISTIC split: the selection sorted by module name, STRIPED into
                ceil(n/N) legs of ≤N (module i → leg i mod k; see `chunk` for why not contiguous).
                A selection of ≤N modules is ONE leg; a single-module repository sees exactly the
                job name, the `module-bundle-<module>` artifact and the receipts it saw before.
  * the state — one JSON file per leg, one record per module: the matrix entry, the facts each
                phase produced (version, ledger plan, compiler, bundle path, …) and a status. A
                phase that fails for ONE module marks THAT module failed with the phase and the
                reason, and every later phase skips it (`list --ok`); the sibling modules in the
                leg still pack, test, and — when the call publishes — POST their own bundle after
                their own suite, exactly as they did in a leg of their own.
  * `verdict` — the leg's LAST step: one status line per module, in the log and the job summary,
                and a non-zero exit when any module failed. The leg is red for the same reason a
                single-module leg was red, and `verify` (`All selected bundles built`) still
                accounts every module by its OWN receipt — a module that failed dropped none.

What this deliberately does NOT do: it never decides WHAT to build (that is node-repo-scope.py
and the ledger, upstream in `select`), never talks to the registry or the ledger itself, and
never hides a failure — `fail` prints a `::error` naming the module and the phase, and `verdict`
refuses to exit 0 over one.

    python3 module-pack-batch.py --self-test --workflow node-repo-module-pack.yml
    python3 module-pack-batch.py chunk --modules @selection.json --size 6 --github-output "$GITHUB_OUTPUT"
    python3 module-pack-batch.py --state S init --batch @batch.json
    python3 module-pack-batch.py --state S list --ok --where decision=build
    python3 module-pack-batch.py --state S set --module M version=1.2.3 floor=3.1.0
    python3 module-pack-batch.py --state S get --module M ledger.key
    python3 module-pack-batch.py --state S fail --module M --phase test --reason "…"
    python3 module-pack-batch.py --state S slots --max 10 --where bundle!=
    python3 module-pack-batch.py --state S verdict --summary "$GITHUB_STEP_SUMMARY"
"""
from __future__ import annotations

import argparse
import json
import os
import sys
import tempfile
from pathlib import Path

# The hard cap on `batch-size`. It is a LITERAL the workflow shares: the bundle artifact
# `module-bundle-<module>` is a per-module NAME every caller's `module-artifacts` pattern and
# every ledger record depends on, and `actions/upload-artifact` uploads one name per step — so
# the lane unrolls exactly this many upload slots, and a batch may never hold more modules.
MAX_BATCH_SIZE = 10
DEFAULT_BATCH_SIZE = 6
# Job names are display text; past this the list is cut and the count says how many are hidden.
LABEL_MAX = 160
# The phases a module can fail in — the ledger's own vocabulary (module-build-ledger.py).
PHASES = ("compile", "workspace", "pack", "test", "publish")


def die(msg: str) -> None:
    print(f"::error::{msg}")
    sys.exit(1)


def load_json_arg(value: str):
    """`@file` reads the file; anything else is inline JSON."""
    text = Path(value[1:]).read_text(encoding="utf-8") if value.startswith("@") else value
    return json.loads(text or "null")


# ── chunking ───────────────────────────────────────────────────────────────────────────────────

def batch_id(modules: list[str], index: int, total: int) -> str:
    """The artifact-name suffix of a batch: the module itself for a singleton (so a single-module
    repository's artifact names are byte-identical to before batching), else `batch-i-of-k`."""
    if len(modules) == 1:
        return modules[0]
    return f"batch-{index}-of-{total}"


def batch_label(modules: list[str], index: int, total: int, label_max: int = LABEL_MAX) -> str:
    """The job's display name inside `Module bundle (…)` / `Module tests (…)`. A singleton is the
    module name, unchanged; a batch names every module it carries, cut with a count when long."""
    if len(modules) == 1:
        return modules[0]
    head = f"batch {index}/{total}: "
    names = ", ".join(modules)
    if len(head) + len(names) <= label_max:
        return head + names
    shown: list[str] = []
    for m in modules:
        candidate = ", ".join(shown + [m])
        if len(head) + len(candidate) + len(f", … +{len(modules) - len(shown) - 1} more") > label_max:
            break
        shown.append(m)
    if not shown:
        shown = [modules[0]]
    hidden = len(modules) - len(shown)
    return head + ", ".join(shown) + (f", … +{hidden} more" if hidden else "")


def chunk(entries: list[dict], size: int, label_max: int = LABEL_MAX) -> list[dict]:
    """Sort by module name, then STRIPE into ceil(n/size) legs of ≤size: module i goes to leg
    i mod k. Deterministic — the same selection always yields the same legs with the same ids,
    whatever order the selector emitted it in — and balanced (leg sizes differ by at most one).

    Striped rather than cut into contiguous runs on purpose: alphabetical neighbours are usually
    a FAMILY (MeshWeaver.AI, MeshWeaver.AI.Anthropic, MeshWeaver.AI.OpenAI, …) whose suite cost is
    correlated, and every job is hard-cut at 45 minutes — six p90 (8.2 min) suites in one leg
    would be 49. Spreading a family over the legs is the one thing a chunker with no duration
    data can do about that; the `batch-size` input is the other.
    """
    if not isinstance(size, int) or size < 1 or size > MAX_BATCH_SIZE:
        raise ValueError(f"batch-size must be an integer from 1 to {MAX_BATCH_SIZE}, got {size!r}")
    names = [e.get("module") for e in entries]
    if any(not isinstance(n, str) or not n for n in names):
        raise ValueError("every matrix entry must carry a non-empty `module`")
    if len(set(names)) != len(names):
        dup = sorted({n for n in names if names.count(n) > 1})
        raise ValueError(f"the selection names a module twice: {', '.join(dup)}")
    ordered = sorted(entries, key=lambda e: e["module"])
    total = -(-len(ordered) // size) if ordered else 0
    runs = [ordered[j::total] for j in range(total)]
    out = []
    for i, run in enumerate(runs, start=1):
        mods = [e["module"] for e in run]
        out.append({
            "id": batch_id(mods, i, total),
            "index": i,
            "total": total,
            "label": batch_label(mods, i, total, label_max),
            "modules": mods,
            "entries": run,
        })
    return out


# ── the per-leg state ──────────────────────────────────────────────────────────────────────────

class State:
    def __init__(self, path: Path) -> None:
        self.path = path
        self.doc: dict = {"order": [], "modules": {}}

    def load(self) -> "State":
        if not self.path.is_file():
            die(f"no batch state at {self.path} — the leg's first step (`init`) did not run, so "
                "nothing below it can say which modules it owns. Refusing to guess.")
        self.doc = json.loads(self.path.read_text(encoding="utf-8"))
        return self

    def save(self) -> None:
        self.path.parent.mkdir(parents=True, exist_ok=True)
        tmp = self.path.with_suffix(".tmp")
        tmp.write_text(json.dumps(self.doc, indent=1, sort_keys=True), encoding="utf-8")
        os.replace(tmp, self.path)

    def init(self, entries: list[dict]) -> None:
        names = [e.get("module") for e in entries]
        if any(not isinstance(n, str) or not n for n in names):
            die("every entry of the batch must carry a non-empty `module`")
        if len(set(names)) != len(names):
            die("the batch names a module twice — the chunker refuses that, so this is not its output")
        self.doc = {"order": names, "modules": {
            e["module"]: {"entry": e, "status": "ok", "phase": "", "reason": "", "facts": {}}
            for e in entries}}

    def record(self, module: str) -> dict:
        rec = self.doc["modules"].get(module)
        if rec is None:
            die(f"module `{module}` is not in this leg's batch ({', '.join(self.doc['order']) or '<empty>'})")
        return rec

    def get(self, module: str, key: str, default: str = "") -> str:
        """A fact this leg recorded, else a field of the matrix entry (dotted path allowed, e.g.
        `ledger.key`). Booleans print as `true`/`false`; null and absent print as the default."""
        rec = self.record(module)
        if key in rec["facts"]:
            return rec["facts"][key]
        node = rec["entry"]
        for part in key.split("."):
            if isinstance(node, dict) and part in node:
                node = node[part]
            else:
                return default
        if node is None:
            return default
        if isinstance(node, bool):
            return "true" if node else "false"
        if isinstance(node, (dict, list)):
            return json.dumps(node, sort_keys=True)
        return str(node)

    def matches(self, module: str, where: list[str]) -> bool:
        for cond in where:
            if "!=" in cond:
                k, v = cond.split("!=", 1)
                if self.get(module, k) == v:
                    return False
            elif "=" in cond:
                k, v = cond.split("=", 1)
                if self.get(module, k) != v:
                    return False
            else:
                die(f"--where wants key=value or key!=value, got {cond!r}")
        return True

    def select(self, ok: bool | None, where: list[str]) -> list[str]:
        out = []
        for m in self.doc["order"]:
            rec = self.doc["modules"][m]
            if ok is True and rec["status"] != "ok":
                continue
            if ok is False and rec["status"] != "failed":
                continue
            if self.matches(m, where):
                out.append(m)
        return out


# ── commands ───────────────────────────────────────────────────────────────────────────────────

def cmd_chunk(a: argparse.Namespace) -> int:
    entries = load_json_arg(a.modules) or []
    if not isinstance(entries, list):
        die("--modules must be a JSON array of matrix entries")
    try:
        size = int(a.size)
    except ValueError:
        die(f"inputs.batch-size is {a.size!r}, which is not an integer. The only values are 1 to "
            f"{MAX_BATCH_SIZE} (default {DEFAULT_BATCH_SIZE}). A batch size that cannot be read "
            "must never resolve to 'one leg per module' quietly, nor to one giant leg.")
    try:
        batches = chunk(entries, size, a.label_max)
    except ValueError as exc:
        die(str(exc))
    count = len(batches)
    per = ", ".join(f"{b['id']} ({len(b['modules'])})" for b in batches) or "<none>"
    print(f"batch-size {size}: {len(entries)} module(s) -> {count} leg(s): {per}")
    for b in batches:
        print(f"  {b['id']}: {', '.join(b['modules'])}")
    if a.github_output:
        with open(a.github_output, "a", encoding="utf-8") as fh:
            fh.write(f"{a.prefix}batches={json.dumps(batches, separators=(',', ':'))}\n")
            fh.write(f"{a.prefix}batch-count={count}\n")
    else:
        print(json.dumps({"batches": batches, "count": count}))
    return 0


def state_of(a: argparse.Namespace) -> State:
    if not a.state:
        die("--state <file> is required before this subcommand")
    return State(Path(a.state))


def cmd_init(a: argparse.Namespace) -> int:
    batch = load_json_arg(a.batch)
    entries = batch.get("entries") if isinstance(batch, dict) else batch
    if not isinstance(entries, list) or not entries:
        die("init wants the matrix batch object (with `entries`) or a non-empty entries array")
    st = state_of(a)
    st.init(entries)
    st.save()
    label = batch.get("label") if isinstance(batch, dict) else ""
    print(f"this leg owns {len(entries)} module(s){' — ' + label if label else ''}:")
    for m in st.doc["order"]:
        e = st.doc["modules"][m]["entry"]
        print(f"  • {m}  (package {e.get('package', '?')}, build {e.get('build') or 'sdk'})")
    return 0


def cmd_list(a: argparse.Namespace) -> int:
    st = state_of(a).load()
    ok = True if a.ok else (False if a.failed else None)
    for m in st.select(ok, a.where or []):
        print(m)
    return 0


def cmd_get(a: argparse.Namespace) -> int:
    st = state_of(a).load()
    print(st.get(a.module, a.key, a.default))
    return 0


def cmd_set(a: argparse.Namespace) -> int:
    st = state_of(a).load()
    rec = st.record(a.module)
    for pair in a.pairs:
        if "=" not in pair:
            die(f"set wants KEY=VALUE, got {pair!r}")
        k, v = pair.split("=", 1)
        if not k:
            die(f"set wants a non-empty key, got {pair!r}")
        rec["facts"][k] = v
    st.save()
    return 0


def cmd_fail(a: argparse.Namespace) -> int:
    st = state_of(a).load()
    rec = st.record(a.module)
    if a.phase not in PHASES:
        die(f"--phase must be one of {', '.join(PHASES)} (the ledger's vocabulary), got {a.phase!r}")
    if rec["status"] == "failed":
        # First failure wins: the phase a follower should read is the one that stopped the module.
        print(f"{a.module}: already failed in {rec['phase']} — keeping that verdict "
              f"(this later failure in {a.phase}: {a.reason})")
        return 0
    rec["status"] = "failed"
    rec["phase"] = a.phase
    rec["reason"] = a.reason
    st.save()
    print(f"::error title=Module {a.module} failed ({a.phase})::{a.reason}")
    return 0


def cmd_slots(a: argparse.Namespace) -> int:
    st = state_of(a).load()
    mods = st.select(True, a.where or [])
    if len(mods) > a.max:
        die(f"{len(mods)} modules for {a.max} slots — the chunker caps a batch at {MAX_BATCH_SIZE} "
            "and the lane unrolls that many upload steps; this leg holds more than either.")
    lines = [f"{a.prefix}s{i}={mods[i - 1] if i <= len(mods) else ''}" for i in range(1, a.max + 1)]
    if a.github_output:
        with open(a.github_output, "a", encoding="utf-8") as fh:
            fh.write("\n".join(lines) + "\n")
    else:
        print("\n".join(lines))
    print(f"{len(mods)} of {a.max} slot(s) filled: {', '.join(mods) or '<none>'}")
    return 0


def cmd_paths(a: argparse.Namespace) -> int:
    """A JSON object module -> fact, for the modules that carry it. One expression for every step
    that reads the same fact (the bundle path the hand-over, the staging and the receipt share)."""
    st = state_of(a).load()
    out = {m: st.get(m, a.key) for m in st.select(True, a.where or []) if st.get(m, a.key)}
    print(json.dumps(out, sort_keys=True, separators=(",", ":")))
    return 0


def cmd_verdict(a: argparse.Namespace) -> int:
    st = state_of(a).load()
    rows = []
    failed = 0
    for m in st.doc["order"]:
        rec = st.doc["modules"][m]
        if rec["status"] == "ok":
            detail = ", ".join(f"{k}: {v}" for k, v in sorted(rec["facts"].items())
                               if k in ("version", "decision", "compiler", "tests", "publication"))
            rows.append((m, "✅ ok", detail))
        else:
            failed += 1
            rows.append((m, f"❌ failed in {rec['phase']}", rec["reason"]))
    width = max((len(m) for m, _, _ in rows), default=6)
    print(f"leg verdict — {len(rows) - failed} ok, {failed} failed:")
    for m, status, detail in rows:
        print(f"  {m.ljust(width)}  {status}  {detail}")
    if a.summary:
        with open(a.summary, "a", encoding="utf-8") as fh:
            fh.write(f"### Module legs in this batch — {len(rows) - failed} ok, {failed} failed\n\n")
            fh.write("| module | status | detail |\n|---|---|---|\n")
            for m, status, detail in rows:
                fh.write(f"| `{m}` | {status} | {detail.replace('|', '\\|')} |\n")
            fh.write("\n")
    if failed:
        print(f"::error title=Batch leg red::{failed} of {len(rows)} module(s) in this leg failed — "
              "each is named above with its phase; the others completed independently.")
        return 1
    return 0


# ── self-test ──────────────────────────────────────────────────────────────────────────────────

def workflow_script_ownership_problems(workflow: str) -> list[str]:
    """Keep lane orchestration on build-logic-ref and compilation on platform-ref.

    The two refs deliberately move independently. The 2026-09-12 batching rollout put this helper
    in a new lane while `pack` and `tests` still invoked it from the older platform checkout; every
    module-test batch then failed before its first suite. The caller passes the exact reusable
    workflow identified by `job.workflow_sha`, so this checks the YAML GitHub loaded rather than a
    copy beside either independently moving checkout.
    """
    problems: list[str] = []
    boundaries = {"select": "prepare", "pack": "tests", "tests": "verify"}
    for job, next_job in boundaries.items():
        start_marker = f"\n  {job}:\n"
        end_marker = f"\n  {next_job}:\n"
        if start_marker not in workflow or end_marker not in workflow:
            problems.append(f"cannot find the {job} job boundary")
            continue
        block = workflow.split(start_marker, 1)[1].split(end_marker, 1)[0]
        steps = block.split("\n      - ")
        logic_ref = ("ref: ${{ inputs.build-logic-ref || steps.workflow.outputs.sha }}"
                     if job == "select"
                     else "ref: ${{ needs.select.outputs.build-logic-ref }}")
        platform_checkouts = [
            step for step in steps
            if "uses: actions/checkout@" in step
            and "repository: Systemorph/MeshWeaver" in step
            and "ref: ${{ inputs.platform-ref }}" in step
            and "path: meshweaver" in step
        ]
        logic_checkouts = [
            step for step in steps
            if "uses: actions/checkout@" in step
            and "repository: Systemorph/MeshWeaver" in step
            and logic_ref in step
            and "path: build-logic" in step
            and (job == "select" or "sparse-checkout: .github/scripts" in step)
        ]
        expected_platform = 0 if job == "select" else 1
        if len(platform_checkouts) != expected_platform:
            problems.append(f"{job} needs exactly {expected_platform} platform-ref checkout(s) at meshweaver")
        if len(logic_checkouts) != 1:
            problems.append(f"{job} needs exactly one build-logic-ref checkout at build-logic")
        declaration = ("MODULE_PACK_BATCH: ${{ github.workspace }}"
                       "/build-logic/.github/scripts/module-pack-batch.py")
        if block.count(declaration) != 1:
            problems.append(f"{job} must declare its batching helper exactly once from build-logic")
        if "meshweaver/.github/scripts/module-pack-batch.py" in block:
            problems.append(f"{job} invokes the batching helper from platform-ref")
        required_calls = 3 if job == "select" else 2
        if block.count('"$MODULE_PACK_BATCH"') < required_calls:
            problems.append(f"{job} does not drive its batch through MODULE_PACK_BATCH")
        if job == "select":
            workflow_checkouts = [
                step for step in steps
                if "uses: actions/checkout@" in step
                and "repository: ${{ steps.workflow.outputs.repository }}" in step
                and "ref: ${{ steps.workflow.outputs.sha }}" in step
                and "path: lane-definition" in step
                and "sparse-checkout: .github/workflows" in step
            ]
            if len(workflow_checkouts) != 1:
                problems.append("select must check out the exact job.workflow_sha at lane-definition")
            if block.count("JOB_CONTEXT: ${{ toJSON(job) }}") != 1:
                problems.append("select must resolve the reusable workflow from the job context")
            if ".workflow_repository // empty" not in block or ".workflow_sha // empty" not in block:
                problems.append("select must read both reusable-workflow identity fields")
            if ('echo "repository=$repository" >> "$GITHUB_OUTPUT"' not in block
                    or 'echo "sha=$sha" >> "$GITHUB_OUTPUT"' not in block):
                problems.append("select must expose the validated job workflow identity to checkout")
            resolved_logic = ("build-logic-ref: "
                              "${{ inputs.build-logic-ref || steps.workflow.outputs.sha }}")
            if block.count(resolved_logic) != 1:
                problems.append("select must expose one exact build-logic ref to downstream jobs")
            executing = ("EXECUTING_WORKFLOW: ${{ github.workspace }}"
                         "/lane-definition/.github/workflows/node-repo-module-pack.yml")
            if block.count(executing) != 1:
                problems.append("select must name the exact checked-out reusable workflow once")
            if block.count("JOB_WORKFLOW_SHA: ${{ steps.workflow.outputs.sha }}") != 1:
                problems.append("select must pass job.workflow_sha to its checkout verification")
            if "git -C lane-definition rev-parse HEAD" not in block:
                problems.append("select must verify the workflow checkout resolved job.workflow_sha")
            exact_self_test = ('python3 "$MODULE_PACK_BATCH" --self-test '
                               '--workflow "$EXECUTING_WORKFLOW"')
            if block.count(exact_self_test) != 1:
                problems.append("select must self-test the exact workflow that defines the job")
            if "meshweaver/.github/scripts/" in block:
                problems.append("select invokes lane orchestration from the platform checkout")
    return problems

def self_test(workflow_path: Path | None = None) -> int:
    import subprocess

    failures: list[str] = []

    def check(name: str, ok: bool, detail: str = "") -> None:
        print(f"  {'ok  ' if ok else 'FAIL'} {name}" + (f" — {detail}" if detail and not ok else ""))
        if not ok:
            failures.append(name)

    def entry(m: str, **kw) -> dict:
        return {"package": m.split(".")[-1], "module": m, "project": f"src/{m}/{m}.csproj", **kw}

    print("== workflow: orchestration ref and platform ref stay separate")
    # Older pinned reusable workflows call `--self-test` without `--workflow`; retain that fallback
    # while current lanes pass the exact `job.workflow_sha` checkout explicitly. Dropping the
    # fallback would make an old lane fetch a new helper and fail before it could migrate.
    workflow_path = workflow_path or (
        Path(__file__).resolve().parents[1] / "workflows" / "node-repo-module-pack.yml"
    )
    if not workflow_path.is_file():
        check("the reusable workflow to inspect is present", False, str(workflow_path))
    else:
        workflow = workflow_path.read_text(encoding="utf-8")
        problems = workflow_script_ownership_problems(workflow)
        check("select, pack and tests keep workflow, tooling and platform refs distinct",
              not problems, "; ".join(problems))
        wrong_ref = workflow.replace(
            "ref: ${{ needs.select.outputs.build-logic-ref }}\n          path: build-logic",
            "ref: ${{ inputs.platform-ref }}\n          path: build-logic",
            1,
        )
        check("the ownership guard catches a helper checkout moved onto platform-ref",
              bool(workflow_script_ownership_problems(wrong_ref)))
        wrong_default = workflow.replace(
            "ref: ${{ inputs.build-logic-ref || steps.workflow.outputs.sha }}\n          path: build-logic",
            "ref: ${{ inputs.build-logic-ref || inputs.platform-ref }}\n          path: build-logic",
            1,
        )
        check("the ownership guard catches the default tooling ref coupled to platform-ref",
              bool(workflow_script_ownership_problems(wrong_default)))
        wrong_workflow_ref = workflow.replace(
            "ref: ${{ steps.workflow.outputs.sha }}\n          path: lane-definition",
            "ref: ${{ inputs.build-logic-ref || inputs.platform-ref }}\n          path: lane-definition",
            1,
        )
        check("the ownership guard catches executing-workflow evidence moved onto another ref",
              bool(workflow_script_ownership_problems(wrong_workflow_ref)))
        wrong_path = workflow.replace(
            'python3 "$MODULE_PACK_BATCH" --self-test --workflow "$EXECUTING_WORKFLOW"',
            "python3 meshweaver/.github/scripts/module-pack-batch.py",
            1,
        )
        check("the ownership guard catches select invoking the platform checkout",
              bool(workflow_script_ownership_problems(wrong_path)))

    print("== chunk: deterministic, sorted, ≤N is one leg, singleton unchanged")
    sel = [entry("MeshWeaver.Zeta"), entry("MeshWeaver.AI"), entry("MeshWeaver.Maps"),
           entry("MeshWeaver.Blazor"), entry("MeshWeaver.Mcp"), entry("MeshWeaver.Import"),
           entry("MeshWeaver.Teams")]
    b6 = chunk(sel, 6)
    names = sorted(e["module"] for e in sel)
    check("7 modules at N=6 → 2 legs", len(b6) == 2, str([b["modules"] for b in b6]))
    check("sorted by module name, then STRIPED (i mod k), so the legs are balanced 4 + 3",
          b6[0]["modules"] == names[0::2] and b6[1]["modules"] == names[1::2], str([b["modules"] for b in b6]))
    check("ids are batch-i-of-k for multi-module legs", [b["id"] for b in b6] == ["batch-1-of-2", "batch-2-of-2"])
    check("label names every module in the leg", b6[0]["label"] == "batch 1/2: " + ", ".join(names[0::2]), b6[0]["label"])
    fam = [entry(f"MeshWeaver.AI.{s}") for s in ("Anthropic", "AzureOpenAI", "Gemini", "OpenAI")] + [entry("MeshWeaver.AI")] + [entry(f"MeshWeaver.Z{i}") for i in range(7)]
    legs = chunk(fam, 6)
    check("a family of alphabetical neighbours is spread over the legs, never stacked in one",
          all(sum(m.startswith("MeshWeaver.AI") for m in b["modules"]) <= 3 for b in legs), str([b["modules"] for b in legs]))
    check("a trailing singleton leg is named by its module",
          chunk(sel[:7], 6)[1]["id"] == "batch-2-of-2" and chunk([entry("MeshWeaver.A"), entry("MeshWeaver.B")], 1)[1]["id"] == "MeshWeaver.B")
    check("the same input in another order chunks identically",
          chunk(list(reversed(sel)), 6) == b6)
    check("≤N modules is ONE leg", len(chunk(sel, 7)) == 1 and len(chunk(sel, 10)) == 1)
    one = chunk([entry("MeshWeaver.Social")], 6)
    check("a single-module repo: one leg, id and label are the module", one[0]["id"] == "MeshWeaver.Social" and one[0]["label"] == "MeshWeaver.Social")
    check("N=1 is one leg per module, each named by its module",
          [b["id"] for b in chunk(sel, 1)] == sorted(e["module"] for e in sel))
    check("an empty selection is zero legs", chunk([], 6) == [])
    long_names = [entry(f"MeshWeaver.Some.Rather.Long.Module.Name.Number{i:02d}") for i in range(10)]
    lbl = chunk(long_names, 10)[0]["label"]
    check("a long label is cut with a count, never past the cap", len(lbl) <= LABEL_MAX and "more" in lbl, lbl)
    check("every entry is carried verbatim", all(e in sel for b in b6 for e in b["entries"]))
    for bad, why in ((0, "0"), (11, "11"), ("6", "a string")):
        try:
            chunk(sel, bad)  # type: ignore[arg-type]
            check(f"batch-size {why} is refused", False)
        except ValueError:
            check(f"batch-size {why} is refused", True)
    try:
        chunk(sel + [entry("MeshWeaver.AI")], 6)
        check("a duplicate module is refused", False)
    except ValueError:
        check("a duplicate module is refused", True)

    print("== state: facts, filters, per-module failure isolation, verdict")
    with tempfile.TemporaryDirectory() as td:
        s = Path(td) / "state.json"
        st = State(s)
        st.init([entry("MeshWeaver.AI", build="container", ledger={"key": "k1", "decision": "build"}),
                 entry("MeshWeaver.Maps", test=False),
                 entry("MeshWeaver.Mcp", ledger={"key": "", "decision": "build"})])
        st.save()
        st = State(s).load()
        check("entry fields read through get", st.get("MeshWeaver.AI", "build") == "container")
        check("dotted entry paths read through get", st.get("MeshWeaver.AI", "ledger.key") == "k1")
        check("a boolean false prints as `false`", st.get("MeshWeaver.Maps", "test") == "false")
        check("an absent field prints the default", st.get("MeshWeaver.Mcp", "build", "sdk") == "sdk" and st.get("MeshWeaver.Mcp", "test") == "")
        check("an empty ledger key prints empty", st.get("MeshWeaver.Mcp", "ledger.key") == "")
        st.record("MeshWeaver.AI")["facts"]["decision"] = "build"
        st.record("MeshWeaver.Maps")["facts"]["decision"] = "reuse"
        st.record("MeshWeaver.Mcp")["facts"]["decision"] = "build"
        st.save()
        check("facts override entry fields and filter", st.select(True, ["decision=build"]) == ["MeshWeaver.AI", "MeshWeaver.Mcp"])
        check("key!=value filters", st.select(True, ["decision!=build"]) == ["MeshWeaver.Maps"])
        check("key!= (empty) selects the non-empty", st.select(True, ["ledger.key!="]) == ["MeshWeaver.AI"])
        check("order is the batch's order", st.select(None, []) == ["MeshWeaver.AI", "MeshWeaver.Maps", "MeshWeaver.Mcp"])

        # The CLI, end to end, the way the workflow drives it.
        here = str(Path(__file__).resolve())

        def run(*args: str, expect: int = 0) -> str:
            p = subprocess.run([sys.executable, here, "--state", str(s), *args], capture_output=True, text=True)
            check(f"`{' '.join(args[:3])}` exits {expect}", p.returncode == expect, p.stdout + p.stderr)
            return p.stdout + p.stderr

        run("set", "--module", "MeshWeaver.AI", "version=1.2.3", "bundle=/tmp/b/MeshWeaver.AI/x.nupkg")
        out = run("get", "--module", "MeshWeaver.AI", "version")
        check("set/get round-trips a fact", out.strip() == "1.2.3", out)
        out = run("fail", "--module", "MeshWeaver.Maps", "--phase", "test", "--reason", "3 tests failed")
        check("fail prints a ::error naming module and phase", "::error title=Module MeshWeaver.Maps failed (test)::3 tests failed" in out, out)
        out = run("list", "--ok")
        check("a failed module leaves the --ok list; siblings stay", out.split() == ["MeshWeaver.AI", "MeshWeaver.Mcp"], out)
        out = run("list", "--failed")
        check("--failed lists it", out.split() == ["MeshWeaver.Maps"], out)
        out = run("fail", "--module", "MeshWeaver.Maps", "--phase", "publish", "--reason", "later")
        check("a second failure keeps the FIRST phase", "keeping that verdict" in out and State(s).load().record("MeshWeaver.Maps")["phase"] == "test", out)
        run("fail", "--module", "MeshWeaver.Maps", "--phase", "bogus", "--reason", "x", expect=1)
        run("get", "--module", "MeshWeaver.Ghost", "version", expect=1)
        out = run("slots", "--max", "10", "--where", "bundle!=")
        check("slots fill s1.. from the --ok modules carrying the fact, rest empty",
              "s1=MeshWeaver.AI" in out and "s2=\n" in out and "s10=\n" in out, out)
        run("set", "--module", "MeshWeaver.Mcp", "bundle=/tmp/b/MeshWeaver.Mcp/y.nupkg")
        out = run("paths", "bundle")
        check("paths is a module->fact map of the --ok modules", json.loads(out.strip().splitlines()[-1]) == {
            "MeshWeaver.AI": "/tmp/b/MeshWeaver.AI/x.nupkg", "MeshWeaver.Mcp": "/tmp/b/MeshWeaver.Mcp/y.nupkg"}, out)
        summary = Path(td) / "summary.md"
        out = run("verdict", "--summary", str(summary), expect=1)
        check("verdict reds the leg over one failed module and names it",
              "1 failed" in out and "MeshWeaver.Maps" in out and "failed in test" in out, out)
        check("…and writes the per-module table to the summary",
              "| `MeshWeaver.Maps` | ❌ failed in test | 3 tests failed |" in summary.read_text(encoding="utf-8"))
        s2 = Path(td) / "green.json"
        st2 = State(s2)
        st2.init([entry("MeshWeaver.AI")])
        st2.save()
        p = subprocess.run([sys.executable, here, "--state", str(s2), "verdict"], capture_output=True, text=True)
        check("a leg with no failed module exits 0", p.returncode == 0 and "1 ok, 0 failed" in p.stdout, p.stdout)
        p = subprocess.run([sys.executable, here, "--state", str(Path(td) / "absent.json"), "list", "--ok"], capture_output=True, text=True)
        check("a missing state is a red, never an empty list", p.returncode == 1 and "did not run" in p.stdout, p.stdout)

        print("== chunk via the CLI, into GITHUB_OUTPUT")
        selp = Path(td) / "sel.json"
        selp.write_text(json.dumps(sel), encoding="utf-8")
        gho = Path(td) / "gho"
        p = subprocess.run([sys.executable, here, "chunk", "--modules", f"@{selp}", "--size", "6", "--github-output", str(gho)], capture_output=True, text=True)
        text = gho.read_text(encoding="utf-8")
        check("chunk writes batches= and batch-count=", p.returncode == 0 and "batch-count=2\n" in text and text.startswith("batches=["), p.stdout + p.stderr + text)
        check("the batches= line is one line of JSON (a GITHUB_OUTPUT value)", text.count("\n") == 2)
        p = subprocess.run([sys.executable, here, "chunk", "--modules", "[]", "--size", "6", "--github-output", str(gho)], capture_output=True, text=True)
        check("an empty selection writes batch-count=0", p.returncode == 0 and "batch-count=0\n" in gho.read_text(encoding="utf-8"))
        p = subprocess.run([sys.executable, here, "chunk", "--modules", f"@{selp}", "--size", "12"], capture_output=True, text=True)
        check("batch-size above the cap is RED", p.returncode == 1 and "1 to 10" in p.stdout, p.stdout)
        p = subprocess.run([sys.executable, here, "chunk", "--modules", f"@{selp}", "--size", "six"], capture_output=True, text=True)
        check("an unreadable batch-size is RED, not a default", p.returncode == 1 and "not an integer" in p.stdout, p.stdout)

    print()
    if failures:
        print(f"::error::{len(failures)} self-test case(s) FAILED: " + "; ".join(failures))
        return 1
    print("module-pack-batch: chunking, the per-module state, failure isolation and the verdict — all green")
    return 0


# ── main ───────────────────────────────────────────────────────────────────────────────────────

def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0])
    ap.add_argument("--state", help="the leg's state file (every subcommand but chunk)")
    ap.add_argument("--self-test", action="store_true")
    ap.add_argument("--workflow", type=Path,
                    help="reusable workflow YAML to inspect during --self-test")
    sub = ap.add_subparsers(dest="cmd")

    c = sub.add_parser("chunk", help="split a selection into batches of ≤N")
    c.add_argument("--modules", required=True, help="JSON array of matrix entries, or @file")
    c.add_argument("--size", required=True, help=f"batch size, 1..{MAX_BATCH_SIZE}")
    c.add_argument("--label-max", type=int, default=LABEL_MAX)
    c.add_argument("--github-output", help="append batches=<json> and batch-count=<n> here")
    c.add_argument("--prefix", default="", help="prefix for the two output names (e.g. test-)")

    i = sub.add_parser("init", help="start a leg's state from its matrix batch")
    i.add_argument("--batch", required=True, help="the matrix batch object (or its entries), or @file")

    l = sub.add_parser("list", help="module names, in batch order")
    l.add_argument("--ok", action="store_true", help="only modules that have not failed")
    l.add_argument("--failed", action="store_true", help="only modules that failed")
    l.add_argument("--where", action="append", help="key=value or key!=value (repeatable)")

    g = sub.add_parser("get", help="one fact or entry field of one module")
    g.add_argument("--module", required=True)
    g.add_argument("key")
    g.add_argument("--default", default="")

    se = sub.add_parser("set", help="record facts for one module")
    se.add_argument("--module", required=True)
    se.add_argument("pairs", nargs="+", metavar="KEY=VALUE")

    f = sub.add_parser("fail", help="mark one module failed in a phase; the leg continues")
    f.add_argument("--module", required=True)
    f.add_argument("--phase", required=True)
    f.add_argument("--reason", required=True)

    sl = sub.add_parser("slots", help="s1..sN outputs for the unrolled per-module upload steps")
    sl.add_argument("--max", type=int, default=MAX_BATCH_SIZE)
    sl.add_argument("--where", action="append")
    sl.add_argument("--github-output")
    sl.add_argument("--prefix", default="")

    pa = sub.add_parser("paths", help="JSON map module -> fact for the --ok modules that carry it")
    pa.add_argument("key")
    pa.add_argument("--where", action="append")

    v = sub.add_parser("verdict", help="per-module status lines; exit 1 if any module failed")
    v.add_argument("--summary", help="append the table to this file (GITHUB_STEP_SUMMARY)")

    a = ap.parse_args(argv)
    if a.self_test:
        return self_test(a.workflow)
    if a.workflow:
        die("--workflow is only valid with --self-test")
    if not a.cmd:
        ap.print_help()
        return 2
    return {
        "chunk": cmd_chunk, "init": cmd_init, "list": cmd_list, "get": cmd_get, "set": cmd_set,
        "fail": cmd_fail, "slots": cmd_slots, "paths": cmd_paths, "verdict": cmd_verdict,
    }[a.cmd](a)


if __name__ == "__main__":
    sys.exit(main())
