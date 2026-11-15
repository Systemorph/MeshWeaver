#!/usr/bin/env python3
"""The DELIVERY LINE core's CD runs for — derived once, refused by name, and held to the workflow.

WHY. A platform major is a LINE (policy `major-lines-run-separately`,
Doc/Architecture/MajorLinesRunSeparately): `main` carries one major, `main3` the previous one, and
each delivers on its own. `main-cd.yml` used to spell `main` wherever it meant "the branch I
deliver" — the tip it resolves, the Plugins branch it builds the host from, the floating image
label it moves, the ref its hand-over dispatches. One file now serves every line, so every one of
those reads the line `gate` derives HERE, and the declared lines live in
`.github/delivery-lines.json`.

COMMANDS
  derive                       the line of THIS run, as `key=value` lines for $GITHUB_OUTPUT.
                               Reads the event from the environment (CD_EVENT, CD_REF_NAME,
                               CD_DEFAULT_BRANCH, CD_RUN_EVENT, CD_RUN_HEAD_BRANCH,
                               CD_PLUGINS_OVERRIDE). A branch that is not a declared line, a
                               workflow_run or schedule that is not the default branch, a push to
                               the default branch, or a repository whose default branch is not the
                               declared one is REFUSED — exit 1 with an ::error:: naming it. There
                               is no default line.
  lines                        every declared line, one per line.
  reconcile-targets            the lines the default branch's hourly tick must dispatch, one per line.
  line-max-ordinal FOREIGN     reads tags on stdin; prints the highest set ordinal among them that is
                               NOT one of the run numbers in the file FOREIGN (one per line: the CD
                               runs of OTHER lines). Empty when there is none.
  platform-major PROPS         the platform MAJOR a line's commit declares: `<PlatformVersion>` in the
                               given Directory.Build.props. Absent or ambiguous is exit 1 — a line's
                               set selection is never handed a guessed major.
  check-workflow ROOT          the guard: no hard-coded `main` meaning "the line" in main-cd.yml, and
                               the triggers of main-cd.yml and dotnet-test.yml agree with the lines.
  --self-test                  every refusal and every answer above, against fabricated events.

Exit codes: 0 answered · 1 refused / guard red · 2 usage or an unreadable declaration.
"""

from __future__ import annotations

import importlib.util
import json
import os
import re
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
DEFAULT_FILE = HERE.parent / "delivery-lines.json"
BRANCH_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._/-]*$")
LABEL_RE = re.compile(r"^[a-z0-9][a-z0-9._-]*$")
REQUIRED = ("branch", "pluginsRef", "satelliteRef", "label", "movesLatest")


class Refusal(Exception):
    """The run is not a delivery of any declared line — said out loud, never defaulted."""


class DeclarationError(ValueError):
    """delivery-lines.json is missing or malformed."""


# ─────────────────────────────── the declaration ───────────────────────────────

def validate(doc) -> list[str]:
    if not isinstance(doc, dict):
        return ["the declaration's root must be an object"]
    problems = []
    if doc.get("schema") != 1:
        problems.append(f"`schema` must be 1, got {doc.get('schema')!r}")
    default = doc.get("defaultBranch")
    if not isinstance(default, str) or not BRANCH_RE.match(default):
        problems.append(f"`defaultBranch` must name a branch, got {default!r}")
    lines = doc.get("lines")
    if not isinstance(lines, list) or not lines:
        return problems + ["`lines` must be a non-empty list — a declaration with no line delivers nothing"]
    branches, labels, movers = set(), set(), []
    for i, line in enumerate(lines):
        where = f"lines[{i}]"
        if not isinstance(line, dict):
            problems.append(f"{where} is not an object")
            continue
        for field in REQUIRED:
            if field not in line:
                problems.append(f"{where} has no `{field}`")
        for field in ("branch", "pluginsRef", "satelliteRef"):
            value = line.get(field)
            if field in line and not (isinstance(value, str) and BRANCH_RE.match(value)):
                problems.append(f"{where}: `{field}` must name a branch, got {value!r}")
        label = line.get("label")
        if "label" in line and not (isinstance(label, str) and LABEL_RE.match(label)):
            problems.append(f"{where}: `label` must be a lower-case image tag, got {label!r}")
        if "movesLatest" in line and not isinstance(line.get("movesLatest"), bool):
            problems.append(f"{where}: `movesLatest` must be true or false")
        branch = line.get("branch")
        # Only a STRING enters the duplicate sets: an object or an array is unhashable, and a
        # TypeError there would escape load() as a traceback instead of the named refusal (exit 2).
        # Its own wrong type is already a problem above.
        if isinstance(branch, str):
            if branch in branches:
                problems.append(f"{where}: duplicate branch {branch!r}")
            branches.add(branch)
        if isinstance(label, str):
            if label in labels:
                problems.append(f"{where}: duplicate label {label!r} — two lines would move one pointer")
            labels.add(label)
        if line.get("movesLatest") is True:
            movers.append(branch)
    if len(movers) != 1:
        problems.append(f"exactly ONE line may move `latest`, got {movers} — two lines interleave run "
                        "numbers, so a forward-only check alone would hand `latest` between majors")
    if isinstance(default, str) and default not in branches:
        problems.append(f"the default branch {default!r} is not a declared line")
    return problems


