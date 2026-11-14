#!/usr/bin/env python3
"""affected-tests.py — which test projects a change set owes (Systemorph/MeshWeaver).

Doc: Doc/Architecture/CoreTestSelection. The fleet rule "tests only for changed functionality",
applied to core: the diff is mapped to the ProjectReference graph and only the test projects whose
transitive references reach a changed project run — or ALL when the change is anything the
selection cannot attribute, or NONE when nothing a .NET test could see changed.

Rules (deliberately conservative — a wrong "none" is a skipped gate, a wrong "all" costs minutes):
  * `src/<P>/…` or `test/<P>/…` → project P changed; a test project is owed when its transitive
    ProjectReference closure contains a changed project, or it changed itself.
  * `docs/**`, `.claude/**`, `*.md` anywhere, `CODEOWNERS`, `.github/ISSUE_TEMPLATE/**`,
    `.github/*.txt` → nothing a test reads: contributes no project and forces nothing.
  * `.github/**` is split by WHO CONSUMES IT, derived from the tree (never hand-listed):
      - `.github/workflows/dotnet-test.yml`, and every `.github/**` file the .NET LANE consumes —
        referenced from a lane job of dotnet-test.yml, or from a reusable workflow / composite
        action / script those jobs reach, transitively — → ALL. The lane is every job of
        dotnet-test.yml EXCEPT the CI's-own-shell jobs (`workflow-shell*`) and the stage-0 static
        controls the stage gate names in `stage0-results`; a job added later is lane by default.
      - a `.github/**` file a TEST PROJECT reads (a `.github/…` path in its sources, or a
        `Path.Combine(…, ".github", …)`) → that test project is owed, nothing more.
      - any other `.github/**` file → contributes no .NET test project. Its own CI is the
        `workflow-shell*` jobs, which run on every pull request — and that is CHECKED here, not
        assumed: if the workflow's `pull_request` trigger carries a path filter, or a shell job
        carries a job-level `if:`/`needs:`, or no shell job exists, this rule is OFF and every
        such file forces ALL again.
      - if the derivation cannot run (no dotnet-test.yml, no `jobs:`, no `build`/`test` job, a
        parse that finds nothing) every `.github/**` file forces ALL — UNKNOWN fails safe.
  * anything else (`Directory.*.props|targets`, `global.json`, `nuget.config`, `*.slnx`,
    `test/*.allow`, `test/appsettings.json`, `tools/**`, `deploy/**`, …) → ALL.
  * an empty diff, a `merge_group`, a `push` to main, a `schedule` or `workflow_dispatch` → ALL
    (the caller decides the event; `--all` forces it).

Output (stdout, and `$GITHUB_OUTPUT` when set): `mode=all|incremental|none`,
`projects=<comma-separated test project names>` (empty for all/none), `reason=<one line>`.
`--self-test` runs the rules against synthetic trees (and, when `--root` holds dotnet-test.yml, a
canary over the real derivation) and exits non-zero on any wrong answer.
`--explain` prints the derived lane-consumed set and the test-project readers.
"""
from __future__ import annotations
import argparse, os, re, subprocess, sys
from dataclasses import dataclass, field
from pathlib import Path

SAFE = re.compile(r"^(docs/|\.claude/|CODEOWNERS$|\.github/ISSUE_TEMPLATE/|\.github/[^/]+\.txt$)|\.md$")
PROJECT = re.compile(r"^(src|test)/([^/]+)/")

LANE_WORKFLOW = ".github/workflows/dotnet-test.yml"
SELF = ".github/scripts/affected-tests.py"
SHELL_JOB_PREFIX = "workflow-shell"
# Jobs the derivation must find, or it does not trust its own parse.
REQUIRED_LANE_JOBS = ("build", "test")
# Basenames too generic to attribute by name alone; reached through their directory instead.
GENERIC_BASENAMES = {"action.yml", "action.yaml", "README.md", "__init__.py"}

# A `.github/<path>` reference in any text (YAML, shell, Python, C#; MSBuild's backslashes are
# normalised to `/` by `_read`). A trailing `*` marks a glob, read as a PREFIX:
# `.github/workflows/node-repo-*.yml` owes every file `.github/workflows/node-repo-` starts.
GH_PATH = re.compile(r"\.github/[A-Za-z0-9_./-]*(\*)?")
# `Path.Combine(<anything>, ".github", "a", "b" …)` — the literal segments after ".github".
PATH_COMBINE = re.compile(r'Path\.Combine\([^;]*?"\.github"((?:\s*,\s*"[^"]*")*)\s*[,)]')
BARE_GH_LITERAL = re.compile(r'"\.github"')
USES_WORKFLOW = re.compile(r"uses:\s*['\"]?(?:\./|[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+/)(\.github/workflows/[A-Za-z0-9_.-]+)")
PY_IMPORT = re.compile(r"^\s*(?:from\s+([A-Za-z_][A-Za-z0-9_]*)\s+import|import\s+([A-Za-z_][A-Za-z0-9_]*))", re.M)
TOKEN = re.compile(r"[A-Za-z0-9_.-]+")


