---
nodeType: Markdown
name: Policy Not Prose
category: Architecture
description: Never hard-code a decision's date or its author into source, a comment or doc prose. A policy is a record with a value and an in-force date; everything else links to it. Includes the register, and the review rule that enforces it.
icon: "<svg viewBox='0 0 24 24' xmlns='http://www.w3.org/2000/svg'><rect width='24' height='24' rx='4' fill='#5319e7'/><path d='M7 6h10M7 10h10M7 14h6' stroke='white' stroke-width='1.8' stroke-linecap='round'/><circle cx='16.5' cy='16' r='3' fill='none' stroke='white' stroke-width='1.6'/></svg>"
---

# Policy Not Prose

> **Never hard-code a decision's DATE or its AUTHOR into committed source, a comment, an XML doc
> comment, or documentation prose. State the rule; link to the policy record. The record carries the
> date and the name — once.**

A rule written as *"Maintainer, 2026-09-07: do it this way"* embeds three different things in one
sentence: the rule, when it took effect, and who decided. Only the first belongs in the place you
are reading. The other two are **data about the rule**, and prose is the worst available store for
them.

## Why

**Prose copies drift.** The same decision gets restated in an `AGENTS.md`, two doc pages and a
skill. Change the decision and you must find all four; you will find three. The fourth goes on
instructing people for months, and it is indistinguishable from the live ones because they all look
equally authoritative.

**Names go stale faster than rules.** People change roles, hand over areas, and leave. The rule
outlives the attribution, so a name in a comment converts a durable instruction into something that
reads as gossip about a decision nobody can now ask about. It also invites the wrong question —
*"is this still true, or just what someone said once?"*

**Prose cannot be queried.** *"Which policies are in force, and since when?"* is a reasonable thing
to ask a system. If the answers live in sentences scattered across repos, there is no answer — only
a grep and a guess. A register answers it in one read.

## The shape

A policy is a record with five fields and a link:

| field | what it is |
|---|---|
| **id** | a stable slug — what other pages cite |
| **value** | the decision itself, in as few words as carry it |
| **status** | `in force`, or `proposed` when it was cited before it existed — the field the self-healing step below turns on |
| **in force since** | the date it started applying |
| **set by** | the role or identity that set it — *a role where one exists* |

Everything else — source, comments, AGENTS.md, docs, skills — states the rule and cites the **id**.
No dates, no names.

```diff
- # A What's New entry is written per RELEASE, not per change (maintainer, 2026-09-20)
+ # A What's New entry is written per RELEASE, not per change — policy `whatsnew-cadence`
```

## 🚨 What this does NOT forbid

This rule is about **policy markers and attributions**, not about dates as such. A date that is
*evidence* stays exactly where it is:

- a measurement — *"the folder reached 1,292 entries, 662 in August"*
- an incident — *"on 2026-09-19 the observation window lapsed and nothing wrote a terminal state"*
- a historical record — a migration note, a retired mechanism, a changelog entry

Those are **facts about events**, and an event without its date is not a fact. The test is simple:
*would this date need changing if the decision changed?* If yes, it is a policy marker and belongs in
the register. If no, it is evidence and belongs where it is.

## For review

**A diff that introduces a hard-coded policy date or a person's name into source, a comment, an XML
doc comment or doc prose is a review finding.** The fix is never to delete the information — it is to
move it: add or cite a register entry, and leave a link behind.

### The finding is self-healing — it never blocks

A reviewer who finds a hard-coded date or name does not hand back a chore. They run a two-step that
always terminates:

1. **Look for a policy that already covers it** in the register below. Found → replace the prose
   with the citation. Done, no issue, one line changed.
2. **Not found → file an issue to introduce the policy**, add a `proposed` row to the register
   naming that issue, and cite it in place.

**The citation is written first and the policy catches up.** That is the whole trick: the text is
never blocked on the policy existing, and the reviewer is never asked to settle a policy question
in a review thread — which is the thing that turns a two-line finding into a three-day argument.
Because step 2 adds the register row immediately, a citation never dangles; what is pending is the
*ratification*, not the reference.

