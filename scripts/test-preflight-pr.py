#!/usr/bin/env python3
"""test-preflight-pr.py — prove scripts/preflight-pr.py can say RED, GREEN and SKIPPED, each for its own reason.

    python3 -I scripts/test-preflight-pr.py

FAKES ONLY. Every case builds throwaway git repositories, draft body files and a fake `gh` on PATH; a
dead proxy is set for every subprocess so a urllib call that slipped through fails instead of reaching
GitHub. Nothing here calls the live API.

What is real and what is a fake, on purpose:
  * the closing-keyword gate is the REAL scripts/check-closing-keywords.py (copied into the fixture
    core repo): it is standalone, so its online half runs against the fake `gh` and its offline half
    is the real `evaluate` the preflight calls with a resolver that asks nothing;
  * the pair gate's offline adapter is exercised against the REAL check-cross-repo-pair.py on this
    checkout (its floors need a real tree), with a synthetic surface report;
  * the surface, pin, implementer and i18n gates — whose floors (500 public types, 50 package pins…) no
    fixture can meet — are STUBS that record their argv and go red on a marker in the body. They test
    the ORCHESTRATION: the right script, the merge base, the body file, one red per marker;
  * the source-guard root scan is the preflight's own code, run over real `git diff -M` output.

Every red case asserts that ONLY its own check is red (each negative control fails for exactly its
own reason) and every green case that nothing is; a neutered detector is shown to turn a red green,
so the red cases cannot pass vacuously.
"""
from __future__ import annotations

import importlib.util
import json
import os
import shutil
import subprocess
import sys
import tempfile
import textwrap
from pathlib import Path

HERE = Path(__file__).resolve().parent
PREFLIGHT = HERE / "preflight-pr.py"
REAL_CLOSING = HERE / "check-closing-keywords.py"
REAL_PAIR = HERE / "check-cross-repo-pair.py"

failures: list[str] = []
passed = 0


def check(label: str, cond: bool, detail: str = "") -> None:
    global passed
    if cond:
        passed += 1
    else:
        failures.append(f"{label}" + (f"\n      {detail}" if detail else ""))


def load_preflight():
    spec = importlib.util.spec_from_file_location("preflight_pr_under_test", PREFLIGHT)
    mod = importlib.util.module_from_spec(spec)
    sys.modules["preflight_pr_under_test"] = mod
    spec.loader.exec_module(mod)
    return mod


# ── fixtures ────────────────────────────────────────────────────────────────────────────────────

FAKE_GH = r'''#!/usr/bin/env python3
import json, os, sys
log = os.environ.get("FAKE_GH_LOG")
if log:
    with open(log, "a") as fh:
        fh.write(" ".join(sys.argv[1:]) + "\n")
args = sys.argv[1:]
if args[:2] == ["auth", "token"]:
    if os.environ.get("FAKE_GH_NO_AUTH"):
        sys.exit(1)
    print("fake-token-from-gh")
    sys.exit(0)
if args and args[0] == "api":
    path = [a for a in args[1:] if a.startswith("repos/")][0]
    number = int(path.rsplit("/", 1)[1])
    labels = [l for l in os.environ.get("FAKE_GH_LABELS", "").split(",") if l]
    print(json.dumps({"number": number, "state": "open",
                      "labels": [{"name": l} for l in labels]}))
    sys.exit(0)
print("fake gh: unexpected call " + " ".join(args), file=sys.stderr)
sys.exit(99)
'''

# A stub gate: records its argv, goes red when the body carries its marker. Used ONLY for gates whose
# real floors no fixture can meet; the preflight's job here is to call them right, not to judge for them.
STUB = r'''#!/usr/bin/env python3
import json, os, sys
NAME = "{name}"
MARKER = "{marker}"
def _stub_main():
    argv = sys.argv[1:]
    with open(os.path.join(os.getcwd(), "calls.log"), "a") as fh:
        fh.write(json.dumps({{"gate": NAME, "argv": argv}}) + "\n")
    def arg(flag):
        return argv[argv.index(flag) + 1] if flag in argv else None
    if NAME == "type-forwards":
        removed = json.loads(os.environ.get("STUB_REMOVED", "[]"))
        with open(arg("--surface-json"), "w") as fh:
            json.dump({{"publicTypesAtBase": 2000, "removed": removed}}, fh)
        return 0
    if NAME == "platform-mount":
        if "--self-test" in argv:
            return 1 if os.environ.get("STUB_PM_FAIL") else 0
        return 1 if os.environ.get("STUB_AUDIT_FAIL") else 0
    body = open(arg("--pr-body-file"), encoding="utf-8").read()
    if MARKER and MARKER in body:
        print("::error::" + NAME + " found " + MARKER)
        return 1
    print(NAME + " ok")
    return 0
if __name__ == "__main__":
    sys.exit(_stub_main())
'''