def load(path: Path = DEFAULT_FILE) -> dict:
    try:
        doc = json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError as error:
        raise DeclarationError(f"{path} is missing — no line can be delivered without it") from error
    except json.JSONDecodeError as error:
        raise DeclarationError(f"{path} is not valid JSON: {error}") from error
    problems = validate(doc)
    if problems:
        raise DeclarationError(f"{path} is malformed: " + "; ".join(problems))
    return doc


def by_branch(doc: dict) -> dict[str, dict]:
    return {line["branch"]: line for line in doc["lines"]}


# ─────────────────────────────── derive ───────────────────────────────

def derive(doc: dict, event: str, ref_name: str, default_branch: str,
           run_event: str = "", run_head_branch: str = "", plugins_override: str = "") -> dict[str, str]:
    """The line of one run. Every path that is not a delivery of a declared line raises Refusal."""
    declared_default = doc["defaultBranch"]
    # An event whose payload carries no repository object (a schedule's may not) reports nothing,
    # and then the DECLARATION is the answer — the one thing never consulted is a guess.
    default_branch = default_branch.strip() or declared_default
    if default_branch != declared_default:
        raise Refusal(
            f"this repository's default branch is {default_branch!r}, but .github/delivery-lines.json "
            f"declares {declared_default!r}. workflow_run and schedule run the DEFAULT branch's copy of "
            "main-cd.yml, so which line they deliver is exactly this fact — update the declaration in "
            "the same change that moves the default branch.")
    if event == "workflow_run":
        if run_event != "push":
            raise Refusal(f"a workflow_run from a {run_event or '<no>'!r} run is not a delivery — only a "
                          "PUSH's Build-and-Test is (a pull request's head_branch can be any name)")
        if run_head_branch != default_branch:
            raise Refusal(
                f"a workflow_run for branch {run_head_branch!r} reached main-cd.yml. workflow_run always "
                f"runs the DEFAULT branch's ({default_branch!r}) copy of this file, so it must never deliver "
                "another line's commit — that line delivers through its own push trigger and dispatch.")
        branch = run_head_branch
    elif event == "schedule":
        if ref_name != default_branch:
            raise Refusal(f"a schedule ran on {ref_name!r}, not the default branch {default_branch!r}")
        branch = default_branch
    elif event in ("push", "workflow_dispatch"):
        branch = ref_name
        if event == "push" and branch == default_branch:
            raise Refusal(
                f"a push to the default branch {branch!r} started main-cd.yml. The default line delivers "
                "AFTER Build-and-Test, through workflow_run; a push delivery would race the tests. Drop "
                "it from `on.push.branches`.")
    else:
        raise Refusal(f"event {event!r} is not a delivery event of main-cd.yml")
    lines = by_branch(doc)
    if branch not in lines:
        raise Refusal(
            f"branch {branch!r} is not a delivery line. The declared lines are {sorted(lines)} "
            "(.github/delivery-lines.json). CD delivers a declared line or nothing — never a guessed one.")
    line = lines[branch]
    return {
        "line": branch,
        "label": line["label"],
        "plugins_ref": plugins_override.strip() or line["pluginsRef"],
        "satellite_ref": line["satelliteRef"],
        "moves_latest": "true" if line["movesLatest"] else "false",
        "default_line": "true" if branch == default_branch else "false",
    }


def reconcile_targets(doc: dict) -> list[str]:
    return [line["branch"] for line in doc["lines"] if line["branch"] != doc["defaultBranch"]]