# ── the ProjectReference graph ──────────────────────────────────────────────────────────────

def project_graph(root: Path) -> tuple[dict[str, set[str]], set[str]]:
    """name → referenced project names (direct), and the set of names under test/."""
    refs: dict[str, set[str]] = {}; tests: set[str] = set()
    for top in ("src", "test", "tools"):
        for csproj in (root / top).rglob("*.csproj"):
            if "/bin/" in str(csproj) or "/obj/" in str(csproj): continue
            name = csproj.stem
            text = csproj.read_text(encoding="utf-8", errors="replace")
            refs[name] = {Path(m.replace("\\", "/")).stem for m in re.findall(r'ProjectReference\s+Include="([^"]+)"', text)}
            if top == "test": tests.add(name)
    return refs, tests

def closure(name: str, refs: dict[str, set[str]], memo: dict[str, set[str]]) -> set[str]:
    if name in memo: return memo[name]
    memo[name] = set(); out = set()
    stack = list(refs.get(name, ()))
    while stack:
        n = stack.pop()
        if n in out: continue
        out.add(n); stack.extend(refs.get(n, ()))
    memo[name] = out; return out


# ── who consumes a .github file ────────────────────────────────────────────────────────────

@dataclass
class Derivation:
    """What the tree says about `.github/**`. `error` set ⇒ nothing below may be trusted."""
    error: str | None = None
    lane_jobs: list[str] = field(default_factory=list)
    shell_jobs: list[str] = field(default_factory=list)
    stage0_jobs: list[str] = field(default_factory=list)
    lane_prefixes: set[str] = field(default_factory=set)     # every `.github/…` prefix the lane names
    lane_files: set[str] = field(default_factory=set)        # existing files those reach, transitively
    shell_cover: str | None = None                           # None = the shell jobs run on every PR; else why not
    readers: dict[str, set[str]] = field(default_factory=dict)  # test project → `.github/…` prefixes it reads
    lane_via: dict[str, str] = field(default_factory=dict)      # lane file → what named it (for --explain)

    def lane_consumes(self, f: str) -> bool:
        return f == LANE_WORKFLOW or f in self.lane_files or any(_under(f, p) for p in self.lane_prefixes)

    def readers_of(self, f: str) -> set[str]:
        return {p for p, prefixes in self.readers.items() if any(_under(f, q) for q in prefixes)}

def _under(f: str, prefix: str) -> bool:
    return f.startswith(prefix) if prefix else False

def _read(path: Path) -> str:
    try:
        t = path.read_text(encoding="utf-8", errors="replace")
    except OSError:
        return ""
    # MSBuild writes paths with backslashes; nothing else is normalised (a C# "\n" must stay "\n").
    return t.replace("\\", "/") if path.suffix.lower() in (".csproj", ".props", ".targets") else t

def code_of(path: str, text: str) -> str:
    """`text` without its comments — a script NAMED in a comment is not a script RUN. Approximate by
    design (whole comment lines, and ` # …` tails in YAML/shell); what survives is over-read, never
    under-read, except a reference that exists ONLY in a comment, which is not a consumer."""
    ext = path.rsplit(".", 1)[-1].lower() if "." in path.rsplit("/", 1)[-1] else ""
    if ext == "py":
        # Docstrings and triple-quoted fixtures are prose and test DATA, not loads: a script that
        # runs another names it in code (`import x`, a path literal, a subprocess argv).
        text = re.sub(r'(?s)("""|\'\'\').*?\1', '""', text)
    out = []
    for l in text.splitlines():
        s = l.lstrip()
        if ext in ("cs",) and s.startswith("//"): continue
        if ext in ("yml", "yaml", "sh", "py", "") and s.startswith("#") and not s.startswith("#!"): continue
        if ext in ("cs", "csproj", "props", "targets") and s.startswith("<!--"): continue
        if ext in ("yml", "yaml", "sh"): l = re.sub(r"\s#\s.*$", "", l)
        out.append(l)
    return "\n".join(out)

PROSE_LINE = re.compile(r"\becho\b|::(error|warning|notice)|^\s*-?\s*(name|description)\s*:")

def command_refs(text: str) -> set[str]:
    """The references `text` makes OUTSIDE message prose (echo, ::error, name:, description:) —
    used to judge a whole-directory reference, which in a message is a pointer for a reader."""
    return gh_refs("\n".join(l for l in text.splitlines() if not PROSE_LINE.search(l)))

def gh_refs(text: str) -> set[str]:
    """Every `.github/…` reference `text` makes, as written: a trailing `*` (a glob) is kept so the
    resolver can read it as a prefix. A bare ".github" literal that is not the head of a resolvable
    Path.Combine names the WHOLE `.github/` tree — unknown fails safe."""
    out = {(m.group(0)[:-1] + "*") if m.group(1) else m.group(0).rstrip("./") for m in GH_PATH.finditer(text)}
    resolved = 0
    for m in PATH_COMBINE.finditer(text):
        segs = re.findall(r'"([^"]*)"', m.group(1))
        out.add("/".join([".github", *segs]).rstrip("/") if segs else ".github")
        resolved += 1
    if len(BARE_GH_LITERAL.findall(text)) > resolved:
        out.add(".github")
    return {".github" if p in (".github", ".github/") else p for p in out}