# The pair gate stub also exposes the functions the preflight's OFFLINE adapter imports.
PAIR_STUB_MODULE = r'''

class PairResolutionError(RuntimeError):
    pass

def read_fleet(root):
    return {"Systemorph/MeshWeaver.Plugins"}

def project_report(root, base):
    return {"projectsAtBase": 1000, "removed": []}

def evaluate(surface, body, fleet, resolve, projects):
    if "PAIR-BAD" in body:
        return 1, ["::error::pair-bad declared"]
    if "Pairs-with: Systemorph/MeshWeaver.Plugins#1" in body:
        try:
            resolve("Systemorph/MeshWeaver.Plugins", 1)
        except PairResolutionError as e:
            return 1, ["::error::Cannot resolve Systemorph/MeshWeaver.Plugins#1: " + str(e)]
    return 0, ["ok"]
'''


def run(cmd, cwd, env=None, check_rc=True):
    p = subprocess.run(cmd, cwd=str(cwd), capture_output=True, text=True, env=env)
    if check_rc and p.returncode != 0:
        raise RuntimeError(f"{cmd} failed: {p.stderr}")
    return p


def git(repo: Path, *args: str) -> str:
    return run(["git", *args], repo).stdout


def make_repo(path: Path, slug: str) -> Path:
    path.mkdir(parents=True)
    git(path, "init", "-q", "-b", "main")
    git(path, "config", "user.email", "preflight@example.com")
    git(path, "config", "user.name", "preflight self-test")
    git(path, "config", "commit.gpgsign", "false")
    git(path, "remote", "add", "origin", f"https://github.com/{slug}.git")
    return path


def write(repo: Path, rel: str, text: str) -> None:
    p = repo / rel
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(text, encoding="utf-8")


def commit(repo: Path, msg: str) -> str:
    git(repo, "add", "-A")
    git(repo, "commit", "-q", "--allow-empty", "-m", msg)
    return git(repo, "rev-parse", "HEAD").strip()


def publish_main(repo: Path) -> None:
    git(repo, "update-ref", "refs/remotes/origin/main", "HEAD")


def stub(name: str, marker: str = "") -> str:
    return STUB.format(name=name, marker=marker)


def env_for(tmp: Path, **extra) -> dict:
    bin_dir = tmp / "fakebin"
    if not (bin_dir / "gh").is_file():
        bin_dir.mkdir(exist_ok=True)
        (bin_dir / "gh").write_text(FAKE_GH)
        (bin_dir / "gh").chmod(0o755)
    env = {k: v for k, v in os.environ.items()
           if k not in ("GH_TOKEN", "GITHUB_TOKEN", "MW_CORE_GIT_URL") and not k.lower().endswith("_proxy")}
    env["PATH"] = f"{bin_dir}{os.pathsep}{env.get('PATH', '')}"
    # The safety net: any urllib/HTTP call that slipped past the fakes dies on a dead proxy.
    env["https_proxy"] = env["HTTPS_PROXY"] = "http://127.0.0.1:9"
    env["http_proxy"] = env["HTTP_PROXY"] = "http://127.0.0.1:9"
    env["FAKE_GH_LOG"] = str(tmp / "gh.log")
    env["GIT_CONFIG_NOSYSTEM"] = "1"
    for k, v in extra.items():
        if v is None:
            env.pop(k, None)
        else:
            env[k] = v
    return env


