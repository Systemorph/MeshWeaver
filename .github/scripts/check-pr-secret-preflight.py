#!/usr/bin/env python3
"""check-pr-secret-preflight.py — every secret a PULL-REQUEST job consumes is asserted by a preflight.

(The name on this first line is load-bearing: node-repo-validate.yml fetches this file at platform-ref
and refuses a body whose first 400 bytes do not name it — the same shape as check-workflow-timeouts.py.)

WHY THIS EXISTS (Systemorph/MeshWeaver#3399, a recurrence of #2249)
-------------------------------------------------------------------
A Dependabot-triggered run resolves `secrets.X` against a SECOND, SEPARATE store — Settings →
Secrets and variables → **Dependabot** — and that store is a *partial* mirror of the Actions one.
There is no Dependabot variables store (`GET /repos/{o}/{r}/dependabot/variables` is 404), so
`vars.X` is single-store and only `secrets.X` is doubled. A name provisioned on the Actions tab and
consumed by every other run of the same workflow therefore reads EMPTY on a Dependabot PR, and the
maintainer who opens the Actions tab finds the secret sitting right there and concludes CI is broken.

The fleet's answer to a missing input is already correct: a `preflight` job asserts every external
input and fails RED naming it (AGENTS.md — "a gate NEVER tests its own inputs; no skip-trapdoors").
What recurs is that the preflight's list goes STALE relative to what the run actually consumes.
Measured on MeshWeaver.Reinsurance#128 (run 33354473532): preflight **passed**, and `compile-check`
died one job later with

    ##[error]compose-sealed-modules.sh: --registry-url needs --registry-key

— an empty `secrets.MW_REGISTRY_KEY`, surfacing as a script-argument complaint that names no secret
at all, because nothing in preflight had asked for it. That is the defect this gate closes: it is a
STATIC check that every `secrets.NAME` a pull-request-reachable job consumes is covered by a
preflight assertion, so the empty value is reported by name, at the first job, in every store.

WHY NOT DIFF THE TWO STORES DIRECTLY IN CI
------------------------------------------
Two independent reasons, both measured 2026-09-06:

1. **A name diff cannot see the failure mode.** Neither store's API returns values, and a secret
   present with an EMPTY value is indistinguishable from a healthy one by name. The in-run
   assertion (`[ -n "${X:-}" ]`) tests the thing that actually matters — emptiness, in whichever
   store this event resolves against — and a name diff is strictly weaker.
2. **No CI credential can read the Dependabot store.** `GITHUB_TOKEN` has no `secrets` or
   `dependabot-secrets` key in the `permissions:` block, and neither GitHub App the fleet mints from
   holds a `secrets` permission (`meshweaver-cloud`, app_id 4220566: contents/metadata/pull_requests;
   the read-only `fleet-reader`: the same three at read).

`--check-stores` below performs the name diff anyway, for an operator running it locally with their
own admin credential. It never reads a value, and it fails loudly rather than skipping when the
credential cannot list a store — a store that could not be read is a FAILED audit, not a clean one.

THE RULE
--------
For every job that can run on a `pull_request` / `pull_request_target` event *triggered by
Dependabot*, every `secrets.NAME` it references (except `GITHUB_TOKEN`) must be asserted by some
pull-request-reachable job in the same repository: bound to an env var via `NAME: ${{ secrets.NAME }}`
and tested with `[ -n "${NAME:-}" ]`.

A name that is genuinely PASSED but not CONSUMED on a pull request — e.g. a publish token handed to
a reusable lane whose publish step is `if: inputs.publish`, false on a PR — is declared once, with a
reason, in `.github/pr-secret-preflight-allow.txt`. An allow entry whose name is no longer
referenced by any pull-request-reachable job is itself a violation: a stale allow entry is how an
exemption outlives the thing it exempted.

USAGE
-----
  check-pr-secret-preflight.py [--root DIR]        gate the tree at DIR (default: cwd)
  check-pr-secret-preflight.py --self-test         prove the gate fires and stays silent
  check-pr-secret-preflight.py --root DIR --check-stores --repo Systemorph/X
                                                   additionally diff the required set against the
                                                   repo's Dependabot store BY NAME (needs `gh` and a
                                                   credential that can list both stores)

Exit 1 on any violation; every violation is a `::error` annotation naming the file, the job and the
exact `gh secret set` command that fixes it.
"""
from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
import tempfile
from pathlib import Path

try:
    import yaml
