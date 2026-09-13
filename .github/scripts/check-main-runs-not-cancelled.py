#!/usr/bin/env python3
"""check-main-runs-not-cancelled — Nothing that runs on `main` is ever cancelled: not a push by the next merge, not the release lane by its poll.

Superseding an in-flight run is the right default on a PR branch: the later push tests a strict
successor of what was killed, and that is where the runner saving is. On `main` it is neither safe
nor recoverable, and this repo loses two distinct things when it happens:

  1. **Module bundles are not published for that commit.** The `modules` job lives in `ci.yml`, so
     cancelling the run cancels the publish. The registry then serves module bytes older than main
     — the convergence failure #723 describes.
  2. **Nothing compiles the combination that LANDED.** Each PR is tested against the main it
     branched from, so the merged tree is first built by main's own run. Core shipped
     `CS0246: 'MeshOperations' could not be found` to its main this way, from two independently-
     green PRs merged fifteen seconds apart (MeshWeaver#2412).

Measured 2026-08-26 on this repo: of the day's `Plugin Catalog CI` runs on `main`, **18 were
`cancelled`** — each by the next merge. Both failures are silent: a cancelled run and a run nobody
needed look identical in the runs list, so "main is quiet" reads as healthy.

\U0001f6a8 AND THE RELEASE LANE NEVER CANCELS EITHER (#826, MeshWeaver#2720). A scheduled or dispatched
run also carries the default-branch ref, and that lane used to keep superseding itself ("release
polling supersedes release polling") — until `cancel-in-progress`, which cannot tell a poll from a
dispatch, let the 15:17 poll of 2026-08-28 cancel a framework-released dispatch with all 29 bundle
jobs running, and let the satellites' polls kill the first full platform wave in four of five
repos, one of them MID publish-bake. So the invariant is now "nothing that runs on the default
branch is ever cancelled": a push to main, the dispatch, the poll and a manual `workflow_dispatch`
on main all QUEUE (GitHub keeps one pending run per group, so a burst still collapses).

\U0001f6a8 AND SINCE 2026-09-12 THE RULE IS EVENT-SHAPED, NOT REF-SHAPED: ONLY A `pull_request` RUN IS
EVER SUPERSEDED (maintainer: "see we *NEVER* restart these monster runs with 90 jobs" — cancel the
superseded PR run, nothing else). The ref-shaped value ("anything off main cancels") also cancelled
a manual `workflow_dispatch` on a feature branch — the maintainer's own force-full switch — and a
merge-queue entry, which is ejected rather than retried when its run is cancelled. So the cases
below require `cancel-in-progress` to be TRUE for exactly one (event, ref) pair.

\U0001f6a8 AND THIS CHECK EVALUATES THE EXPRESSION, IT DOES NOT PATTERN-MATCH IT. The sibling guard
in core was first written as a substring test for `refs/heads/main`, which
`${{ github.ref == 'refs/heads/main' }}` satisfies **while cancelling on main** — the exact inverse
of the intent. Review caught it. Matching a spelling is not checking a meaning.

    python3 scripts/check-main-runs-not-cancelled.py --self-test
    python3 scripts/check-main-runs-not-cancelled.py
"""
from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

WORKFLOW = Path(".github/workflows/ci.yml")

# The contexts the assertion is made under: (event_name, ref, must_cancel)
#
# 🚨 ONLY A PULL REQUEST RUN IS EVER SUPERSEDED (maintainer, 2026-09-12: "see we *NEVER* restart
# these monster runs with 90 jobs" — cancel the superseded PR run, and NOTHING else). Until that
# day the rule was ref-shaped ("anything off main cancels"), which also cancelled a manual
# `workflow_dispatch` on a feature branch — the maintainer's own force-full switch — and a
# merge-queue run (dormant, but a cancelled queue entry is ejected, not retried). Every non-PR
# event now QUEUES: a push (main is the only pushed branch this workflow listens to), the release
# lane, and a dispatch on any ref.
# Each case: (event, ref, sender type, must cancel). The sender is the account whose push made the
# run: a person (`User`) or the resolver bot (`Bot`, MeshWeaver.Plugins' meshweaver-cloud[bot]).
# 🚨 A BOT push never supersedes (maintainer, 2026-09-13: "again manifest.lock conflict. again
# when it's almost through … can we just change the manifest and let it run?"): the resolver's
# lock-only merge changes nothing the PR authored, so the running verdict stays and the run for
# the new head ADOPTS it (scripts/ci-change-set.py, `adopted-from`). It queues behind the running
# one — GitHub keeps one pending run per group — and starts when the verdict is in.
CASES = [
    ("push", "refs/heads/main", "User", False),        # the invariant this guard was written for
    ("push", "refs/heads/feature", "User", False),     # not a PR run (and not a trigger here: push is main-only)
    ("pull_request", "refs/pull/1/merge", "User", True),   # THE one superseded run: the later push is a strict successor
    ("pull_request", "refs/pull/1/merge", "Bot", False),   # the resolver's lock-only merge keeps the running verdict
    ("merge_group", "refs/heads/gh-readonly-queue/main/pr-1-0123abcd", "User", False),  # a cancelled entry is ejected
    # The release lane QUEUES, never cancels: a poll must never kill a dispatched rebuild
    # (#826, MeshWeaver#2720), and the flag cannot tell the two apart.
    ("schedule", "refs/heads/main", "User", False),
    ("repository_dispatch", "refs/heads/main", "User", False),
    ("workflow_dispatch", "refs/heads/main", "User", False),   # a manual run on main queues in its own group
    ("workflow_dispatch", "refs/heads/feature", "User", False),  # the maintainer's force-full run is never cancelled
]

