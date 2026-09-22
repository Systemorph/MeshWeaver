---
Name: Closing Keywords and Issue State
Category: Architecture
Description: GitHub binds a closing keyword to the one reference that follows it and reads nothing else — not the negation in front of it, not the qualifier behind it, not the sentence it sits in. Three pull requests closed an issue they said they were not closing, on one day; the gate that now refuses each shape, the escape that keeps it from being a wall, and what the gate still cannot see.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M9 11l3 3L22 4"/><path d="M21 12v7a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11"/></svg>
---

# Closing Keywords and Issue State

**GitHub's closing-keyword parser reads a keyword and the reference immediately after it. It reads
nothing else.** Not the negation in front of the keyword, not the possessive behind the number, not
the paragraph two lines down that says the issue stays open. A pull-request body is prose written
for people, and one substring of it is an instruction to a machine that cannot tell the two apart.

That is a latent trap in every body. On one day it fired three times in this repository.

## The three misfires

Each was confirmed against the API: `closingIssuesReferences` registered a closing link on all
three pull requests, and each issue's own timeline shows the merging app closing it within two
seconds of the merge.

| pull request | the text that fired | what happened |
|---|---|---|
| #5201 | `**Does not close #5057**` | #5057 (`sev:H`) closed at merge + 2 s; reopened by hand 2 min later |
| #5174 | `Closes #2299's classification half` | #2299 closed at merge + 1 s, against the body's own statement that it stays open for the half the change did not fix; reopened 91 min later with the reason written out |
| #5190 | `… closed #2299 against its own body's statement …` | #2299 closed a SECOND time, 57 minutes after that deliberate reopen — by a **documentation** pull request narrating the first misfire in the past tense |

Three different shapes, one mechanism:

- **#5201 is a disclaimer.** The author wrote the clearest sentence available — *does not close* —
  and the words that make it a disclaimer are the words that fired. A negation is invisible to the
  parser, so writing one is not a way of being careful; it is a way of closing the issue while
  believing you have not.
- **#5174 is a partial claim.** `Closes #N's classification half` is an accurate English sentence
  about closing *part* of something. The parser sees `Closes` `#2299` and closes the whole issue.
  The body said two sentences later that the issue stays open for the bounce half — the live,
  unfixed root then had no open record until a reader noticed.
- **#5190 is narration.** A documentation pull request describing the misfire *performed it again*.
  The same body carried `` `Closes #2299's classification half` `` inside a code span, which fires
  nothing; the occurrence that fired was the plain-prose `closed #2299` two words later. Prose ABOUT
  a close closes.

🚨 **The second and third closes landed on an issue that had been deliberately reopened with the
reason written out.** Nothing in the mechanism notices that. An issue's history is not an input to
the parser, so "somebody already decided this stays open" is worth exactly as much as the sentence
around the keyword.

## Why a wrong close is worse than it looks

The failure is **silent and inverted**. The pull request merged, the fix is on main, and the issue
reads *closed* — which is the safe-looking direction, and therefore the one nobody re-checks:

- a `sev:H` or `sev:B` issue that reads closed leaves the **release readiness count** (policy
  [`release-blocker-gate`](/Doc/Architecture/PolicyNotProse)) satisfied on evidence nobody has;
- on an issue fed by the recurrence bot it is a **churn loop** — the accidental close reads as
  fixed until the bot reopens it;
- a later triage sweep re-files work that is already done, or starts fixing it again.

## What may close on a merge, and what may not

A merge puts the fix on `main`. What a severity label gates is whether the defect is **gone from
the running portal**, which a merge cannot establish: the image still has to be built, sealed,
rolled, and the behaviour exercised against the running address. So a `sev:B` / `sev:H` issue
closes on **post-roll production verification**, never on a merge — policy
[`severity-closes-on-verification`](/Doc/Architecture/PolicyNotProse). `sev:M`, `sev:L`, `chore`, `enhancement` and
`documentation` issues carry no such obligation and close on a merge as they always did.

🚨 **A duplicate is not an exception, it is a different close.** A closing keyword closes an issue
as *completed*. An issue that is a duplicate, superseded or not-planned wants that state reason
instead — so it gets `Refs #N` in the body and a close by hand, with the comment that says where
the record moved.