def workflow_jobs(text: str) -> dict[str, str] | None:
    """job id → its body text, by indentation (no YAML dependency — precheck runs it bare)."""
    lines = text.splitlines()
    try:
        start = next(i for i, l in enumerate(lines) if re.match(r"^jobs:\s*(#.*)?$", l))
    except StopIteration:
        return None
    jobs: dict[str, list[str]] = {}; cur = None
    for l in lines[start + 1:]:
        if re.match(r"^\S", l) and not l.startswith("#"):
            break
        m = re.match(r"^  ([A-Za-z0-9_-]+):\s*(#.*)?$", l)
        if m:
            cur = m.group(1); jobs[cur] = []
        elif cur is not None:
            jobs[cur].append(l)
    return {k: "\n".join(v) for k, v in jobs.items()}

def on_block(text: str) -> str | None:
    lines = text.splitlines(); out = []; inside = False
    for l in lines:
        if not inside:
            if re.match(r"^(on|\"on\"|'on'):", l):
                inside = True; out.append(l)
        else:
            if re.match(r"^\S", l) and not l.startswith("#"):
                break
            out.append(l)
    return "\n".join(out) if inside else None

def shell_coverage_gap(text: str, jobs: dict[str, str], shell: list[str]) -> str | None:
    """None when every shell job runs on every pull request; otherwise the reason it may not."""
    on = on_block(text)
    if on is None:
        return f"{LANE_WORKFLOW} has no `on:` block the selector can read"
    code = "\n".join(l.split("#", 1)[0] for l in on.splitlines())
    if not re.search(r"\bpull_request\b(?!_)", code):
        return f"{LANE_WORKFLOW} does not trigger on pull_request"
    if re.search(r"^\s+(paths|paths-ignore|branches-ignore)\s*:", code, re.M):
        return f"{LANE_WORKFLOW}'s triggers carry a path/branch filter — the shell jobs might not run"
    if not shell:
        return f"{LANE_WORKFLOW} has no `{SHELL_JOB_PREFIX}*` job"
    for j in shell:
        if re.search(r"^    (if|needs)\s*:", jobs[j], re.M):
            return f"shell job `{j}` carries a job-level if:/needs: — it might not run"
    return None

