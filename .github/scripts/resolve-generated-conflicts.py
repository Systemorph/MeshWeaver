#!/usr/bin/env python3
"""resolve-generated-conflicts.py — land the merge a pull request is DIRTY on, when the ONLY
conflicts are generated `*/manifest.lock` files.

    python3 scripts/resolve-generated-conflicts.py --repo Systemorph/MeshWeaver.Plugins
    python3 scripts/resolve-generated-conflicts.py --repo … --pr 1575        # one PR
    python3 scripts/resolve-generated-conflicts.py --repo … --dry-run        # classify, touch nothing
    python3 $RUNNER_TEMP/resolve-generated-conflicts.py --repo … --root "$GITHUB_WORKSPACE"
    python3 scripts/resolve-generated-conflicts.py --self-test

The repository it acts on is `--root` (default: the working directory) — NOT wherever this file
sits. The shared lane runs a copy fetched into `$RUNNER_TEMP`; see `resolve_root`.

Run by `.github/workflows/resolve-generated-conflicts.yml` on every push to `main` (a merge that
just landed is what makes other pull requests conflict) and on a schedule as the net.

## Why this exists — measured, not assumed (Hosting/PullRequestDrain.md)

Over the 50 pull requests merged or open on 2026-09-09 … 09-11, GitHub's own (driver-free) merge
reported a pull request CONFLICTING 40 times. **37 of the 40 conflicted on nothing but generated
`manifest.lock` files.** The local `mw-manifest-lock` merge driver (`scripts/setup-git-merge.py`)
cannot help there: GitHub computes mergeability on its servers, where no custom merge driver
exists, so the driver only ever shortens the AUTHOR's half — after the author has noticed. Until
then the PR sits DIRTY: no check suite, a stale wall of green, auto-merge dropped. Summed over the
37, the conflicted windows (a union per PR) last 116 PR-hours, and 83 of those are windows longer
than eight hours — pull requests whose author (an agent session, most of the time) had moved on.

Every lock conflicts because its trailer — `moduleVersion`, `sourceCommit`, `version` — changes on
EVERY content change to the module, so two pull requests touching one module (or one `src/`
project that rides into ten bundles) always collide there. The content has no meaningful three-way
merge; the only correct answer is a regeneration over the merged tree, which
`gen-manifests.py --resolve` has always done by hand. This script does it for the author.

## What it will and will not do

  * It only acts on a pull request whose conflict set, as GitHub sees it, is NON-EMPTY and ENTIRELY
    `*/manifest.lock`. One source file in the set and it does nothing — a real conflict is the
    author's to resolve, and `--resolve` would refuse it anyway.
  * GitHub's view is reproduced exactly: the merge runs with `attr.tree=<empty tree>`, so a clone
    that has the local driver armed still sees the text conflict GitHub sees (the self-test's
    control proves the driver WOULD otherwise hide it).
  * Consent is auto-arm's: every non-draft pull request from a branch in this repository. Draft is
    the opt-out. Dependabot's branches are left to Dependabot (a foreign commit stops it rebasing).
  * It pushes a plain fast-forward — never a force. If the author pushed in the meantime, the push
    is rejected and the next run re-reads the branch.
  * Before pushing, the regenerated tree must pass `gen-manifests.py --check` with THIS repo's copy
    AND with the platform's canonical copy (`--platform-checker`), which is the one the
    `Validate node repos` gate believes. A resolution the gate would call stale is never pushed.
  * The outcome lands where the author looks: ONE comment on the pull request, edited in place.

## The one thing it cannot do today, and says so

GitHub refuses a GitHub App push that brings `.github/workflows/**` changes onto a branch unless
the App holds `Workflows: write`. The `meshweaver-cloud` installation holds contents +
pull_requests only (measured: `gh api orgs/Systemorph/installations`). So when `main` changed a
workflow since the pull request's base — 13 of the 37 measured lock-only conflicts — the push is
refused by name. That refusal is reported as `needs-workflows-grant` in the run summary AND in the
pull request's comment, with the three commands that resolve it locally. Granting the permission
on the App (a maintainer action) makes those cases resolve like the rest, with no change here.

Exit codes: 0 every pull request reached a legitimate outcome · 1 the lane itself hit a defect
(listing failed, a merge did not reproduce the conflict it classified, a resolution failed its own
check, an unexplained push refusal) · 2 usage.
Stdlib + git + the `gh` CLI only.
"""
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