def preflight(repo: Path, tmp: Path, *args: str, script: Path = PREFLIGHT, **env_extra):
    env = env_for(tmp, **env_extra)
    out = tmp / "result.json"
    out.unlink(missing_ok=True)
    p = subprocess.run([sys.executable, str(script), "--repo-root", str(repo), "--json", str(out), *args],
                       cwd=str(repo), capture_output=True, text=True, env=env)
    data = json.loads(out.read_text()) if out.is_file() else {"results": []}
    statuses = {r["name"]: r["status"] for r in data["results"]}
    return p.returncode, statuses, p.stdout + p.stderr, data


def body(tmp: Path, text: str, name: str = "body.md") -> Path:
    p = tmp / name
    p.write_text(text, encoding="utf-8")
    return p


# ── 1. the source-guard root scan ──────────────────────────────────────────────────────────────

GUARD1 = """namespace G;
public class Guard1
{
    private static readonly string[] ScannedRoots = ["src/A", "tools"];
}
"""
GUARD2 = """namespace G;
public class Guard2
{
    public void Scan(string root) => SourceScan.SourceFiles(root, ["src/B"]).ToList();
}
"""
GUARD3 = """namespace G;
public class Guard3
{
    private static readonly ImmutableArray<string> ScannedRoots =
    [
        // a comment with "quotes" is not a root
        "src/C",
    ];
}
"""


def guard_repo(tmp: Path, name: str) -> Path:
    repo = make_repo(tmp / name, "Systemorph/Fixture")
    for rel in ("src/A/a.cs", "src/B/b.cs", "src/C/c.cs", "tools/t.cs", "docs/readme.md"):
        write(repo, rel, "// x\n")
    write(repo, "test/G.Test/Guard1.cs", GUARD1)
    write(repo, "test/G.Test/Guard2.cs", GUARD2)
    write(repo, "test/G.Test/Guard3.cs", GUARD3)
    commit(repo, "base")
    publish_main(repo)
    return repo


def guards_status(repo, tmp, *extra):
    rc, st, out, data = preflight(repo, tmp, "--only", "guards", "--offline", *extra)
    res = [r for r in data["results"] if r["name"] == "Source-guard scanned roots"]
    return rc, (res[0] if res else {"status": "MISSING", "details": [], "summary": ""}), out