TOKEN = re.compile(r"\s*(\|\||&&|==|!=|\(|\)|!|'[^']*'|[A-Za-z0-9_.\-]+)")


def tokenize(text: str) -> list[str]:
    tokens, position = [], 0
    while position < len(text):
        if text[position].isspace():
            position += 1
            continue
        match = TOKEN.match(text, position)
        if not match:
            raise ValueError(f"unexpected character {text[position]!r} in {text!r}")
        tokens.append(match.group(1))
        position = match.end()
    return tokens


class Parser:
    """Recursive descent over the slice of the GitHub expression language a
    `cancel-in-progress` value can reasonably use. Precedence: ! > comparison > && > ||.
    Truthiness follows Actions: a non-empty string is true. Anything outside the subset
    RAISES rather than guessing — an unparseable expression must fail loudly."""

    def __init__(self, tokens: list[str], context: dict[str, str]) -> None:
        self.tokens, self.position, self.context = tokens, 0, context

    def peek(self) -> str | None:
        return self.tokens[self.position] if self.position < len(self.tokens) else None

    def next(self) -> str:
        token = self.tokens[self.position]
        self.position += 1
        return token

    def parse(self):
        value = self.parse_or()
        if self.position != len(self.tokens):
            raise ValueError(f"trailing tokens from {self.tokens[self.position]!r}")
        return value

    def parse_or(self):
        # 🚨 Both sides are PARSED before either is combined. `truthy(left) or
        # truthy(self.parse_and())` would short-circuit in Python and never consume the right
        # operand's tokens, so a false left operand left the parser mid-expression and raised
        # "missing closing parenthesis" on a perfectly valid input. The self-test caught this on
        # its first run — parsing and evaluating are different jobs and only one of them may skip.
        left = self.parse_and()
        while self.peek() == "||":
            self.next()
            right = self.parse_and()
            left = truthy(left) or truthy(right)
        return left

    def parse_and(self):
        left = self.parse_comparison()
        while self.peek() == "&&":
            self.next()
            right = self.parse_comparison()
            left = truthy(left) and truthy(right)
        return left

    def parse_comparison(self):
        left = self.parse_unary()
        if self.peek() in ("==", "!="):
            operator = self.next()
            equal = stringify(left) == stringify(self.parse_unary())
            return equal if operator == "==" else not equal
        return left

    def parse_unary(self):
        if self.peek() == "!":
            self.next()
            return not truthy(self.parse_unary())
        return self.parse_primary()

    def parse_primary(self):
        token = self.next()
        if token == "(":
            inner = self.parse_or()
            if self.next() != ")":
                raise ValueError("missing closing parenthesis")
            return inner
        if token.startswith("'"):
            return token.strip("'")
        if token in ("true", "false"):
            return token == "true"
        if token in self.context:
            return self.context[token]
        raise ValueError(
            f"expression reads context value {token!r}, which this guard does not model. "
            "Add it to the contexts under test rather than loosening the assertion."
        )


def truthy(value) -> bool:
    if isinstance(value, bool):
        return value
    if isinstance(value, str):
        return len(value) > 0
    raise ValueError(f"cannot take the truth value of {value!r}")


def stringify(value) -> str:
    return ("true" if value else "false") if isinstance(value, bool) else str(value)


def evaluate(raw: str, event_name: str, ref: str, sender_type: str = "User") -> bool:
    text = raw.strip()
    wrapped = re.match(r"^\$\{\{(.*)\}\}$", text, re.S)
    if wrapped:
        text = wrapped.group(1)
    text = text.strip()
    if len(text) > 1 and text[0] == '"' and text[-1] == '"':
        text = text[1:-1]
    context = {"github.event_name": event_name, "github.ref": ref, "github.event.sender.type": sender_type}
    return truthy(Parser(tokenize(text), context).parse())


