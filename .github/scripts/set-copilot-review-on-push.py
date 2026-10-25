#!/usr/bin/env python3
"""set-copilot-review-on-push.py — reconcile every fleet ruleset's `copilot_code_review` rule to the in-force `review_on_push` value, nothing else.

Policy `review-on-real-change` (Doc/Architecture/PolicyNotProse; manual:
Doc/Architecture/ReviewFindingsAnswered → "A review on every real change, none for a merge of main"):
Copilot reviews a pull request when it opens, a push that only merges the default branch costs NO
review, and a push that changes the pull request's own diff gets one from the review-on-change lane
(node-repo-request-review.yml) — so the rule runs with `review_on_push: false`. The value lives in
ONE place, `REVIEW_ON_PUSH` below; the script only ever moves a ruleset TO it.

🚨 Apply it to a repository ONLY once that repository CALLS the review-on-change lane
(`request-review-on-change.yml`) — `--repo NAME` per adopted repository. Flipping a repository that
does not call it leaves every real change after the opening review unreviewed, and its
`Automatic review answered` red until a person re-requests the review. `--out DIR` writes, before
anything is PUT, BOTH files per ruleset: `<repo>.<id>.before.json` — the restore body, the ruleset's
fields AS READ (`review_on_push` at its old value) — and `<repo>.<id>.put.json`, the body this run
PUTs. Restoring is a PUT of the `.before.json` body (`gh api --method PUT … --input <file>`).
`--apply` therefore REQUIRES `--repo` (never the whole fleet by default) and refuses a repository whose
default branch carries no `LANE_WORKFLOW`; `--out` refuses to overwrite a `.before.json` already there,
so a retry after the change can never replace the restore point with the changed value — give each
run a fresh directory.

What it does, per repository:
  1. lists the rulesets (REST), reads each in full, and picks every one carrying `copilot_code_review`
     (none → RED: the repository is outside the policy, say so rather than skip it);
  2. builds the PUT body from the ruleset AS READ — name, target, enforcement, bypass_actors,
     conditions, rules — changing ONLY `rules[copilot_code_review].parameters.review_on_push` to
     `REVIEW_ON_PUSH` (`review_draft_pull_requests` keeps its value);
  3. already at that value → "unchanged", nothing is written (idempotent);
  4. `--apply` PUTs it, reads the ruleset back and fails unless the read-back equals the body it sent.
Without `--apply` it prints each body (a dry run). `--out DIR` also writes `<repo>.<id>.before.json`
(the restore body, as read) and `<repo>.<id>.put.json` (what is PUT).

A ruleset read without `bypass_actors` (a token that cannot see them) is REFUSED: a PUT without the
field could not be shown to keep them.

USAGE
  set-copilot-review-on-push.py --self-test
  set-copilot-review-on-push.py [--repo NAME ...] [--out DIR]          (dry run; default: the fleet)
  set-copilot-review-on-push.py --repo NAME [--repo NAME ...] [--out DIR] --apply
Every GitHub call is `gh api` (REST). Exit 0 = every repository done or unchanged, 1 = any refusal
or failed read-back, 2 = usage error.
"""
from __future__ import annotations

import argparse
import contextlib
import copy
import io
import json
import os
import subprocess
import sys
import tempfile

OWNER = "Systemorph"
FLEET = ("MeshWeaver", "MeshWeaver.Plugins", "Memex", "MeshWeaver.Crm", "MeshWeaver.Education",
         "MeshWeaver.Reinsurance", "MeshWeaver.SocialMedia", "MeshWeaver.Manufacturing", "MeshWeaver.FundReporting",
         "MeshWeaver.Deploy")
RULE = "copilot_code_review"
# The in-force value (policy `review-on-real-change`). Change it only with the policy.
REVIEW_ON_PUSH = False
# The review-on-change lane a repository must CALL before its push reviews may be switched off.
LANE_WORKFLOW = ".github/workflows/request-review-on-change.yml"
BODY_FIELDS = ("name", "target", "enforcement", "bypass_actors", "conditions", "rules")