def test_guard_roots(tmp: Path) -> None:
    # RED: a deleted directory a guard names, committed.
    repo = guard_repo(tmp, "g1")
    git(repo, "rm", "-q", "src/A/a.cs")
    commit(repo, "drop A")
    rc, r, out = guards_status(repo, tmp)
    findings = [d for d in r["details"] if d.startswith("test/")]
    check("guards: a deleted root is RED", rc == 1 and r["status"] == "FAIL", out)
    check("guards: the finding names the guard file:line and the root",
          len(findings) == 1 and findings[0].startswith("test/G.Test/Guard1.cs:4 ScannedRoots names root `src/A`, now MISSING"),
          "\n".join(r["details"]))

    # RED: the same deletion, UNCOMMITTED (the working tree is what the preflight checks).
    repo = guard_repo(tmp, "g2")
    (repo / "src/A/a.cs").unlink()
    rc, r, out = guards_status(repo, tmp)
    check("guards: an uncommitted deletion is RED too", rc == 1 and r["status"] == "FAIL", out)

    # RED: a RENAME away from an inline SourceFiles root.
    repo = guard_repo(tmp, "g3")
    git(repo, "mv", "src/B", "src/B2")
    commit(repo, "move B")
    rc, r, out = guards_status(repo, tmp)
    findings = [d for d in r["details"] if d.startswith("test/")]
    check("guards: a renamed inline root is RED, naming the move",
          rc == 1 and len(findings) == 1 and "Guard2.cs:4" in findings[0] and "`src/B`" in findings[0]
          and "R src/B/b.cs -> src/B2/b.cs" in findings[0], "\n".join(r["details"]))

    # RED: a multi-line ImmutableArray root, with a comment inside the initializer.
    repo = guard_repo(tmp, "g4")
    git(repo, "rm", "-q", "-r", "src/C")
    commit(repo, "drop C")
    rc, r, out = guards_status(repo, tmp)
    findings = [d for d in r["details"] if d.startswith("test/")]
    pf = load_preflight()
    parsed = [(d.symbol, d.line, d.roots) for d in pf.parse_root_declarations("G3.cs", GUARD3)]
    check("guards: a quoted word in a comment inside the initializer is not a root",
          parsed == [("ScannedRoots", 4, ["src/C"])], str(parsed))
    check("guards: a multi-line root list is parsed (and a quoted comment is not a root)",
          rc == 1 and len(findings) == 1 and "Guard3.cs:4" in findings[0] and "`src/C`" in findings[0],
          "\n".join(r["details"]))

    # RED: deleting a PARENT empties every root under it — one finding per root.
    repo = guard_repo(tmp, "g5")
    git(repo, "rm", "-q", "-r", "src")
    commit(repo, "drop src")
    rc, r, out = guards_status(repo, tmp)
    findings = [d for d in r["details"] if d.startswith("test/")]
    check("guards: deleting a parent reports each root under it", rc == 1 and len(findings) == 3,
          "\n".join(r["details"]))

    # GREEN: a deletion under a root that still holds a file.
    repo = guard_repo(tmp, "g6")
    write(repo, "tools/u.cs", "// still here\n")
    git(repo, "rm", "-q", "tools/t.cs")
    commit(repo, "swap tools file")
    rc, r, out = guards_status(repo, tmp)
    check("guards: a root that still holds a file is GREEN", rc == 0 and r["status"] == "PASS", out)

    # GREEN: the move AND the guard repointed in the same change.
    repo = guard_repo(tmp, "g7")
    git(repo, "mv", "src/B", "src/B2")
    write(repo, "test/G.Test/Guard2.cs", GUARD2.replace('"src/B"', '"src/B2"'))
    commit(repo, "move B and repoint")
    rc, r, out = guards_status(repo, tmp)
    check("guards: a move with its guard repointed is GREEN", rc == 0 and r["status"] == "PASS", out)

    # GREEN: an unrelated deletion.
    repo = guard_repo(tmp, "g8")
    git(repo, "rm", "-q", "docs/readme.md")
    commit(repo, "drop docs")
    rc, r, out = guards_status(repo, tmp)
    check("guards: an unrelated deletion is GREEN", rc == 0 and r["status"] == "PASS", out)

    # NEUTERED DETECTOR: with no declaration recognised, the RED case above turns GREEN — so the red
    # cases depend on the detector, and a detector that stopped detecting would be caught here.
    pf = load_preflight()
    repo = guard_repo(tmp, "g9")
    git(repo, "rm", "-q", "src/A/a.cs")
    commit(repo, "drop A")
    ctx = pf.Context(root=repo, slug="Systemorph/Fixture", base_ref="origin/main",
                     merge_base=git(repo, "merge-base", "origin/main", "HEAD").strip(), body_file=None,
                     offline=True, scratch=tmp, core_scripts=None, core_scripts_why="", token=None, token_why="")
    real = pf.guard_roots(ctx)[0].status
    saved = pf.ROOTS_DECL, pf.INLINE_ROOTS
    pf.ROOTS_DECL = pf.INLINE_ROOTS = pf.re.compile(r"(?!x)x")
    try:
        neutered = pf.guard_roots(ctx)[0].status
    finally:
        pf.ROOTS_DECL, pf.INLINE_ROOTS = saved
    check("guards: the RED case is RED only because the detector fires (neutered → PASS)",
          real == "FAIL" and neutered == "PASS", f"real={real} neutered={neutered}")


# ── 2. the body gates in a core-shaped repo (stubs + the REAL closing-keyword gate) ─────────────

BODY_GATES = {
    "Pairs-with (cross-repo pair)": "PAIR-BAD",
    "Satellite-pins (package pin removal)": "PIN-BAD",
    "Implementers (interface additions)": "IFACE-BAD",
    "Mirror-sync (i18n catalog)": "I18N-BAD",
    "Closing keywords / Verified-closing": "Does not close #7",
}