def derive(root: Path) -> Derivation:
    d = Derivation()
    wf = root / LANE_WORKFLOW
    if not wf.is_file():
        d.error = f"{LANE_WORKFLOW} is absent"; return d
    text = _read(wf)
    jobs = workflow_jobs(text)
    if not jobs:
        d.error = f"no jobs could be read from {LANE_WORKFLOW}"; return d
    missing = [j for j in REQUIRED_LANE_JOBS if j not in jobs]
    if missing:
        d.error = f"{LANE_WORKFLOW} has no {', '.join(missing)} job — the parse is not trusted"; return d
    d.shell_jobs = sorted(j for j in jobs if j.startswith(SHELL_JOB_PREFIX))
    stage0: set[str] = set()
    for body in jobs.values():
        stage0 |= {m.group(1) for m in re.finditer(r"\b([A-Za-z0-9_-]+)=\$\{\{\s*needs\.\1\.result\s*\}\}", body)}
    d.stage0_jobs = sorted(stage0 & set(jobs))
    d.lane_jobs = sorted(j for j in jobs if j not in stage0 and not j.startswith(SHELL_JOB_PREFIX))
    if not set(REQUIRED_LANE_JOBS) <= set(d.lane_jobs):
        d.error = "the build/test jobs were classified outside the lane — the parse is not trusted"; return d
    d.shell_cover = shell_coverage_gap(text, jobs, d.shell_jobs)

    gh_files = sorted(str(p.relative_to(root)).replace("\\", "/") for p in (root / ".github").rglob("*") if p.is_file())
    gh_set = set(gh_files)
    by_base: dict[str, set[str]] = {}
    for f in gh_files:
        b = f.rsplit("/", 1)[-1]
        if b not in GENERIC_BASENAMES: by_base.setdefault(b, set()).add(f)
    action_dirs = {f.rsplit("/", 1)[0] for f in gh_files if f.startswith(".github/actions/") and f.count("/") >= 3}

    def resolve(ref: str) -> tuple[set[str], bool]:
        """(files a reference names, whether it named something the resolver cannot attribute to
        files — a whole directory other than one composite action, or the bare tree)."""
        if ref.endswith("*"):
            pre = ref[:-1]
            return {f for f in gh_files if f.startswith(pre)}, False
        if ref in gh_set: return {ref}, False
        if ref in action_dirs: return {f for f in gh_files if f.startswith(ref + "/")}, False
        if any(f.startswith(ref + "/") for f in gh_files) or ref == ".github": return set(), True
        return set(), False      # a file that does not exist (deleted, fetched, a fixture): matched as a name only

    via: dict[str, str] = {}
    called: set[str] = set()

    def is_yaml_code(f: str) -> bool:
        return f.startswith((".github/workflows/", ".github/actions/")) and f.endswith((".yml", ".yaml"))

    def is_script(f: str) -> bool:
        return f.startswith(".github/scripts/") and not f.endswith((".json", ".txt", ".allow", ".list", ".md"))

    def reach(seed: str, follow) -> tuple[set[str], set[str], set[str]]:
        """(references named, existing files reached, unattributable directory references).

        WHAT IS WALKED, and why:
          * YAML that RUNS as the lane — the seed, a workflow it CALLS (`uses:`), a composite
            action — is walked: every `.github/…` path it names, and every non-workflow file it
            names by basename (`$ACTION_PATH/../../scripts/ci-fresh-merge.py`), is consumed.
            A workflow it names WITHOUT calling it is read as data: consumed, not walked; one
            named by basename alone is an API name or prose (`actions/workflows/clients.yml/runs`).
          * a SCRIPT is walked for the SCRIPTS it loads — a sibling named in its code (`import x`,
            a path literal, an argv) with docstrings, triple-quoted fixtures and comments removed —
            and for the data files it names. Never for workflows: a script's `uses: …` is a
            fixture, and its workflow names are API names.
          * a DATA file (json, allow, list, …) is consumed and not walked: it names, it does not run.
        """
        refs_seen: set[str] = set(); files: set[str] = set(); dirs: set[str] = set(); todo: list[str] = []
        def add(f: str, why: str):
            if f not in files:
                files.add(f); todo.append(f); via[f] = why
        def take(text: str, origin: str, yaml_code: bool):
            if yaml_code:
                for m in USES_WORKFLOW.finditer(text):
                    called.add(m.group(1))
            for r in gh_refs(text):
                fs, is_dir = resolve(r)
                if not yaml_code:
                    fs = {f for f in fs if not f.startswith(".github/workflows/")}
                    if r.startswith(".github/workflows"): continue
                refs_seen.add(r)
                if is_dir and (not yaml_code or r in command_refs(text)): dirs.add(f"{r} (named by {origin})")
                for f in fs: add(f, f"{origin} names {r}")
            names = set(TOKEN.findall(text)) | {f"{x or y}.py" for x, y in PY_IMPORT.findall(text)}
            for n in names & by_base.keys():
                for f in by_base[n]:
                    if not f.startswith(".github/workflows/"): add(f, f"{origin} names {n}")
        take(seed, "the seed", True)
        while todo:
            f = todo.pop()
            # dotnet-test.yml: its lane half IS the seed. The selector itself: consumed (it forces),
            # never walked — it loads nothing, and its own self-test fixtures name files on purpose.
            if f in (LANE_WORKFLOW, SELF) or not follow(f): continue
            if f.startswith(".github/workflows/") and f not in called: continue
            if is_yaml_code(f): take(code_of(f, _read(root / f)), f, True)
            elif is_script(f): take(code_of(f, _read(root / f)), f, False)
        return refs_seen, files, dirs

    lane_code = code_of(LANE_WORKFLOW, "\n".join(jobs[j] for j in d.lane_jobs))
    refs_seen, d.lane_files, dirs = reach(lane_code, lambda f: True)
    d.lane_via = dict(via)
    # What a CHANGED path is matched against beyond the existing files reached: a glob's prefix, and
    # a named file that does not exist here (deleted by this very change, or fetched). A directory
    # reference is not a prefix: an action directory is already its files, and any other directory
    # the lane's commands name stopped the derivation below.
    is_existing_dir = lambda r: r == ".github" or any(f.startswith(r + "/") for f in gh_files)
    d.lane_prefixes = ({r[:-1] for r in refs_seen if r.endswith("*")}
                       | {r for r in refs_seen if not r.endswith("*") and r not in gh_set and not is_existing_dir(r)})
    # 🚨 A whole-directory reference in the lane's own YAML (its jobs, the reusable workflows and
    # composite actions they reach) is a consumer the derivation cannot enumerate — fail safe. In a
    # SCRIPT it is almost always prose or a scanner's subject, and is not treated as consumption.
    yaml_dirs = sorted(x for x in dirs if "(named by the seed)" in x or ".yml)" in x or ".yaml)" in x)
    if yaml_dirs:
        d.error = f"the lane's YAML names a whole directory it may read: {yaml_dirs[0]}"; return d
    if not d.lane_files:
        d.error = "the lane jobs reference no .github file — the parse is not trusted"; return d

    # Test projects that READ a .github file at test time (guards over workflows and scripts). Only
    # test/: runtime code under src/ does not read this repository's .github tree. A test that READS
    # a workflow reads text; one that RUNS a script runs its imports too — so the walk follows
    # scripts, not workflows. A directory the test names is every file under it.
    for proj in sorted(p for p in (root / "test").iterdir() if p.is_dir()) if (root / "test").is_dir() else []:
        texts = []
        for f in proj.rglob("*"):
            if not f.is_file() or "/bin/" in str(f) or "/obj/" in str(f): continue
            if f.suffix.lower() not in (".cs", ".csproj", ".props", ".targets", ".json", ".py", ".sh", ".yml", ".yaml"): continue
            t = _read(f)
            if ".github" in t: texts.append(code_of(str(f), t))
        if not texts: continue
        seed = "\n".join(texts)
        _, fs, _ = reach(seed, lambda f: f.startswith(".github/scripts/") or f.startswith(".github/actions/"))
        prefixes = set(fs)
        # Directory reads count from the TEST's own text only; a script it runs that names a
        # directory is not thereby reading all of it (that script's file reads are its files).
        for r in gh_refs(seed):
            if r.endswith("*"): prefixes.add(r[:-1])
            elif r == ".github": prefixes.add(".github/")
            elif r not in gh_set: prefixes.add(r + "/" if any(f.startswith(r + "/") for f in gh_files) else r)
        if prefixes:
            for csproj in proj.glob("*.csproj"):
                d.readers[csproj.stem] = prefixes
    return d