EMPTY_TREE = "4b825dc642cb6eb9a060e54bf8d69288fbee4904"
# GitHub's view of a merge: no custom merge driver exists on its servers. Reading attributes from
# the empty tree drops `merge=mw-manifest-lock`, so git falls back to the text merge GitHub runs.
GITHUB_VIEW = (f"attr.tree={EMPTY_TREE}",)
# Resolution must not run the local post-merge hook (it would commit a second regeneration).
NO_HOOKS = ("core.hooksPath=/dev/null",)
MARKER = "<!-- mw:resolve-generated-conflicts -->"
GENERATED_SUFFIX = "/manifest.lock"

WORKFLOWS_REFUSAL = re.compile(r"without [`']?workflows[`']? permission", re.I)
RACED_REFUSAL = re.compile(r"\(fetch first\)|non-fast-forward|\[rejected\]|stale info|cannot lock ref", re.I)

# Outcomes. Only DEFECTS fail the run: every other outcome is a legitimate state of a pull request.
DEFECTS = {"defect", "push-failed"}


class LaneDefect(Exception):
    """Something the lane itself got wrong — never a property of the pull request."""


def git(root: Path, args: list[str], cfg: tuple[str, ...] = (), env: dict | None = None,
        timeout: int = 900) -> tuple[int, str, str]:
    cmd = ["git"] + [x for kv in cfg for x in ("-c", kv)] + ["-C", str(root), *args]
    p = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout,
                       env={**os.environ, **(env or {})})
    return p.returncode, p.stdout.strip(), p.stderr.strip()


def must(root: Path, args: list[str], **kw) -> str:
    rc, out, err = git(root, args, **kw)
    if rc != 0:
        raise LaneDefect(f"git {' '.join(args)} failed ({rc}): {err or out}")
    return out


def resolve_root(explicit: str | None) -> Path:
    """The repository this lane ACTS ON — never the one this file happens to be stored in.

    `Path(__file__).parent.parent` was the root until 2026-09-13, which held only while the script
    lived at `<repo>/scripts/` and was run from its own checkout — the shape MeshWeaver.Plugins
    still uses. The shared lane (`.github/workflows/node-repo-resolve-locks.yml`) deliberately
    fetches this file into `$RUNNER_TEMP` and runs it against the CALLER's checkout, so that
    expression resolved to `/home/runner/work` and every git command died with
    `fatal: not a git repository`. The lane was therefore red on 5 of 5 runs in each of Education,
    Reinsurance, SocialMedia, Manufacturing and Crm from the hour it was adopted, having resolved
    nothing; Plugins stayed green only because its own copy still runs in-tree.

    So the root is taken from `--root`, else the working directory, and is PROVEN to be a git work
    tree here — a misconfiguration is named where it can be acted on, instead of surfacing from
    inside the first fetch as a git error about a directory nobody passed.
    """
    start = Path(explicit).resolve() if explicit else Path.cwd()
    rc, out, err = git(start, ["rev-parse", "--show-toplevel"])
    if rc != 0:
        raise LaneDefect(
            f"{start} is not inside a git work tree ({err or out or 'no output'}). This script acts "
            f"on the repository it is POINTED AT, not on the one it is stored in: pass "
            f"--root <checkout>, or run it with the checkout as the working directory.")
    return Path(out).resolve()


def is_generated(path: str) -> bool:
    return path.endswith(GENERATED_SUFFIX)


