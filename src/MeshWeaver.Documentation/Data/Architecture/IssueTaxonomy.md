---
nodeType: Markdown
name: Issue Taxonomy and the Release Readiness Gate
category: Architecture
description: Four axes on every issue — type, area or plugin, feature, and a severity (blocking ones for bugs only) — and the one of them that is a release gate. sev:B and sev:H must both be zero to cut a release; sev:M and sev:L are a priority conversation, not a gate.
icon: "<svg viewBox='0 0 24 24' xmlns='http://www.w3.org/2000/svg'><rect width='24' height='24' rx='4' fill='#b60205'/><path d='M12 6v7' stroke='white' stroke-width='2.2' stroke-linecap='round'/><circle cx='12' cy='17' r='1.4' fill='white'/></svg>"
---

# Issue Taxonomy and the Release Readiness Gate

A queue you cannot route is a queue you cannot finish. Before this taxonomy an open issue carried a
type at best, so the two questions that actually drive work — *what is still open for this plugin?*
and *what is still open for this feature?* — could only be answered by reading every title. And the
question that gates a release — *is anything blocking?* — could not be answered at all.

Every **open** issue now carries up to four labels (closed issues are out of scope — policy
[`issue-taxonomy-scope`](../PolicyNotProse), see the last section). Three are routing. One is a gate.

---

## The four axes

| axis | label | applies to | purpose |
|---|---|---|---|
| **Type** | `bug` · `enhancement` · `documentation` · `chore` | everything, exactly one | what kind of work |
| **Area** *(core)* | `area:<subsystem>` | MeshWeaver | which part of the framework |
| **Plugin** *(modules)* | `plugin:<Partition>` | plugin repos | which module owns it |
| **Feature** | `feature:<slug>` | everything with a specific subject | the thing itself, across repos |
| **Severity** | `sev:B` · `sev:H` · `sev:M` · `sev:L` | everything, exactly one — `sev:B`/`sev:H` on a **`bug` only** | how bad, or how much it matters |

`chore` means CI, build, dependencies or cleanup with no user-visible behaviour change.

**`feature:` is the axis that crosses repositories.** A defect in core and the plugin change that
depends on it share a feature slug and nothing else; that shared slug is the only way to see them
as one piece of work. It also exposes duplication that titles hide — the first pass found six
separately-filed issues that were one `RoutingGrain` back-pressure defect, one per activation.

---

## Severity, and why only bugs can block

| label | meaning |
|---|---|
| **`sev:B`** | **BLOCKING — the release cannot ship while this is open** |
| **`sev:H`** | **BLOCKING — a primary path broken but a workaround exists; an intermittent user-visible failure; or silently WRONG results anywhere** |
| `sev:M` | a secondary path broken, or a clear defect with an easy workaround |
| `sev:L` | cosmetic, a rare edge case, or developer-only |

**Every open issue carries exactly one severity — an `enhancement`, `documentation` or `chore`
included** (policy [`every-issue-has-a-severity`](../PolicyNotProse)). For a non-bug the severity is a
priority, and only the two non-blocking values are allowed: **`sev:M`** for work that matters soon,
**`sev:L`** for the rest. **`sev:B` and `sev:H` are reserved for defects.** If something is not broken
it cannot block a release, and letting a feature request carry a blocking flag is how a release gate
rots into a wish-list — so a blocking severity on a non-bug is a taxonomy violation, not a stronger
opinion. Carrying a severity on every issue is what lets one query order the whole queue; carrying a
blocking one only on bugs is what keeps the gate a gate.

`sev:B` and `sev:H` are the classes where *"we will do it next sprint"* is not an available answer
— policy [`release-blocker-gate`](../PolicyNotProse). `sev:M` and `sev:L` are a priority conversation and
never gate a cut.

---

## The gate

```
label:bug  label:sev:B  state:open   →   must be ZERO
label:bug  label:sev:H  state:open   →   must be ZERO
```

across the **seven repositories** that carry the taxonomy: `MeshWeaver`, `MeshWeaver.Plugins`,
`MeshWeaver.Crm`, `MeshWeaver.SocialMedia`, `MeshWeaver.Reinsurance`, `MeshWeaver.Manufacturing`,
`MeshWeaver.Education`. `Memex` and `MeshWeaver.Feedback` are deliberately not gated: they ship no
product code.