# ── the classification ─────────────────────────────────────────────────────────────────────

def classify(changed: list[str], refs: dict[str, set[str]], tests: set[str], d: Derivation | None = None) -> tuple[str, list[str], str]:
    """`d=None` is the pre-derivation rule (every non-safe `.github` file forces) — kept as the
    fail-safe and as the self-test's negative control."""
    if not changed:
        return "all", [], "no diff could be computed — the full suite runs"
    projects: set[str] = set(); forcing: list[tuple[str, str]] = []; ignored = 0
    outside_lane: list[str] = []; read_by: set[str] = set()
    for f in changed:
        m = PROJECT.match(f)
        if m:
            projects.add(m.group(2))
        elif SAFE.search(f):
            ignored += 1
        elif f.startswith(".github/"):
            if d is None or d.error:
                forcing.append((f, f"is outside src/ and test/ and the .github derivation is unavailable ({d.error if d else 'not run'})"))
            elif d.lane_consumes(f):
                forcing.append((f, "is consumed by the .NET build/test lane"))
            elif d.shell_cover:
                forcing.append((f, f"is outside the .NET lane, but the shell jobs are not guaranteed to run ({d.shell_cover})"))
            else:
                outside_lane.append(f)
                r = d.readers_of(f)
                read_by |= r; projects |= r
        else:
            forcing.append((f, "is outside src/, test/ and .github/"))
    if forcing:
        f, why = forcing[0]
        more = f" (+{len(forcing)-1} more forcing)" if len(forcing) > 1 else ""
        return "all", [], f"{f} {why}{more} — the full suite runs"
    gh_note = ""
    if outside_lane:
        gh_note = (f"{len(outside_lane)} workflow/script file(s) outside the .NET lane — "
                   + (f"read by {', '.join(sorted(read_by))}; " if read_by else "no .NET test owed; ")
                   + "the shell jobs cover them")
    if not projects:
        if outside_lane:
            return "none", [], gh_note
        return "none", [], f"{len(changed)} changed file(s) are docs/markdown only — no test project reads them"
    memo: dict[str, set[str]] = {}
    owed = sorted(t for t in tests if t in projects or (closure(t, refs, memo) & projects))
    if not owed:
        return "none", [], f"changed project(s) {', '.join(sorted(projects))} are referenced by no test project" + (f"; {gh_note}" if gh_note else "")
    return "incremental", owed, f"{len(owed)} of {len(tests)} test project(s) reach {', '.join(sorted(projects))}" + (f"; {gh_note}" if gh_note else "")


# ── self-test ──────────────────────────────────────────────────────────────────────────────

SYNTH_WORKFLOW = """\
name: synthetic
on:
  push:
    branches: [ "main" ]
  pull_request:
  merge_group:
jobs:
  precheck:
    runs-on: ubuntu-latest
    steps:
    - run: python3 .github/scripts/affected-tests.py --self-test
  stage-gate:
    needs: [precheck, pr-body, workflow-shell, workflow-shell-executions-a]
    uses: ./.github/workflows/stage-gate.yml
    with:
      stage0-results: >-
        pr-body=${{ needs.pr-body.result }} workflow-shell=${{ needs.workflow-shell.result }}
        workflow-shell-executions-a=${{ needs.workflow-shell-executions-a.result }}
  build:
    needs: [precheck, stage-gate]
    steps:
    - uses: ./.github/actions/fresh
    - run: python3 .github/scripts/lane.py
  test:
    needs: [build]
    uses: ./.github/workflows/reusable.yml
  pr-body:
    steps:
    - run: python3 .github/scripts/pr-body-check.py
  workflow-shell:
    runs-on: ubuntu-latest
    steps:
    - run: python3 .github/scripts/shell-only.py --self-test
  workflow-shell-executions-a:
    runs-on: ubuntu-latest
    steps:
    - run: python3 .github/scripts/shell-exec.py
"""