def core_repo(tmp: Path) -> tuple[Path, str]:
    repo = make_repo(tmp / "core", "Systemorph/MeshWeaver")
    write(repo, "scripts/check-type-forwards.py", stub("type-forwards"))
    write(repo, "scripts/check-cross-repo-pair.py", stub("cross-repo-pair", "PAIR-BAD") + PAIR_STUB_MODULE)
    write(repo, "scripts/check-package-pin-removal.py", stub("package-pin-removal", "PIN-BAD"))
    write(repo, "scripts/check-interface-addition.py", stub("interface-addition", "IFACE-BAD"))
    write(repo, "scripts/check-i18n-mirror-sync.py", stub("i18n-mirror-sync", "I18N-BAD"))
    shutil.copy(REAL_CLOSING, repo / "scripts/check-closing-keywords.py")
    write(repo, "src/x.cs", "// x\n")
    write(repo, ".gitignore", "calls.log\n")
    merge_base = commit(repo, "base")
    # main moves on AFTER the branch point, so the merge base and origin/main differ: the gates must
    # be handed the merge base, never the tip.
    git(repo, "checkout", "-q", "-b", "feature")
    write(repo, "src/y.cs", "// feature\n")
    commit(repo, "feature")
    git(repo, "checkout", "-q", "main")
    write(repo, "src/z.cs", "// main moved\n")
    commit(repo, "main moved")
    publish_main(repo)
    git(repo, "checkout", "-q", "feature")
    return repo, merge_base


def calls(repo: Path) -> list[dict]:
    log = repo / "calls.log"
    return [json.loads(ln) for ln in log.read_text().splitlines()] if log.is_file() else []


def test_core_body_gates(tmp: Path) -> None:
    repo, mb = core_repo(tmp)
    green = "A change.\n\nRefs #7\n"

    # GREEN, online (fake gh supplies the token and the labels).
    (repo / "calls.log").unlink(missing_ok=True)
    b = body(tmp, green)
    rc, st, out, _ = preflight(repo, tmp, "--body-file", str(b), "--only", "body")
    check("core: a clean body is GREEN online", rc == 0 and all(st.get(n) == "PASS" for n in BODY_GATES), out)
    by_gate = {c["gate"]: c["argv"] for c in calls(repo)}
    for gate in ("package-pin-removal", "i18n-mirror-sync"):
        argv = by_gate.get(gate, [])
        check(f"core: {gate} is handed the MERGE BASE and the body file",
              argv[:2] == ["--base", mb] and argv[2:] == ["--pr-body-file", str(b.resolve())], str(argv))
    check("core: the surface detector runs against the merge base",
          by_gate.get("type-forwards", [])[:2] == ["--base", mb], str(by_gate.get("type-forwards")))
    pair = by_gate.get("cross-repo-pair", [])
    check("core: the pair gate reads the surface report, the body and the merge base",
          "--surface-json" in pair and pair[pair.index("--base") + 1] == mb
          and pair[pair.index("--pr-body-file") + 1] == str(b.resolve()), str(pair))
    check("core: the implementer gate reads the same surface report",
          "--surface-json" in by_gate.get("interface-addition", []), str(by_gate.get("interface-addition")))

    # RED, one gate at a time — and ONLY that gate.
    for gate, marker in BODY_GATES.items():
        b = body(tmp, f"A change.\n\n{marker}\n")
        rc, st, out, _ = preflight(repo, tmp, "--body-file", str(b), "--only", "body")
        others = [n for n in BODY_GATES if n != gate and st.get(n) != "PASS"]
        check(f"core: `{marker}` reds {gate} and nothing else",
              rc == 1 and st.get(gate) == "FAIL" and not others, f"{st}\n{out}")

    # RED online through the REAL closing-keyword gate + the fake gh: a sev:H issue closed by a merge.
    b = body(tmp, "Closes #7\n")
    rc, st, out, _ = preflight(repo, tmp, "--body-file", str(b), "--only", "body", FAKE_GH_LABELS="bug,sev:H")
    check("core: closing a sev:H issue is RED online (labels read through gh)",
          rc == 1 and st["Closing keywords / Verified-closing"] == "FAIL" and "sev:H" in out, out)
    rc, st, out, _ = preflight(repo, tmp, "--body-file", str(b), "--only", "body", FAKE_GH_LABELS="bug,sev:M")
    check("core: closing a sev:M issue is GREEN online", rc == 0, out)
    b = body(tmp, "Closes #7\n\nVerified-closing: #7 — the fix was read back on the staging portal after the roll\n")
    rc, st, out, _ = preflight(repo, tmp, "--body-file", str(b), "--only", "body", FAKE_GH_LABELS="sev:H")
    check("core: a declared Verified-closing releases the sev:H close", rc == 0, out)

    # OFFLINE: the static half still reds; what needs GitHub is SKIPPED — never a pass.
    b = body(tmp, "Closes #7\n")
    gh_log = tmp / "gh.log"
    gh_log.unlink(missing_ok=True)
    rc, st, out, _ = preflight(repo, tmp, "--body-file", str(b), "--only", "body", "--offline")
    check("core offline: a label it could not read is SKIPPED and the exit is 2",
          rc == 2 and st["Closing keywords / Verified-closing"] == "SKIPPED" and "INCOMPLETE" in out, out)
    check("core offline: gh was never called", not gh_log.is_file() or not gh_log.read_text().strip(),
          gh_log.read_text() if gh_log.is_file() else "")
    b = body(tmp, "**Does not close #7**\n")
    rc, st, out, _ = preflight(repo, tmp, "--body-file", str(b), "--only", "body", "--offline")
    check("core offline: a negated closing keyword is RED with no network",
          rc == 1 and st["Closing keywords / Verified-closing"] == "FAIL" and "NEGATED" in out, out)
    b = body(tmp, "Pairs-with: Systemorph/MeshWeaver.Plugins#1\n")
    rc, st, out, _ = preflight(repo, tmp, "--body-file", str(b), "--only", "body", "--offline")
    check("core offline: a counterpart it could not resolve is SKIPPED, naming it",
          rc == 2 and st["Pairs-with (cross-repo pair)"] == "SKIPPED" and "MeshWeaver.Plugins#1" in out, out)
    b = body(tmp, "PAIR-BAD\n")
    rc, st, out, _ = preflight(repo, tmp, "--body-file", str(b), "--only", "body", "--offline")
    check("core offline: a static pair finding is RED", rc == 1 and st["Pairs-with (cross-repo pair)"] == "FAIL", out)

    # NO TOKEN, online: the network halves degrade to SKIPPED, saying why.
    b = body(tmp, "Pairs-with: Systemorph/MeshWeaver.Plugins#1\n")
    rc, st, out, _ = preflight(repo, tmp, "--body-file", str(b), "--only", "body", FAKE_GH_NO_AUTH="1")
    check("core: no token degrades the pair gate to SKIPPED, saying so",
          rc == 2 and st["Pairs-with (cross-repo pair)"] == "SKIPPED" and "no token" in out, out)

    # NO BODY: every body gate SKIPPED, exit 2.
    rc, st, out, _ = preflight(repo, tmp, "--only", "body", "--offline")
    check("core: no --body-file skips every body gate (exit 2, never 0)",
          rc == 2 and all(st.get(n) == "SKIPPED" for n in BODY_GATES), out)