def read_expression(body: str) -> str:
    match = re.search(r"^\s*cancel-in-progress:\s*(.+?)\s*$", body, re.M)
    if not match:
        raise ValueError(
            f"{WORKFLOW} no longer declares cancel-in-progress. If concurrency was removed "
            "entirely that is fine for main, but this guard can no longer see it — re-point or "
            "delete it deliberately rather than letting it rot."
        )
    return match.group(1)


def check(body: str) -> list[str]:
    expression = read_expression(body)
    failures = []
    for event_name, ref, sender_type, must_cancel in CASES:
        actual = evaluate(expression, event_name, ref, sender_type)
        if actual == must_cancel:
            continue
        if event_name == "pull_request" and sender_type == "Bot":
            failures.append(
                f"cancel-in-progress is TRUE for the resolver bot's push to a pull request ({expression!r}). "
                "A lock-only merge changes nothing the PR authored; cancelling the running verdict for "
                "it is what threw away three 30-minute runs on 2026-09-13. Exclude the bot: "
                "github.event.sender.type != 'Bot'."
            )
        elif event_name == "push" and ref == "refs/heads/main":
            failures.append(
                f"cancel-in-progress is TRUE for a push to main ({expression!r}). Each merge then "
                "cancels the run for the merge before it: module bundles are not published for "
                "that commit (the `modules` job is in this workflow), and nothing compiles the "
                "tree that landed. 18 of one day's main runs were cancelled this way."
            )
        else:
            failures.append(
                f"cancel-in-progress is {str(actual).upper()} for {event_name} on {ref} "
                f"({expression!r}), expected {str(must_cancel).upper()}. Superseding must stay ON "
                "for PR branches (where the runner saving is) and OFF for everything on the default "
                "branch — a push to main AND the release lane (dispatch, poll, manual rebuild): a "
                "poll that cancels the wave tears a publication mid-seal (#826, MeshWeaver#2720)."
            )
    return failures


SELF_TESTS = [
    ("the shipped expression — a person's pull request push supersedes, the resolver bot's never",
     "${{ github.event_name == 'pull_request' && github.event.sender.type != 'Bot' }}", True),
    ("the pre-2026-09-13 expression — cancels the running verdict for the bot's lock-only merge",
     "${{ github.event_name == 'pull_request' }}", False),
    # The value shipped until 2026-09-12: ref-shaped, so it cancelled a manual dispatch on a
    # feature branch (the force-full switch) and a merge-queue entry.
    ("the ref-shaped value shipped before — cancels a feature-branch dispatch",
     "${{ !(github.ref == 'refs/heads/main' || github.event_name == 'repository_dispatch' || github.event_name == 'schedule') }}",
     False),
    ("bare true — the pre-fix value", "true", False),
    ("bare false — kills superseding everywhere", "false", False),
    # The counterexample that defeated a substring check in the sibling guard.
    ("mentions main but cancels there", "${{ github.ref == 'refs/heads/main' }}", False),
    # The value shipped until #826: right for a push, but it let the poll cancel the wave.
    ("push-and-main only — cancels the release lane",
     "${{ !(github.event_name == 'push' && github.ref == 'refs/heads/main') }}", False),
    # Ref-only cancels every non-main ref, a feature-branch dispatch included.
    ("ref-only — cancels a feature-branch dispatch", "${{ github.ref != 'refs/heads/main' }}", False),
    # Event-only forgets the manual rebuild: a workflow_dispatch on main would still be cancelled.
    ("event-only — cancels a manual rebuild on main",
     "${{ !(github.event_name == 'push' || github.event_name == 'repository_dispatch' || github.event_name == 'schedule') }}",
     False),
    # A merge-queue run is not a pull request run: a cancelled entry is ejected, never retried.
    ("pull request OR merge group — cancels a queue entry",
     "${{ github.event_name == 'pull_request' || github.event_name == 'merge_group' }}", False),
]