def _synth_tree(root: Path, workflow: str = SYNTH_WORKFLOW) -> None:
    files = {
        LANE_WORKFLOW: workflow,
        ".github/workflows/stage-gate.yml": "jobs:\n  gate:\n    steps:\n    - run: echo gate\n",
        ".github/workflows/reusable.yml": "jobs:\n  run:\n    steps:\n    - run: bash .github/scripts/reused.sh\n",
        ".github/workflows/other.yml": "jobs:\n  x:\n    steps:\n    - run: python3 .github/scripts/other-only.py\n",
        ".github/workflows/module-pack.yml": "jobs:\n  x:\n    steps:\n    - run: echo pack\n",
        ".github/actions/fresh/action.yml": "runs:\n  steps:\n  - run: python3 \"$P/../../scripts/fresh-impl.py\"\n",
        ".github/scripts/affected-tests.py": "print('selector')\n",
        ".github/scripts/lane.py": "import helper_mod\nHERE = 1\nload(HERE / 'loaded-by-path.py')\n",
        ".github/scripts/helper_mod.py": "x = 1\n",
        ".github/scripts/loaded-by-path.py": "y = 1\n",
        ".github/scripts/reused.sh": "echo reused\n",
        ".github/scripts/fresh-impl.py": "z = 1\n",
        ".github/scripts/shell-only.py": "print('shell')\n",
        ".github/scripts/shell-exec.py": "print('exec')\n",
        ".github/scripts/pr-body-check.py": "print('body')\n",
        ".github/scripts/other-only.py": "print('other')\n",
        ".github/scripts/read-by-guard.py": "import guard_helper\nprint('guard target')\n",
        ".github/scripts/guard_helper.py": "h = 1\n",
        ".github/scripts/unreferenced.py": "print('nobody')\n",
        ".github/dependabot.yml": "version: 2\n",
        "test/Doc.Test/Doc.Test.csproj": "<Project />\n",
        "test/Doc.Test/Guard.cs": 'var p = Path.Combine(RepoPaths.Root, ".github", "workflows", "other.yml");\n'
                                  'RunPython(".github/scripts/read-by-guard.py");\n'
                                  '// a comment naming .github/scripts/unreferenced.py is not a read\n',
        "test/Data.Test/Data.Test.csproj": "<Project />\n",
    }
    for rel, body in files.items():
        p = root / rel; p.parent.mkdir(parents=True, exist_ok=True); p.write_text(body, encoding="utf-8")