except ImportError:  # pragma: no cover - the CI step installs PyYAML; locally `pip install pyyaml`
    print("::error::check-pr-secret-preflight.py needs PyYAML (pip install pyyaml)")
    sys.exit(2)

SECRET_REF = re.compile(r"secrets\.([A-Za-z_][A-Za-z0-9_-]*)")
ALLOW_FILE = ".github/pr-secret-preflight-allow.txt"
# GITHUB_TOKEN is minted per run and is present in every store by construction.
ALWAYS_PRESENT = {"GITHUB_TOKEN"}
MIN_REASON = 12


# ---------------------------------------------------------------------------
# A three-valued evaluator for the subset of GitHub expressions that job `if:`
# conditions actually use in this fleet. UNKNOWN is the honest answer for
# anything that depends on run-time state (needs.*, inputs.*, steps.*), and a
# job is treated as REACHABLE unless its condition is provably FALSE — the
# conservative direction, because a missed job is a missed gate.
# ---------------------------------------------------------------------------
UNKNOWN = object()

# The facts of a Dependabot-triggered pull_request run. Dependabot always pushes its
# branch to the base repository, so the head repo is never a fork and never differs.
DEPENDABOT_PR_FACTS = {
    "github.event_name": "pull_request",
    "github.actor": "dependabot[bot]",
    "github.triggering_actor": "dependabot[bot]",
    "github.ref": "refs/pull/1/merge",
    "github.head_ref": "dependabot/nuget/example",
    "github.repository": "owner/repo",
    "github.event.pull_request.head.repo.full_name": "owner/repo",
    "github.event.pull_request.head.repo.fork": False,
    "github.event.pull_request.draft": False,
    "github.event.pull_request.base.ref": "main",
}

_TOKEN = re.compile(
    r"\s*(\(|\)|&&|\|\||!=|==|!|'[^']*'|\"[^\"]*\"|[A-Za-z_][A-Za-z0-9_.\-\[\]]*(?:\(\))?|\S)"
)


def _tokenize(expr: str) -> list[str]:
    out, pos = [], 0
    while pos < len(expr):
        m = _TOKEN.match(expr, pos)
        if not m:
            break
        out.append(m.group(1))
        pos = m.end()
    return out


class _Parser:
    def __init__(self, tokens: list[str], facts: dict):
        self.t, self.i, self.facts = tokens, 0, facts

    def peek(self):
        return self.t[self.i] if self.i < len(self.t) else None

    def next(self):
        tok = self.peek()
        self.i += 1
        return tok

    def parse(self):
        v = self.or_expr()
        return v if self.i >= len(self.t) else UNKNOWN  # unconsumed input => do not guess

    def or_expr(self):
        v = self.and_expr()
        while self.peek() == "||":
            self.next()
            r = self.and_expr()
            if v is True or r is True:
                v = True
            elif v is UNKNOWN or r is UNKNOWN:
                v = UNKNOWN
            else:
                v = bool(v) or bool(r)
        return v

    def and_expr(self):
        v = self.cmp_expr()
        while self.peek() == "&&":
            self.next()
            r = self.cmp_expr()
            if v is False or r is False:
                v = False
            elif v is UNKNOWN or r is UNKNOWN:
                v = UNKNOWN
            else:
                v = bool(v) and bool(r)
        return v

    def cmp_expr(self):
        left = self.unary()
        op = self.peek()
        if op in ("==", "!="):
            self.next()
            right = self.unary()
            if left is UNKNOWN or right is UNKNOWN:
                return UNKNOWN
            return (left == right) if op == "==" else (left != right)
        return left

    def unary(self):
        if self.peek() == "!":
            self.next()
            v = self.unary()
            return UNKNOWN if v is UNKNOWN else (not v)
        return self.primary()

    def primary(self):
        tok = self.next()
        if tok is None:
            return UNKNOWN
        if tok == "(":
            v = self.or_expr()
            if self.peek() == ")":
                self.next()
            return v
        if len(tok) >= 2 and tok[0] in "'\"" and tok[-1] == tok[0]:
            return tok[1:-1]
        if tok == "true":
            return True
        if tok == "false":
            return False
        if tok == "always()":
            return True
        if tok in self.facts:
            return self.facts[tok]
        return UNKNOWN  # needs.*, inputs.*, steps.*, vars.*, env.*, success(), cancelled(), …


