#!/usr/bin/env python3
"""preflight-pr.py — every pull-request gate CI would red on for a reason knowable LOCALLY, in one command.

    python3 scripts/preflight-pr.py --body-file pr-body.md                 # core, online (gh's token)
    python3 scripts/preflight-pr.py --body-file pr-body.md --offline       # no network: static halves only
    python3 <core>/scripts/preflight-pr.py --repo-root . --body-file b.md  # a satellite checkout
    python3 scripts/preflight-pr.py --self-test                            # = scripts/test-preflight-pr.py

Run it before `gh pr create` and before every push. Doc: Doc/Architecture/PrePrPreflight.

WHY THIS EXISTS
---------------
The pull-request gates that read the BODY (`Pairs-with:`, `Satellite-pins:`, `Implementers:`,
`Mirror-sync:`, closing keywords and `Verified-closing:`, `Db-migration:`) and the source guards that
scan fixed directory roots all decide on inputs the author already has locally: the diff against the
merge base and the body they are about to paste. Each red that reaches CI for one of those reasons
costs a full round — a push or a body edit, a re-run, a wait — for a verdict that was knowable before
the pull request existed.

WHAT IT DOES — and what it deliberately does NOT do
---------------------------------------------------
It REUSES the gate scripts. It never re-implements a gate's rule: each gate is invoked exactly the way
the CI job invokes it (same script, same arguments, the merge base for `--base`), with the repository
root as the working directory. In core the scripts are the checkout's OWN copies (`<repo>/scripts/…`,
as core's CI runs them at the head under test); in a satellite the core half (the closing-keyword gate
in its protected-core shape, as `node-repo-validate.yml` runs it) is read from a core checkout
(`--core-root`, or the directory this script lives in) or fetched over anonymous git at `--core-ref`,
the same transport as `.github/scripts/mw-core-file.sh`; the satellite's own gates
(`scripts/check-db-migration-declaration.py`, `scripts/platform-mount.py`) are run where they exist.

`--offline` runs only the STATIC half. Where a gate needs GitHub (resolving a declared counterpart's
state, reading an issue's labels) the gate's own evaluation function is called with a resolver that
asks nothing; whatever it would have had to ask is reported as SKIPPED with that reason. A SKIPPED
check is never a pass: the verdict line says INCOMPLETE and the exit code is 2.

The source-guard check is data-driven: every `…Roots` declaration (and every inline root list handed
to `SourceScan.SourceFiles`) in the test trees is parsed, and each root a deleted or renamed path
touches is reported when it is now MISSING or EMPTY — some guards fail red on that, others skip the
root silently, which is worse.

Exit: 0 every check passed (or does not apply) · 1 at least one check would be RED in CI ·
2 nothing red, but at least one check was SKIPPED (not verified).
"""
from __future__ import annotations

import argparse
import importlib.util
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
from dataclasses import dataclass, field
from pathlib import Path

CORE_SLUG = "systemorph/meshweaver"
PLUGINS_SLUG = "systemorph/meshweaver.plugins"
CORE_GIT_URL = "https://github.com/Systemorph/MeshWeaver.git"
OFFLINE_MARK = "OFFLINE-PREFLIGHT"

PASS, FAIL, SKIPPED, NA = "PASS", "FAIL", "SKIPPED", "N/A"
GROUPS = ("body", "guards", "plugins")


@dataclass
class Result:
    name: str
    status: str
    summary: str
    details: list[str] = field(default_factory=list)
    group: str = "body"


@dataclass
class Context:
    root: Path
    slug: str
    base_ref: str
    merge_base: str
    body_file: Path | None
    offline: bool
    scratch: Path
    core_scripts: Path | None
    core_scripts_why: str
    token: str | None
    token_why: str
    platform_core: Path | None = None
    verbose: bool = False

    @property
    def is_core(self) -> bool:
        return self.slug.lower() == CORE_SLUG

    @property
    def is_plugins(self) -> bool:
        return self.slug.lower() == PLUGINS_SLUG


# ── small helpers ───────────────────────────────────────────────────────────────────────────────