This is why the rule converges without a migration. Nobody sweeps the corpus, but every page that
gets edited for its own reasons leaves behind one more citation and, where it was missing, one more
policy. The register fills from actual traffic — the decisions people actually touch — rather than
from an archaeology project, and the pages nobody edits are, by definition, the pages nobody is
misled by.

> File the issue and carry on. Do not stop the change you came to make in order to ratify a policy
> you just discovered was implicit — that is the incidental-findings rule, and it applies here
> exactly as it does everywhere else.

**This is the hamster wheel, applied to policy.** Triage → implement → test, with every step filing
what it finds back into triage rather than absorbing it: the same loop, and the same reason. A
change that stopped to ratify every implicit policy it brushed against would land late, review
badly, and abandon what it set out to do. The exit has to be cheap or the wheel stops turning — so
the exit here is one register row and one issue, and then you carry on with what you were doing.

### 🚨 Forward-only. No backward migration.

**This rule applies to what is WRITTEN FROM NOW ON. It is not a licence to sweep the existing
corpus, and a pull request whose purpose is to retrofit old pages is out of scope.**

The existing prose is not a defect. It records decisions that were taken and communicated the way
the house wrote at the time, it is accurate, and rewriting it would touch a large number of pages to
change nothing a reader relies on — while burning the review attention that new work needs. A
migration would also be the more dangerous edit, because mechanical rewriting of attributions is
exactly the kind of change that quietly alters meaning in the one page nobody re-reads.

So a reviewer checks the **direction of travel**: does this diff ADD a hard-coded policy date or
name? If it does, that is the finding. If it merely fails to remove existing ones, that is not.
Old pages migrate only when they are being edited anyway for their own reasons, and only the lines
already being touched.

## The register

A row is **`in force`** once ratified, or **`proposed`** when a reviewer cited it before it existed —
a proposed row names the issue that will settle it, so the citation resolves from the moment it is
written.

| id | value | status | in force since | set by |
|---|---|---|---|---|
| `whatsnew-cadence` | A What's New entry is written per RELEASE, not per change. A merge updates its doc page and mints no dated file. | in force | 2026-09-20 | maintainer |
| `issue-taxonomy-scope` | Classification covers OPEN issues only. Closed issues are not classified, not counted, and appear in no query. | in force | 2026-09-20 | maintainer |
| `release-blocker-gate` | A release may not be cut while any `sev:B` or `sev:H` bug is open in the seven repositories that carry the taxonomy; `sev:M` and `sev:L` never gate a cut. Enforced by the `release.cut` standard in the Governance package. | in force | 2026-09-20 | maintainer |
| `data-sync-approval` | Adding or widening the synchronisation of data needs a global admin's approval. | in force | 2026-09-20 | maintainer |
| `version-shapes` | Exactly two version shapes: `X.Y.Z-ci.<n>` and clean `X.Y.Z`. No rc, preview or labelled line is ever minted. | in force | 2026-09-07 | maintainer |

Cited by: [Release Process](../ReleaseProcess) ·
[Issue Taxonomy and the Release Readiness Gate](../IssueTaxonomy) ·
[Adding a Data Sync Needs a Global Admin](../DataSyncApproval).

> 🚨 **A `proposed` row is not a weaker `in force` — it is an honest one.** The first draft of this
> register listed `release-blocker-gate` as `in force` while the standard that enforces it was still
> an unmerged pull request; the row was moved to `proposed` naming what was owed, and back to
> `in force` only once that standard had merged. That is the precise failure this page exists to
> prevent, committed in the page that defines the rule. If a row's mechanism does not exist yet, the
> row says `proposed` and names what is owed.

> Adding a policy here is cheap and reversing one is cheap. That is the point: a register entry can
> be changed in one place and every citation follows, which is exactly what a sentence copied into
> four files cannot do.