def condition_is_provably_false(cond, facts: dict = None) -> bool:
    """True only when the `if:` can be shown to exclude a Dependabot pull-request run."""
    if cond is None:
        return False
    if isinstance(cond, bool):
        return not cond
    text = str(cond).strip()
    # `${{ … }}` wrappers are decoration; job `if:` is an expression either way.
    while text.startswith("${{") and text.endswith("}}"):
        text = text[3:-2].strip()
    text = text.replace("\n", " ")
    value = _Parser(_tokenize(text), facts if facts is not None else DEPENDABOT_PR_FACTS).parse()
    return value is False


# ---------------------------------------------------------------------------
# Workflow analysis
# ---------------------------------------------------------------------------
def _triggers(doc: dict) -> list[str]:
    # PyYAML parses a bare `on:` key as the boolean True.
    on = doc.get(True, doc.get("on"))
    if on is None:
        return []
    if isinstance(on, str):
        return [on]
    if isinstance(on, list):
        return [str(x) for x in on]
    if isinstance(on, dict):
        return [str(k) for k in on]
    return []


def _secret_names(node) -> set[str]:
    return set(SECRET_REF.findall(yaml.safe_dump(node, default_flow_style=False)))


def _env_to_secret(mapping) -> dict[str, str]:
    """env var name -> secret name, for `MY_VAR: ${{ secrets.MY_VAR }}` bindings."""
    out: dict[str, str] = {}
    if not isinstance(mapping, dict):
        return out
    for var, expr in mapping.items():
        found = SECRET_REF.findall(str(expr))
        if len(found) == 1:
            out[str(var)] = found[0]
    return out


def strip_shell_comments(script: str) -> str:
    """Drop `#` comments so a COMMENTED-OUT assertion cannot count as one.

    Without this the guard is defeatable — and silently — by the one edit most likely to happen
    by accident: commenting a `[ -n "${X:-}" ]` line out while debugging and never restoring it.
    A `#` only opens a comment at the start of a word (POSIX), and the fleet's preflight messages
    routinely contain `#2249` / `#3399` INSIDE the quoted remediation text, so the scan has to
    track quoting rather than cut at the first `#`.
    """
    out = []
    for line in script.splitlines():
        in_single = in_double = False
        cut = None
        i = 0
        while i < len(line):
            c = line[i]
            if in_single:
                if c == "'":
                    in_single = False
            elif in_double:
                if c == "\\":
                    i += 2
                    continue
                if c == '"':
                    in_double = False
            elif c == "'":
                in_single = True
            elif c == '"':
                in_double = True
            elif c == "#" and (i == 0 or line[i - 1] in " \t"):
                cut = i
                break
            i += 1
        out.append(line if cut is None else line[:cut])
    return "\n".join(out)


def _asserted_in_job(job: dict) -> set[str]:
    """Secret names this job proves non-empty with `[ -n "${VAR:-}" ]` (or `[ -n "${VAR}" ]`)."""
    asserted: set[str] = set()
    job_env = _env_to_secret(job.get("env"))
    for step in job.get("steps") or []:
        if not isinstance(step, dict):
            continue
        run = step.get("run")
        if not isinstance(run, str):
            continue
        run = strip_shell_comments(run)
        env = dict(job_env)
        env.update(_env_to_secret(step.get("env")))
        for var, secret in env.items():
            # Both shells in use here: `[ -n "${VAR:-}" ]` / `[ -n "${VAR}" ]` and the brace-less
            # `[ -n "$VAR" ]` (auto-arm.yml). A word boundary keeps $FOO from matching $FOO_BAR.
            v = re.escape(var)
            if re.search(r'-n\s+"?\$(?:\{' + v + r'(?::-[^}]*)?\}|' + v + r'(?![A-Za-z0-9_]))', run):
                asserted.add(secret)
    return asserted