# ── 3. the pair gate's offline adapter against the REAL check-cross-repo-pair.py ───────────────


def test_real_pair_adapter(tmp: Path) -> None:
    if not REAL_PAIR.is_file():
        check("real pair gate present beside the preflight", False, str(REAL_PAIR))
        return
    pf = load_preflight()
    root = HERE.parent
    ctx = pf.Context(root=root, slug="Systemorph/MeshWeaver", base_ref="HEAD", merge_base="HEAD",
                     body_file=None, offline=True, scratch=tmp, core_scripts=None, core_scripts_why="",
                     token=None, token_why="")
    removed = [{"key": "MeshWeaver.X:MeshWeaver.X.Gone", "assembly": "MeshWeaver.X",
                "fullName": "MeshWeaver.X.Gone", "category": "departed", "landedIn": []}]

    def verdict(rem, text):
        s = tmp / "surface-real.json"
        s.write_text(json.dumps({"publicTypesAtBase": 2000, "removed": rem}))
        return pf.offline_pair(ctx, s, text)

    r = verdict([], "nothing declared\n")
    check("real pair: nothing removed is GREEN offline", r.status == "PASS", f"{r}")
    r = verdict(removed, "nothing declared\n")
    check("real pair: an undeclared removal is RED offline", r.status == "FAIL", f"{r}")
    r = verdict(removed, "Pairs-with: Systemorph/MeshWeaver.Plugins#904\n")
    check("real pair: a declared counterpart is SKIPPED offline (state unread), naming it",
          r.status == "SKIPPED" and "Systemorph/MeshWeaver.Plugins#904" in r.summary, f"{r}")
    r = verdict(removed, "Pairs-with: none — nothing outside core referenced MeshWeaver.X.Gone\n")
    check("real pair: `Pairs-with: none — <reason>` is GREEN offline", r.status == "PASS", f"{r}")
    r = verdict(removed, "Pairs-with: Systemorph/NotInTheFleet#1\n")
    check("real pair: a counterpart outside the fleet is RED offline (static)", r.status == "FAIL", f"{r}")