## The gate

`Closing keywords (no accidental close)` — `scripts/check-closing-keywords.py`, run as its own job
on every `pull_request` and carried in the required check's `needs:`. Three checks, each with its
own message and its own remedy:

| check | fires on | remedy the message names |
|---|---|---|
| **negated keyword** | a keyword bound to `#N` with a negation within two words in front of it | `Refs #N` / `see #N` — and if you did mean to close it, delete the negation |
| **release-blocking close** | a keyword bound to an issue currently labelled `sev:H` or `sev:B` | `Refs #N` now, close it after the roll — or declare the escape below |
| **possessive reference** | a keyword bound to `#N` immediately followed by `'s` | `Fixes the <half> of #N` — the keyword is no longer before the number, so nothing closes and the sentence still reads |

🚨 **Three checks and not two, because two would have missed one of the three incidents.** Measured
at the state each pull request had AT ITS MERGE: the negation check catches #5201 alone; the
severity check catches #5201 and #5190; **#5174 is caught by neither**, because #2299 was labelled
`sev:M` when it merged and was relabelled `sev:H` only after the reopen, 91 minutes later. The
possessive check is what catches it, and it is the narrowest of the three.

### What the gate mirrors, and what it deliberately does not improve on

The detector reproduces the parser rather than correcting it, because a gate that is *cleverer*
than the thing it guards produces verdicts an author cannot act on:

- **a keyword binds ONE reference.** `Closes #A, #B` closes #A only, so each reference is reported
  on its own and a list is never assumed to have been understood;
- **a keyword inside a code span or a fence fires nothing** — measured: #3018 merged with
  `` `Fixes #2897` `` and #2897 stayed open. Both are stripped before the scan, which is also what
  lets this page and the gate's own docstring quote the syntax without firing;
- **a keyword anywhere else fires** — headings, tables, future tense, past-tense narration, a
  disclaimer;
- **`owner/repo#N` closes in that repository.** The gate reads its own repository's issues only; a
  cross-repository reference is named in the run output and label-checked nowhere. That limit is
  printed on every run rather than being silent.

### The escape

A pull request that legitimately closes a `sev:H`/`sev:B` — because the verification has already
happened — declares it in the body, per issue:

```
Verified-closing: #5057 — verified on the rolled portal: the release node now names the
attempted id, and the pre-fix wording has not recurred in ten minutes of logs.
```

The shape is the house's declaration idiom (`Pairs-with:`, `Implementers:`, `Mirror-sync:`) and its
spirit is [Transitional Allow Entries](/Doc/Architecture/TransitionalAllowEntries): it names exactly what it releases
and it carries a reason.

- 🚨 **It releases the severity check ONLY.** It cannot release a negation — a body that says it
  does not close the issue and a declaration that says the close is verified are two statements
  that cannot both be true, and the remedy is to delete the negation rather than out-vote it. It
  cannot release a possessive either: rewording costs four words and says what the author meant.
- 🚨 **It does not close anything.** `Verified-closing:` contains no closing keyword, so a body
  carrying only the declaration closes nothing. The declaration says the close is *allowed*; a
  plain `Closes #N` still performs it. An escape naming an issue the body does not close is
  therefore **refused as stale**, not ignored — that is the `Closes #A, #B` trap wearing a
  different hat, and an escape nobody can act on is the kind of line that rots into a template.
- An escape for an issue the body closes that turns out to carry neither label is **inert**: it is
  printed and it passes. Labels move — #2299 moved between two of these incidents — and refusing a
  declaration because the label changed while the pull request was open would red a race.
- 🚨 **What it cannot check is whether the verification happened.** No regular expression can
  adjudicate that, and one that pretended to would be worse than none: it would teach authors which
  words to type. The escape's contribution is to make the close DELIBERATE, PER-ISSUE and
  REVIEWABLE — the run prints the reason beside the label the issue carried when the gate read it,
  so the claim is on the record for the reviewer and for whoever reads the run afterwards.

### No skip-trapdoor