> 🚨 **Name the repositories; never write "every repo of the product".** A repo that is not on the
> list is not gated, and — worse — a repo on the list that has never had the `sev:B` label *created*
> answers a label query with an empty array, which folds to "no open issues" and is **green
> forever**. That was live: the gate named seven repositories and the label existed in five —
> which is why `NoOpenIssues` now refuses a label that does not exist instead of folding it to
> Green (MeshWeaver.Plugins#2190).

That is the whole release-readiness predicate — policy [`release-blocker-gate`](../PolicyNotProse) —
and it is enforced rather than remembered: the `release.cut` standard in the Governance package
requires `Gate.NoOpenIssues(repo, "sev:B")` **and** `Gate.NoOpenIssues(repo, "sev:H")` for each of
the seven gated repositories — 14 gates — so a release proposal cannot reach `Ready` while one is
open. The pattern it uses is the long-running check (`get Governance/Skill/long-running-check`); see
[Release Process](../ReleaseProcess) for what a release then is.

What takes an issue OUT of that count is equally a rule, because a close is what turns the gate
green: a `sev:B` / `sev:H` issue closes on **post-roll production verification**, never on the merge
that lands its fix — policy [`severity-closes-on-verification`](../PolicyNotProse). Where the event
that would exercise the fix cannot be provoked, it closes on the fix merged, a merged regression test
that fails without it, and the fix running on the affected instance(s), named together in the
closing comment with *reopen if it recurs*; and an issue waiting only on the owner's hand action
closes with that step on the owner's checklist. The routes and the gate that refuses a merge-close
are in [Closing Keywords and Issue State](../ClosingKeywordsAndIssueState).

### Two rules that keep the gate honest

**1. A blocking severity is never assigned by guess — and never REMOVED to clear the gate.** If you
cannot prove from the evidence that a defect blocks the release, assign the severity you *can* justify
and say why. 🚨 Raising the bar to `sev:H` creates a pressure the `sev:B`-only gate never had: the
cheapest way to a green gate is now to downgrade an `H` to `M`. That is the one move this gate cannot
survive, so `release.cut`'s acceptance criterion names it — *not relabelled or DOWNGRADED to sev:M to
clear the gate*. A gate that fills with
defensive B's stops being a gate — people start shipping past it, and then the one real B ships
too. Under-calling is recoverable because someone raises it; over-calling destroys the signal.

**2. Re-run the query, and check its coverage.** A zero means *either* "nothing blocking" *or*
"truncated, unanchored, rate-limited, or answered from a subset". Those are indistinguishable from
the count alone, and this is the one place where a false zero ships a known blocker. Read the count
against the coverage; treat a refusal as an error, never as a pass.

### What this gate does NOT decide

It says nothing about whether the artifacts exist. *"Is every package available for this release?"*
is a different predicate with its own page —
[Release Availability Gates](../ReleaseGates) — and the two are complementary: one asks whether the
product is **built**, this one asks whether it is **fit to ship**. A release needs both, and
neither substitutes for the other.

It also does not restate what `release.yml` already refuses at tag push (an unsealed set, an
unpromoted commit, a mismatched `PlatformVersion`, missing release notes). A gate that duplicates
the lane is a second source of truth that will drift.

---

## Where the tracking lives

**GitHub is the ledger — the one place an issue is WRITTEN. The mesh reads it, and adds what GitHub
cannot hold.**

| | holds | written by |
|---|---|---|
| **GitHub issues** | *what must be done* — the labelled queue, and the gate query | people and triage |
| **`GitHubIssue`** at `{space}/_Issue/{number}` | a **one-way mirror** of the ledger, refreshed by sync and by webhook | `system-security` |
| **`Feedback/Feedback`** | *intake* — a finding as it arrives, before it is a work item | agents, users |
| **`Hosting/TriageItem`** | *what is being done* — the thread a defect is worked in, and what came of it (`threadPath`, `status`, `issueUrl`) | the triage agent |

🚨 **The mirror already exists and is one-way by design.** `MeshWeaver.GitSync`'s `IssueService`
materialises every issue as a `GitHubIssue` node under the space's `_Issue` satellite, refreshed by
an explicit sync and by a live webhook. **The node is never edited directly**: a mutation runs
against GitHub and the mirror re-syncs. So there is exactly one writer, and none of the
bi-directional problems a two-way mirror would bring.

That one-way direction is the load-bearing part. Making the mirror writable would put two writers
over one set of rows for no gain — PRs close GitHub issues natively, and a `NoOpenIssues` gate reads
GitHub directly, so nothing downstream needs the copy to be authoritative.

### How an issue reaches triage

An issue opened straight on GitHub reaches the triage agent by two routes, both on the control
instance, and both limited to the same **triage scope** (`Hosting:Triage:Repositories`: the active
fleet repositories by default; the control instance's record declares it). An issue in any other
repository is counted and logged, and gets no item and no thread.

- **The organisation Issues webhook** delivers `opened`/`reopened` to the control instance's inbox.
- **The periodic sweep** (`TriageIssueSweep`) lists every OPEN issue in scope that is missing a
  classification — including one that already existed, and one that lost its `sev:` or type label
  later, which the webhook deliberately ignores.

Each issue becomes ONE item, `Hosting/Triage/issue/{repo}-{number}`, whichever route found it
(`foundBy`). The mechanism, the settings and the idempotency rule are in MeshWeaver.Plugins'
`Hosting/Triage` (`get Hosting/Triage`). A finding an agent makes goes in by the third route,
`Feedback/Feedback` (AGENTS.md, "file it and move on"), never as a hand-opened issue.

### How "always classified" is held — by construction, not by memory

Every route an issue can take into an open repository either classifies it or NAMES why it could not.
None leaves the classification to a model, a person or an agent remembering.

| route | what makes the type and severity certain |
|---|---|
| a **sentinel workflow** opens its own issue | it stamps type + severity itself (table below); a missing label is created first, and a sentinel it reopens is stamped too unless a person already classified it (`scripts/check-issue-taxonomy.py stamp`) |
| the **red-main ledger** (`ci-main-red`, every satellite) | `.github/scripts/ci-failure-ledger.py` stamps `bug` + `sev:H` on the issue it files, reopens or appends to |
| an **agent** files through MCP `git_hub_issue` | the tool refuses a `bug` without exactly one `sev:B/H/M/L` and any other `sev:` — and, predating `every-issue-has-a-severity`, still refuses a severity on a non-bug (MeshWeaver.Plugins `McpMeshPlugin`; the triage classifier likewise drops one), so a non-bug filed there reaches the daily check without its `sev:M`/`sev:L` |
| triage **files** a defect (`FileIssue`) | refused without exactly one severity |
| the **log-incident filer** | stamps the default `sev:M` when its policy proposes none |
| a person or an agent opens one **directly** | the webhook and the hourly sweep classify it: the model is asked ONE question and the answer is validated in code — a bug without a valid severity is never applied; it is re-asked ONCE naming the defect, and a second invalid answer is marked **UNCLASSIFIED — needs a person** on the item and listed on `Hosting/Triage/blocking/issues-need-a-person` |

What triage cannot complete is always a person's, and always listed on that one finding: labels that
CONTRADICT the taxonomy (triage only adds, it never removes a type or a severity), an author with no
write access to the repository (a stranger's text reaches no model), an answer that stayed invalid,
a model call that kept failing, a label the repository lacks.

🚨 **Trust is write access, not `author_association`.** GitHub reports a private organisation member to
a webhook and to an App as `CONTRIBUTOR`. Triage once trusted `OWNER`/`MEMBER`/`COLLABORATOR` alone, so
every issue a maintainer (or an agent under a maintainer's credential) opened was HELD as a stranger's
and never classified. Triage now reads the author's permission on the repository through its App and
trusts `write`, `maintain` and `admin`; anything else, and any lookup that fails, stays held.

A model call that failed — the EU-only pool down, a provider error — is never a person's on the first
miss and never retried in a loop: it is made again when the model has ANSWERED a classification since
the failure (or, while nothing answers, one probe per hour), and handed to a person after a bounded
number of failed calls.

**The sentinels' declared severities** (each stated, with its reason, in the workflow that opens it):

| sentinel | label | type + severity |
|---|---|---|
| CD on main failed / incomplete image set (`main-cd.yml`) | `ci-failure` | `bug` `sev:H` |
| a satellite's red main (`node-repo-ci-failure.yml`) | `ci-main-red` | `bug` `sev:H` |
| the promotion gate left the fleet unarmed (`main-cd.yml` → `.github/scripts/fleet-unarmed-alert.py`) | `fleet-unarmed` | `bug` `sev:H` |
| control image lane failed (`main-cd.yml`) | `control-image` | `bug` `sev:M` |
| control behind the newest build (`control-always-latest.yml`) | `control-lag` | `bug` `sev:M` |
| the issue-text name scan could not check (`issue-text-names.yml`) | `issue-text-names-sentinel` | `bug` `sev:M` |
| core main breaks a satellite's pin (`node-repo-platform-canary.yml`) | the caller's | `bug` `sev:M` |
| the issue-taxonomy check did not pass (`issue-taxonomy.yml`) | `issue-taxonomy-sentinel` | `bug` `sev:M` |

**And it is measured independently.** `.github/workflows/issue-taxonomy.yml` runs daily and goes RED
naming every open issue of the triage scope that has violated the taxonomy for more than 24 hours —
no type or more than one, no severity or more than one, an unknown `sev:`, or `sev:B`/`sev:H` on a
non-bug — and every repository missing a severity label (`scripts/check-issue-taxonomy.py check`; a run that could
not read is red, never green).

> ⚠️ `Hosting/Issue` is **not** part of this. It is fleet health — "no replicas ready", "not
> observed" — machine-written, self-resolving, one node per deployment × condition. It carries its
> own `Severity` (`Warning`/`Critical`, set by the detector), which is unrelated to the `sev:` scale
> here: that one grades a live deployment symptom, this one grades a defect in the product.

---

## Scope: the OPEN set, and only the open set

**Closed issues are out of scope.** They are not classified, not counted, and no query on this page
looks at them — policy [`issue-taxonomy-scope`](../PolicyNotProse). Every query here carries
`state:open`, including the gate.

That is affordable because the open set is **hundreds, not thousands** — 127 across five
repositories on the day this was written, which is why the whole of it could be classified in a
single pass and kept classified since. A taxonomy is worth maintaining at a scale where every open
row can carry it; a backlog that outgrew that would be a backlog problem, not a labelling one.

The corollary for an agent: do not go back over history. Classify what is open, keep it classified
as triage files new work, and let closure — not labelling — dispose of what is dead.

## The first classification pass — 2026-09-20

All 127 open issues across the five repositories were classified in one pass.

| | bugs | `sev:B` | `sev:H` | `sev:M` | `sev:L` |
|---|---|---|---|---|---|
| MeshWeaver | 63 | **0** | 18 | 32 | 13 |
| MeshWeaver.Plugins | 21 | **0** | 11 | 8 | 2 |
| Crm · Reinsurance · Education | 2 | **0** | 0 | 2 | 0 |

**Read the zero carefully.** The classifiers were instructed never to guess `B`, so it means
*nothing was proven blocking* — not *nothing blocks*. On the day the taxonomy was introduced the
gate was already open, which is a finding rather than a success: a gate that has never refused
anything has not yet been tested.

Several `sev:H` calls sit close to the line and deserve a human ruling — a half-finished migration
that gates portal boot, an install whose verify can never succeed, a `publicRead` that would
publish every future user submission. The rubric says each of those could be `B`; the evidence in
the issue did not prove it. That is exactly the judgement the first rule above reserves for a
person.

> **That ruling was made on 2026-09-20: the bar is no `sev:H` left.** It settles the question this
> paragraph opened without needing a per-issue re-litigation — the three calls above block a release
> either way now. It also makes the gate non-vacuous for the first time: on the day the bar moved,
> `sev:B` was 0 across all nine repositories while `sev:H` stood at **30** (MeshWeaver 18,
> MeshWeaver.Plugins 12, every other repo 0). A gate that refuses something is a gate.
>
> 🚨 **These are a SECOND, later snapshot, not a correction of the table above.** The first-pass
> table records the classification as it stood when the pass finished (Plugins `sev:H` = 11);
> this count was read from the live labels at ~12:35Z the same day, when the bar moved, by which
> time Plugins carried 12. Both are true of their instant. The number that gates a release is
> never either of them — it is whatever `release.cut`'s `NoOpenIssues` gates read when the cut is
> proposed.