def merge_conflicts(root: Path, base: str, head: str, cfg: tuple[str, ...] = GITHUB_VIEW) -> list[str]:
    """The paths GitHub reports as conflicting when merging `head` into `base`. `[]` = clean."""
    rc, out, err = git(root, ["merge-tree", "--write-tree", "--name-only", "--no-messages", base, head],
                       cfg=cfg)
    if rc == 0:
        return []
    if rc == 1:
        lines = [line for line in out.splitlines() if line.strip()]
        return sorted(set(lines[1:]))  # line 1 is the (conflicted) tree id
    raise LaneDefect(f"git merge-tree {base[:9]} {head[:9]} failed ({rc}): {err or out}")


def classify(conflicts: list[str]) -> str:
    if not conflicts:
        return "clean"
    return "generated-only" if all(is_generated(p) for p in conflicts) else "source-conflict"


def classify_push_error(stderr: str) -> str:
    if WORKFLOWS_REFUSAL.search(stderr):
        return "needs-workflows-grant"
    if RACED_REFUSAL.search(stderr):
        return "raced"
    return "push-failed"


def run_checker(cmd: list[str], cwd: Path, what: str) -> None:
    p = subprocess.run(cmd, cwd=cwd, capture_output=True, text=True, timeout=900,
                       env={**os.environ, "MW_REPO_ROOT": str(cwd)})
    if p.returncode != 0:
        tail = "\n".join((p.stdout + p.stderr).strip().splitlines()[-15:])
        raise LaneDefect(f"{what} exited {p.returncode}:\n{tail}")


def resolve_in_worktree(root: Path, head: str, base: str, ref: str,
                        platform_checker: str | None) -> tuple[str, list[str]]:
    """Merge `base` into `head` in a throwaway worktree, regenerate the locks, commit, verify.

    Returns `(merge commit sha, the locks that conflicted)`. Raises LaneDefect — never pushes a
    half-answer — if the merge does not reproduce a generated-only conflict, if `--resolve`
    refuses, or if either checker calls the result stale.
    """
    tmp = Path(tempfile.mkdtemp(prefix="mw-resolve-"))
    wt = tmp / "wt"
    must(root, ["worktree", "add", "--detach", str(wt), head])
    try:
        rc, out, err = git(wt, ["merge", "--no-ff", "--no-edit", base], cfg=GITHUB_VIEW + NO_HOOKS)
        if rc == 0:
            raise LaneDefect("the merge completed without a conflict, but it was classified as "
                             "conflicting — the classification and the merge disagree")
        unmerged = sorted(set(must(wt, ["diff", "--name-only", "--diff-filter=U"]).split()))
        if not unmerged or not all(is_generated(p) for p in unmerged):
            raise LaneDefect(f"refusing: the merge left non-generated paths unmerged: "
                             f"{[p for p in unmerged if not is_generated(p)] or unmerged}")
        # The repo's own, self-tested manual path — it refuses on any unmerged source path and
        # stages every lock the regeneration moved, not just the conflicted ones (#1023).
        run_checker([sys.executable, "scripts/gen-manifests.py", "--resolve"], wt,
                    "gen-manifests.py --resolve")
        still = must(wt, ["diff", "--name-only", "--diff-filter=U"])
        if still:
            raise LaneDefect(f"paths still unmerged after --resolve: {still.split()}")
        msg = (f"Merge main into {ref} — only generated manifest.lock files conflicted\n\n"
               f"Regenerated over the merged tree by `gen-manifests.py --resolve`:\n"
               + "".join(f"  {p}\n" for p in unmerged)
               + "\nNo source file conflicted. Done by scripts/resolve-generated-conflicts.py "
                 "(.github/workflows/resolve-generated-conflicts.yml).\n")
        msg_file = tmp / "MSG"
        msg_file.write_text(msg)
        must(wt, ["commit", "-F", str(msg_file)], cfg=NO_HOOKS)
        run_checker([sys.executable, "scripts/gen-manifests.py", "--check"], wt,
                    "this repo's gen-manifests.py --check")
        if platform_checker:
            run_checker([sys.executable, platform_checker, "--check"], wt,
                        "the platform's gen-manifests.py --check (the gate's verdict)")
        if (dirty := must(wt, ["status", "--porcelain"])):
            raise LaneDefect(f"the resolution left the tree dirty: {dirty.splitlines()[:5]}")
        parents = must(wt, ["rev-list", "--parents", "-n", "1", "HEAD"]).split()
        if parents[1:] != [head, base]:
            raise LaneDefect(f"the merge commit's parents are {parents[1:]}, expected [{head}, {base}]")
        return parents[0], unmerged
    finally:
        git(root, ["worktree", "remove", "--force", str(wt)])
        shutil.rmtree(tmp, ignore_errors=True)