def git(root: Path, *args: str, check: bool = True) -> str:
    proc = subprocess.run(["git", "-C", str(root), *args], capture_output=True, text=True)
    if check and proc.returncode != 0:
        raise RuntimeError(f"git {' '.join(args)} failed ({proc.returncode}): {proc.stderr.strip()[:400]}")
    return proc.stdout


def slug_from_remote(url: str) -> str | None:
    m = re.search(r"github\.com[:/]+([A-Za-z0-9_.-]+)/([A-Za-z0-9_.-]+?)(?:\.git)?/?$", url.strip())
    return f"{m.group(1)}/{m.group(2)}" if m else None


def load_module(path: Path, name: str):
    """Load a gate script by PATH (its file name carries hyphens), registered so dataclasses resolve."""
    spec = importlib.util.spec_from_file_location(name, path)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"cannot load {path}")
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    try:
        spec.loader.exec_module(module)
    except BaseException:
        sys.modules.pop(name, None)
        raise
    return module


def run_script(script: Path, args: list[str], cwd: Path, env: dict | None = None) -> tuple[int, str]:
    """Invoke a gate the way CI does: `python3 <script> <args>` from the repository root."""
    proc = subprocess.run([sys.executable, str(script), *args], cwd=str(cwd), capture_output=True,
                          text=True, env=env if env is not None else os.environ.copy())
    return proc.returncode, (proc.stdout or "") + (proc.stderr or "")


def error_lines(output: str) -> list[str]:
    found = [ln.split("::error::", 1)[1].strip() if "::error::" in ln else ln.strip()
             for ln in output.splitlines() if "::error" in ln]
    return [ln for ln in found if ln]


def failure_details(output: str, limit: int = 40) -> list[str]:
    errs = error_lines(output)
    if errs:
        return errs[:limit]
    tail = [ln.rstrip() for ln in output.splitlines() if ln.strip()]
    return tail[-limit:]


def from_exit(name: str, rc: int, output: str, pass_summary: str, group: str = "body") -> Result:
    if rc == 0:
        return Result(name, PASS, pass_summary, [], group)
    if rc == 3:
        # The fleet's NO-VERDICT convention (read-current-pr-body.py, mw-core-file.sh): the gate could
        # not ASK — not a finding about this change, and not a pass either.
        return Result(name, SKIPPED, "NO VERDICT — the gate could not ask GitHub (exit 3)",
                      failure_details(output), group)
    return Result(name, FAIL, f"would be RED in CI (exit {rc})", failure_details(output), group)


# ── where the core gate scripts come from (satellites) ──────────────────────────────────────────


def fetch_core_file(ref: str, path: str, dest_dir: Path) -> Path:
    """Read core's <path> at <ref> over anonymous git — the transport of .github/scripts/mw-core-file.sh."""
    if re.fullmatch(r"[0-9a-fA-F]{7,39}", ref):
        raise RuntimeError(f"'{ref}' looks like an ABBREVIATED sha — pass the full 40-hex sha or a branch/tag")
    clone = dest_dir / "core-git"
    if not (clone / ".fetched").is_file():
        shutil.rmtree(clone, ignore_errors=True)
        subprocess.run(["git", "init", "-q", str(clone)], check=True, capture_output=True)
        url = os.environ.get("MW_CORE_GIT_URL") or CORE_GIT_URL
        subprocess.run(["git", "-C", str(clone), "remote", "add", "origin", url], check=True, capture_output=True)
        proc = subprocess.run(["git", "-C", str(clone), "fetch", "-q", "--depth=1", "--filter=blob:none",
                               "origin", ref], capture_output=True, text=True)
        if proc.returncode != 0:
            raise RuntimeError(f"could not fetch core at '{ref}' from {url}: {proc.stderr.strip()[:300]}")
        (clone / ".fetched").write_text(ref)
    proc = subprocess.run(["git", "-C", str(clone), "show", f"FETCH_HEAD:{path}"], capture_output=True)
    if proc.returncode != 0:
        raise RuntimeError(f"core has no {path} at '{ref}'")
    out = dest_dir / "core-scripts" / Path(path).name
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_bytes(proc.stdout)
    return out


