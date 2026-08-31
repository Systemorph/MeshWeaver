#!/usr/bin/env python3
"""Gate the shell that CI itself runs.

WHY THIS EXISTS
---------------
`main-cd.yml` is 2000 lines of shell that decides what gets built, published and rolled to
the fleet — and until this script, NOTHING opened it. It fires on `workflow_run`, `schedule`
and `workflow_dispatch`; never on `pull_request`. So an edit to it merged on the strength of
being valid YAML, and the first execution was in production, on a schedule, at 03:00.

What that cost, measured (MeshWeaver#2642, written up in #2643):

    tags=$(az acr manifest list-metadata ... \
             --query "[?contains(tags, '${SHORT_SHA}')].tags[]" -o tsv 2>/dev/null || true)

`tags` is NULL on the orphaned per-platform manifests an index push leaves behind (16 of
memex-portal-ai's 2101). jmespath's `contains()` THROWS on null rather than returning false,
and the throw aborts the WHOLE query — so one orphan made the read unable to find any tag.
`2>/dev/null || true` then turned "the query crashed" into "there is no version tag", and the
step blamed a component that was working perfectly. Nine consecutive scheduled runs, ~33
hours, and the step had never once succeeded in its life.

THE THREE CHECKS, and what each is honestly worth
-------------------------------------------------
  shellcheck  Extract every `run:` block from every workflow and shellcheck it. This would
              NOT have caught the bug above. It is here because it is nearly free and it
              catches a real class (quoting, dead variables, broken tests) — and because the
              only thing standing between a workflow `run:` and production is review.

  swallow     THE bug class above: a command substitution whose value is CAPTURED and then
              read as authoritative, with BOTH its stderr sent to /dev/null AND its exit
              status swallowed. That combination converts "the call failed" into "the answer
              is empty" and leaves no trace anywhere — not in the log, not in the exit code.
              Only captured substitutions are flagged: `cp … 2>/dev/null || true` collecting
              diagnostics is a different act, because nothing reads its output.

  jmespath    The specific throw: an `az --query` filter that calls contains()/starts_with()/
              ends_with()/length() on a field that can be null. Checked by EXECUTION, not by
              regex — the real jmespath engine az uses, over a fixture that mirrors the live
              registry (a null-field row beside a populated one). No Azure, no credentials.

THE ALLOW FILE (.github/workflow-shell.allow) is a one-way ratchet
-----------------------------------------------------------------
Every entry needs a `#` reason on the line(s) above it, an entry that matches nothing FAILS
(so the list can only shrink), and a reasonless entry FAILS. Seeded with the seven
fail-open/best-effort sites that already existed; adding to it is a diagnosis, not a fix.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
from dataclasses import dataclass
from pathlib import Path

# ── what is scanned ─────────────────────────────────────────────────────────────────────────
WORKFLOW_GLOBS = [".github/workflows/*.yml", ".github/workflows/*.yaml"]
SHELL_GLOBS = [".github/scripts/*.sh"]

# shellcheck severity. Matches the level the operator-scripts gate already runs at
# (hosting-operator.yml), so one repo does not carry two different bars for the same tool.
SHELLCHECK_SEVERITY = "warning"

# A `${{ … }}` expression is not shell. Substituting a LITERAL for it makes shellcheck
# "helpfully" report every `[ "${{ github.event_name }}" = "push" ]` as a constant expression
# (11 such findings, plus 3 SC2157 and 2 SC2043, on this repo — all pure artefacts of the
# substitution). Substituting a VARIABLE reference is faithful: the value is unknown at
# check time, which is exactly what a GitHub expression is. That drops the corpus from
# 16 artefacts to 0, and leaves the 2 findings that are real.
GHA_EXPR = re.compile(r"\$\{\{[^}]*\}\}")
GHA_PLACEHOLDER = "$_GHA_EXPR"

# jmespath functions that RAISE on a null argument instead of returning a falsy answer.
NULL_INTOLERANT_FUNCS = ("contains", "starts_with", "ends_with", "length")

STDERR_SWALLOW = re.compile(r"2\s*>\s*/dev/null|&>\s*/dev/null|>\s*/dev/null\s+2\s*>\s*&\s*1|2\s*>\s*&\s*-")
# The failure path must yield a value INDISTINGUISHABLE from a successful-but-empty call:
# `|| true`, `|| :`, `|| echo` with nothing, `|| echo ""`, `|| printf ''`.
#
# 🚨 `|| echo unknown` / `|| echo parse-error` is deliberately NOT flagged, and the distinction is
# the whole point of the rule rather than a softening of it. A named sentinel is the CORRECT
# handling of this class: the caller can tell "the call failed" from "the answer is no", and the
# repo already does this in seven places. Flagging it would punish the fix and teach people to
# reach for `|| true` instead. What #2642 shipped was the other kind — an empty string that reads
# exactly like a real, authoritative "there is no version tag".
EXIT_SWALLOW = re.compile(
    r"""(?x)
    \|\|\s*
    (?: true\b
      | :\s* (?: [;)&|]|$ )
      | echo\s* (?: ""|'' )? \s* (?: [;)&|]|$ )
      | printf\s+ (?: ""|'' ) \s* (?: [;)&|]|$ )
    )
    """
)
# A capture: an assignment from a command substitution, or a process substitution feeding a
# redirect (`done < <(…)`), or a here-string. In every case something downstream READS the
# value and acts on it.
CAPTURE = re.compile(
    r"""(?x)
    (?: (?:^|;|\||&&|\|\||\bthen\b|\bdo\b|\{)\s*
        (?:local\s+|export\s+|declare\s+(?:-\w+\s+)?|readonly\s+|typeset\s+)?
        [A-Za-z_][A-Za-z0-9_]*\s*=\s*"?\$\(          # VAR=$( … )   /   VAR="$( … )"
    )
  | (?: <\s*<\(  )                                    # done < <( … )
  | (?: <<<\s*"?\$\( )                                # <<< "$( … )"
    """
)


@dataclass
class Finding:
    check: str          # shellcheck | swallow | jmespath | script-needs-tree
    path: str
    line: int
    code: str           # the offending source, normalized
    message: str

    @property
    def key(self) -> str:
        """Stable identity for the allow file: check + file + hash of the normalized code.

        Deliberately NOT the line number — a gate whose allow file goes stale every time
        someone adds a comment above the line teaches people to regenerate it, not to read
        it. Deliberately DOES include the code — changing the line re-opens the question.
        """
        h = hashlib.sha256(self.code.encode()).hexdigest()[:12]
        return f"{self.check} {self.path} {h}"


# ── logical lines ───────────────────────────────────────────────────────────────────────────
def logical_lines(script: str) -> list[tuple[int, str]]:
    """Join backslash continuations and unbalanced `$(` so a multi-line az call is ONE line.

    The bug this gate exists for spans three physical lines: the command on the first, the
    `--query` on the third, and `2>/dev/null || true` at the very end. A line-at-a-time
    scanner sees none of it.
    """
    out: list[tuple[int, str]] = []
    buf: list[str] = []
    start = 0
    depth = 0
    for i, raw in enumerate(script.split("\n"), start=1):
        if not buf:
            start = i
        buf.append(raw)
        joined = " ".join(s.rstrip("\\").strip() if s.rstrip().endswith("\\") else s for s in buf)
        # Count only substitution parens, ignoring anything inside single quotes.
        stripped = re.sub(r"'[^']*'", "", joined)
        depth = stripped.count("$(") + stripped.count("<(") - stripped.count(")")
        if raw.rstrip().endswith("\\") or depth > 0:
            continue
        out.append((start, joined))
        buf = []
    if buf:
        out.append((start, " ".join(buf)))
    return out


# ── workflow parsing ────────────────────────────────────────────────────────────────────────
def run_blocks(path: Path) -> list[dict]:
    """Every `run:` scalar in a workflow, with the file line its body starts on."""
    import yaml

    node = yaml.compose(path.read_text())
    found: list[dict] = []

    def walk(n, step_name=None):
        if isinstance(n, yaml.MappingNode):
            keys = {k.value: v for k, v in n.value if isinstance(k, yaml.ScalarNode)}
            nm = keys["name"].value if isinstance(keys.get("name"), yaml.ScalarNode) else step_name
            run = keys.get("run")
            if isinstance(run, yaml.ScalarNode):
                found.append(
                    dict(
                        # start_mark of a block scalar points at the `|`/`>` line; the body
                        # begins on the next one.
                        line=run.start_mark.line + 2,
                        name=nm,
                        body=run.value,
                    )
                )
            for _, v in n.value:
                walk(v, nm)
        elif isinstance(n, yaml.SequenceNode):
            for v in n.value:
                walk(v, step_name)

    walk(node)
    return found


def sources(root: Path) -> list[tuple[str, int, str, str | None]]:
    """(path, first-line-of-body, shell text, step-name) for everything this gate reads."""
    out = []
    for g in WORKFLOW_GLOBS:
        for p in sorted(root.glob(g)):
            rel = str(p.relative_to(root))
            for blk in run_blocks(p):
                out.append((rel, blk["line"], blk["body"], blk["name"]))
    for g in SHELL_GLOBS:
        for p in sorted(root.glob(g)):
            out.append((str(p.relative_to(root)), 1, p.read_text(), None))
    return out


# ── check 1: shellcheck ─────────────────────────────────────────────────────────────────────
def check_shellcheck(root: Path) -> list[Finding]:
    findings: list[Finding] = []
    for rel, base, body, step in sources(root):
        if not rel.startswith(".github/workflows/"):
            continue  # .sh files on disk are shellchecked where they live, not re-extracted
        neutral = GHA_EXPR.sub(GHA_PLACEHOLDER, body)
        # Two prologue lines: the shebang, and a definition so the placeholder is a real
        # variable rather than an "undefined" finding of its own.
        script = "#!/usr/bin/env bash\n_GHA_EXPR=x\n" + neutral
        proc = subprocess.run(
            ["shellcheck", "-f", "gcc", "-s", "bash", "-S", SHELLCHECK_SEVERITY, "-"],
            input=script, capture_output=True, text=True,
        )
        lines = neutral.split("\n")
        for out_line in proc.stdout.splitlines():
            m = re.match(r"-:(\d+):(\d+): (\w+): (.*) \[(SC\d+)\]", out_line)
            if not m:
                continue
            off = int(m.group(1)) - 3  # minus the two prologue lines, minus 1-based
            code = lines[off].strip() if 0 <= off < len(lines) else ""
            findings.append(
                Finding(
                    "shellcheck", rel, base + off, f"{m.group(5)} {code}",
                    f"{m.group(5)} ({m.group(3)}) in step {step!r}: {m.group(4)}",
                )
            )
    return findings


# ── check 2: the swallowed exit ─────────────────────────────────────────────────────────────
def check_swallow(root: Path) -> list[Finding]:
    findings: list[Finding] = []
    for rel, base, body, step in sources(root):
        for off, line in logical_lines(body):
            code = " ".join(line.split())
            if code.lstrip().startswith("#"):
                continue
            if not CAPTURE.search(line):
                continue
            if not (STDERR_SWALLOW.search(line) and EXIT_SWALLOW.search(line)):
                continue
            findings.append(
                Finding(
                    "swallow", rel, base + off - 1, code,
                    "a CAPTURED command substitution swallows both stderr and the exit status "
                    f"(step {step!r}) — a failed call becomes an empty answer with no trace. "
                    "Either let it fail (drop `|| true`), or keep the stderr so the log says why "
                    "it was empty. If the empty answer is genuinely a designed fail-open, put it "
                    "in .github/workflow-shell.allow with the reason.",
                )
            )
    return findings


# ── check 3: jmespath null-tolerance, by EXECUTION ──────────────────────────────────────────
QUERY_ARG = re.compile(r"--query(?:\s+|=)(\"[^\"]*\"|'[^']*'|[^\s]+)")
SHELL_VAR = re.compile(r"\$\{[A-Za-z_][A-Za-z0-9_]*(?::-[^}]*)?\}|\$[A-Za-z_][A-Za-z0-9_]*")


def check_jmespath(root: Path) -> list[Finding]:
    import jmespath
    from jmespath.exceptions import JMESPathError

    findings: list[Finding] = []
    for rel, base, body, step in sources(root):
        neutral = GHA_EXPR.sub("GHAEXPR", body)
        for off, line in logical_lines(neutral):
            for m in QUERY_ARG.finditer(line):
                raw = m.group(1)
                if raw[:1] in "\"'" and raw[-1:] == raw[:1]:
                    raw = raw[1:-1]
                expr = SHELL_VAR.sub("SHELLVAR", raw)
                if not any(f + "(" in expr for f in NULL_INTOLERANT_FUNCS):
                    continue  # a query with no throwing function cannot throw on null
                try:
                    parsed = jmespath.compile(expr)
                except JMESPathError as e:
                    findings.append(
                        Finding("jmespath", rel, base + off - 1, expr,
                                f"is not a valid jmespath expression ({e}) — az would exit "
                                "non-zero every time this ran."))
                    continue

                fields = sorted(_fields(parsed.parsed))
                if not fields:
                    continue
                full = [{f: "meshweaver-fixture" for f in fields}] * 2
                # The live shape: one row whose fields are null (the untagged manifests an
                # index push orphans) sitting beside a populated one.
                nulled = [{f: None for f in fields}, {f: "meshweaver-fixture" for f in fields}]
                try:
                    parsed.search(full)
                except JMESPathError as e:
                    findings.append(
                        Finding("jmespath", rel, base + off - 1, expr,
                                f"could not be verified — the derived fixture {full[0]!r} does "
                                f"not fit it ({e}). Refusing to report a pass on no evidence; "
                                "give this query a hand-written fixture or allow-list it."))
                    continue
                try:
                    parsed.search(nulled)
                except JMESPathError as e:
                    findings.append(
                        Finding("jmespath", rel, base + off - 1, expr,
                                f"THROWS on a row whose field is null ({e}). jmespath aborts the "
                                "WHOLE query on that throw, so one such row makes this read find "
                                "nothing at all — and az exits non-zero. Guard the field: "
                                "`[?field && contains(field, …)]`. (MeshWeaver#2642.)"))
    return findings


def _fields(node) -> set[str]:
    out: set[str] = set()
    if isinstance(node, dict):
        if node.get("type") == "field":
            out.add(node["value"])
        for c in node.get("children") or []:
            out |= _fields(c)
        v = node.get("value")
        if isinstance(v, (dict, list)):
            out |= _fields(v)
    elif isinstance(node, list):
        for c in node:
            out |= _fields(c)
    return out


# ── the allow file ──────────────────────────────────────────────────────────────────────────
def check_script_needs_tree(root: Path) -> list[Finding]:
    """A job that RUNS a repo file must CHECK OUT the repo.

    The class, measured (#2857 → CD 33344774738): the retried ACR login became a script,
    six call sites were swapped, and the three that lived in jobs WITHOUT a checkout —
    `promote` among them — failed their first production execution with
    `bash: .github/scripts/acr-login.sh: No such file or directory` (exit 127). The PR was
    green: those jobs only run on a publishing CD run, never on a PR, so the green attested
    to a code path that had never executed. A job that only runs in production is not
    covered by anything that gates the change to it — so the TREE requirement is asserted
    statically here, where it CAN run on the PR.

    Flagged: any job with a `run:` step referencing `.github/scripts/` and no
    `actions/checkout` step. The reference is textual on the run block (the same posture as
    the swallow check) — a path built by string concatenation can evade it, which is
    accepted: the common case is the literal path, and the evasion still has to get past
    review wearing a concatenated path for no stated reason.
    """
    import yaml

    findings: list[Finding] = []
    for pattern in WORKFLOW_GLOBS:
        for path in sorted(root.glob(pattern)):
            doc = yaml.safe_load(path.read_text())
            if not isinstance(doc, dict) or not isinstance(doc.get("jobs"), dict):
                continue
            for job_name, job in doc["jobs"].items():
                steps = job.get("steps") if isinstance(job, dict) else None
                if not isinstance(steps, list):
                    continue
                has_checkout = any(
                    isinstance(st, dict) and str(st.get("uses", "")).startswith("actions/checkout")
                    for st in steps
                )
                if has_checkout:
                    continue
                for st in steps:
                    run_block = st.get("run") if isinstance(st, dict) else None
                    if isinstance(run_block, str) and ".github/scripts/" in run_block:
                        rel = str(path.relative_to(root))
                        code = f"{job_name}: " + run_block.strip().splitlines()[0]
                        findings.append(Finding(
                            "script-needs-tree", rel, 1, code,
                            f"job `{job_name}` runs a repo script but has no actions/checkout step — "
                            "on the runner that file does not exist (exit 127, the #2857 Promote break)"))
                        break
    return findings


def read_allow(path: Path) -> tuple[dict[str, str], list[str]]:
    """Returns {key: reason} and a list of format errors.

    An entry with no `#` reason directly above it is a format ERROR, not a silent pass: the
    whole point of the list is that every waiver was diagnosed by a person.
    """
    entries: dict[str, str] = {}
    errors: list[str] = []
    if not path.exists():
        return entries, errors
    reason: list[str] = []
    for n, raw in enumerate(path.read_text().split("\n"), start=1):
        line = raw.strip()
        if not line:
            reason = []
            continue
        if line.startswith("#"):
            reason.append(line.lstrip("#").strip())
            continue
        parts = line.split()
        if len(parts) != 3:
            errors.append(f"{path}:{n}: expected `<check> <path> <hash>`, got {line!r}")
            reason = []
            continue
        text = " ".join(r for r in reason if r)
        if not text:
            errors.append(
                f"{path}:{n}: `{line}` has no reason. Put the diagnosis in `#` comment lines "
                "directly above it — an undiagnosed waiver is how debt becomes permanent.")
        entries[line] = text
        reason = []
    return entries, errors


# ── self-test ───────────────────────────────────────────────────────────────────────────────
SELF_TEST_WORKFLOW = """\
name: fixture
on: [push]
jobs:
  j:
    runs-on: ubuntu-latest
    steps:
      - name: The pre-2642 read
        run: |
          set -uo pipefail
          tags=$(az acr manifest list-metadata --registry meshweaver --name memex-portal-ai \\
                   --query "[?contains(tags, '${SHORT_SHA}')].tags[]" -o tsv 2>/dev/null || true)
          echo "$tags"
"""

SELF_TEST_FIXED = """\
name: fixture
on: [push]
jobs:
  j:
    runs-on: ubuntu-latest
    steps:
      - name: The post-2642 read
        run: |
          set -uo pipefail
          tags=$(az acr manifest list-metadata --registry meshweaver --name memex-portal-ai \\
                   --query "[?tags && contains(tags, '${SHORT_SHA}')].tags[]" -o tsv)
          echo "$tags"
"""

SELF_TEST_BEST_EFFORT = r"""name: fixture
on: [push]
jobs:
  j:
    runs-on: ubuntu-latest
    steps:
      - name: Collect diagnostics
        run: |
          cp -r probe-logs diagnostics/ 2>/dev/null || true
          find . -name '*.trx' -exec cp {} out/ \; 2>/dev/null || true
"""

SELF_TEST_SHELL_BUG = """\
name: fixture
on: [push]
jobs:
  j:
    runs-on: ubuntu-latest
    steps:
      - name: Dead variable
        run: |
          UNUSED_HERE=(--seed /seed)
          echo ok
"""


def self_test(root: Path) -> int:
    """Prove each check is non-vacuous: it must FIRE on the defect and stay SILENT on the fix.

    Mirrors `affected-modules.py --self-test` in MeshWeaver.Plugins. A gate whose self-test
    only asserts the happy path proves nothing — every case below has both halves.
    """
    failures: list[str] = []

    def case(name: str, ok: bool, detail: str = "") -> None:
        print(f"  {'PASS' if ok else 'FAIL'}  {name}" + (f"  — {detail}" if detail and not ok else ""))
        if not ok:
            failures.append(name)

    with tempfile.TemporaryDirectory() as td:
        tmp = Path(td)
        (tmp / ".github" / "workflows").mkdir(parents=True)
        (tmp / ".github" / "scripts").mkdir(parents=True)
        wf = tmp / ".github" / "workflows" / "fixture.yml"

        # ── swallow: the exact regression, and the exact fix ────────────────────────────
        wf.write_text(SELF_TEST_WORKFLOW)
        got = check_swallow(tmp)
        case("swallow FIRES on the pre-#2642 `2>/dev/null || true` capture", len(got) == 1,
             f"got {len(got)}: {[f.code for f in got]}")

        wf.write_text(SELF_TEST_FIXED)
        got = check_swallow(tmp)
        case("swallow is SILENT on the #2642 fix", len(got) == 0, f"got {[f.code for f in got]}")

        # A swallow whose output nobody reads is a different act, and must not be flagged —
        # otherwise the allow file fills with diagnostics-collection noise and stops meaning
        # anything.
        wf.write_text(SELF_TEST_BEST_EFFORT)
        got = check_swallow(tmp)
        case("swallow is SILENT on best-effort cp/find (nothing reads the output)",
             len(got) == 0, f"got {[f.code for f in got]}")

        # ── shellcheck ─────────────────────────────────────────────────────────────────
        wf.write_text(SELF_TEST_SHELL_BUG)
        got = check_shellcheck(tmp)
        case("shellcheck FIRES on a dead variable",
             len(got) == 1 and "SC2034" in got[0].message, f"got {[f.message for f in got]}")

        wf.write_text(SELF_TEST_FIXED)
        got = check_shellcheck(tmp)
        case("shellcheck is SILENT on the #2642 fix", len(got) == 0, f"got {[f.message for f in got]}")

        # A `${{ … }}` expression must not be turned into a constant — that artefact alone
        # produced 16 false findings on this repo, which is more than enough to make a gate
        # unusable and then allow-listed into uselessness.
        wf.write_text(
            "name: f\non: [push]\njobs:\n  j:\n    runs-on: ubuntu-latest\n    steps:\n"
            '      - run: |\n          if [ "${{ github.event_name }}" = "push" ]; then echo hi; fi\n'
        )
        got = check_shellcheck(tmp)
        case("shellcheck does not misread a ${{ }} expression as a constant",
             len(got) == 0, f"got {[f.message for f in got]}")

        # ── script-needs-tree: the #2857 Promote break ─────────────────────────────────
        wf.write_text(
            "name: f\non: [push]\njobs:\n  promote:\n    runs-on: ubuntu-latest\n    steps:\n"
            "      - name: ACR login\n        run: bash .github/scripts/acr-login.sh\n"
        )
        got = check_script_needs_tree(tmp)
        case("script-needs-tree FIRES on a repo-script run in a checkout-less job",
             len(got) == 1 and "promote" in got[0].code, f"got {[f.code for f in got]}")

        wf.write_text(
            "name: f\non: [push]\njobs:\n  promote:\n    runs-on: ubuntu-latest\n    steps:\n"
            "      - uses: actions/checkout@v7\n"
            "        with:\n          sparse-checkout: .github/scripts\n"
            "      - name: ACR login\n        run: bash .github/scripts/acr-login.sh\n"
        )
        got = check_script_needs_tree(tmp)
        case("script-needs-tree is SILENT once the job checks out (sparse counts)",
             len(got) == 0, f"got {[f.code for f in got]}")

        # ── jmespath: THE fixture — a manifest list with an untagged row ────────────────
        wf.write_text(SELF_TEST_WORKFLOW)
        got = check_jmespath(tmp)
        case("jmespath FIRES on contains() over an unguarded field", len(got) == 1,
             f"got {[f.message for f in got]}")

        wf.write_text(SELF_TEST_FIXED)
        got = check_jmespath(tmp)
        case("jmespath is SILENT on the `tags && contains(tags, …)` guard", len(got) == 0,
             f"got {[f.message for f in got]}")

        # Belt and braces: prove the property directly against a fixture shaped like the
        # live registry, so the check above is not merely agreeing with itself.
        import jmespath
        from jmespath.exceptions import JMESPathError

        manifests = (
            [{"digest": f"sha256:{i:064x}", "tags": None} for i in range(16)]      # index orphans
            + [{"digest": "sha256:6c3ab", "tags": ["aaf95af", "main", "3.0.0-rc8.ci.6360"]}]
        )
        threw = False
        try:
            jmespath.compile("[?contains(tags, 'aaf95af')].tags[]").search(manifests)
        except JMESPathError:
            threw = True
        case("fixture: the unguarded query really does throw on 16 untagged manifests", threw)

        guarded = jmespath.compile("[?tags && contains(tags, 'aaf95af')].tags[]").search(manifests)
        case("fixture: the guarded query recovers 3.0.0-rc8.ci.6360",
             guarded == ["aaf95af", "main", "3.0.0-rc8.ci.6360"], f"got {guarded!r}")

        # ── allow-file mechanics ───────────────────────────────────────────────────────
        allow = tmp / "allow"
        allow.write_text("# a reason\nswallow .github/workflows/fixture.yml deadbeefcafe\n")
        entries, errs = read_allow(allow)
        case("allow: an entry with a reason parses", entries and not errs, f"{entries} {errs}")

        allow.write_text("swallow .github/workflows/fixture.yml deadbeefcafe\n")
        _, errs = read_allow(allow)
        case("allow: a REASONLESS entry is an error", len(errs) == 1, f"got {errs}")

        allow.write_text("# reason\nnonsense\n")
        _, errs = read_allow(allow)
        case("allow: a malformed entry is an error", len(errs) == 1, f"got {errs}")

        # The ratchet: an entry that no longer matches anything must FAIL, so the list can
        # only ever shrink.
        wf.write_text(SELF_TEST_FIXED)
        # Captured: this case DELIBERATELY produces the gate's failure output, and a raw
        # `::error::` from a passing job would put a spurious annotation on a green run.
        import contextlib, io
        sink = io.StringIO()
        with contextlib.redirect_stdout(sink):
            rc = run(tmp, allow_path=Path(td) / "stale.allow", write_stale=True)
        case("allow: a STALE entry fails the gate (the list may only shrink)",
             rc != 0 and "may only shrink" in sink.getvalue(), f"exit {rc}: {sink.getvalue()}")

    print()
    if failures:
        print(f"::error::self-test FAILED: {len(failures)} case(s) — {', '.join(failures)}")
        return 1
    print("self-test: all cases passed")
    return 0


# ── driver ──────────────────────────────────────────────────────────────────────────────────
def run(root: Path, allow_path: Path, write_stale: bool = False) -> int:
    if write_stale:
        allow_path.write_text("# stale on purpose (self-test)\nswallow nowhere.yml 000000000000\n")

    entries, errors = read_allow(allow_path)
    for e in errors:
        print(f"::error::{e}")

    findings = (check_shellcheck(root) + check_swallow(root) + check_jmespath(root)
                + check_script_needs_tree(root))

    used: set[str] = set()
    live: list[Finding] = []
    for f in findings:
        if f.key in entries:
            used.add(f.key)
            continue
        live.append(f)

    stale = sorted(set(entries) - used)

    for f in live:
        print(f"::error file={f.path},line={f.line}::[{f.check}] {f.message}")
        print(f"    {f.path}:{f.line}")
        print(f"        {f.code}")
        print(f"    If this is deliberate, add to {allow_path.name} WITH the reason:")
        print(f"        # <why this failure must not fail the step>")
        print(f"        {f.key}")
        print()

    for k in stale:
        print(f"::error::{allow_path.name} entry `{k}` no longer matches anything. "
              "Delete it — this list is a one-way ratchet and may only shrink.")

    ok = not live and not stale and not errors
    print(f"{len(findings)} finding(s): {len(live)} live, {len(used)} allow-listed, "
          f"{len(stale)} stale entr(ies), {len(errors)} allow-file error(s)")
    return 0 if ok else 1


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--root", default=".", help="repository root")
    ap.add_argument("--allow", default=".github/workflow-shell.allow")
    ap.add_argument("--self-test", action="store_true",
                    help="prove every check fires on its defect and stays silent on its fix")
    args = ap.parse_args()

    # 🚨 The tools are ASSERTED, never assumed. shellcheck ships on hosted runners and
    # jmespath is a pip install; either one missing must turn this RED. A gate that quietly
    # degrades to "the checks it could run" is a gate that reports a tick for work it did
    # not do — and GitHub renders that tick identically to a real one.
    missing = []
    if not shutil.which("shellcheck"):
        missing.append("shellcheck — apt-get install shellcheck")
    try:
        import yaml  # noqa: F401
    except ImportError:
        missing.append("PyYAML — pip install pyyaml")
    try:
        import jmespath  # noqa: F401
    except ImportError:
        missing.append("jmespath — pip install jmespath (the same engine the azure-cli uses)")
    if missing:
        print("::error::the workflow-shell gate cannot run — provide:")
        for m in missing:
            print(f"  • {m}")
        return 1

    root = Path(args.root).resolve()
    if args.self_test:
        return self_test(root)
    return run(root, root / args.allow)


if __name__ == "__main__":
    sys.exit(main())
