#!/usr/bin/env python3
"""An allow entry is TRANSITIONAL: it lives for one pull request and dies with the merge.

WHY THIS EXISTS (MeshWeaver#3422)
---------------------------------
Both binary-compatibility ratchets — `scripts/check-record-signatures.py` and
`scripts/check-type-forwards.py` — take a tracked allow file whose entries say "this break is
deliberate, the atomic move is planned". An entry is meaningful only while the diff that carries
the break is unmerged. Every one of those files told the author so, in prose:

    # One fully-qualified type per line, then a reason. DELETE the line once the change has
    # shipped: a stale entry FAILS the gate …

and #3414 wrote the instruction into the entry itself — *"DELETE THIS LINE in the change right
after #3414 merges."* Nobody did. It merged at 13:38Z; for the next ~40 minutes every C#-touching
pull request in the fleet went red on

    ✗ scripts/record-signatures.allow lists `Rail`, but its primary constructor no longer differs.
    🚨 0 binary-breaking record change(s), 1 stale allow entr(ies).

— three of them dequeued by the merge-queue steward — while `main` read green, because its own
gate was reuse-skipped. `scripts/type-forwards.allow` had produced the identical incident earlier.

**The defect is that the deletion was an INSTRUCTION addressed to a future reader, inside the
artefact, rather than a MECHANISM.** An instruction is not enforcement, and the pull request that
adds the entry is by construction the one whose merge makes it stale — so the author is asked to
remember something at exactly the moment they stop looking.

THE MECHANISM: SCOPE THE ALLOWANCE TO THE DIFF THAT INTRODUCES IT
-----------------------------------------------------------------
An entry is in force **only in the diff that adds it** — the line must be absent from the allow
file at the merge base and present at HEAD. An entry that is already in the base has LANDED, and
is INERT: it allows nothing and it fails nothing.

That is the whole fix, and it is a mechanism rather than a reminder, because *merging is exactly
the moment the line stops being an addition*. There is no line for anyone to forget to delete and
no second issue to remember.

It also closes, rather than merely relocating, the danger the old stale ratchet existed for. That
ratchet's stated reason was "an entry that outlives its change hides the next break" — true,
because a landed entry kept SUPPRESSING findings for its key. An inert entry suppresses nothing,
so a later break on the same record fails the gate exactly as if the entry had been deleted. The
ratchet is not weakened; its blast radius stops being everybody else's pull request.

The merge queue is covered by the same rule with no special case: a `merge_group` build compares
main against the queue branch, so a queued pull request's entry is still an addition there.

THE CLASSIFIER: AN ENTRY NAMES THE PULL REQUEST IT IS TRANSITIONAL FOR
----------------------------------------------------------------------
Scoping alone would accept an entry that was dead on arrival — copied from an older change, or
written against a pull request that has already landed. So the format becomes machine-readable
and the reference is resolved:

    <key>  #<pull request> — <reason>

    Rail  #3414 — SuppliedNavigationRail's two order-losing buckets become one ordered sequence …

🚨 `#3406` — what the incident's entry actually carried — is the ISSUE. The pull request appeared
only in prose. So this is not a tightening of an existing convention: the number was never
machine-readable, and an entry that names an issue is refused here by name, with the pull request
being what the gate can resolve into "has this landed yet?".

    open (draft included)  -> LIVE       the allowance is in force. This is the entry's purpose.
    merged                 -> EXPIRED    stale BY DEFINITION — the change it was written for has
                                         already landed, so the entry can only be a copy.
    closed, never merged   -> ABANDONED  the change was dropped; the allowance never applied.
    resolves to an issue   -> NOT_A_PR   name the pull request, not the issue it fixes.
    no `#<n>` at all       -> NO_REFERENCE
    names a different pull request than the one under test  -> WRONG_PR
    the API could not answer -> UNRESOLVED

🚨 EVERY non-LIVE verdict FAILS. There is deliberately no verdict that passes on missing evidence:
"the gate could not tell" must never render as "allowed", which is the grey-reads-as-green shape
AGENTS.md forbids outright. An unreachable api.github.com is a RED gate, not an implicit allow.

NETWORK
-------
This is the first API dependency in the `Public surface (binary compatibility)` job, and it is
deliberately conditional: `resolve` is called ONLY for entries the diff introduces. Both allow
files hold zero entries today and gain roughly one per year, so an ordinary pull request performs
no request at all and behaves byte-for-byte as before. When there IS something to resolve, the
pull request is in THIS repository, so the automatic `GITHUB_TOKEN` with `pull-requests: read`
suffices — no App token, no new secret and no preflight job, unlike
`scripts/check-cross-repo-pair.py`, which needs an installation token only because it reads OTHER
repositories' pull requests.
"""