def locate_core_scripts(args, scratch: Path) -> tuple[Path | None, str]:
    if args.core_root:
        d = Path(args.core_root).resolve() / "scripts"
        if not (d / "check-closing-keywords.py").is_file():
            return None, f"--core-root {args.core_root} has no scripts/check-closing-keywords.py"
        return d, f"core checkout {args.core_root}"
    here = Path(__file__).resolve().parent
    if (here / "check-closing-keywords.py").is_file():
        return here, f"beside this script ({here})"
    if args.offline:
        return None, ("no core gate scripts beside this script and --offline forbids fetching them — "
                      "pass --core-root <a core checkout>")
    try:
        p = fetch_core_file(args.core_ref, "scripts/check-closing-keywords.py", scratch)
        return p.parent, f"fetched from core at '{args.core_ref}' over anonymous git"
    except (RuntimeError, OSError, subprocess.CalledProcessError) as exc:
        return None, f"could not fetch the core gate scripts: {exc}"


def resolve_token(offline: bool) -> tuple[str | None, str]:
    if offline:
        return None, "--offline"
    for name in ("GH_TOKEN", "GITHUB_TOKEN"):
        if os.environ.get(name):
            return os.environ[name], f"${name}"
    if shutil.which("gh"):
        proc = subprocess.run(["gh", "auth", "token"], capture_output=True, text=True)
        if proc.returncode == 0 and proc.stdout.strip():
            return proc.stdout.strip(), "`gh auth token`"
    return None, "no GH_TOKEN/GITHUB_TOKEN and `gh auth token` gave none — run `gh auth login`, or pass --offline"


def online_env(ctx: Context) -> dict:
    env = os.environ.copy()
    if ctx.token:
        # Each gate reads the variable its CI step sets: the pair gate GITHUB_TOKEN, the closing-keyword
        # gate GH_TOKEN (through `gh`). An explicitly set variable is left as the caller set it.
        env.setdefault("GITHUB_TOKEN", ctx.token)
        env.setdefault("GH_TOKEN", ctx.token)
    return env


# ── the body gates ──────────────────────────────────────────────────────────────────────────────


def surface_report(ctx: Context) -> tuple[Path | None, Result | None]:
    """check-type-forwards.py --surface-json, the detector both the pair and the implementer gate read."""
    script = ctx.root / "scripts" / "check-type-forwards.py"
    out = ctx.scratch / "surface.json"
    rc, output = run_script(script, ["--base", ctx.merge_base, "--surface-json", str(out)], ctx.root)
    if rc != 0 or not out.is_file():
        return None, Result("Public-surface detector (check-type-forwards --surface-json)", FAIL,
                            f"the detector did not produce a report (exit {rc}) — the pair and implementer "
                            "gates have nothing to read", failure_details(output))
    return out, None


def offline_pair(ctx: Context, surface: Path, body: str) -> Result:
    name = "Pairs-with (cross-repo pair)"
    mod = load_module(ctx.root / "scripts" / "check-cross-repo-pair.py", "_preflight_pair")
    asked: list[str] = []

    def resolve(repo: str, number: int) -> dict:
        asked.append(f"{repo}#{number}")
        raise mod.PairResolutionError(f"{OFFLINE_MARK}: not resolved — --offline")

    try:
        fleet = mod.read_fleet(ctx.root)
        projects = mod.project_report(ctx.root, ctx.merge_base)
        rc, lines = mod.evaluate(json.loads(surface.read_text(encoding="utf-8")), body, fleet, resolve, projects)
    except mod.PairResolutionError as exc:
        return Result(name, FAIL, "the gate could not read its subject", [str(exc)])
    return classify_offline(name, rc, lines, asked, "counterpart state not resolved")