# ── 4. a satellite (MeshWeaver.Plugins-shaped) checkout ─────────────────────────────────────────


def plugins_repo(tmp: Path) -> Path:
    repo = make_repo(tmp / "plugins", "Systemorph/MeshWeaver.Plugins")
    write(repo, "scripts/check-db-migration-declaration.py", stub("db-migration", "DB-BAD"))
    write(repo, "scripts/platform-mount.py", stub("platform-mount"))
    write(repo, "src/p.cs", "// p\n")
    write(repo, ".gitignore", "calls.log\n")
    commit(repo, "base")
    publish_main(repo)
    return repo


def core_scripts_dir(tmp: Path) -> Path:
    core = tmp / "core-for-satellite"
    (core / "scripts").mkdir(parents=True, exist_ok=True)
    shutil.copy(REAL_CLOSING, core / "scripts/check-closing-keywords.py")
    return core


def test_satellite(tmp: Path) -> None:
    repo = plugins_repo(tmp)
    core = core_scripts_dir(tmp)
    sat = ["--core-root", str(core), "--offline"]
    rc, st, out, _ = preflight(repo, tmp, "--body-file", str(body(tmp, "DB-BAD\n")), *sat)
    check("plugins: `DB-BAD` reds the Db-migration gate and nothing else",
          rc == 1 and st.get("Db-migration (database version)") == "FAIL"
          and [n for n, s in st.items() if s == "FAIL"] == ["Db-migration (database version)"], f"{st}\n{out}")
    check("plugins: core-only gates do not run in a satellite",
          not any(n in st for n in ("Pairs-with (cross-repo pair)", "Mirror-sync (i18n catalog)")), str(st))
    rc, st, out, _ = preflight(repo, tmp, "--body-file", str(body(tmp, "A change.\n")), *sat)
    check("plugins: a clean body with the audit unrun is INCOMPLETE (exit 2), never green",
          rc == 2 and st.get("Platform mount — self-test") == "PASS"
          and st.get("Platform mount — audit (--audit-core)") == "SKIPPED", f"{st}\n{out}")
    rc, st, out, _ = preflight(repo, tmp, "--body-file", str(body(tmp, "A change.\n")), *sat,
                               "--platform-core", str(core))
    check("plugins: with --platform-core the audit runs and a clean change is GREEN", rc == 0, f"{st}\n{out}")
    rc, st, out, _ = preflight(repo, tmp, "--body-file", str(body(tmp, "A change.\n")), *sat,
                               "--platform-core", str(core), STUB_AUDIT_FAIL="1")
    check("plugins: a failing audit is RED", rc == 1 and st.get("Platform mount — audit (--audit-core)") == "FAIL",
          f"{st}\n{out}")
    rc, st, out, _ = preflight(repo, tmp, "--body-file", str(body(tmp, "A change.\n")), *sat, STUB_PM_FAIL="1")
    check("plugins: a failing platform-mount self-test is RED",
          rc == 1 and st.get("Platform mount — self-test") == "FAIL", f"{st}\n{out}")

    name = "Closing keywords / Verified-closing (core issues)"
    rc, st, out, _ = preflight(repo, tmp, "--body-file", str(body(tmp, "Closes #7\n")), *sat, "--only", "body")
    check("plugins: a satellite-local closing keyword is not the core gate's business (GREEN)",
          st.get(name) == "PASS", f"{st}\n{out}")
    rc, st, out, _ = preflight(repo, tmp, "--body-file", str(body(tmp, "Fixes Systemorph/MeshWeaver#7\n")), *sat,
                               "--only", "body")
    check("plugins offline: closing a CORE issue is SKIPPED (labels unread)", st.get(name) == "SKIPPED", f"{st}\n{out}")
    rc, st, out, _ = preflight(repo, tmp, "--body-file",
                               str(body(tmp, "This does not fix Systemorph/MeshWeaver#7\n")), *sat, "--only", "body")
    check("plugins offline: a negated close of a CORE issue is RED", st.get(name) == "FAIL", f"{st}\n{out}")

    # A satellite that fetched preflight-pr.py ALONE: the core gates come over anonymous git at
    # --core-ref (MW_CORE_GIT_URL points at a fixture core, as mw-core-file.sh honours it).
    bare_src = make_repo(tmp / "core-origin-src", "Systemorph/MeshWeaver")
    (bare_src / "scripts").mkdir(parents=True)
    shutil.copy(REAL_CLOSING, bare_src / "scripts/check-closing-keywords.py")
    commit(bare_src, "core")
    bare = tmp / "core-origin.git"
    run(["git", "clone", "-q", "--bare", str(bare_src), str(bare)], tmp)
    lone = tmp / "lone"
    lone.mkdir()
    shutil.copy(PREFLIGHT, lone / "preflight-pr.py")
    rc, st, out, _ = preflight(repo, tmp, "--body-file", str(body(tmp, "A change.\n")), "--only", "body",
                               script=lone / "preflight-pr.py", MW_CORE_GIT_URL=bare.as_uri())
    check("plugins: a lone preflight fetches the core gate at --core-ref over git",
          st.get(name) == "PASS" and "fetched from core at 'main'" in out, f"{st}\n{out}")
    rc, st, out, _ = preflight(repo, tmp, "--body-file", str(body(tmp, "A change.\n")), "--only", "body",
                               "--offline", script=lone / "preflight-pr.py")
    check("plugins: a lone preflight offline cannot fetch, and says so (SKIPPED)",
          rc == 2 and st.get(name) == "SKIPPED" and "--core-root" in out, f"{st}\n{out}")