from __future__ import annotations

import json
import os
import re
import subprocess
import urllib.error
import urllib.request
from dataclasses import dataclass
from enum import Enum
from pathlib import Path

THIS_REPO_DEFAULT = "Systemorph/MeshWeaver"

# `Rail  #3414 — reason`. The reference is required and is a PULL REQUEST number; the separator
# between it and the reason is free (an em dash by convention, but nothing depends on that).
REFERENCE_RE = re.compile(r"#(?P<number>\d+)\b")


class Verdict(Enum):
    """What an introduced entry resolved to. LIVE is the only one that allows anything."""

    LIVE = "live"
    EXPIRED = "expired"
    ABANDONED = "abandoned"
    NOT_A_PR = "not-a-pull-request"
    NO_REFERENCE = "no-reference"
    WRONG_PR = "wrong-pull-request"
    UNRESOLVED = "unresolved"

    @property
    def allows(self) -> bool:
        return self is Verdict.LIVE


@dataclass(frozen=True)
class Entry:
    """One non-comment line of an allow file."""

    key: str
    reference: int | None
    reason: str
    raw: str


@dataclass(frozen=True)
class Judgement:
    entry: Entry
    verdict: Verdict
    detail: str

    @property
    def allows(self) -> bool:
        return self.verdict.allows


class AllowResolutionError(RuntimeError):
    """The reference could not be resolved. ALWAYS fatal — never a silent pass."""


# ─────────────────────────────── parsing ───────────────────────────────


def parse_entries(text: str) -> list[Entry]:
    """Every non-comment, non-blank line of an allow file, in order.

    A line with no `#<n>` still parses — into an entry whose `reference` is None, which the
    classifier refuses by name. Dropping it here instead would make a malformed entry
    indistinguishable from no entry at all: the author believes they declared an allowance and
    the gate believes they did not, which is the same trapdoor as a gate that skips on a missing
    input.
    """
    entries: list[Entry] = []
    for line in text.splitlines():
        stripped = line.strip()
        if not stripped or stripped.startswith("#"):
            continue
        key, _, rest = stripped.partition(" ")
        rest = rest.strip()
        m = REFERENCE_RE.search(rest)
        reference = int(m.group("number")) if m else None
        entries.append(Entry(key.strip(), reference, rest, stripped))
    return entries


def _git(root: Path, *args: str) -> subprocess.CompletedProcess:
    return subprocess.run(["git", "-C", str(root), *args], capture_output=True, text=True)


def read_at(root: Path, allow_file: str, rev: str | None) -> str:
    """The allow file's text at `rev`, or from the working tree when `rev` is None.

    A file that does not exist at `rev` reads as empty — which is the correct answer, and the
    reason this cannot be written as `git show … | grep … || echo clean`: an absent path there
    reads as "nothing to see" for the wrong reason.
    """
    if rev is None:
        path = root / allow_file
        return path.read_text(encoding="utf-8") if path.exists() else ""
    done = _git(root, "show", f"{rev}:{allow_file}")
    return done.stdout if done.returncode == 0 else ""


def partition_entries(
    root: Path, allow_file: str, base: str, head: str | None = None
) -> tuple[list[Entry], list[Entry]]:
    """(introduced by this diff, already landed in the base) — the SCOPE rule.

    Membership is decided on the entry's exact line text, not on its key: editing an entry's
    reason re-states the allowance, so the edited line is introduced and is judged afresh. That
    fails safe in the one direction that matters — a changed line is never mistaken for one the
    base already carried.
    """
    at_head = parse_entries(read_at(root, allow_file, head))
    landed_lines = {e.raw for e in parse_entries(read_at(root, allow_file, base))}
    introduced = [e for e in at_head if e.raw not in landed_lines]
    landed = [e for e in at_head if e.raw in landed_lines]
    return introduced, landed