def classify_offline(name: str, rc: int, lines: list[str], asked: list[str], what: str) -> Result:
    errs = [ln.split("::error::", 1)[1] if "::error::" in ln else ln for ln in lines if "::error" in ln]
    real = [e for e in errs if OFFLINE_MARK not in e]
    if rc == 0:
        return Result(name, PASS, "static half and verdict clean (nothing needed resolving)")
    if real:
        return Result(name, FAIL, "would be RED in CI — a static finding, no network needed", real)
    if asked:
        return Result(name, SKIPPED, f"static half clean; {what} offline: {', '.join(asked)} — "
                      "run online for the verdict")
    return Result(name, FAIL, f"red (exit {rc}) for a reason this preflight cannot classify", errs or lines[-10:])


def pair_gate(ctx: Context, surface: Path, body: str) -> Result:
    name = "Pairs-with (cross-repo pair)"
    if ctx.offline:
        return offline_pair(ctx, surface, body)
    if not ctx.token:
        r = offline_pair(ctx, surface, body)
        if r.status == SKIPPED:
            r.summary += f" (no token: {ctx.token_why})"
        return r
    env = online_env(ctx)
    env["GITHUB_TOKEN"] = os.environ.get("GITHUB_TOKEN") or ctx.token
    rc, output = run_script(ctx.root / "scripts" / "check-cross-repo-pair.py",
                            ["--surface-json", str(surface), "--pr-body-file", str(ctx.body_file),
                             "--base", ctx.merge_base, "--root", str(ctx.root)], ctx.root, env)
    return from_exit(name, rc, output, "every removal declared; every declared counterpart has landed")


def closing_gate(ctx: Context, body: str, script: Path, cli: list[str], name: str,
                 checked: set[str], only_checked: bool) -> Result:
    if ctx.offline or not ctx.token:
        mod = load_module(script, "_preflight_closing")
        asked: list[str] = []

        def resolve(repo: str, number: int) -> dict:
            asked.append(f"{repo}#{number}")
            # Labels unknown offline: answer "an open issue with no labels" so every STATIC refusal
            # (negation, possessive, a refused or unbound Verified-closing) still fires, and record
            # the read so the label half is reported SKIPPED rather than passed.
            return {"number": number, "is_pull_request": False, "state": "open", "labels": []}

        try:
            errors, _notes, _honoured = mod.evaluate(body, cli_repo(cli), resolve, checked, only_checked)
        except mod.Undecidable as exc:
            return Result(name, FAIL, "the gate could not read its subject", [str(exc)])
        why = "--offline" if ctx.offline else f"no token: {ctx.token_why}"
        if errors:
            return Result(name, FAIL, "would be RED in CI — a static finding, no network needed", errors)
        if asked:
            return Result(name, SKIPPED, f"static half clean; labels not read ({why}) for "
                          f"{', '.join(sorted(set(asked)))} — a sev:B/sev:H label there is RED in CI")
        return Result(name, PASS, "no closing keyword binds an issue this gate checks")
    rc, output = run_script(script, [*cli, "--pr-body-file", str(ctx.body_file)], ctx.root, online_env(ctx))
    return from_exit(name, rc, output, "every closing keyword closes something a merge may close")


def cli_repo(cli: list[str]) -> str:
    return cli[cli.index("--repo") + 1]