# ─────────────────────────────── GitHub side ───────────────────────────────

def gh(args: list[str], token_env: str | None = None, input_text: str | None = None):
    env = dict(os.environ)
    if token_env and os.environ.get(token_env):
        env["GH_TOKEN"] = os.environ[token_env]
    p = subprocess.run(["gh", *args], capture_output=True, text=True, env=env, input=input_text,
                       timeout=120)
    if p.returncode != 0:
        raise LaneDefect(f"gh {' '.join(args[:3])} failed: {p.stderr.strip()[:300]}")
    return json.loads(p.stdout) if p.stdout.strip() else None


def open_pulls(repo: str) -> list[dict]:
    pulls: list[dict] = []
    for page in range(1, 11):
        batch = gh(["api", f"repos/{repo}/pulls?state=open&per_page=100&page={page}"]) or []
        pulls += batch
        if len(batch) < 100:
            return pulls
    raise LaneDefect("more than 1000 open pull requests — refusing to act on a partial listing")


def skip_reason(pr: dict, repo: str, base: str) -> str | None:
    if pr.get("draft"):
        return "draft (the opt-out, as for auto-arm)"
    if (pr.get("head", {}).get("repo") or {}).get("full_name") != repo:
        return "fork — no push access to its branch"
    if pr.get("base", {}).get("ref") != base:
        return f"targets {pr.get('base', {}).get('ref')}, not {base}"
    if (pr.get("user") or {}).get("login") == "dependabot[bot]":
        return "Dependabot's branch — Dependabot rebases its own"
    return None


def comment_body(outcome: str, pr_ref: str, locks: list[str], sha: str | None, base: str) -> str:
    listed = "".join(f"- `{p}`\n" for p in locks)
    if outcome == "resolved":
        return (f"{MARKER}\n🔁 **`main` merged in — the only conflicts were generated "
                f"`manifest.lock` files.**\n\n`{sha[:9]}` merges `main@{base[:9]}` and regenerates "
                f"them over the merged tree with `gen-manifests.py --resolve`:\n\n{listed}\n"
                f"No source file conflicted; both `gen-manifests.py --check` copies passed before "
                f"the push. CI re-runs on the new head and auto-merge re-arms.\n\n"
                f"Your local `{pr_ref}` is now one merge behind — `git pull` before your next push.\n")
    return (f"{MARKER}\n⏸️ **This pull request conflicts with `main` only in generated "
            f"`manifest.lock` files**, and the resolver could not push the fix:\n\n{listed}\n"
            f"`main` changed `.github/workflows/**` since this branch's base, and GitHub refuses a "
            f"GitHub App push that brings workflow changes without `Workflows: write` — which the "
            f"`meshweaver-cloud` App does not hold. Resolve it locally:\n\n"
            f"```bash\ngit fetch origin && git merge origin/main   # stops only on the locks\n"
            f"python3 scripts/gen-manifests.py --resolve   # regenerate + stage them\n"
            f"git commit --no-edit && git push\n```\n")