# ─────────────────────────────── the classifier ───────────────────────────────


def classify(entry: Entry, resolve, this_pr: int | None = None) -> Judgement:
    """Resolve one introduced entry's reference into a verdict.

    `resolve(number) -> dict` answers `{isPullRequest, state, merged, title, url}` and raises
    `AllowResolutionError` when it cannot. Injecting it is what lets the self-tests prove every
    branch without a network.
    """
    if entry.reference is None:
        return Judgement(
            entry,
            Verdict.NO_REFERENCE,
            "the entry names no pull request. Write `"
            + entry.key
            + "  #<pull request> — <reason>`: the number is what lets this gate see that the "
            "allowance has expired, instead of asking a future reader to remember.",
        )

    if this_pr is not None and entry.reference != this_pr:
        return Judgement(
            entry,
            Verdict.WRONG_PR,
            f"the entry names #{entry.reference}, but the pull request that carries it is "
            f"#{this_pr}. An allow entry is transitional for ONE change — the one it travels "
            f"with — so it must name this pull request.",
        )

    try:
        state = resolve(entry.reference)
    except AllowResolutionError as e:
        return Judgement(
            entry,
            Verdict.UNRESOLVED,
            f"#{entry.reference} could not be resolved: {e}. This gate refuses to treat an "
            f"unresolvable reference as an allowance — a gate that cannot tell must go red, "
            f"never quietly permit a binary break.",
        )

    where = f"#{entry.reference}" + (f" ({state.get('title')})" if state.get("title") else "")
    url = state.get("url") or ""

    if not state.get("isPullRequest"):
        return Judgement(
            entry,
            Verdict.NOT_A_PR,
            f"{where} is an ISSUE, not a pull request. An issue's state says nothing about "
            f"whether the change has landed — #3414's entry named issue #3406 and the pull "
            f"request appeared only in prose, which is how it outlived its merge. Name the "
            f"pull request. {url}",
        )
    if state.get("merged"):
        return Judgement(
            entry,
            Verdict.EXPIRED,
            f"{where} has already MERGED. An allow entry is transitional: it is in force only "
            f"while the change it covers is unmerged, so an entry naming a merged pull request "
            f"is stale by definition and cannot be what permits this diff. {url}",
        )
    if str(state.get("state", "")).lower() != "open":
        return Judgement(
            entry,
            Verdict.ABANDONED,
            f"{where} was CLOSED without merging — the change it covered was dropped, so the "
            f"allowance never applied. {url}",
        )
    return Judgement(entry, Verdict.LIVE, f"{where} is open — the allowance is in force. {url}")


def judge(
    root: Path,
    allow_file: str,
    base: str,
    resolve,
    head: str | None = None,
    this_pr: int | None = None,
) -> tuple[list[Judgement], list[Entry]]:
    """(judgements on the introduced entries, the inert entries the base already carried)."""
    introduced, landed = partition_entries(root, allow_file, base, head)
    return [classify(e, resolve, this_pr) for e in introduced], landed


def in_force(judgements: list[Judgement]) -> dict[str, str]:
    """key -> reason, for the entries that actually allow something."""
    return {j.entry.key: j.entry.reason for j in judgements if j.allows}


# ─────────────────────────────── report ───────────────────────────────


def report(allow_file: str, judgements: list[Judgement], landed: list[Entry]) -> list[str]:
    """Lines to print. The INERT list is informational and never a failure — that is the point."""
    out: list[str] = []
    if landed:
        out.append(
            f"\n{len(landed)} allow entr(ies) in {allow_file} are already in the merge base and "
            f"are therefore INERT — they permit nothing and fail nothing here. Deleting them is "
            f"tidying, never a fix:"
        )
        for e in landed:
            out.append(f"    · {e.key}  (#{e.reference})" if e.reference else f"    · {e.key}")
    for j in judgements:
        mark = "✓" if j.allows else "✗"
        out.append(f"\n{mark} {allow_file} entry `{j.entry.key}` [{j.verdict.value}]\n    {j.detail}")
    return out


def failures(judgements: list[Judgement]) -> list[Judgement]:
    return [j for j in judgements if not j.allows]