def body_gates(ctx: Context) -> list[Result]:
    results: list[Result] = []
    if ctx.body_file is None:
        names = (["Pairs-with (cross-repo pair)", "Satellite-pins (package pin removal)",
                  "Implementers (interface additions)", "Mirror-sync (i18n catalog)",
                  "Closing keywords / Verified-closing"] if ctx.is_core
                 else ["Closing keywords / Verified-closing (core issues)"])
        if (ctx.root / "scripts" / "check-db-migration-declaration.py").is_file():
            names.append("Db-migration (database version)")
        return [Result(n, SKIPPED, "no --body-file: the body gates judge the pull-request body") for n in names]
    body = ctx.body_file.read_text(encoding="utf-8")

    if ctx.is_core:
        surface, broken = surface_report(ctx)
        if broken:
            results.append(broken)
        if surface:
            results.append(pair_gate(ctx, surface, body))
        else:
            results.append(Result("Pairs-with (cross-repo pair)", FAIL, "no surface report (see the detector above)"))
        rc, out = run_script(ctx.root / "scripts" / "check-package-pin-removal.py",
                             ["--base", ctx.merge_base, "--pr-body-file", str(ctx.body_file)], ctx.root)
        results.append(from_exit("Satellite-pins (package pin removal)", rc, out,
                                 "no PackageVersion removal, or every removal declared"))
        if surface:
            rc, out = run_script(ctx.root / "scripts" / "check-interface-addition.py",
                                 ["--surface-json", str(surface), "--pr-body-file", str(ctx.body_file)], ctx.root)
            results.append(from_exit("Implementers (interface additions)", rc, out,
                                     "no implementer-obliging addition, or every one declared"))
        else:
            results.append(Result("Implementers (interface additions)", FAIL, "no surface report (see the detector above)"))
        rc, out = run_script(ctx.root / "scripts" / "check-i18n-mirror-sync.py",
                             ["--base", ctx.merge_base, "--pr-body-file", str(ctx.body_file)], ctx.root)
        results.append(from_exit("Mirror-sync (i18n catalog)", rc, out,
                                 "no catalog change, or the mirror sync handed over"))
        results.append(closing_gate(ctx, body, ctx.root / "scripts" / "check-closing-keywords.py",
                                    ["--repo", ctx.slug], "Closing keywords / Verified-closing",
                                    {ctx.slug.lower()}, False))
    else:
        name = "Closing keywords / Verified-closing (core issues)"
        if ctx.core_scripts is None:
            results.append(Result(name, SKIPPED, ctx.core_scripts_why))
        else:
            results.append(closing_gate(
                ctx, body, ctx.core_scripts / "check-closing-keywords.py",
                ["--repo", ctx.slug, "--protect-public-repo", "Systemorph/MeshWeaver", "--only-protected-repos"],
                name, {CORE_SLUG}, True))

    db = ctx.root / "scripts" / "check-db-migration-declaration.py"
    if db.is_file():
        rc, out = run_script(db, ["--base", ctx.merge_base, "--pr-body-file", str(ctx.body_file)], ctx.root)
        results.append(from_exit("Db-migration (database version)", rc, out,
                                 "no database-version change, or it is declared and expand-only"))
    return results


# ── source guards whose scanned roots a deletion empties ────────────────────────────────────────

ROOTS_DECL = re.compile(r"\b(?P<name>[A-Za-z_]\w*Roots)\b\s*(?:\(\s*\))?\s*(?:=>|=)(?!=)")
INLINE_ROOTS = re.compile(r"SourceFiles\(\s*[^,()]+,\s*(?=\[)")
STRING = re.compile(r'@?"((?:[^"\\]|\\.)*)"')
TEST_SEGMENT = re.compile(r"(^|/)(test|tests|[^/]+\.Tests?)(/|$)")


def initializer_span(text: str, start: int) -> str:
    """The initializer text from `start` to the `;` (or, for an inline list, the `]`) at depth 0."""
    depth = 0
    i = start
    inline = text[start:start + 1] == "["
    while i < len(text):
        ch = text[i]
        if ch == '"':
            m = re.compile(r'"(?:[^"\\]|\\.)*"').match(text, i)
            i = m.end() if m else i + 1
            continue
        if ch in "[({":
            depth += 1
        elif ch in "])}":
            depth -= 1
            if inline and depth == 0:
                return text[start:i + 1]
            if depth < 0:
                return text[start:i]
        elif ch == ";" and depth == 0:
            return text[start:i]
        i += 1
    return text[start:]


def looks_like_root(s: str) -> bool:
    return bool(s) and not re.search(r"[\s*?<>|:{}]", s) and not s.startswith("/") and ".." not in s.split("/")


@dataclass
class RootDecl:
    file: str
    line: int
    symbol: str
    roots: list[str]