class Refused(RuntimeError):
    pass


def carries_rule(ruleset: dict) -> bool:
    return any(r.get("type") == RULE for r in ruleset.get("rules") or ())


def put_body(ruleset: dict) -> tuple[dict, bool]:
    """(the PUT body, whether it changes anything). Pure. Raises Refused when the ruleset cannot be
    rewritten safely."""
    missing = [f for f in BODY_FIELDS if f not in ruleset]
    if missing:
        raise Refused(f"ruleset {ruleset.get('id')} was read without {', '.join(missing)} — refusing to PUT a body that could drop them")
    body = {f: copy.deepcopy(ruleset[f]) for f in BODY_FIELDS}
    rules = [r for r in body["rules"] if r.get("type") == RULE]
    if len(rules) != 1:
        raise Refused(f"ruleset {ruleset.get('id')} carries {len(rules)} `{RULE}` rules, expected 1")
    params = rules[0].setdefault("parameters", {})
    if "review_on_push" not in params:
        raise Refused(f"ruleset {ruleset.get('id')}: `{RULE}` has no `review_on_push` parameter — the rule's shape changed, look before writing")
    changed = params["review_on_push"] is not REVIEW_ON_PUSH
    params["review_on_push"] = REVIEW_ON_PUSH
    return body, changed


def restore_body(ruleset: dict) -> dict:
    """The ruleset's own PUT-able fields AS READ — the restore point, written before any PUT. Pure."""
    missing = [f for f in BODY_FIELDS if f not in ruleset]
    if missing:
        raise Refused(f"ruleset {ruleset.get('id')} was read without {', '.join(missing)} — no complete restore body")
    return {f: copy.deepcopy(ruleset[f]) for f in BODY_FIELDS}


def readback_matches(sent: dict, readback: dict) -> str:
    """'' when every field the PUT sent reads back identically; otherwise the first difference."""
    for f in BODY_FIELDS:
        if readback.get(f) != sent.get(f):
            return f"`{f}` reads back as {json.dumps(readback.get(f))[:300]} — sent {json.dumps(sent.get(f))[:300]}"
    return ""


def gh(*args: str, stdin: str | None = None):
    out = subprocess.run(["gh", "api", *args], input=stdin, capture_output=True, text=True)
    if out.returncode != 0:
        raise Refused(f"gh api {' '.join(args)} failed: {out.stderr.strip()[:400]}")
    return json.loads(out.stdout) if out.stdout.strip() else None


def lane_adopted(repo: str) -> bool:
    """Whether the repository's default branch carries the review-on-change lane (REST contents read;
    a 404 is "not adopted", any other failure is a refusal)."""
    out = subprocess.run(["gh", "api", f"repos/{OWNER}/{repo}/contents/{LANE_WORKFLOW}", "--jq", ".path"],
                         capture_output=True, text=True)
    if out.returncode == 0:
        return out.stdout.strip() == LANE_WORKFLOW
    if "HTTP 404" in out.stderr:
        return False
    raise Refused(f"{repo}: could not read {LANE_WORKFLOW}: {out.stderr.strip()[:400]}")


def write_new(path: str, payload: dict) -> None:
    """Writes `payload` to a file that must NOT exist yet — a restore point is never overwritten."""
    try:
        with open(path, "x") as f:
            json.dump(payload, f, indent=2)
    except FileExistsError:
        raise Refused(f"{path} already exists — refusing to overwrite a restore point; use a fresh --out directory")