def analyse(root: Path) -> tuple[dict[str, set[str]], set[str], set[str], list[str]]:
    """Return (required{secret->'file:job' evidence}, required_names, asserted_names, hard_errors)."""
    evidence: dict[str, set[str]] = {}
    asserted: set[str] = set()
    errors: list[str] = []
    wf_dir = root / ".github" / "workflows"
    if not wf_dir.is_dir():
        errors.append(f"::error::{root}/.github/workflows does not exist — nothing to gate is a failure, not a pass")
        return evidence, set(), asserted, errors
    files = sorted(p for p in wf_dir.iterdir() if p.suffix in (".yml", ".yaml") and p.is_file())
    if not files:
        errors.append(f"::error::{wf_dir} holds no workflow files — nothing to gate is a failure, not a pass")
        return evidence, set(), asserted, errors
    for path in files:
        rel = path.relative_to(root)
        try:
            doc = yaml.safe_load(path.read_text(encoding="utf-8"))
        except yaml.YAMLError as e:
            errors.append(f"::error file={rel}::not valid YAML: {e}")
            continue
        if not isinstance(doc, dict):
            continue
        if not any(t in ("pull_request", "pull_request_target") for t in _triggers(doc)):
            continue
        for job_id, job in (doc.get("jobs") or {}).items():
            if not isinstance(job, dict):
                continue
            if condition_is_provably_false(job.get("if")):
                continue
            if job.get("secrets") == "inherit":
                errors.append(
                    f"::error file={rel}::job '{job_id}' passes `secrets: inherit` into a reusable lane on a "
                    f"pull-request event. This gate cannot see which names the callee consumes, so completeness "
                    f"cannot be proven — pass the secrets explicitly (`secrets: {{name: ${{{{ secrets.NAME }}}} }}`)."
                )
            for name in _secret_names(job) - ALWAYS_PRESENT:
                evidence.setdefault(name, set()).add(f"{rel}:{job_id}")
            asserted |= _asserted_in_job(job)
    return evidence, set(evidence), asserted, errors


def read_allow(root: Path) -> tuple[dict[str, str], list[str]]:
    """name -> reason, from the per-repo allow file. A reason is mandatory and must say something."""
    allow: dict[str, str] = {}
    errors: list[str] = []
    path = root / ALLOW_FILE
    if not path.is_file():
        return allow, errors
    for lineno, raw in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        name, sep, reason = line.partition("#")
        name, reason = name.strip(), reason.strip()
        if not sep or len(reason) < MIN_REASON:
            errors.append(
                f"::error file={ALLOW_FILE},line={lineno}::'{name or raw.strip()}' has no reason. Every entry is "
                f"`NAME  # why this secret is passed but never consumed on a pull request` "
                f"(at least {MIN_REASON} characters)."
            )
            continue
        allow[name] = reason
    return allow, errors


def check_tree(root: Path) -> tuple[list[str], int, int, set[str]]:
    """Return (violations, required_count, asserted_count, required_names)."""
    violations: list[str] = []
    evidence, required, asserted, errors = analyse(root)
    violations.extend(errors)
    allow, allow_errors = read_allow(root)
    violations.extend(allow_errors)

    for name in sorted(required - asserted - set(allow)):
        where = ", ".join(sorted(evidence[name]))
        violations.append(
            f"::error::secrets.{name} is consumed by a pull-request job ({where}) but no preflight asserts it. "
            f"On a Dependabot PR that name resolves against the DEPENDABOT store, so an empty value surfaces "
            f"deep in the lane with a message that does not name the secret. Add "
            f"`[ -n \"${{{name}:-}}\" ] || missing+=(\"secrets.{name} — …\")` to the preflight (binding "
            f"`{name}: ${{{{ secrets.{name} }}}}` in its env), or declare it in {ALLOW_FILE} with a reason."
        )
    for name in sorted(set(allow) - required):
        violations.append(
            f"::error file={ALLOW_FILE}::'{name}' is allow-listed but no pull-request job references it any more. "
            f"A stale allow entry outlives the thing it exempted — delete the line."
        )
    return violations, len(required), len(asserted), required


# ---------------------------------------------------------------------------
# Optional operator-side store diff — names only, never a value.
# ---------------------------------------------------------------------------
def _list_store(repo: str, app: str) -> set[str]:
    path = f"repos/{repo}/{'dependabot' if app == 'dependabot' else 'actions'}/secrets?per_page=100"
    proc = subprocess.run(
        ["gh", "api", path, "--jq", "{t: .total_count, n: [.secrets[].name]}"],
        capture_output=True, text=True,
    )
    if proc.returncode != 0:
        raise RuntimeError(
            f"could not list the {app} secret store of {repo}: {proc.stderr.strip()}. "
            f"A store that could not be read is a FAILED audit, not a clean one."
        )
    payload = json.loads(proc.stdout)
    names = set(payload["n"])
    if len(names) != payload["t"]:
        raise RuntimeError(
            f"{repo} {app} store: listed {len(names)} name(s) but total_count is {payload['t']} — "
            f"the listing is truncated, so the diff would be wrong."
        )
    return names


