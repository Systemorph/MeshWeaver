---
Name: Transitional Allow Entries
Category: Architecture
Description: An allow entry in a binary-compatibility ratchet is written for exactly one merge — and the pull request that adds it is, by construction, the one whose merge makes it stale. Why the deletion had to stop being an instruction to a future reader, and the two rules (scope, then expiry) that make it a mechanism.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="9"/><polyline points="12 7 12 12 15 14"/></svg>
---

# Transitional Allow Entries

**An instruction to a future reader is not enforcement.** Two of this repository's gates —
`scripts/check-record-signatures.py` and `scripts/check-type-forwards.py` — take a tracked allow
file, and every entry in one is *transitional*: it says a binary break is deliberate and the atomic
move is planned, which is true only while the change carrying it is unmerged. Both files told the
author to delete the line afterwards. That instruction was the defect.

## What it cost, measured

`#3414` added one line to `scripts/record-signatures.allow`:

```
Rail  #3406 — SuppliedNavigationRail.Rail: the two order-losing buckets become one ordered
Items sequence. … DELETE THIS LINE in the change right after #3414 merges.
```

It merged at **13:38Z on 2026-09-06**. Nobody deleted it. From that minute, every pull request in
the fleet whose diff touched any `.cs` file reported:

```
✗ scripts/record-signatures.allow lists `Rail`, but its primary constructor no longer differs.
🚨 0 binary-breaking record change(s), 1 stale allow entr(ies).
```

`#3415`, `#3418` and `#3420` went red on it; **three were dequeued by the merge-queue steward**.
`main` itself read green throughout, because its own run of the gate was reuse-skipped — so the
signal pointed at everyone except the commit that caused it. `#3421` removed the line by hand,
about forty minutes later. `scripts/type-forwards.allow` had produced the identical incident
earlier.

Two details make this a design problem rather than a lapse:

* **The pull request that adds the entry is the one whose merge makes it stale.** The author is
  asked to remember something at precisely the moment they stop looking at it.
* **Staleness was a property of the OBSERVER, not of the entry.** The old rule was "listed, but
  nothing of that name differs *in this diff*" — which is true for every pull request in the world
  except the one that carried the change. That is why one line reddened a fleet.

## The mechanism: an allowance is scoped to the diff that introduces it

`scripts/transitional_allow.py` holds both halves, and both gates use it.

**An entry is in force only in the diff that ADDS it.** The line must be absent from the allow file
at the merge base and present at HEAD. An entry the merge base already carries has LANDED, and is
**inert**: it permits nothing and it fails nothing.

That is the fix, and it is a mechanism rather than a reminder, because *merging is exactly the
moment the line stops being an addition*. There is no line for anyone to forget to delete, and no
second issue to remember.

It also **closes** — rather than relocates — the danger the old stale ratchet existed for. That
ratchet's stated reason was that "an entry which outlives its change hides the next break", and it
was right: a landed entry kept suppressing findings for its key, so the *next* break on that record
would have passed silently. An inert entry suppresses nothing. A later break on the same record
fails exactly as if the line had been deleted. The protection is kept; only its blast radius —
everybody else's pull request — is gone.

The merge queue needs no special case. A `merge_group` build compares `main` against the queue
branch, so a queued pull request's entry is still an addition there.

| Where the entry is | What it does | What it fails |
|---|---|---|
| added by this diff, reference OPEN | permits its key | nothing |
| added by this diff, permits nothing | — | **red**, on its own author's pull request |
| added by this diff, reference not open/unreadable | — | **red**, naming which |
| already in the merge base (landed) | nothing | nothing — reported as inert |

## The classifier: an entry names the pull request it is transitional for

Scoping alone would still accept an entry that was dead on arrival — copied from an older change,
or written against a pull request that has already landed. So the format is machine-readable and
the reference is resolved:

```
<key>  #<pull request> — <reason>

Rail  #3414 — the two order-losing buckets become one ordered sequence …
```

🚨 **This is not a tightening of an existing convention.** The number in the incident's entry was
`#3406` — the *issue*. The pull request appeared only in prose, which is precisely how it outlived
its merge. An entry that names an issue is now refused by name: an issue's state says nothing about
whether the change has landed.

| The reference resolves to | Verdict | Gate |
|---|---|---|
| an OPEN pull request (draft included) | `LIVE` | the allowance stands — this is the entry's purpose |
| a MERGED pull request | `EXPIRED` | **red** — stale by definition; the change it covers has landed |
| a CLOSED, never-merged pull request | `ABANDONED` | **red** — the change was dropped |
| an ISSUE | `NOT_A_PR` | **red** — name the pull request |
| nothing (`#<n>` absent) | `NO_REFERENCE` | **red** — never an ignored line |
| a pull request other than the one under test | `WRONG_PR` | **red** (see below) |
| an API that would not answer | `UNRESOLVED` | **red** — see the next section |

`WRONG_PR` applies only where the gate knows which pull request it is judging — the
`pull_request` event. A `merge_group` build has no single pull request (the queue ref can carry
several), so the weaker rule applies there, and nothing reaches the queue without this job having
been green on the `pull_request` event first: it is a `needs:` of `Consolidate test results`, the
one required check.

## "Could not tell" never renders as "allowed"