The severity check reads live labels, so it has an external input, and it is wired the way
`AGENTS.md` requires of one:

- the self-test runs FIRST and fails the job — an unproven gate is no gate;
- a **preflight** step asserts the input and fails RED naming what to provision. It does not ask
  whether a token is set: it proves the token can read, with a positive control
  (`repos/{o}/{r}/labels/sev:H` and `sev:B` both resolve) and a **negative** control
  (`labels/sev:DOESNOTEXIST` must 404). Both halves matter — an unknown label folds a label query
  to green, which is the failure mode the readiness gate already records;
- no `continue-on-error:`, no path filter, and no `if:` asking whether a secret is set. The only
  exemption is on the EVENT — `merge_group` carries no pull-request body, and a pull request cannot
  reach the queue without this job green;
- an unresolvable reference is **Undecidable** and reds, naming why. "Could not tell" and "closes
  nothing" are never one colour;
- the body is re-read through REST at run time rather than taken from the frozen event payload, so
  a rerun sees the corrected body.

It needs no secret: `GITHUB_TOKEN` reads this repository's issues, so the gate also runs on fork
pull requests.

### Base rate

Measured over the **99 merged pull requests** in the two days to 2026-09-22T11:17Z: **16** carry a
closing keyword bound to a reference at all, and **7** would be red — the three incidents above,
plus four more that each closed a `sev:H` issue on merge (#4973→#1172, #4984→#4761, #5070→#4722,
#5183→#5177). The other nine close `sev:M`, `sev:L`, `chore`, `enhancement` or unlabelled issues
and pass. **Zero false positives** over those 99 real bodies.

So this gate is not a rarity like `Implementers:` — it meets roughly one merge in fourteen, and six
of those seven are the defect rather than the gate being noisy: six release-blocking issues were
taken out of the readiness count by a merge in two days.

### The self-test, and its control

`check-closing-keywords.py --self-test` classifies **52 bodies** (29 red, 23 green) — the three
real bodies as the API returned them, every negation and possessive shape, the `Closes #A, #B`
trap, a keyword in a code span / a fence / an HTML comment, `Refs #N`, a body with no reference at
all, and both arms of every escape rule.

🚨 **A self-test that passes against a detector which has stopped detecting proves nothing**, so
four **neutered-detector controls** run afterwards, each disabling exactly one arm — the negation
function, the severity label set, the possessive group, the escape's refusals. The assertion is
that the case list then goes RED. A neutered arm that still passes every case means the cases do
not cover it, and the self-test fails saying so.

## What this does not establish

- **Whether the parser reads a keyword inside a BLOCKQUOTE.** Not measured. The gate scans quoted
  lines, which is the fail-closed direction: a quoted `Closes #N` reds the gate and is reworded,
  which is cheap, while skipping them would let a real close through if the parser does read them.
- **Whether a closing keyword can close a PULL REQUEST.** The gate resolves every reference and
  reports a pull-request target as closing nothing, without firing.
- **Whether the escape's claim is true.** Stated again here because it is the gate's one soft
  edge: the declaration is checked for being explicit, per-issue and reasoned, never for being
  correct.
- **The gate reads labels at PULL-REQUEST time, and a label can move before the merge.** An issue
  relabelled `sev:H` after the last run of this gate merges unrefused. That is the #5174 timeline
  exactly, one step later — and closing it would mean re-reading every reference at merge time,
  which is a different mechanism from a pull-request check.

## Related

- [Policy Not Prose](/Doc/Architecture/PolicyNotProse) — the register that carries
  `severity-closes-on-verification` and `release-blocker-gate`
- [Issue Taxonomy and the Release Readiness Gate](/Doc/Architecture/IssueTaxonomy) — what a severity label means and
  what it gates
- [Transitional Allow Entries](/Doc/Architecture/TransitionalAllowEntries) — the escape idiom this one follows
- [The Cross-Repo Pair Gate](/Doc/Architecture/CrossRepoPairGate) — the sibling declaration gates
  (`Pairs-with:`, `Implementers:`)
- [Log Watch Triage](/Doc/Architecture/LogWatchTriage) — where the #5174 trap was first written down, one sentence
  long