def upsert_comment(repo: str, number: int, body: str) -> None:
    comments = []
    for page in range(1, 6):
        batch = gh(["api", f"repos/{repo}/issues/{number}/comments?per_page=100&page={page}"],
                   token_env="GH_COMMENT_TOKEN") or []
        comments += batch
        if len(batch) < 100:
            break
    mine = [c for c in comments if MARKER in (c.get("body") or "")]
    if mine:
        gh(["api", "-X", "PATCH", f"repos/{repo}/issues/comments/{mine[-1]['id']}", "-f", f"body={body}"],
           token_env="GH_COMMENT_TOKEN")
    else:
        gh(["api", "-X", "POST", f"repos/{repo}/issues/{number}/comments", "-f", f"body={body}"],
           token_env="GH_COMMENT_TOKEN")


def run(root: Path, repo: str, base_ref: str, only: int | None, dry_run: bool,
        platform_checker: str | None) -> int:
    must(root, ["fetch", "--no-tags", "--quiet", "origin",
                f"+refs/heads/{base_ref}:refs/remotes/origin/{base_ref}"])
    base = must(root, ["rev-parse", f"refs/remotes/origin/{base_ref}"])
    rows: list[tuple[int, str, str, str]] = []
    try:
        pulls = open_pulls(repo)
    except LaneDefect as e:
        print(f"::error::cannot list open pull requests: {e}")
        return 1
    pulls = [p for p in pulls if only is None or p["number"] == only]
    if only is not None and not pulls:
        print(f"::error::pull request #{only} is not open in {repo}")
        return 1
    print(f"{repo}: {len(pulls)} open pull request(s); main = {base[:9]}")
    for pr in sorted(pulls, key=lambda p: p["number"]):
        n, ref, head = pr["number"], pr["head"]["ref"], pr["head"]["sha"]
        if (why := skip_reason(pr, repo, base_ref)):
            rows.append((n, ref, "skipped", why))
            continue
        try:
            local = f"refs/mw-resolve/pr/{n}"
            must(root, ["fetch", "--no-tags", "--quiet", "origin", f"+refs/pull/{n}/head:{local}"])
            fetched = must(root, ["rev-parse", local])
            if fetched != head:
                rows.append((n, ref, "moved", f"head moved while reading ({head[:9]} → {fetched[:9]}); next run"))
                continue
            conflicts = merge_conflicts(root, base, head)
            kind = classify(conflicts)
            if kind == "clean":
                rows.append((n, ref, "clean", "merges into main without a conflict"))
                continue
            if kind == "source-conflict":
                src = [p for p in conflicts if not is_generated(p)]
                rows.append((n, ref, "source-conflict", f"left for its author: {', '.join(src[:6])}"))
                continue
            if dry_run:
                rows.append((n, ref, "would-resolve", ", ".join(conflicts)))
                continue
            sha, locks = resolve_in_worktree(root, head, base, ref, platform_checker)
            rc, out, err = git(root, ["push", "origin", f"{sha}:refs/heads/{ref}"])
            if rc == 0:
                rows.append((n, ref, "resolved", f"{sha[:9]} ({len(locks)} lock(s))"))
                upsert_comment(repo, n, comment_body("resolved", ref, locks, sha, base))
                continue
            outcome = classify_push_error(err)
            rows.append((n, ref, outcome, err.splitlines()[-1][:160] if err else "no stderr"))
            if outcome == "needs-workflows-grant":
                print(f"::warning::#{n}: lock-only conflict, but the App cannot push workflow changes "
                      f"(needs Workflows: write on meshweaver-cloud) — commented on the PR")
                upsert_comment(repo, n, comment_body(outcome, ref, locks, None, base))
        except (LaneDefect, subprocess.TimeoutExpired) as e:
            rows.append((n, ref, "defect", str(e).splitlines()[0][:200]))
            print(f"::error::#{n}: {e}")

    width = max([len(r[1]) for r in rows] + [6])
    print(f"\n{'PR':>6}  {'branch':<{width}}  outcome               detail")
    for n, ref, outcome, detail in rows:
        print(f"#{n:<5}  {ref:<{width}}  {outcome:<20}  {detail}")
    if (summary := os.environ.get("GITHUB_STEP_SUMMARY")):
        with open(summary, "a", encoding="utf-8") as f:
            f.write(f"### Generated-lock conflicts vs `main@{base[:9]}`\n\n| PR | outcome | detail |\n|---|---|---|\n")
            for n, ref, outcome, detail in rows:
                f.write(f"| #{n} `{ref}` | {outcome} | {detail.replace('|', '/')} |\n")
    bad = [r for r in rows if r[2] in DEFECTS]
    if bad:
        print(f"\n✗ {len(bad)} pull request(s) hit a lane defect — see the ::error:: lines above")
        return 1
    print(f"\n✓ {len(rows)} pull request(s) read; "
          f"{sum(r[2] == 'resolved' for r in rows)} resolved, "
          f"{sum(r[2] == 'needs-workflows-grant' for r in rows)} need the Workflows grant, "
          f"{sum(r[2] == 'source-conflict' for r in rows)} have a real conflict")
    return 0