🚨 There is deliberately **no verdict that passes on evidence the gate could not read**. An empty
token, a 401, a 403, a 404, a timeout, an unreachable `api.github.com` — every one of them is
`UNRESOLVED`, and `UNRESOLVED` is red. This is the same rule as
[a gate never testing its own inputs](/Doc/Architecture/ReadingCiSignals): GitHub paints a skipped
job with the same tick as a passed one, and an implicit allow on an unreadable reference would be
that defect wearing a different hat — one that permits a binary break rather than merely hiding a
missing check.

## The network dependency, weighed rather than added silently

This is the first API call in the *Public surface (binary compatibility)* job, whose workflow
carries — deliberately — no secret and no preflight job. Three things keep it from becoming the
shape that note rules out:

1. **The pull request is in THIS repository**, so the automatic `GITHUB_TOKEN` with
   `pull-requests: read` answers it. No App token, no organisation secret, nothing to provision and
   nothing that can be missing. (Contrast [the cross-repo pair gate](/Doc/Architecture/CrossRepoPairGate),
   which needs an installation token *only* because it reads **other** repositories' pull requests,
   and therefore needs the credential assertion this one does not.)
2. **The call happens only when the diff introduces an entry.** Both allow files hold zero entries
   today and gain roughly one a year, so an ordinary pull request makes no request at all and
   behaves byte-for-byte as it did before. The gate's availability is not coupled to GitHub's API
   except on the rare diff that is asking for an exemption.
3. **An unreachable API is red.** The dependency can cost a re-run; it can never cost a silent pass.

## Proving it, and the falsification

Both scripts run `--self-test` as a step of their own before any verdict, and both self-tests
drive the real entry points — `run()` and `check()` — against **real git repositories**, because
"already in the merge base" is a fact about history and cannot be expressed as a fixture of file
contents. Ten cases each, in both directions:

```
ok   a break with no allow entry FAILS                       ← the control arm
ok   …with an entry naming an OPEN pull request it PASSES
ok   …naming a MERGED pull request it FAILS as stale
ok   …naming a CLOSED-unmerged pull request it FAILS
ok   …naming an ISSUE it FAILS
ok   …with an UNRESOLVABLE reference it FAILS rather than passing
ok   …naming a DIFFERENT pull request than the one under test it FAILS
ok   …and naming THIS pull request it PASSES
ok   a LATER pull request is NOT reddened by the landed entry ← the incident
ok   …and the landed entry does NOT hide the next break on the same key
```

The last two are the pair that cannot both be satisfied by accident: if the gate stopped consulting
the mechanism, or went back to reading the allow file whole, the *inert* case would go red and the
*live* case would go green. A guard whose subject moved and whose roots did not passes having
checked nothing; these roots move with it.

Those cases inject a resolver, so they prove the *verdicts* and say nothing about whether the real
one reads GitHub's payload correctly. Eight further cases drive `github_resolver` itself with a
fake `fetch`, asserting the verdict **and which URLs were requested** — so "one request normally"
is measured, not claimed.

### Why `/issues/{n}` and not `/pulls/{n}`

`/issues/{n}` is the only route that tells an **issue** apart from a number that **does not
exist**: `/pulls/{n}` answers 404 for both, and this gate must say which — `NOT_A_PR` is an author
error with an obvious fix, `UNRESOLVED` is not. The issues payload omits the `pull_request` object
entirely for an issue and, for a pull request, carries it with `merged_at`. Measured against the
live API with the incident's own numbers:

```
#3414     (merged pull request) -> expired              allows=False
#3406     (issue)               -> not-a-pull-request   allows=False
#99999999 (absent)              -> unresolved           allows=False   404
```

Because that is a claim about somebody else's API, a second request exists as a fallback for the
day it stops holding: a payload that says *pull request* but carries no `merged_at` **key** —
absent, not null; an open pull request sends the key with `null` — falls through to `/pulls/{n}`.
It never fires against today's API, and both paths are covered by the resolver self-tests. Note
that even a total loss of the field could not make this gate *pass*: `merged` false with `state`
closed reads as `ABANDONED`, which is equally red. The fallback protects the **message**, not the
verdict.

## What this does not do

* It does not stop a landed entry from **accumulating**. An inert line is litter, not a defect —
  deleting one is tidying, and the gate reports the inert set on every run so the tidying is
  obvious rather than remembered.
* It does not judge whether the *reason* is true. "No shipped module can hold this TypeRef" is
  still an attributable human statement, exactly like a `Pairs-with: none — …` waiver.
* It does not extend to the other allow files in the repository (`doc-gate.allow`,
  `workflow-shell.allow`, `samples-gate.allow`). Those are **ratchets** — a bounded backlog that
  only shrinks — not transitional statements attached to one merge, so their entries are meant to
  outlive the pull request that adds them.

## See also

- [The Cross-Repo Pair Gate](/Doc/Architecture/CrossRepoPairGate) — the same "one repo's merge reds
  another's trunk" class, and the precedent for resolving a declared pull request through the API
- [Reading CI Signals](/Doc/Architecture/ReadingCiSignals) — why a skipped gate and a passed gate
  look identical, and what a required context actually attests
- [Controls That Cannot Fail](/Doc/Architecture/ControlsThatCannotFail) — the wider family of
  verifications that could not have gone red
- [The Merge Queue](/Doc/Architecture/MergeQueue) — the steward that dequeued three pull requests
  over this one line