def self_test() -> int:
    failures = []
    for name, expression, should_pass in SELF_TESTS:
        body = f"concurrency:\n  group: x\n  cancel-in-progress: {expression}\n"
        try:
            passed = not check(body)
        except ValueError as error:
            failures.append(f"  {name}: raised {error}")
            continue
        if passed != should_pass:
            verdict = "PASSED" if passed else "FAILED"
            expected = "pass" if should_pass else "fail"
            failures.append(f"  {name}: {verdict} but should {expected} — {expression}")

    # The checker must also fail loudly, not silently, on an expression it cannot parse.
    try:
        check("concurrency:\n  cancel-in-progress: ${{ github.actor == 'x' }}\n")
        failures.append("  unmodelled context: parsed instead of raising")
    except ValueError:
        pass

    if failures:
        print(f"\n✗ check-main-runs-not-cancelled self-test: {len(failures)} failure(s)")
        print("\n".join(failures))
        return 1
    print("✓ check-main-runs-not-cancelled self-test: "
          f"{len(SELF_TESTS)} expressions both directions, plus the unparseable case")
    return 0


SUPERSEDE_CALL = re.compile(r"^\s*uses:\s*\S*/node-repo-supersede\.yml@", re.M)


PER_MODULE_DEPLOY = re.compile(r"^\s*uses:\s*\S*/node-repo-module-publish\.yml@", re.M)


def supersede_calls(body: str) -> list[str]:
    """Per-module deploy (2026-09-11): no job may call the supersede lane. The lane refuses every
    event but a push to main, so ANY call is a canceller of main runs — the second canceller, beside
    `cancel-in-progress`, that the expression check above cannot see. Comment lines never match.

    Fleet (2026-09-13): the rule binds a repo that publishes PER MODULE (it calls
    node-repo-module-publish.yml — a cancelled main run then publishes nothing). A repo still on the
    whole-repo bake (the satellites: publish-bake, no module-publish) keeps the supersede lane by
    design — its design of record is ModuleBuildArchitecture → "Superseded runs on main"."""
    if not PER_MODULE_DEPLOY.search(body):
        return []
    return [
        f"line {body.count(chr(10), 0, m.start()) + 1}: calls node-repo-supersede.yml. This repo is on "
        "PER-MODULE DEPLOY: a push run on main is never cancelled, because a cancelled run publishes "
        "nothing and — under a steady merge rate — every run was cancelled before it published (main "
        "shipped NO module for nine hours on 2026-09-11). Ordering is kept per module instead "
        "(`publish-newest-only`). See core Doc/Architecture/ModuleBuildArchitecture → Per-module deploy."
        for m in SUPERSEDE_CALL.finditer(body)
    ]


SUPERSEDE_SELF_TESTS = [
    ("a supersede call on a per-module-deploy repo", "jobs:\n  s:\n    uses: Systemorph/MeshWeaver/.github/workflows/node-repo-supersede.yml@main\n  p:\n    uses: Systemorph/MeshWeaver/.github/workflows/node-repo-module-publish.yml@main\n", 1),
    ("a supersede call on a whole-repo-bake satellite (no module-publish) is by design", "jobs:\n  s:\n    uses: Systemorph/MeshWeaver/.github/workflows/node-repo-supersede.yml@main\n  b:\n    uses: Systemorph/MeshWeaver/.github/workflows/node-repo-publish-bake.yml@main\n", 0),
    ("a comment naming the lane", "jobs:\n  # uses: Systemorph/MeshWeaver/.github/workflows/node-repo-supersede.yml@main\n  p:\n    uses: Systemorph/MeshWeaver/.github/workflows/node-repo-module-publish.yml@main\n", 0),
    ("another lane", "jobs:\n  v:\n    uses: Systemorph/MeshWeaver/.github/workflows/node-repo-validate.yml@main\n", 0),
]


def supersede_self_test() -> list[str]:
    return [f"  supersede check, {name}: {len(supersede_calls(body))} finding(s), expected {want}"
            for name, body, want in SUPERSEDE_SELF_TESTS if len(supersede_calls(body)) != want]


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--self-test", action="store_true",
                        help="run the checker over embedded expressions and verify it can fail")
    arguments = parser.parse_args()

    if arguments.self_test:
        broken = supersede_self_test()
        if broken:
            print("\n✗ check-main-runs-not-cancelled self-test: supersede check")
            print("\n".join(broken))
            return 1
        print(f"✓ supersede-call check: {len(SUPERSEDE_SELF_TESTS)} cases, both directions")
        return self_test()

    if not WORKFLOW.exists():
        print(f"✗ {WORKFLOW} not found — run from the repository root.")
        return 1

    body = WORKFLOW.read_text(encoding="utf-8")
    failures = check(body) + supersede_calls(body)
    if failures:
        print(f"✗ {WORKFLOW}: {len(failures)} failure(s) — something cancels runs on main:\n")
        for failure in failures:
            print(f"  - {failure}")
        return 1

    print(f"✓ {WORKFLOW}: nothing on main is ever cancelled (a push, the release lane) — neither by "
          "concurrency nor by a supersede lane; only a pull_request run is superseded.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