def check_stores(repo: str, required: set[str]) -> list[str]:
    """Diff the required names against the Dependabot store. Names only; no value is ever read."""
    problems: list[str] = []
    dependabot = _list_store(repo, "dependabot")
    actions = _list_store(repo, "actions")
    for name in sorted(required - dependabot):
        where = "also absent from the Actions store" if name not in actions else "present in the Actions store"
        problems.append(
            f"::error::{repo}: secrets.{name} is required by a pull-request job but is NOT in the Dependabot "
            f"store ({where}). Fix: gh secret set {name} --app dependabot --repo {repo}"
        )
    print(
        f"store diff {repo}: {len(required)} required, {len(dependabot)} in the Dependabot store, "
        f"{len(actions)} in the Actions store, {len(problems)} missing. "
        f"(A name present with an EMPTY value is invisible here — only the in-run assertion catches that.)"
    )
    return problems


# ---------------------------------------------------------------------------
# Self-test — every check must FIRE on its defect and stay SILENT on its fix,
# or the gate is vacuous (AGENTS.md: "a verification step that cannot fail is
# not a verification step").
# ---------------------------------------------------------------------------
_PREFLIGHT = (
    "  preflight:\n"
    "    runs-on: ubuntu-latest\n"
    "    steps:\n"
    "      - env:\n"
    "          {name}: ${{{{ secrets.{name} }}}}\n"
    '        run: |\n'
    '          [ -n "${{{name}:-}}" ] || missing+=("secrets.{name} — needed")\n'
)


def _wf(jobs: str, on: str = "on:\n  pull_request:\n") -> str:
    return f"name: t\n{on}jobs:\n{jobs}"