def strip_comments(text: str) -> str:
    """Blank out `//` and `/* */` comments (newlines kept, so line numbers hold); strings are kept."""
    out: list[str] = []
    i, n = 0, len(text)
    while i < n:
        ch = text[i]
        if ch == '"':
            m = re.compile(r'"(?:[^"\\\n]|\\.)*"').match(text, i)
            j = m.end() if m else i + 1
            out.append(text[i:j])
            i = j
        elif text.startswith("//", i):
            j = text.find("\n", i)
            j = n if j < 0 else j
            out.append(" " * (j - i))
            i = j
        elif text.startswith("/*", i):
            j = text.find("*/", i + 2)
            j = n if j < 0 else j + 2
            out.append(re.sub(r"[^\n]", " ", text[i:j]))
            i = j
        else:
            out.append(ch)
            i += 1
    return "".join(out)


def parse_root_declarations(rel: str, text: str) -> list[RootDecl]:
    decls: list[RootDecl] = []
    text = strip_comments(text)
    for rx, symbol_of in ((ROOTS_DECL, lambda m: m.group("name")), (INLINE_ROOTS, lambda m: "SourceScan.SourceFiles([...])")):
        for m in rx.finditer(text):
            span = initializer_span(text, m.end() if rx is INLINE_ROOTS else _skip_ws(text, m.end()))
            roots = [s.strip("/") for s in STRING.findall(span) if looks_like_root(s.strip("/"))]
            if roots:
                decls.append(RootDecl(rel, text.count("\n", 0, m.start()) + 1, symbol_of(m), roots))
    return decls


def _skip_ws(text: str, i: int) -> int:
    while i < len(text) and text[i].isspace():
        i += 1
    return i


def removed_paths(root: Path, merge_base: str) -> list[tuple[str, str, str | None]]:
    """(status, old path, new path) for every path the diff deletes or renames, vs the merge base."""
    raw = git(root, "diff", "--name-status", "-M", "-z", merge_base)
    fields = raw.split("\0")
    out: list[tuple[str, str, str | None]] = []
    i = 0
    while i < len(fields) and fields[i]:
        status = fields[i]
        if status.startswith(("R", "C")):
            old, new = fields[i + 1], fields[i + 2]
            if status.startswith("R"):
                out.append(("R", old, new))
            i += 3
        else:
            if status.startswith("D"):
                out.append(("D", fields[i + 1], None))
            i += 2
    return out


def present_files(root: Path) -> list[str]:
    tracked = [p for p in git(root, "ls-files", "-z").split("\0") if p]
    return [p for p in tracked if (root / p).exists()]


def touches(root_path: str, removed: str) -> bool:
    return removed == root_path or removed.startswith(root_path + "/") or root_path.startswith(removed + "/")


def guard_roots(ctx: Context) -> list[Result]:
    name = "Source-guard scanned roots"
    removed = removed_paths(ctx.root, ctx.merge_base)
    files = present_files(ctx.root)
    guards = [p for p in files if p.endswith(".cs") and TEST_SEGMENT.search(p)]
    decls: list[RootDecl] = []
    for rel in guards:
        try:
            text = (ctx.root / rel).read_text(encoding="utf-8", errors="replace")
        except OSError:
            continue
        if "Roots" in text or "SourceFiles(" in text:
            decls.extend(parse_root_declarations(rel, text))
    if not removed:
        return [Result(name, PASS, f"the diff deletes or renames nothing ({len(decls)} root declaration(s) "
                       f"read in {len(guards)} test source file(s))", group="guards")]
    present_dirs: set[str] = set()
    present_set = set(files)
    for p in files:
        parts = p.split("/")
        for k in range(1, len(parts)):
            present_dirs.add("/".join(parts[:k]))
    findings: list[str] = []
    seen: set[tuple[str, int, str]] = set()
    for d in decls:
        for r in d.roots:
            hits = [x for x in removed if touches(r, x[1])]
            if not hits:
                continue
            if r in present_set or r in present_dirs:
                continue  # the root still holds (or is) a tracked file
            state = "EMPTY (no tracked file left under it)" if (ctx.root / r).is_dir() else "MISSING"
            key = (d.file, d.line, r)
            if key in seen:
                continue
            seen.add(key)
            why = "; ".join(f"{s} {o}" + (f" -> {n}" if n else "") for s, o, n in hits[:3])
            findings.append(f"{d.file}:{d.line} {d.symbol} names root `{r}`, now {state} — touched by: {why}"
                            + (f" (+{len(hits) - 3} more)" if len(hits) > 3 else ""))
    if findings:
        findings.append("Point the root at the path's new home (a move) or drop it (a retirement) in THIS "
                        "change — a guard that fails on an absent root goes red in CI, and one that skips it "
                        "passes having scanned nothing.")
        return [Result(name, FAIL, f"{len(findings) - 1} guard root(s) left pointing at a deleted path",
                       findings, group="guards")]
    return [Result(name, PASS, f"{len(removed)} deleted/renamed path(s) leave every one of {len(decls)} "
                   f"root declaration(s) non-empty", group="guards")]