# ── 5. the verdict mapping itself ───────────────────────────────────────────────────────────────


def test_verdict() -> None:
    pf = load_preflight()
    R = pf.Result
    check("verdict: all PASS/N/A is 0", pf.verdict([R("a", pf.PASS, ""), R("b", pf.NA, "")])[0] == 0)
    check("verdict: a SKIPPED is 2, never 0", pf.verdict([R("a", pf.PASS, ""), R("b", pf.SKIPPED, "")])[0] == 2)
    check("verdict: a FAIL is 1 even beside a SKIPPED",
          pf.verdict([R("a", pf.FAIL, ""), R("b", pf.SKIPPED, "")])[0] == 1)
    check("verdict: exit 3 (NO VERDICT) maps to SKIPPED, not PASS", pf.from_exit("x", 3, "", "").status == pf.SKIPPED)
    check("verdict: any other non-zero exit maps to FAIL", pf.from_exit("x", 2, "", "").status == pf.FAIL)
    check("slug: an ssh remote parses", pf.slug_from_remote("git@github.com:Systemorph/MeshWeaver.Plugins.git")
          == "Systemorph/MeshWeaver.Plugins")
    check("slug: an https remote parses", pf.slug_from_remote("https://github.com/Systemorph/MeshWeaver")
          == "Systemorph/MeshWeaver")


def main() -> int:
    if not PREFLIGHT.is_file() or not REAL_CLOSING.is_file():
        print(f"::error::test-preflight-pr.py needs {PREFLIGHT.name} and check-closing-keywords.py beside it")
        return 1
    with tempfile.TemporaryDirectory(prefix="test-preflight-pr-") as t:
        tmp = Path(t)
        for sub in ("guards", "corecase", "realpair", "satellite"):
            (tmp / sub).mkdir()
        test_verdict()
        test_guard_roots(tmp / "guards")
        test_core_body_gates(tmp / "corecase")
        test_real_pair_adapter(tmp / "realpair")
        test_satellite(tmp / "satellite")
    if failures:
        print(f"test-preflight-pr: {len(failures)} FAILED, {passed} passed:")
        for f in failures:
            print(f"  ✗ {f}")
        return 1
    print(f"✓ test-preflight-pr: {passed} checks passed — red for each gate's own reason only, green when clean, "
          "SKIPPED (exit 2, never 0) for whatever needs GitHub offline or without a token, the guard-root scan "
          "red on a deleted/renamed/emptied root and green once repointed, and a neutered detector turns its red green.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