def self_test(real_root: Path | None) -> int:
    import tempfile
    refs = {"Core": set(), "Data": {"Core"}, "Hosting": {"Data"}, "Data.Test": {"Data", "Fixture"}, "Hosting.Test": {"Hosting", "Fixture"}, "Fixture": {"Core"}, "Other.Test": {"Core"}, "Lonely": set(), "Doc.Test": set()}
    tests = {"Data.Test", "Hosting.Test", "Other.Test", "Fixture", "Doc.Test"}
    ran = 0
    def ok(changed, mode, projects=None, d=None, reason_has=None):
        nonlocal ran
        m, p, r = classify(changed, refs, tests, d)
        assert m == mode and (projects is None or p == projects), f"{changed} → {m} {p} ({r}); expected {mode} {projects}"
        assert reason_has is None or reason_has in r, f"{changed}: reason {r!r} lacks {reason_has!r}"
        ran += 1
        return r

    # ── the project-scoped rules (unchanged) ──
    ok(["src/Data/X.cs"], "incremental", ["Data.Test", "Hosting.Test"])
    ok(["src/Core/X.cs"], "incremental", ["Data.Test", "Fixture", "Hosting.Test", "Other.Test"])
    ok(["test/Other.Test/T.cs"], "incremental", ["Other.Test"])
    ok(["src/Lonely/X.cs"], "none")
    ok(["docs/a.md", "README.md", ".claude/skills/x/SKILL.md"], "none")
    ok(["Directory.Packages.props"], "all"); ok(["Directory.Build.targets"], "all"); ok(["global.json"], "all")
    ok(["nuget.config"], "all"); ok(["test/AwaitedMeshReadSites.allow"], "all"); ok(["Doc/Overview.md"], "none")
    ok(["tools/x/y.cs"], "all"); ok(["weird/unknown.bin"], "all", reason_has="outside src/, test/ and .github/")
    ok([], "all")

    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp); _synth_tree(root)
        d = derive(root)
        assert d.error is None, d.error
        assert d.shell_cover is None, d.shell_cover
        assert d.shell_jobs == ["workflow-shell", "workflow-shell-executions-a"], d.shell_jobs
        assert d.stage0_jobs == ["pr-body", "workflow-shell", "workflow-shell-executions-a"], d.stage0_jobs
        assert "build" in d.lane_jobs and "test" in d.lane_jobs and "pr-body" not in d.lane_jobs

        # a script a lane job references directly → ALL
        ok([".github/scripts/lane.py"], "all", d=d, reason_has="consumed by the .NET build/test lane")
        # …and its transitive imports: by `import`, and by a path literal naming the sibling
        ok([".github/scripts/helper_mod.py"], "all", d=d)
        ok([".github/scripts/loaded-by-path.py"], "all", d=d)
        # a composite action the lane uses, and the script IT reaches by a relative path
        ok([".github/actions/fresh/action.yml"], "all", d=d)
        ok([".github/scripts/fresh-impl.py"], "all", d=d)
        # a reusable workflow the test job calls, and its script
        ok([".github/workflows/reusable.yml"], "all", d=d); ok([".github/scripts/reused.sh"], "all", d=d)
        # the selector itself (precheck is lane) and dotnet-test.yml → ALL
        ok([".github/scripts/affected-tests.py"], "all", d=d)
        ok([LANE_WORKFLOW], "all", d=d)
        # an unreferenced script, a shell-only script, a stage-0 control's script, another workflow → NONE
        r = ok([".github/scripts/shell-only.py"], "none", d=d, reason_has="outside the .NET lane — no .NET test owed; the shell jobs cover them")
        ok([".github/scripts/shell-exec.py", ".github/scripts/pr-body-check.py", ".github/scripts/unreferenced.py",
            ".github/workflows/module-pack.yml", ".github/dependabot.yml"], "none", d=d, reason_has="5 workflow/script file(s)")
        # an unknown NON-.github path still forces, even beside an outside-lane script
        ok([".github/scripts/shell-only.py", "deploy/x.yaml"], "all", d=d)
        # an unreferenced script + a src file → that src closure only
        ok([".github/scripts/unreferenced.py", "src/Data/X.cs"], "incremental", ["Data.Test", "Hosting.Test"], d=d,
           reason_has="1 workflow/script file(s) outside the .NET lane")
        # a .github file a TEST PROJECT reads → that test project only (Path.Combine form, and a path in a comment)
        ok([".github/workflows/other.yml"], "incremental", ["Doc.Test"], d=d, reason_has="read by Doc.Test")
        ok([".github/scripts/read-by-guard.py"], "incremental", ["Doc.Test"], d=d)
        # …transitively through a script the test RUNS (its import), but not through a workflow the
        # test only READS: other.yml names other-only.py, and reading YAML text runs nothing
        ok([".github/scripts/guard_helper.py"], "incremental", ["Doc.Test"], d=d)
        ok([".github/scripts/other-only.py"], "none", d=d)
        # a script named only in a test's COMMENT is not read by it
        ok([".github/scripts/unreferenced.py"], "none", d=d)

        # 🚨 NEGATIVE CONTROL: the pre-derivation rule (d=None) forces ALL on the very file the
        # derivation lets through — so the "none" above is the derivation's doing, not an accident.
        ok([".github/scripts/shell-only.py"], "all", d=None, reason_has="derivation is unavailable")
        ok([".github/scripts/unreferenced.py", "src/Data/X.cs"], "all", d=None)

    # ── UNKNOWN fails safe: every way the derivation can lose its footing forces ALL ──
    def variant(workflow: str | None, why: str):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp); _synth_tree(root, workflow if workflow is not None else SYNTH_WORKFLOW)
            if workflow is None: (root / LANE_WORKFLOW).unlink()
            dv = derive(root)
            ok([".github/scripts/shell-only.py"], "all", d=dv)
            ok([".github/scripts/unreferenced.py"], "all", d=dv)
            return dv
    variant(None, "no dotnet-test.yml")
    variant("name: x\non:\n  pull_request:\n", "no jobs")
    variant(SYNTH_WORKFLOW.replace("  test:\n", "  tests-renamed:\n"), "no test job")
    variant(SYNTH_WORKFLOW.replace("  pull_request:\n", "  pull_request:\n    paths: [ 'src/**' ]\n"), "path filter")
    variant(SYNTH_WORKFLOW.replace("  pull_request:\n", "  pull_request:\n    paths-ignore: [ '.github/**' ]\n"), "paths-ignore")
    variant(SYNTH_WORKFLOW.replace("  workflow-shell:\n    runs-on", "  workflow-shell:\n    if: github.event_name == 'push'\n    runs-on"), "shell job if:")
    variant(SYNTH_WORKFLOW.replace("  workflow-shell-executions-a:\n    runs-on", "  workflow-shell-executions-a:\n    needs: [build]\n    runs-on"), "shell job needs:")
    variant(SYNTH_WORKFLOW.replace("workflow-shell", "static-shell"), "no shell job")
    # a lane COMMAND that names a whole directory reads files the derivation cannot enumerate
    variant(SYNTH_WORKFLOW.replace("    - run: python3 .github/scripts/lane.py\n",
                                   "    - run: python3 .github/scripts/lane.py\n    - run: for f in .github/scripts/*; do sh \"$f\"; done\n"), "glob over a dir")
    variant(SYNTH_WORKFLOW.replace("    - run: python3 .github/scripts/lane.py\n",
                                   "    - run: python3 .github/scripts/lane.py --dir .github/scripts\n"), "dir argument")
    # …while the same directory in a MESSAGE to a reader is prose, and the derivation stands
    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp); _synth_tree(root, SYNTH_WORKFLOW.replace(
            "    - run: python3 .github/scripts/lane.py\n",
            "    - run: python3 .github/scripts/lane.py || { echo \"::error::see .github/scripts for the lane\"; exit 1; }\n"))
        dv = derive(root); assert dv.error is None, dv.error
        ok([".github/scripts/shell-only.py"], "none", d=dv)
    # a bare ".github" literal a test cannot resolve owes that test project on EVERY .github file
    assert gh_refs('var dirs = new[] { ".github", "tools" };') == {".github"}
    assert gh_refs('Path.Combine(root, ".github", "workflows", Workflow)') == {".github/workflows"}
    assert gh_refs('see .github/scripts/shard-assign.sh.') == {".github/scripts/shard-assign.sh"}
    ran += 3

    # ── canary over the REAL tree: the parse must still find the lane's known consumers ──
    if real_root is not None and (real_root / LANE_WORKFLOW).is_file():
        d = derive(real_root)
        assert d.error is None, f"real tree: {d.error}"
        assert d.shell_cover is None, f"real tree: {d.shell_cover}"
        for must in (".github/scripts/shard-assign.sh", ".github/scripts/affected-tests.py",
                     ".github/actions/fresh-merge/action.yml", ".github/scripts/ci-fresh-merge.py"):
            assert d.lane_consumes(must), f"real tree: the lane no longer consumes {must} — the derivation lost its footing"
        assert not d.lane_consumes(".github/scripts/check-workflow-shell.py"), \
            "real tree: check-workflow-shell.py (shell-only) reads as lane-consumed — the lane/shell split collapsed"
        ran += 1
        print(f"  real tree: {len(d.lane_jobs)} lane jobs, {len(d.shell_jobs)} shell jobs, "
              f"{len(d.lane_files)} lane-consumed .github files, {len(d.readers)} test project(s) read .github")
    print(f"✓ affected-tests self-test: {ran} cases — closure, safe paths, forcing paths, lane derivation "
          f"(direct, transitive, action, reusable), shell-only/unreferenced → none, test readers, "
          f"fail-safe variants, negative control"); return 0