# ── MeshWeaver.Plugins: the platform-mount audit ───────────────────────────────────────────────


def plugins_checks(ctx: Context) -> list[Result]:
    script = ctx.root / "scripts" / "platform-mount.py"
    if not ctx.is_plugins or not script.is_file():
        return [Result("Platform mount (MeshWeaver.Plugins)", NA,
                       "not a MeshWeaver.Plugins checkout carrying scripts/platform-mount.py", group="plugins")]
    out: list[Result] = []
    rc, output = run_script(script, ["--self-test"], ctx.root)
    out.append(from_exit("Platform mount — self-test", rc, output,
                         "every refusal of the mount fires (as portal-hosts runs it first)", group="plugins"))
    if ctx.platform_core is None:
        out.append(Result("Platform mount — audit (--audit-core)", SKIPPED,
                          "needs --platform-core <the core checkout a build against the platform mount used as "
                          "MeshWeaverRoot>; CI's portal-hosts lane runs it after the one-graph build",
                          group="plugins"))
    else:
        rc, output = run_script(script, ["--audit-core", str(ctx.platform_core)], ctx.root)
        out.append(from_exit("Platform mount — audit (--audit-core)", rc, output,
                             "nothing of the platform compiled from source", group="plugins"))
    return out


# ── orchestration ───────────────────────────────────────────────────────────────────────────────


def build_context(args, scratch: Path) -> Context:
    root = Path(args.repo_root).resolve()
    top = git(root, "rev-parse", "--show-toplevel").strip()
    root = Path(top)
    slug = args.repo or slug_from_remote(git(root, "remote", "get-url", "origin", check=False))
    if not slug:
        raise RuntimeError("cannot tell which repository this is (no GitHub `origin` remote) — pass --repo owner/name")
    if args.fetch and not args.offline:
        remote, _, branch = args.base.partition("/")
        if branch:
            git(root, "fetch", "--quiet", remote, branch)
    merge_base = git(root, "merge-base", args.base, "HEAD").strip()
    body = Path(args.body_file).resolve() if args.body_file else None
    if body is not None and not body.is_file():
        raise RuntimeError(f"--body-file {args.body_file} does not exist")
    core_scripts, why = (None, "core itself") if slug.lower() == CORE_SLUG else locate_core_scripts(args, scratch)
    token, token_why = resolve_token(args.offline)
    return Context(root=root, slug=slug, base_ref=args.base, merge_base=merge_base, body_file=body,
                   offline=args.offline, scratch=scratch, core_scripts=core_scripts, core_scripts_why=why,
                   token=token, token_why=token_why,
                   platform_core=Path(args.platform_core).resolve() if args.platform_core else None,
                   verbose=args.verbose)


def run_all(ctx: Context, groups: set[str]) -> list[Result]:
    results: list[Result] = []
    if "body" in groups:
        results.extend(body_gates(ctx))
    if "guards" in groups:
        results.extend(guard_roots(ctx))
    if "plugins" in groups:
        results.extend(plugins_checks(ctx))
    return results