def self_test() -> int:
    consumer = (
        "  gate:\n"
        "    runs-on: ubuntu-latest\n"
        "    steps:\n"
        "      - run: echo ${{ secrets.MW_REGISTRY_KEY }}\n"
    )
    cases: list[tuple[str, str, str | None, bool]] = [
        # (name, workflow body, allow-file body or None, expect_violation)
        ("asserted", _wf(consumer + _PREFLIGHT.format(name="MW_REGISTRY_KEY")), None, False),
        ("not-asserted", _wf(consumer), None, True),
        ("allow-listed", _wf(consumer), "MW_REGISTRY_KEY  # publish-only, never read on a PR\n", False),
        ("allow-no-reason", _wf(consumer), "MW_REGISTRY_KEY\n", True),
        ("allow-short-reason", _wf(consumer), "MW_REGISTRY_KEY  # x\n", True),
        ("allow-stale", _wf(_PREFLIGHT.format(name="A")), "GONE  # was consumed before the lane moved\n", True),
        (
            "push-only-job-ignored",
            _wf("  publish:\n    if: github.event_name == 'push'\n    runs-on: ubuntu-latest\n"
                "    steps: [{run: 'echo ${{ secrets.PUBLISH_TOKEN }}'}]\n"),
            None, False,
        ),
        (
            "dependabot-exempt-job-ignored",
            _wf("  gate:\n    if: ${{ github.actor != 'dependabot[bot]' }}\n    runs-on: ubuntu-latest\n"
                "    steps: [{run: 'echo ${{ secrets.APP_KEY }}'}]\n"),
            None, False,
        ),
        (
            "bake-shape-ignored",
            _wf("  publish-bake:\n"
                "    if: (github.event_name == 'push' && github.ref == 'refs/heads/main') || github.event_name == 'schedule'\n"
                "    runs-on: ubuntu-latest\n    steps: [{run: 'echo ${{ secrets.AZURE_CLIENT_ID }}'}]\n"),
            None, False,
        ),
        (
            "unknown-condition-is-reachable",
            _wf("  gate:\n    if: ${{ needs.changes.outputs.mesh == 'true' }}\n    runs-on: ubuntu-latest\n"
                "    steps: [{run: 'echo ${{ secrets.MW_REGISTRY_KEY }}'}]\n"),
            None, True,
        ),
        ("github-token-exempt", _wf("  a:\n    runs-on: ubuntu-latest\n    steps: [{run: 'echo ${{ secrets.GITHUB_TOKEN }}'}]\n"), None, False),
        ("no-pr-trigger", _wf(consumer, on="on:\n  push:\n"), None, False),
        (
            "reusable-caller-counts",
            _wf("  call:\n    uses: ./.github/workflows/x.yml\n    secrets:\n      key: ${{ secrets.MW_REGISTRY_KEY }}\n"),
            None, True,
        ),
        (
            "secrets-inherit-refused",
            _wf("  call:\n    uses: ./.github/workflows/x.yml\n    secrets: inherit\n"),
            None, True,
        ),
        (
            "assert-without-default-braces",
            _wf(consumer + "  preflight:\n    runs-on: ubuntu-latest\n    env:\n"
                "      MW_REGISTRY_KEY: ${{ secrets.MW_REGISTRY_KEY }}\n"
                '    steps: [{run: \'[ -n "${MW_REGISTRY_KEY}" ] || exit 1\'}]\n'),
            None, False,
        ),
        (
            "assert-braceless",
            _wf(consumer + "  preflight:\n    runs-on: ubuntu-latest\n    env:\n"
                "      MW_REGISTRY_KEY: ${{ secrets.MW_REGISTRY_KEY }}\n"
                '    steps: [{run: \'[ -n "$MW_REGISTRY_KEY" ] || exit 1\'}]\n'),
            None, False,
        ),
        (
            "assert-longer-name-does-not-count",
            _wf(consumer + "  preflight:\n    runs-on: ubuntu-latest\n    env:\n"
                "      MW_REGISTRY_KEY: ${{ secrets.MW_REGISTRY_KEY }}\n"
                '    steps: [{run: \'[ -n "$MW_REGISTRY_KEY_PATH" ] || exit 1\'}]\n'),
            None, True,
        ),
        (
            "commented-out-assertion-does-not-count",
            _wf(consumer + "  preflight:\n    runs-on: ubuntu-latest\n    env:\n"
                "      MW_REGISTRY_KEY: ${{ secrets.MW_REGISTRY_KEY }}\n"
                '    steps: [{run: \'# [ -n "${MW_REGISTRY_KEY:-}" ] || exit 1\'}]\n'),
            None, True,
        ),
        (
            "hash-inside-the-quoted-message-still-counts",
            _wf(consumer + "  preflight:\n    runs-on: ubuntu-latest\n    env:\n"
                "      MW_REGISTRY_KEY: ${{ secrets.MW_REGISTRY_KEY }}\n"
                '    steps: [{run: \'[ -n "${MW_REGISTRY_KEY:-}" ] || missing+=("provision it, see #3399")\'}]\n'),
            None, False,
        ),
        ("not-yaml", "jobs: [unclosed\n  - ::\n", None, True),
    ]
    failures = 0
    for name, body, allow_body, expect in cases:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / ".github" / "workflows").mkdir(parents=True)
            (root / ".github" / "workflows" / "t.yml").write_text(body, encoding="utf-8")
            if allow_body is not None:
                (root / ALLOW_FILE).write_text(allow_body, encoding="utf-8")
            violations, _, _, _ = check_tree(root)
            fired = bool(violations)
            if fired != expect:
                failures += 1
            print(f"self-test {'ok' if fired == expect else 'FAIL':4} {name:32} "
                  f"expected={'fire' if expect else 'silent'} got={'fire' if fired else 'silent'}")
    with tempfile.TemporaryDirectory() as tmp:  # an empty tree must fail, never pass vacuously
        violations, _, _, _ = check_tree(Path(tmp))
        fired = bool(violations)
        failures += 0 if fired else 1
        print(f"self-test {'ok' if fired else 'FAIL':4} {'no-workflows-dir':32} expected=fire "
              f"got={'fire' if fired else 'silent'}")
    if failures:
        print(f"::error::check-pr-secret-preflight.py self-test: {failures} case(s) did not behave — the gate is not proven")
        return 1
    print("self-test: every case fired on its defect and stayed silent on its fix")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0])
    ap.add_argument("--root", default=".", help="repository root holding .github/workflows (default: cwd)")
    ap.add_argument("--self-test", action="store_true", help="prove the gate is non-vacuous and exit")
    ap.add_argument("--check-stores", action="store_true",
                    help="also diff the required names against the Dependabot store (needs gh + an admin credential)")
    ap.add_argument("--repo", help="owner/name, required with --check-stores")
    args = ap.parse_args()
    if args.self_test:
        return self_test()
    root = Path(args.root).resolve()
    violations, required, asserted, required_names = check_tree(root)
    if args.check_stores:
        if not args.repo:
            print("::error::--check-stores needs --repo owner/name")
            return 2
        try:
            violations.extend(check_stores(args.repo, required_names))
        except RuntimeError as e:  # never downgrade an unreadable store to a pass
            print(f"::error::{e}")
            return 1
    for v in violations:
        print(v)
    print(f"check-pr-secret-preflight: {required} secret(s) reachable on a Dependabot pull request, "
          f"{asserted} asserted by a preflight, {len(violations)} violation(s), root={root}")
    return 1 if violations else 0


if __name__ == "__main__":
    sys.exit(main())