# ─────────────────────────────── the line's major ───────────────────────────────

PLATFORM_VERSION_RE = re.compile(r"<PlatformVersion\b[^>]*>\s*(\d+)\.(\d+)\.(\d+)[^<\s]*\s*</PlatformVersion>")


def platform_major(props_text: str) -> int:
    """The major of the ONE `<PlatformVersion>` a Directory.Build.props declares. The line's set
    selection (arm, armed-state, the promotion record's base) keys on it, so it is read from the
    line's own commit — never a number written into the workflow."""
    found = {int(m.group(1)) for m in PLATFORM_VERSION_RE.finditer(props_text)}
    if len(found) != 1:
        raise Refusal(f"Directory.Build.props declares {len(found)} distinct PlatformVersion majors "
                      f"({sorted(found)}) — the line's major cannot be read, and is never guessed")
    return found.pop()


# ─────────────────────────────── never-backwards, per line ───────────────────────────────

def _platform_version():
    spec = importlib.util.spec_from_file_location("mw_platform_version_cdline", HERE / "platform-version.py")
    if spec is None or spec.loader is None:
        raise DeclarationError("cannot load platform-version.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def line_max_ordinal(tags: list[str], foreign: set[int]) -> int | None:
    """The newest set ordinal among `tags` that another line's CD run did NOT mint. Every line's sets
    share one run counter (one workflow), so the line's own never-backwards check must not let a
    NEWER set of another line hold its pointer back forever."""
    pv = _platform_version()
    return pv.max_ordinal([t for t in tags if (n := pv.ordinal(t)) is not None and n not in foreign])


# ─────────────────────────────── the guard ───────────────────────────────

CD_WORKFLOW = ".github/workflows/main-cd.yml"
TEST_WORKFLOW = ".github/workflows/dotnet-test.yml"

# Shapes in which `main` names "the branch this run delivers" — in CODE, not prose. A comment line is
# never scanned; message text ("main `abc1234` cannot be published") is not one of these shapes.
LINE_LITERALS = [
    (re.compile(r"""(?<![A-Za-z0-9_.-])(?:'main'|"main")"""), "a quoted 'main'"),
    (re.compile(r"commits/main(?![A-Za-z0-9_.-])"), "commits/main"),
    (re.compile(r"[?&]sha=main(?![A-Za-z0-9_.-])"), "?sha=main"),
    (re.compile(r"--ref\s+main(?![A-Za-z0-9_.-])"), "--ref main"),
    (re.compile(r"refs/heads/main(?![A-Za-z0-9_.-])"), "refs/heads/main"),
    # `<repo>:main` / `$repo:main` / `}}:main` — an image reference, never `::error::main …`.
    (re.compile(r"(?:[A-Za-z0-9_}-]|\$\w+):main(?![A-Za-z0-9_.-])"), "an image tag :main"),
    (re.compile(r"\bref:\s*main\s*$"), "a checkout ref: main"),
    (re.compile(r"""\bfor\s+\w+\s+in\b.*(?<![A-Za-z0-9_.$/-])main(?![A-Za-z0-9_.-])"""), "a tag loop over main"),
    (re.compile(r"branches:\s*\[[^\]]*(?<![A-Za-z0-9_.-])main(?![A-Za-z0-9_.-])"), "a branches: filter"),
]

# The ONE place `main` may stay spelled, and why — keyed on its POSITION (the YAML path of the key
# the line belongs to), never on its text: the same `branches: [main]` under `on.push` or any other
# trigger is a finding, because only workflow_run's reason applies to workflow_run.
ALLOWED = {
    ("on", "workflow_run", "branches"):
        "on.workflow_run.branches: workflow_run runs the DEFAULT branch's copy of this file, so it may "
        "only ever deliver the default line; the guard below holds it equal to delivery-lines.json's "
        "defaultBranch.",
}

_KEY_RE = re.compile(r"^(?P<indent> *)(?P<key>[A-Za-z_][A-Za-z0-9_-]*|'[^']*'|\"[^\"]*\"):(?:\s|$)")


def _key_paths(text: str) -> dict[int, tuple[str, ...]]:
    """Line number → the YAML path of the mapping key that line opens (`("on", "workflow_run",
    "branches")`), for every line that opens one. Indentation-tracked, which is all a workflow's
    block-style mappings need; a line inside a `run: |` scalar is never a key (its indent is deeper
    than any key it could be read as, and it is not on the stack's path)."""
    paths: dict[int, tuple[str, ...]] = {}
    stack: list[tuple[int, str]] = []
    block_indent: int | None = None
    for number, raw in enumerate(text.splitlines(), 1):
        if not raw.strip() or raw.lstrip().startswith("#"):
            continue
        indent = len(raw) - len(raw.lstrip(" "))
        if block_indent is not None:
            if indent > block_indent:
                continue            # inside a block scalar (`run: |`, `>-`)
            block_indent = None
        m = _KEY_RE.match(raw)
        if not m:
            continue
        while stack and stack[-1][0] >= indent:
            stack.pop()
        key = m.group("key").strip("'\"")
        stack.append((indent, key))
        paths[number] = tuple(k for _, k in stack)
        rest = raw[m.end():].strip()
        if rest.startswith(("|", ">")):
            block_indent = indent
    return paths


def _code_lines(text: str):
    for number, raw in enumerate(text.splitlines(), 1):
        stripped = raw.strip()
        if not stripped or stripped.startswith("#"):
            continue
        yield number, stripped


def literal_findings(text: str) -> list[str]:
    out = []
    paths = _key_paths(text)
    for number, stripped in _code_lines(text):
        if paths.get(number) in ALLOWED:
            continue
        for pattern, what in LINE_LITERALS:
            if pattern.search(stripped):
                out.append(f"{CD_WORKFLOW}:{number}: {what} — `{stripped[:140]}`. Read the line `gate` "
                           "derives (needs.gate.outputs.line / label / plugins_ref) instead; if this `main` "
                           "really is not the line, add it to cd-line.py ALLOWED with the reason.")
                break
    return out


def _on(doc: dict) -> dict:
    # PyYAML reads the bare key `on` as the boolean True.
    value = doc.get("on", doc.get(True))
    return value if isinstance(value, dict) else {}


def _branches(trigger) -> list[str]:
    if isinstance(trigger, dict):
        value = trigger.get("branches") or []
        return [str(b) for b in value] if isinstance(value, list) else []
    return []


def trigger_findings(doc: dict, cd: dict, test: dict) -> list[str]:
    out = []
    default = doc["defaultBranch"]
    others = sorted(reconcile_targets(doc))
    every = sorted(by_branch(doc))
    on_cd, on_test = _on(cd), _on(test)
    run_branches = _branches(on_cd.get("workflow_run"))
    if run_branches != [default]:
        out.append(f"{CD_WORKFLOW}: on.workflow_run.branches is {run_branches}, must be exactly [{default!r}] "
                   "— workflow_run runs the default branch's file and must deliver nothing else")
    push_branches = sorted(_branches(on_cd.get("push")))
    if push_branches != others:
        out.append(f"{CD_WORKFLOW}: on.push.branches is {push_branches}, must be exactly the non-default "
                   f"lines {others} — each delivers with ITS OWN copy of the file; the default line never "
                   "delivers on push (it would race Build-and-Test)")
    if "schedule" not in on_cd or "workflow_dispatch" not in on_cd:
        out.append(f"{CD_WORKFLOW}: the schedule and workflow_dispatch triggers are the reconciler — both must stay")
    test_push = sorted(_branches(on_test.get("push")))
    if test_push != every:
        out.append(f"{TEST_WORKFLOW}: on.push.branches is {test_push}, must be every line {every} — the "
                   "reconcile only ships a commit whose `Consolidate test results` is green, and only a "
                   "push run puts that check on a landed commit")
    jobs = cd.get("jobs") or {}
    if "reconcile-lines" not in jobs:
        out.append(f"{CD_WORKFLOW}: no `reconcile-lines` job — nothing would reconcile {others} hourly")
    return out


def concurrency_findings(doc: dict, test_text: str) -> list[str]:
    """Build-and-Test's per-commit concurrency group and its cancel-in-progress name every delivery
    line, and no other branch (policy `major-lines-run-separately`). A line missing from the group
    would group its pushes by branch NAME — landed commits evicting each other from the pending
    slot — and one missing from cancel-in-progress would cancel its own runs: either way commits
    of that line never get the green check CD ships on."""
    out = []
    every = sorted(by_branch(doc))
    group = re.search(r"^\s*group:\s*(?P<v>.+?)\s*$", test_text, re.MULTILINE)
    cancel = re.search(r"^\s*cancel-in-progress:\s*(?P<v>.+?)\s*$", test_text, re.MULTILINE)
    if not group or not cancel:
        return [f"{TEST_WORKFLOW}: no concurrency group / cancel-in-progress found — this guard cannot see them"]
    in_group = sorted(set(re.findall(r"github\.ref\s*==\s*'refs/heads/([^']+)'", group.group("v"))))
    in_cancel = sorted(set(re.findall(r"github\.ref\s*!=\s*'refs/heads/([^']+)'", cancel.group("v"))))
    if in_group != every:
        out.append(f"{TEST_WORKFLOW}: the concurrency group gives a per-commit group to {in_group}, must be every "
                   f"delivery line {every} — a line grouped by branch NAME evicts its own landed commits")
    if in_cancel != every:
        out.append(f"{TEST_WORKFLOW}: cancel-in-progress exempts {in_cancel}, must exempt every delivery line "
                   f"{every} — a line it does not exempt cancels its own runs on every merge")
    return out


def check_workflow(root: Path) -> list[str]:
    sys.path.insert(0, str(HERE))
    import workflow_yaml  # noqa: PLC0415 — the shared extractor; needs PyYAML, as every harness does
    doc = load(root / ".github" / "delivery-lines.json")
    text = (root / CD_WORKFLOW).read_text(encoding="utf-8")
    return (literal_findings(text)
            + trigger_findings(doc, workflow_yaml.load(root / CD_WORKFLOW), workflow_yaml.load(root / TEST_WORKFLOW))
            + concurrency_findings(doc, (root / TEST_WORKFLOW).read_text(encoding="utf-8")))


# ─────────────────────────────── self-test ───────────────────────────────

def self_test() -> int:
    failures: list[str] = []

    def expect(name: str, cond: bool, detail: str = "") -> None:
        print(f"  {'PASS' if cond else 'FAIL'}  {name}" + (f" — {detail}" if detail and not cond else ""))
        if not cond:
            failures.append(name)

    def refused(fn, *args, **kwargs) -> str:
        try:
            fn(*args, **kwargs)
        except Refusal as error:
            return str(error) or "refused"
        return ""

    doc = load()
    expect("the shipped declaration loads and validates", True)
    expect("the default branch is main and it moves `latest`",
           doc["defaultBranch"] == "main" and by_branch(doc)["main"]["movesLatest"] is True)

    # The fabricated events the brief names, one per entry shape.
    m = derive(doc, "workflow_run", "main", "main", run_event="push", run_head_branch="main")
    # The Plugins and satellite refs are the DECLARED ones (the first 4.0 set builds main against a
    # Plugins branch other than main for one bootstrap commit), so the case compares to the file, not a literal.
    declared_main = by_branch(doc)["main"]
    expect("workflow_run for a push to main → line main, its declared Plugins/satellite refs, label main, moves latest",
           m == {"line": "main", "label": "main", "plugins_ref": declared_main["pluginsRef"],
                 "satellite_ref": declared_main["satelliteRef"], "moves_latest": "true", "default_line": "true"}, str(m))
    expect("workflow_run for main3 → REFUSED (main's file must never deliver main3)",
           "DEFAULT branch" in refused(derive, doc, "workflow_run", "main", "main", run_event="push",
                                       run_head_branch="main3"))
    expect("workflow_run from a pull_request → REFUSED",
           bool(refused(derive, doc, "workflow_run", "main", "main", run_event="pull_request",
                        run_head_branch="main")))
    s = derive(doc, "schedule", "main", "main")
    expect("schedule → line main", s["line"] == "main" and s["default_line"] == "true", str(s))
    expect("schedule on a non-default ref → REFUSED", bool(refused(derive, doc, "schedule", "main3", "main")))
    p3 = derive(doc, "push", "main3", "main")
    expect("push main3 → line main3, Plugins main3, satellites main3, label main3, does NOT move latest",
           p3 == {"line": "main3", "label": "main3", "plugins_ref": "main3", "satellite_ref": "main3",
                  "moves_latest": "false", "default_line": "false"}, str(p3))
    expect("push main → REFUSED (the default line delivers after the tests, on workflow_run)",
           "workflow_run" in refused(derive, doc, "push", "main", "main"))
    d3 = derive(doc, "workflow_dispatch", "main3", "main")
    expect("dispatch ref main3 → line main3", d3["line"] == "main3" and d3["plugins_ref"] == "main3", str(d3))
    dm = derive(doc, "workflow_dispatch", "main", "main")
    expect("dispatch ref main → line main", dm["line"] == "main" and dm["label"] == "main", str(dm))
    expect("dispatch on an unknown ref → REFUSED by name",
           "feat/x" in refused(derive, doc, "workflow_dispatch", "feat/x", "main"))
    expect("push to an undeclared branch → REFUSED", bool(refused(derive, doc, "push", "release/9", "main")))
    expect("an unknown event → REFUSED", bool(refused(derive, doc, "pull_request", "main", "main")))
    expect("a payload without a default branch falls back to the DECLARED one, never a guess",
           derive(doc, "schedule", "main", "")["line"] == "main"
           and bool(refused(derive, doc, "schedule", "main3", "")))
    expect("a repository whose default branch moved → REFUSED until the declaration follows",
           "default branch" in refused(derive, doc, "schedule", "main4", "main4"))
    o = derive(doc, "workflow_dispatch", "main3", "main", plugins_override="hotfix/x")
    expect("vars.MW_PLUGINS_REF still overrides the line's Plugins branch", o["plugins_ref"] == "hotfix/x", str(o))
    o = derive(doc, "workflow_dispatch", "main3", "main", plugins_override="   ")
    expect("a blank override is no override", o["plugins_ref"] == "main3", str(o))
    expect("the hourly tick reconciles exactly the non-default lines", reconcile_targets(doc) == ["main3"])

    # The declaration's own refusals.
    def broken(mutate) -> list[str]:
        copy = json.loads(json.dumps(doc))
        mutate(copy)
        return validate(copy)
    expect("two lines moving `latest` is malformed",
           any("exactly ONE" in p for p in broken(lambda d: d["lines"][1].update(movesLatest=True))))
    expect("no line moving `latest` is malformed",
           any("exactly ONE" in p for p in broken(lambda d: d["lines"][0].update(movesLatest=False))))
    expect("a duplicate label is malformed",
           any("duplicate label" in p for p in broken(lambda d: d["lines"][1].update(label="main"))))
    expect("an undeclared default branch is malformed",
           any("not a declared line" in p for p in broken(lambda d: d.update(defaultBranch="trunk"))))
    expect("an empty line list is malformed", bool(broken(lambda d: d.update(lines=[]))))
    for field in ("branch", "label"):
        for bad in ({"x": 1}, ["main"]):
            try:
                problems = broken(lambda d, f=field, v=bad: d["lines"][1].update({f: v}))
                expect(f"a non-string {field} ({type(bad).__name__}) is a NAMED problem, never a TypeError",
                       any(f"`{field}` must" in p for p in problems), "; ".join(problems))
            except TypeError as error:
                expect(f"a non-string {field} ({type(bad).__name__}) is a NAMED problem, never a TypeError",
                       False, f"TypeError: {error}")
    with tempfile.TemporaryDirectory() as tmp:
        unhashable = json.loads(json.dumps(doc))
        unhashable["lines"][1]["branch"] = {"x": 1}
        bad_file = Path(tmp) / "unhashable.json"
        bad_file.write_text(json.dumps(unhashable), encoding="utf-8")
        expect("cli: an object-valued branch exits 2 with the named refusal, never a traceback",
               main(["x", "derive"], env={"CD_EVENT": "schedule", "CD_REF_NAME": "main"}, declaration=bad_file) == 2)
    expect("a missing field is malformed", bool(broken(lambda d: d["lines"][0].pop("pluginsRef"))))

    # The line's major, from its own Directory.Build.props.
    props = (HERE.parent.parent / "Directory.Build.props").read_text(encoding="utf-8")
    try:
        real_major = platform_major(props)
        expect("platform-major reads this tree's PlatformVersion", isinstance(real_major, int) and real_major >= 3)
    except Refusal as error:
        expect("platform-major reads this tree's PlatformVersion", False, str(error))
    expect("platform-major: 4.0.0 → 4",
           platform_major("<PlatformVersion Condition=\"'$(PlatformVersion)' == ''\">4.0.0</PlatformVersion>") == 4)
    expect("platform-major: no PlatformVersion → REFUSED", bool(refused(platform_major, "<Project/>")))
    expect("platform-major: two different majors → REFUSED",
           bool(refused(platform_major, "<PlatformVersion>3.1.0</PlatformVersion><PlatformVersion>4.0.0</PlatformVersion>")))

    # Never-backwards, per line.
    tags = ["3.1.100", "3.1.105", "3.1.103", "main", "main3", "3-latest", "abc1234"]
    expect("with no foreign run the line's max is the max of every set (main's behaviour today)",
           line_max_ordinal(tags, set()) == 105)
    expect("another line's newer set does not hold this line's pointer back",
           line_max_ordinal(tags, {105}) == 103)
    expect("only foreign sets → none", line_max_ordinal(["3.1.105", "main"], {105}) is None)

    # The guard, on the real files and on planted breaks.
    root = HERE.parent.parent
    try:
        real = check_workflow(root)
    except ModuleNotFoundError as error:
        print(f"::error::the guard cannot run — {error}. pip install pyyaml.")
        return 1
    expect("the shipped main-cd.yml and dotnet-test.yml are clean", real == [], "; ".join(real))
    cd_text = (root / CD_WORKFLOW).read_text(encoding="utf-8")
    for planted, why in (
            ('          tip=$(gh api "repos/x/commits/main" --jq .sha)', "commits/main"),
            ("          PLUGINS_REF: ${{ vars.MW_PLUGINS_REF || 'main' }}", "a quoted 'main'"),
            ('          gh workflow run main-cd.yml --ref main', "--ref main"),
            ('          docker buildx imagetools create --tag "$ACR/mw-plugin-test:main" x', "an image tag :main"),
            ("      content-ref: main", "a checkout ref: main"),
            ('          for t in "$SHA" main; do', "a tag loop over main"),
            ("       github.event.workflow_run.head_branch == 'main')", "a quoted 'main'"),
            ('          done < <(gh api "repos/x/commits?sha=main&per_page=10")', "?sha=main")):
        found = literal_findings(cd_text + "\n" + planted + "\n")
        expect(f"guard: a planted {why} is caught", any(why in f for f in found), "; ".join(found) or "nothing found")
    expect("guard: message prose (`::error::main …`, `main's HEAD`) is not a finding",
           literal_findings('          echo "::error::main `$SHORT` is GREEN"\n'
                            '          --body "main\'s HEAD is missing"\n') == [])
    expect("guard: `$repo:main` is caught", bool(literal_findings('  x "${{ env.ACR }}/$repo:main"\n')))
    expect("guard: a comment that says main is not a finding",
           literal_findings("# resolves commits/main and tags :main\n") == [])
    expect("guard: the allowed filter, UNDER on.workflow_run, is not a finding",
           literal_findings("on:\n  workflow_run:\n    types: [completed]\n    branches: [main]\n") == [])
    expect("guard: the SAME text under on.push is a finding (the exemption is positional, not textual)",
           any("branches: filter" in f for f in
               literal_findings("on:\n  workflow_run:\n    branches: [main]\n  push:\n    branches: [main]\n")))
    expect("guard: the same text under a job (not a trigger) is a finding",
           bool(literal_findings("jobs:\n  x:\n    branches: [main]\n")))
    expect("guard: a key-shaped line inside a `run: |` block is not read as a key",
           _key_paths("jobs:\n  a:\n    run: |\n      on: x\n      branches: [main]\n").get(5) is None)
    test_text = (root / TEST_WORKFLOW).read_text(encoding="utf-8")
    expect("guard: Build-and-Test's group and cancel-in-progress name every line (shipped file)",
           concurrency_findings(doc, test_text) == [], "; ".join(concurrency_findings(doc, test_text)))
    third = json.loads(json.dumps(doc))
    third["lines"].append({"branch": "main2", "pluginsRef": "main2", "satelliteRef": "main",
                           "label": "main2", "movesLatest": False})
    found = concurrency_findings(third, test_text)
    expect("guard: a THIRD declared line that the group and cancel-in-progress do not name is caught (both)",
           len(found) == 2 and all("main2" in f for f in found), "; ".join(found))
    dropped = test_text.replace(" || github.ref == 'refs/heads/main3'", "", 1)
    expect("guard: main3 dropped from the per-commit group is caught",
           dropped != test_text and any("concurrency group" in f for f in concurrency_findings(doc, dropped)))
    cd_doc = {"on": {"workflow_run": {"branches": ["main"]}, "push": {"branches": ["main3"]},
                     "schedule": [{}], "workflow_dispatch": {}}, "jobs": {"reconcile-lines": {}}}
    test_doc = {"on": {"push": {"branches": ["main", "main3"]}}}
    expect("guard: consistent triggers are clean", trigger_findings(doc, cd_doc, test_doc) == [])
    bad = json.loads(json.dumps(cd_doc))
    bad["on"]["workflow_run"]["branches"] = ["main", "main3"]
    expect("guard: workflow_run listening on main3 is caught",
           any("workflow_run" in f for f in trigger_findings(doc, bad, test_doc)))
    bad = json.loads(json.dumps(cd_doc))
    bad["on"]["push"]["branches"] = ["main", "main3"]
    expect("guard: a push trigger on the default branch is caught",
           any("on.push" in f for f in trigger_findings(doc, bad, test_doc)))
    bad = json.loads(json.dumps(cd_doc))
    del bad["on"]["push"]
    expect("guard: a missing push trigger for main3 is caught",
           any("on.push" in f for f in trigger_findings(doc, bad, test_doc)))
    expect("guard: Build-and-Test not running on main3 pushes is caught",
           any(TEST_WORKFLOW in f for f in trigger_findings(doc, cd_doc, {"on": {"push": {"branches": ["main"]}}})))
    bad = json.loads(json.dumps(cd_doc))
    bad["jobs"] = {}
    expect("guard: a missing reconcile-lines job is caught",
           any("reconcile-lines" in f for f in trigger_findings(doc, bad, test_doc)))
    expect("guard: PyYAML's `True` key for `on` is read", _on({True: {"push": {}}}) == {"push": {}})

    # The CLI, end to end — exit codes are the contract the workflow reads.
    env = {"CD_EVENT": "workflow_dispatch", "CD_REF_NAME": "nope", "CD_DEFAULT_BRANCH": "main"}
    expect("cli: derive on an unknown ref exits 1", main(["x", "derive"], env=env) == 1)
    env = {"CD_EVENT": "push", "CD_REF_NAME": "main3", "CD_DEFAULT_BRANCH": "main"}
    expect("cli: derive on main3 exits 0", main(["x", "derive"], env=env) == 0)
    with tempfile.TemporaryDirectory() as tmp:
        missing = Path(tmp) / "absent.json"
        expect("cli: an absent declaration exits 2, never a line",
               main(["x", "derive"], env=env, declaration=missing) == 2)

    if failures:
        print(f"::error::cd-line.py self-test: {len(failures)} case(s) failed: " + ", ".join(failures))
        return 1
    print("cd-line.py self-test: all cases passed")
    return 0


def main(argv: list[str], env=None, declaration: Path = DEFAULT_FILE) -> int:
    env = os.environ if env is None else env
    args = argv[1:]
    if args == ["--self-test"]:
        return self_test()
    try:
        if args == ["derive"]:
            doc = load(declaration)
            try:
                out = derive(doc, env.get("CD_EVENT", ""), env.get("CD_REF_NAME", ""),
                             env.get("CD_DEFAULT_BRANCH", ""), env.get("CD_RUN_EVENT", ""),
                             env.get("CD_RUN_HEAD_BRANCH", ""), env.get("CD_PLUGINS_OVERRIDE", ""))
            except Refusal as error:
                print(f"::error title=Not a delivery line::{error}")
                return 1
            for key, value in out.items():
                print(f"{key}={value}")
            return 0
        if args == ["lines"]:
            print("\n".join(line["branch"] for line in load(declaration)["lines"]))
            return 0
        if args == ["reconcile-targets"]:
            print("\n".join(reconcile_targets(load(declaration))))
            return 0
        if len(args) == 2 and args[0] == "line-max-ordinal":
            foreign = {int(x) for x in Path(args[1]).read_text(encoding="utf-8").split() if x.isdigit()}
            n = line_max_ordinal(sys.stdin.read().split(), foreign)
            if n is not None:
                print(n)
            return 0
        if len(args) == 2 and args[0] == "platform-major":
            try:
                print(platform_major(Path(args[1]).read_text(encoding="utf-8")))
            except (Refusal, OSError) as error:
                print(f"::error::{error}")
                return 1
            return 0
        if len(args) == 2 and args[0] == "check-workflow":
            problems = check_workflow(Path(args[1]))
            for p in problems:
                print(f"::error::{p}")
            if problems:
                return 1
            print("cd-line: main-cd.yml reads the derived line everywhere, and the triggers match the declared lines")
            return 0
    except DeclarationError as error:
        print(f"::error::{error}")
        return 2
    print(__doc__, file=sys.stderr)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv))