def main() -> int:
    ap = argparse.ArgumentParser(); ap.add_argument("--base"); ap.add_argument("--head", default="HEAD"); ap.add_argument("--all", action="store_true")
    ap.add_argument("--self-test", action="store_true"); ap.add_argument("--explain", action="store_true"); ap.add_argument("--root", default=".")
    ap.add_argument("--files", help="classify these newline-separated paths (a file, or - for stdin) instead of a git diff")
    a = ap.parse_args()
    root = Path(a.root).resolve()
    if a.self_test: return self_test(root)
    if a.explain:
        d = derive(root)
        print(f"error: {d.error}\nshell cover gap: {d.shell_cover}\nlane jobs: {', '.join(d.lane_jobs)}\nshell jobs: {', '.join(d.shell_jobs)}\nstage-0: {', '.join(d.stage0_jobs)}")
        print(f"lane-consumed files ({len(d.lane_files)}):"); [print(f"  {f}    <- {d.lane_via.get(f, '')}") for f in sorted(d.lane_files)]
        print("test-project readers:"); [print(f"  {p}: {', '.join(sorted(x for x in v if not any(x != y and x.startswith(y) for y in v)))}") for p, v in sorted(d.readers.items())]
        return 0 if d.error is None else 1
    if a.files is not None:
        src = sys.stdin.read() if a.files == "-" else Path(a.files).read_text(encoding="utf-8")
        changed = [l.strip() for l in src.splitlines() if l.strip()]
        refs, tests = project_graph(root)
        mode, projects, reason = classify(changed, refs, tests, derive(root))
    elif a.all or not a.base:
        mode, projects, reason = "all", [], "not a pull request (merge group, push, schedule or dispatch) — the full suite runs"
    else:
        # --no-renames: a rename lists BOTH paths, so a file moved out of the lane still forces.
        r = subprocess.run(["git", "-C", str(root), "diff", "-z", "--no-renames", "--name-only", f"{a.base}...{a.head}"], capture_output=True, text=True)
        if r.returncode != 0:
            r = subprocess.run(["git", "-C", str(root), "diff", "-z", "--no-renames", "--name-only", a.base, a.head], capture_output=True, text=True)
        changed = [p for p in r.stdout.split("\0") if p] if r.returncode == 0 else []
        refs, tests = project_graph(root)
        mode, projects, reason = classify(changed, refs, tests, derive(root))
    lines = [f"mode={mode}", f"projects={','.join(projects)}", f"reason={reason}"]
    print("\n".join(lines))
    if os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as f: f.write("\n".join(lines) + "\n")
    return 0

if __name__ == "__main__": sys.exit(main())