def one_repo(repo: str, apply: bool, out_dir: str | None, adopted=None) -> bool:
    if apply and not (adopted or lane_adopted)(repo):
        raise Refused(f"{repo}: {LANE_WORKFLOW} is not on the default branch — switching push reviews off "
                      "before the review-on-change lane exists would leave every later change unreviewed")
    listing = gh(f"repos/{OWNER}/{repo}/rulesets")
    if not isinstance(listing, list):
        raise Refused(f"{repo}: the ruleset listing was not a list")
    hits = [rs for rs in (gh(f"repos/{OWNER}/{repo}/rulesets/{x['id']}") for x in listing) if carries_rule(rs)]
    if not hits:
        raise Refused(f"{repo}: no ruleset carries `{RULE}`")
    for rs in hits:
        body, changed = put_body(rs)
        tag = f"{repo} ruleset {rs['id']} ({rs.get('name')}, {rs.get('enforcement')})"
        if out_dir:
            # 🚨 The BEFORE state is the restore point (#7142 review): `body` already carries the new
            # value, so writing only it would "restore" the change itself.
            # It is never overwritten either: a retry after the change would read the NEW value as
            # "before" and make the restore a no-op.
            write_new(os.path.join(out_dir, f"{repo}.{rs['id']}.before.json"), restore_body(rs))
            write_new(os.path.join(out_dir, f"{repo}.{rs['id']}.put.json"), body)
        if not changed:
            print(f"unchanged  {tag}: review_on_push is already {json.dumps(REVIEW_ON_PUSH)}")
            continue
        if not apply:
            print(f"would PUT  {tag}: PUT /repos/{OWNER}/{repo}/rulesets/{rs['id']}\n{json.dumps(body, indent=2)}")
            continue
        gh("--method", "PUT", f"repos/{OWNER}/{repo}/rulesets/{rs['id']}", "--input", "-", stdin=json.dumps(body))
        diff = readback_matches(body, gh(f"repos/{OWNER}/{repo}/rulesets/{rs['id']}"))
        if diff:
            raise Refused(f"{tag}: read-back differs after the PUT — {diff}")
        print(f"applied    {tag}: review_on_push {json.dumps(REVIEW_ON_PUSH)}, read back identical")
    return True