# ─────────────────────────────── self-test ───────────────────────────────

STAND_IN = r'''
import pathlib, subprocess, sys
src = pathlib.Path("M/src.txt").read_text().strip()
other = pathlib.Path("M/other.txt").read_text().strip()
want = f"lock-of:{src}+{other}\n"
lock = pathlib.Path("M/manifest.lock")
if "--check" in sys.argv:
    sys.exit(0 if lock.exists() and lock.read_text() == want else 1)
if "--resolve" in sys.argv:
    un = subprocess.run(["git", "diff", "--name-only", "--diff-filter=U"], capture_output=True, text=True).stdout.split()
    if any(not p.endswith("/manifest.lock") for p in un):
        sys.exit(1)
lock.write_text(want)
if "--resolve" in sys.argv:
    subprocess.run(["git", "add", "--", str(lock)], check=True)
'''


def self_test() -> int:
    failures: list[str] = []

    def expect(cond: bool, what: str) -> None:
        if not cond:
            failures.append(what)

    # Push-refusal classification, against the exact text GitHub answers (PlatformRefBumpLane.md).
    expect(classify_push_error(
        "! [remote rejected] b -> b (refusing to allow a GitHub App to create or update workflow "
        "`.github/workflows/ci.yml` without `workflows` permission)") == "needs-workflows-grant",
        "the Workflows-grant refusal is not recognised")
    expect(classify_push_error("! [rejected]        abc -> b (fetch first)") == "raced",
           "a moved branch is not recognised as a race")
    expect(classify_push_error("fatal: unable to access: 403") == "push-failed",
           "an unexplained refusal must be a DEFECT, not a legitimate outcome")
    expect(classify([]) == "clean" and classify(["A/manifest.lock"]) == "generated-only"
           and classify(["A/manifest.lock", "A/x.cs"]) == "source-conflict",
           "conflict classification is wrong")

    with tempfile.TemporaryDirectory(ignore_cleanup_errors=True) as t:
        t = Path(t)
        remote, work, other = t / "remote.git", t / "work", t / "other"
        subprocess.run(["git", "init", "-q", "--bare", "-b", "main", str(remote)], check=True)
        subprocess.run(["git", "clone", "-q", str(remote), str(work)], check=True,
                       capture_output=True)
        for k, v in (("user.email", "t@t"), ("user.name", "t"), ("gc.auto", "0"),
                     ("maintenance.auto", "false"),
                     # The local driver ARMED, as on a developer clone: keeps ours, never stops.
                     ("merge.mw-manifest-lock.driver", "true")):
            must(work, ["config", k, v])

        def commit(files: dict[str, str], msg: str) -> str:
            for rel, text in files.items():
                (work / rel).parent.mkdir(parents=True, exist_ok=True)
                (work / rel).write_text(text)
            must(work, ["add", "-A"])
            must(work, ["commit", "-qm", msg])
            return must(work, ["rev-parse", "HEAD"])

        commit({"scripts/gen-manifests.py": STAND_IN, ".gitattributes": "*/manifest.lock merge=mw-manifest-lock\n",
                "M/src.txt": "base\n", "M/other.txt": "o\n", "M/manifest.lock": "lock-of:base+o\n"}, "base")
        must(work, ["branch", "feat-lock"]); must(work, ["branch", "feat-src"])
        must(work, ["branch", "feat-clean"]); must(work, ["branch", "feat-race"])
        main = commit({"M/other.txt": "main\n", "M/manifest.lock": "lock-of:base+main\n"}, "main moves M")
        must(work, ["checkout", "-q", "feat-lock"])
        lock_head = commit({"M/src.txt": "feat\n", "M/manifest.lock": "lock-of:feat+o\n"}, "feat edits M")
        must(work, ["checkout", "-q", "feat-race"])
        race_head = commit({"M/src.txt": "race\n", "M/manifest.lock": "lock-of:race+o\n"}, "race edits M")
        must(work, ["checkout", "-q", "feat-src"])
        src_head = commit({"M/other.txt": "mine\n", "M/manifest.lock": "lock-of:base+mine\n"}, "src conflict")
        must(work, ["checkout", "-q", "feat-clean"])
        clean_head = commit({"README.txt": "docs\n"}, "no module touched")
        must(work, ["checkout", "-q", "main"])
        must(work, ["push", "-q", "origin", "main", "feat-lock", "feat-src", "feat-clean", "feat-race"])

        # 1. The instrument: GitHub's view sees the lock conflict even with the driver armed…
        seen = merge_conflicts(work, main, lock_head)
        expect(seen == ["M/manifest.lock"], f"GitHub's view should see only the lock conflict, saw {seen}")
        # …and the CONTROL: the plain local view (driver armed) would have hidden it. If this ever
        # stops being true the emulation is no longer what makes detection correct — re-derive it.
        expect(merge_conflicts(work, main, lock_head, cfg=()) == [],
               "control: the armed local driver no longer hides the conflict — the attr.tree "
               "emulation is untested")
        expect(classify(merge_conflicts(work, main, src_head)) == "source-conflict",
               "a source conflict was not classified as one")
        expect(merge_conflicts(work, main, clean_head) == [], "a clean branch reported conflicts")

        # 2. Resolution: a merge commit on top of the head, lock regenerated over the MERGED tree.
        try:
            sha, locks = resolve_in_worktree(work, lock_head, main, "feat-lock", None)
            expect(locks == ["M/manifest.lock"], f"resolved locks {locks}")
            lock = must(work, ["show", f"{sha}:M/manifest.lock"])
            expect(lock == "lock-of:feat+main", f"the lock describes {lock!r}, not the merged tree")
            rc, _, err = git(work, ["push", "origin", f"{sha}:refs/heads/feat-lock"])
            expect(rc == 0, f"a fast-forward push of the resolution failed: {err}")
        except LaneDefect as e:
            failures.append(f"a lock-only conflict did not resolve: {e}")

        # 3. A source conflict is REFUSED even if resolve is called directly.
        try:
            resolve_in_worktree(work, src_head, main, "feat-src", None)
            failures.append("resolve_in_worktree resolved a SOURCE conflict — it must refuse")
        except LaneDefect:
            pass

        # 4. The author pushed meanwhile: the plain push is rejected and reads as a race.
        try:
            sha, _ = resolve_in_worktree(work, race_head, main, "feat-race", None)
            subprocess.run(["git", "clone", "-q", "-b", "feat-race", str(remote), str(other)],
                           check=True, capture_output=True)
            for k, v in (("user.email", "a@a"), ("user.name", "author")):
                must(other, ["config", k, v])
            (other / "M" / "note.txt").write_text("author keeps working\n")
            must(other, ["add", "-A"]); must(other, ["commit", "-qm", "author"])
            must(other, ["push", "-q", "origin", "feat-race"])
            rc, _, err = git(work, ["push", "origin", f"{sha}:refs/heads/feat-race"])
            expect(rc != 0 and classify_push_error(err) == "raced",
                   f"a push over the author's newer commit must be refused as a race (rc={rc}, {err!r})")
        except LaneDefect as e:
            failures.append(f"race fixture: {e}")

        # 5. No worktree left behind.
        wts = must(work, ["worktree", "list", "--porcelain"]).count("worktree ")
        expect(wts == 1, f"{wts - 1} throwaway worktree(s) left behind")

        # 6. The root comes from the ARGUMENT (or the working directory), never from this file's
        #    own location — the shared lane runs a copy fetched into $RUNNER_TEMP, where a
        #    `__file__`-relative root landed outside every checkout and killed the lane in five
        #    satellites at once. Both halves, because only the pair is a guard: a root inside a
        #    checkout resolves to its top level, and a directory outside one is REFUSED by name.
        try:
            expect(resolve_root(str(work / "M")) == work.resolve(),
                   "a --root inside the checkout must resolve to the checkout's top level")
        except LaneDefect as e:
            failures.append(f"a --root inside the checkout was refused: {e}")
        outside = t / "not-a-repo"
        outside.mkdir()
        # The negative control must be able to fail: if the temp dir is itself inside a work tree
        # (TMPDIR under a clone), the refusal below would pass having tested nothing.
        rc_outside, _, _ = git(outside, ["rev-parse", "--show-toplevel"])
        expect(rc_outside != 0,
               "the negative control is vacuous — this run's temp dir is inside a git work tree")
        try:
            resolve_root(str(outside))
            failures.append("a root outside any git work tree was accepted — the lane would fail "
                            "later, inside git, naming a directory nobody passed")
        except LaneDefect:
            pass
        # …and the ENTRY POINT, which is where it actually broke: `main` must hand `run` the root it
        # was POINTED at. A check on `resolve_root` alone would not have caught the original defect,
        # because the offending expression lived in `main`. `run` is swapped out so this reaches no
        # network; the swap is restored in `finally` so a failure here cannot strand the module.
        acted_on: list[Path] = []
        real_run = globals()["run"]
        entry_point = globals()["main"]          # `main` is a commit sha in this scope
        globals()["run"] = lambda root, *a, **kw: acted_on.append(root) or 0
        try:
            rc_main = entry_point(["--repo", "o/n", "--root", str(work / "M")])
        finally:
            globals()["run"] = real_run
        expect(rc_main == 0 and acted_on == [work.resolve()],
               f"main() must act on the repository --root names; it handed run() {acted_on} "
               f"(rc={rc_main}) — an entry point that derives the root from __file__ lands "
               f"outside the checkout whenever the lane delivers this script out of tree")

    if failures:
        print("✗ resolve-generated-conflicts self-test:")
        for f in failures:
            print(f"  - {f}")
        return 1
    print("✓ resolve-generated-conflicts self-test: GitHub's view sees a lock conflict the armed "
          "local driver hides (control), a lock-only conflict resolves to a merge whose lock "
          "describes the merged tree, a source conflict is refused, a moved branch is a race, and "
          "the Workflows-grant refusal is named")
    return 0


def main(argv: list[str]) -> int:
    if "--self-test" in argv:
        return self_test()
    args = {"--repo": os.environ.get("GITHUB_REPOSITORY"), "--base": "main", "--pr": None,
            "--platform-checker": None, "--root": None}
    flags = {"--dry-run": False}
    it = iter(argv)
    for a in it:
        if a in flags:
            flags[a] = True
        elif a in args:
            args[a] = next(it, None)
        else:
            print(f"✗ unknown argument {a!r}; known: {', '.join(sorted([*args, *flags, '--self-test']))}")
            return 2
    if not args["--repo"]:
        print("✗ --repo OWNER/NAME is required (or GITHUB_REPOSITORY)")
        return 2
    only = int(args["--pr"]) if args["--pr"] else None
    if args["--platform-checker"] and not Path(args["--platform-checker"]).is_file():
        print(f"✗ --platform-checker {args['--platform-checker']} does not exist")
        return 2
    try:
        root = resolve_root(args["--root"])
    except LaneDefect as e:
        print(f"✗ {e}")
        return 2
    return run(root, args["--repo"], args["--base"], only, flags["--dry-run"], args["--platform-checker"])


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