# ─────────────────────────────── the API resolver ───────────────────────────────


def _api(url: str, token: str) -> dict:
    req = urllib.request.Request(
        url,
        headers={
            "Authorization": f"Bearer {token}",
            "Accept": "application/vnd.github+json",
            "X-GitHub-Api-Version": "2022-11-28",
            "User-Agent": "meshweaver-transitional-allow-gate",
        },
    )
    try:
        with urllib.request.urlopen(req, timeout=30) as resp:  # noqa: S310 — fixed https host
            return json.loads(resp.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        meaning = {
            401: "the token is invalid or expired",
            403: "the token is valid but lacks pull-requests:read here, or is rate-limited",
            404: "no such issue or pull request in this repository",
        }.get(e.code, f"HTTP {e.code}")
        raise AllowResolutionError(f"{url} -> {e.code}: {meaning}") from e
    except (urllib.error.URLError, TimeoutError, ValueError) as e:
        raise AllowResolutionError(f"{url} -> could not reach api.github.com: {e}") from e


def github_resolver(token: str, repo: str | None = None, fetch=_api):
    """Resolve a number in THIS repository, normally in ONE request.

    🚨 WHY `/issues/{n}` AND NOT `/pulls/{n}`. It is the only route that tells an ISSUE apart from
    a number that does not exist: `/pulls/{n}` answers 404 for both, and this gate must say which
    (`NOT_A_PR` is an author error with an obvious fix; `UNRESOLVED` is not). The issues payload
    omits the `pull_request` object entirely for an issue and, for a pull request, carries it with
    a `merged_at` field — which is exactly the discrimination the classifier needs.

    MEASURED against the live API on 2026-09-06, with the incident's own numbers:

        #3414     (merged pull request) -> expired              allows=False
        #3406     (issue)               -> not-a-pull-request   allows=False
        #99999999 (absent)              -> unresolved           allows=False   404

    …so `merged_at` is present on this route, and the one-request shape is not an assumption.
    Because that is a claim about somebody else's API, the SECOND request exists as a fallback for
    the day it stops being true: if the payload says pull request but carries no `merged_at` KEY at
    all (absent, not null — an open pull request sends the key with null), `merged` cannot be read
    and `/pulls/{n}` is asked. It never fires against today's API, and the resolver self-tests
    below prove both paths without a network. Note that even a total loss of the field could not
    make this gate PASS: `merged` false with `state` closed reads as ABANDONED, which is equally
    red — the fallback protects the MESSAGE, not the verdict.
    """
    slug = repo or os.environ.get("GITHUB_REPOSITORY") or THIS_REPO_DEFAULT

    def resolve(number: int) -> dict:
        if not token:
            raise AllowResolutionError(
                "GITHUB_TOKEN is empty, so the reference could not be read. This gate resolves "
                "the pull request an allow entry names, and refuses to pass on one it never saw"
            )
        payload = fetch(f"https://api.github.com/repos/{slug}/issues/{number}", token)
        pull = payload.get("pull_request")
        is_pr = isinstance(pull, dict)
        state = str(payload.get("state", ""))
        merged = bool(pull.get("merged_at")) if is_pr else False
        if is_pr and "merged_at" not in pull:
            # The field this route is documented to carry is gone. Ask the route that cannot
            # omit it, rather than reporting a merged pull request as an abandoned one.
            detail = fetch(f"https://api.github.com/repos/{slug}/pulls/{number}", token)
            merged = bool(detail.get("merged_at")) or bool(detail.get("merged"))
            state = str(detail.get("state", state))
        return {
            "isPullRequest": is_pr,
            "merged": merged,
            "state": state,
            "title": str(payload.get("title", "")),
            "url": str(payload.get("html_url", "")),
        }

    return resolve


def resolver_from_env(repo: str | None = None):
    return github_resolver(os.environ.get("GITHUB_TOKEN", ""), repo)


# ─────────────────────────────── self-test ───────────────────────────────
#
# 🚨 A guard whose subject moved and whose roots did not passes having checked nothing. These
# cases are therefore written against the MECHANISM (scope, then verdict), and each PASSING row
# carries as much weight as each failing one: without them a classifier that refused everything
# would score identically.


def _state(**over) -> dict:
    base = {
        "isPullRequest": True,
        "merged": False,
        "state": "open",
        "title": "the change this entry travels with",
        "url": "https://github.com/Systemorph/MeshWeaver/pull/3414",
    }
    base.update(over)
    return base


def _resolver(state: dict):
    def resolve(number: int) -> dict:
        return state

    return resolve


def _raising(message: str):
    def resolve(number: int) -> dict:
        raise AllowResolutionError(message)

    return resolve


_RAIL = "Rail  #3414 — the two order-losing buckets become one ordered sequence"

# (label, entry line, resolver, this_pr, expected verdict)
_CLASSIFIER_CASES: list[tuple] = [
    ("an entry naming an OPEN pull request is in force", _RAIL, _resolver(_state()), None, Verdict.LIVE),
    ("…a DRAFT is open too — the change has not landed", _RAIL,
     _resolver(_state(draft=True)), None, Verdict.LIVE),
    # THE #3422 SHAPE. #3414 merged at 13:38Z and its entry stayed behind.
    ("an entry naming a MERGED pull request is stale by definition", _RAIL,
     _resolver(_state(merged=True, state="closed")), None, Verdict.EXPIRED),
    ("an entry naming a CLOSED-unmerged pull request never applied", _RAIL,
     _resolver(_state(state="closed")), None, Verdict.ABANDONED),
    # What the incident's entry ACTUALLY carried: #3406 is the issue, not the pull request.
    ("an entry naming an ISSUE is refused — an issue cannot say whether the change landed",
     "Rail  #3406 — the rail loses the supplier's order",
     _resolver(_state(isPullRequest=False, state="closed")), None, Verdict.NOT_A_PR),
    ("an entry with NO reference is refused, not ignored",
     "Rail  the two buckets become one sequence", _resolver(_state()), None, Verdict.NO_REFERENCE),
    ("an entry naming a DIFFERENT pull request than the one carrying it is refused", _RAIL,
     _resolver(_state()), 3415, Verdict.WRONG_PR),
    ("…and naming THIS pull request is what passes", _RAIL, _resolver(_state()), 3414, Verdict.LIVE),
    # 🚨 The one thing the gate must never do is pass because it could not tell.
    ("an UNREACHABLE api.github.com is a red gate, never an implicit allow", _RAIL,
     _raising("could not reach api.github.com"), None, Verdict.UNRESOLVED),
    ("an EMPTY token is the same red", _RAIL,
     github_resolver(""), None, Verdict.UNRESOLVED),
]


# ─────────────────────── the RESOLVER, against canned API payloads ───────────────────────
#
# 🚨 The classifier cases above inject a resolver, so they prove the VERDICTS and say nothing
# about whether the real one reads GitHub's payload correctly. These cases close that gap: they
# drive `github_resolver` itself with a fake `fetch`, asserting both the verdict AND which URLs
# were requested — so "one request normally, two only when the field is missing" is measured
# rather than claimed, and a resolver that silently started asking the wrong route fails here.

_ISSUE_PAYLOAD = {"state": "closed", "title": "an issue", "html_url": "https://x/issues/3406"}
_OPEN_PR_PAYLOAD = {
    "state": "open", "title": "the change", "html_url": "https://x/pull/3414",
    "pull_request": {"merged_at": None},
}
_MERGED_PR_PAYLOAD = {
    "state": "closed", "title": "the change", "html_url": "https://x/pull/3414",
    "pull_request": {"merged_at": "2026-09-06T13:38:00Z"},
}
_CLOSED_PR_PAYLOAD = {
    "state": "closed", "title": "the change", "html_url": "https://x/pull/3414",
    "pull_request": {"merged_at": None},
}
# The day GitHub stops sending the field this route is documented to carry.
_PR_WITHOUT_MERGED_AT = {
    "state": "closed", "title": "the change", "html_url": "https://x/pull/3414",
    "pull_request": {"url": "https://api.github.com/repos/o/r/pulls/3414"},
}


def _fake_fetch(by_suffix: dict[str, dict], seen: list[str]):
    def fetch(url: str, token: str) -> dict:
        seen.append(url)
        for suffix, payload in by_suffix.items():
            if url.endswith(suffix):
                return payload
        raise AllowResolutionError(f"{url} -> 404: no such issue or pull request in this repository")
    return fetch


# (label, routes, expected verdict, expected number of requests)
_RESOLVER_CASES: list[tuple] = [
    ("the resolver reads a MERGED pull request off /issues in ONE request",
     {"/issues/3414": _MERGED_PR_PAYLOAD}, Verdict.EXPIRED, 1),
    ("…an OPEN one likewise", {"/issues/3414": _OPEN_PR_PAYLOAD}, Verdict.LIVE, 1),
    ("…a CLOSED-unmerged one likewise",
     {"/issues/3414": _CLOSED_PR_PAYLOAD}, Verdict.ABANDONED, 1),
    ("…and an ISSUE, which /pulls could not tell from a missing number",
     {"/issues/3414": _ISSUE_PAYLOAD}, Verdict.NOT_A_PR, 1),
    # The fallback: `merged_at` absent as a KEY. /pulls settles it, and the verdict stays EXPIRED.
    ("a payload with no `merged_at` KEY falls back to /pulls and still reports MERGED",
     {"/issues/3414": _PR_WITHOUT_MERGED_AT,
      "/pulls/3414": {"merged_at": "2026-09-06T13:38:00Z", "state": "closed"}},
     Verdict.EXPIRED, 2),
    ("…and when /pulls says it was never merged, ABANDONED — still red either way",
     {"/issues/3414": _PR_WITHOUT_MERGED_AT,
      "/pulls/3414": {"merged_at": None, "state": "closed"}},
     Verdict.ABANDONED, 2),
    ("an unknown number is UNRESOLVED, never NOT_A_PR", {}, Verdict.UNRESOLVED, 1),
]


def resolver_self_test() -> tuple[int, int]:
    """Prove the real resolver against canned payloads. Returns (failures, cases run)."""
    failed = 0
    for label, routes, expected, requests in _RESOLVER_CASES:
        seen: list[str] = []
        resolve = github_resolver("a-token", "Systemorph/MeshWeaver", _fake_fetch(routes, seen))
        got = classify(parse_entries(_RAIL)[0], resolve)
        ok = got.verdict is expected and len(seen) == requests
        print(f"  {'ok  ' if ok else 'FAIL'} {label}")
        if not ok:
            failed += 1
            print(f"         expected {expected.value} in {requests} request(s); "
                  f"got {got.verdict.value} in {len(seen)}: {seen}")
    # An empty token must never reach the network at all.
    seen = []
    resolve = github_resolver("", "Systemorph/MeshWeaver", _fake_fetch({}, seen))
    got = classify(parse_entries(_RAIL)[0], resolve)
    ok = got.verdict is Verdict.UNRESOLVED and seen == []
    print(f"  {'ok  ' if ok else 'FAIL'} an EMPTY token is refused before any request is made")
    if not ok:
        failed += 1
    return failed, len(_RESOLVER_CASES) + 1


def self_test() -> int:
    """Prove the mechanism and every verdict, with no network."""
    failed = 0

    def check(label: str, ok: bool, detail: str = "") -> None:
        nonlocal failed
        print(f"  {'ok  ' if ok else 'FAIL'} {label}" + (f"\n         {detail}" if not ok and detail else ""))
        if not ok:
            failed += 1

    # ── the classifier ────────────────────────────────────────────────────────────────────
    for label, line, resolve, this_pr, expected in _CLASSIFIER_CASES:
        entries = parse_entries(line)
        if len(entries) != 1:
            check(label, False, f"the line parsed into {len(entries)} entr(ies)")
            continue
        got = classify(entries[0], resolve, this_pr)
        check(label, got.verdict is expected, f"expected {expected.value}, got {got.verdict.value}")
        check(f"…and {expected.value} " + ("allows" if expected is Verdict.LIVE else "allows nothing"),
              got.allows is (expected is Verdict.LIVE))

    # ── the parser ────────────────────────────────────────────────────────────────────────
    check("comments and blank lines are not entries",
          parse_entries("# a comment\n\n   \n# another\n") == [])
    parsed = parse_entries(_RAIL)[0]
    check("the key is the first token", parsed.key == "Rail")
    check("the reference is the pull-request number", parsed.reference == 3414)
    check("a qualified key survives", parse_entries("MeshWeaver.Foo:MeshWeaver.Foo.Widget  #12 — x")[0].key
          == "MeshWeaver.Foo:MeshWeaver.Foo.Widget")
    check("a reference later in the reason is still found",
          parse_entries("Rail  see the plan on #99 — x")[0].reference == 99)

    # ── the mechanism: scope ──────────────────────────────────────────────────────────────
    # 🚨 THE INCIDENT, replayed end to end on a real repository. If `partition_entries` ever
    # stopped consulting the base, `introduced_after` below would be non-empty and this fails.
    import tempfile

    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        allow = "the.allow"
        env = {**os.environ, "GIT_AUTHOR_NAME": "t", "GIT_AUTHOR_EMAIL": "t@t",
               "GIT_COMMITTER_NAME": "t", "GIT_COMMITTER_EMAIL": "t@t"}

        def run(*args: str) -> None:
            done = subprocess.run(["git", "-C", str(root), *args], capture_output=True,
                                  text=True, env=env)
            if done.returncode != 0:
                raise RuntimeError(f"git {' '.join(args)}: {done.stderr}")

        run("init", "-q", "-b", "main")
        (root / allow).write_text("# header only\n", encoding="utf-8")
        run("add", allow)
        run("commit", "-qm", "base")
        base = subprocess.run(["git", "-C", str(root), "rev-parse", "HEAD"],
                              capture_output=True, text=True).stdout.strip()

        # The pull request adds the entry. It is INTRODUCED, so it is judged and is in force.
        (root / allow).write_text(f"# header only\n{_RAIL}\n", encoding="utf-8")
        introduced, landed = partition_entries(root, allow, base)
        check("the entry the diff ADDS is introduced", [e.key for e in introduced] == ["Rail"])
        check("…and nothing is inert yet", landed == [])
        judgements, _ = judge(root, allow, base, _resolver(_state()))
        check("…and it is in force", in_force(judgements) == {"Rail": parsed.reason})

        # It merges. The line is now in the base of every later pull request.
        run("add", allow)
        run("commit", "-qm", "the change plus its entry")
        merged = subprocess.run(["git", "-C", str(root), "rev-parse", "HEAD"],
                                capture_output=True, text=True).stdout.strip()

        # 🚨 The fleet-wide red: EVERY later pull request saw this line. Now it is inert.
        introduced_after, landed_after = partition_entries(root, allow, merged)
        check("once merged the entry is NOT introduced by anybody else's diff",
              introduced_after == [])
        check("…it is inert", [e.key for e in landed_after] == ["Rail"])
        judgements_after, inert = judge(root, allow, merged, _raising("must not be called"))
        check("…so nothing is resolved for it — no network, no verdict, no red",
              judgements_after == [] and [e.key for e in inert] == ["Rail"])
        check("…and it allows NOTHING, so it cannot hide the next break",
              in_force(judgements_after) == {})
        check("…and the gate reports it without failing", failures(judgements_after) == [])
        check("…and says so", any("INERT" in line for line in report(allow, judgements_after, inert)))

        # Editing the reason RE-STATES the allowance, so it is judged afresh.
        (root / allow).write_text("# header only\nRail  #3414 — a different reason\n",
                                  encoding="utf-8")
        introduced_edit, _ = partition_entries(root, allow, merged)
        check("editing a landed entry re-states it, so it is judged again",
              [e.key for e in introduced_edit] == ["Rail"])

        # An allow file absent from the base reads as empty, not as an error.
        (root / allow).write_text(f"# header only\n{_RAIL}\n", encoding="utf-8")
        introduced_new, _ = partition_entries(root, "not-tracked.allow", base)
        check("an allow file that does not exist reads as empty", introduced_new == [])

    resolver_failures, resolver_cases = resolver_self_test()
    failed += resolver_failures

    if failed:
        print(f"\n{failed} self-test(s) failed — the transitional-allow mechanism cannot be trusted.")
        return 1
    print(f"\nAll transitional-allow self-tests passed ({resolver_cases} of them over the real "
          f"resolver, against canned API payloads).")
    return 0


if __name__ == "__main__":
    import sys

    sys.exit(self_test())