def self_test() -> int:
    failures = 0

    def check(name, ok):
        nonlocal failures
        failures += 0 if ok else 1
        print(f"self-test {'ok' if ok else 'FAIL':4} {name}")

    base = {"id": 1, "name": "main pr protection", "target": "branch", "enforcement": "active",
            "bypass_actors": [{"actor_id": 5, "actor_type": "RepositoryRole", "bypass_mode": "always"}],
            "conditions": {"ref_name": {"include": ["~DEFAULT_BRANCH"], "exclude": []}},
            "rules": [{"type": "deletion"},
                      {"type": RULE, "parameters": {"review_on_push": not REVIEW_ON_PUSH, "review_draft_pull_requests": False}}],
            "_links": {}, "node_id": "x", "source": "o/r"}
    body, changed = put_body(base)
    cop = [r for r in body["rules"] if r["type"] == RULE][0]["parameters"]
    check("moves review_on_push to the in-force value", changed and cop["review_on_push"] is REVIEW_ON_PUSH)
    check("keeps review_draft_pull_requests", cop["review_draft_pull_requests"] is False)
    check("keeps every other field identical",
          all(body[f] == base[f] for f in BODY_FIELDS if f != "rules") and body["rules"][0] == base["rules"][0])
    check("never sends read-only fields", set(body) == set(BODY_FIELDS))
    check("does not mutate the ruleset as read", base["rules"][1]["parameters"]["review_on_push"] is (not REVIEW_ON_PUSH))
    _, again = put_body(dict(base, rules=body["rules"]))
    check("idempotent: already at the in-force value -> unchanged", again is False)
    check("in-force value is false (policy review-on-real-change)", REVIEW_ON_PUSH is False)
    for name, bad in [("no bypass_actors read -> refused", {k: v for k, v in base.items() if k != "bypass_actors"}),
                      ("no copilot rule -> refused", dict(base, rules=[{"type": "deletion"}])),
                      ("rule without review_on_push -> refused", dict(base, rules=[{"type": RULE, "parameters": {}}]))]:
        try:
            put_body(bad)
            check(name, False)
        except Refused:
            check(name, True)
    before = restore_body(base)
    check("restore body keeps the OLD review_on_push (the before-state, not the PUT)",
          [r for r in before["rules"] if r["type"] == RULE][0]["parameters"]["review_on_push"] is (not REVIEW_ON_PUSH)
          and before != body and set(before) == set(BODY_FIELDS))
    with tempfile.TemporaryDirectory() as d:
        calls = []

        def fake_gh(*args, stdin=None):
            calls.append(args)
            return [{"id": 1}] if args[0].endswith("/rulesets") else copy.deepcopy(base)
        real = globals()["gh"]
        globals()["gh"] = fake_gh
        try:
            with contextlib.redirect_stdout(io.StringIO()):
                one_repo("R", False, d)
        finally:
            globals()["gh"] = real
        with open(os.path.join(d, "R.1.before.json")) as f:
            saved_before = json.load(f)
        with open(os.path.join(d, "R.1.put.json")) as f:
            saved_put = json.load(f)
        check("--out writes the before-state AND the PUT body, and a dry run PUTs nothing",
              saved_before == before and saved_put == body and not any("PUT" in a for a in calls))
        globals()["gh"] = fake_gh
        try:
            with contextlib.redirect_stdout(io.StringIO()):
                one_repo("R", False, d)
            check("a second run into the same --out is refused (the restore point is never overwritten)", False)
        except Refused:
            with open(os.path.join(d, "R.1.before.json")) as f:
                check("a second run into the same --out is refused (the restore point is never overwritten)",
                      json.load(f) == before)
        finally:
            globals()["gh"] = real
        calls.clear()
        globals()["gh"] = fake_gh
        try:
            one_repo("R", True, None, adopted=lambda _r: False)
            check("--apply on a repository without the review-on-change lane is refused before any read", False)
        except Refused:
            check("--apply on a repository without the review-on-change lane is refused before any read", not calls)
        finally:
            globals()["gh"] = real
    # 🚨 Under a fake `gh` and a fake lane read: if the guard ever regressed, this call would
    # otherwise PUT every fleet ruleset for real.
    puts = []
    real_gh, real_lane = globals()["gh"], globals()["lane_adopted"]
    globals()["gh"] = lambda *args, stdin=None: puts.append(args) or ([] if args[0].endswith("/rulesets") else {})
    globals()["lane_adopted"] = lambda _r: True
    try:
        with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            rc = main(["--apply"])
    finally:
        globals()["gh"], globals()["lane_adopted"] = real_gh, real_lane
    check("--apply without --repo is a usage error and calls nothing", rc == 2 and not puts)
    check("read-back identical -> ''", readback_matches(body, dict(body, id=1)) == "")
    check("read-back differs -> named", "rules" in readback_matches(body, dict(body, rules=base["rules"])))
    print("self-test: " + ("every case held" if not failures else f"{failures} FAILED"))
    return 0 if not failures else 1


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--self-test", action="store_true")
    ap.add_argument("--repo", action="append", help="repository name under Systemorph (repeatable); default: the fleet")
    ap.add_argument("--apply", action="store_true", help="PUT the change; without it, a dry run")
    ap.add_argument("--out", help="also write DIR/<repo>.<id>.before.json (restore) and .put.json; never overwrites")
    a = ap.parse_args(argv)
    if a.self_test:
        return self_test()
    if a.apply and not a.repo:
        print("usage: --apply needs --repo NAME for each repository that already calls the review-on-change lane",
              file=sys.stderr)
        return 2
    if a.out:
        os.makedirs(a.out, exist_ok=True)
    failed = 0
    for repo in a.repo or FLEET:
        try:
            one_repo(repo, a.apply, a.out)
        except Refused as e:
            failed += 1
            print(f"REFUSED    {e}", file=sys.stderr)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