def verdict(results: list[Result]) -> tuple[int, str]:
    fails = [r for r in results if r.status == FAIL]
    skips = [r for r in results if r.status == SKIPPED]
    if fails:
        return 1, f"RED — {len(fails)} check(s) would fail in CI" + (f"; {len(skips)} more not verified" if skips else "")
    if skips:
        return 2, f"INCOMPLETE — nothing red, but {len(skips)} check(s) were SKIPPED and are NOT verified"
    return 0, "GREEN — every check that applies passed"


def report(ctx: Context, results: list[Result]) -> int:
    dirty = [e for e in git(ctx.root, "status", "--porcelain", "-z").split("\0") if e.strip()]
    print(f"Pre-PR preflight — {ctx.slug} at {ctx.root}")
    print(f"  base {ctx.base_ref} -> merge base {ctx.merge_base[:12]}; HEAD {git(ctx.root, 'rev-parse', 'HEAD').strip()[:12]}")
    if dirty:
        print(f"  working tree: {len(dirty)} uncommitted/untracked entr(y/ies) — checked as they stand, but CI "
              "sees only what you push")
    print(f"  body: {ctx.body_file or '(none)'}   mode: {'OFFLINE (static halves only)' if ctx.offline else 'online'}"
          + ("" if ctx.offline or ctx.token else f" — but no token ({ctx.token_why})"))
    if not ctx.is_core:
        print(f"  core gate scripts: {ctx.core_scripts or '(none)'} — {ctx.core_scripts_why}")
    print()
    width = max((len(r.name) for r in results), default=10)
    for r in results:
        print(f"  {r.status:<8} {r.name:<{width}}  {r.summary}")
        for d in r.details if (r.status != PASS or ctx.verbose) else []:
            print(f"  {'':<8} {'':<{width}}  | {d}")
    code, line = verdict(results)
    print()
    print(f"Verdict: {line}")
    return code


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--repo-root", default=".", help="the checkout to check (default: cwd)")
    ap.add_argument("--base", default="origin/main", help="the base ref; the gates compare against the MERGE BASE with HEAD")
    ap.add_argument("--body-file", help="the DRAFT pull-request body (a file — never inline text)")
    ap.add_argument("--offline", action="store_true", help="no network: run the static halves, report the rest SKIPPED")
    ap.add_argument("--fetch", action="store_true", help="`git fetch` the base branch first (ignored with --offline)")
    ap.add_argument("--repo", help="owner/name (default: from the `origin` remote)")
    ap.add_argument("--only", default=",".join(GROUPS), help=f"comma list of groups to run: {', '.join(GROUPS)}")
    ap.add_argument("--core-root", help="satellite: a core checkout whose scripts/ holds the core gates")
    ap.add_argument("--core-ref", default="main", help="satellite: core ref to fetch the core gates at (default main)")
    ap.add_argument("--platform-core", help="Plugins: the core checkout to run platform-mount.py --audit-core on")
    ap.add_argument("--json", help="also write the results as JSON to this path")
    ap.add_argument("--verbose", action="store_true", help="print each gate's details for passes too")
    ap.add_argument("--self-test", action="store_true", help="run scripts/test-preflight-pr.py")
    args = ap.parse_args(argv)

    if args.self_test:
        test = Path(__file__).resolve().parent / "test-preflight-pr.py"
        return subprocess.run([sys.executable, "-I", str(test)]).returncode
    groups = {g.strip() for g in args.only.split(",") if g.strip()}
    unknown = groups - set(GROUPS)
    if unknown:
        ap.error(f"unknown group(s) {', '.join(sorted(unknown))}; known: {', '.join(GROUPS)}")
    with tempfile.TemporaryDirectory(prefix="preflight-pr-") as tmp:
        try:
            ctx = build_context(args, Path(tmp))
        except RuntimeError as exc:
            print(f"::error::preflight could not start: {exc}")
            return 1
        results = run_all(ctx, groups)
        code = report(ctx, results)
        if args.json:
            Path(args.json).write_text(json.dumps({
                "repo": ctx.slug, "mergeBase": ctx.merge_base, "offline": ctx.offline, "exit": code,
                "results": [r.__dict__ for r in results]}, indent=2), encoding="utf-8")
        return code


if __name__ == "__main__":
    sys.exit(main())
