---
nodeType: Markdown
name: Issue Taxonomy and the Release Readiness Gate
category: Architecture
description: Four axes on every issue — type, area or plugin, feature, and for bugs a severity — and the one of them that is a release gate. sev:B and sev:H must both be zero to cut a release; sev:M and sev:L are a priority conversation, not a gate.
icon: "<svg viewBox='0 0 24 24' xmlns='http://www.w3.org/2000/svg'><rect width='24' height='24' rx='4' fill='#b60205'/><path d='M12 6v7' stroke='white' stroke-width='2.2' stroke-linecap='round'/><circle cx='12' cy='17' r='1.4' fill='white'/></svg>"
---

# Issue Taxonomy and the Release Readiness Gate

A queue you cannot route is a queue you cannot finish. Until 2026-09-20 an open issue carried a
type at best, so the two questions that actually drive work — *what is still open for this plugin?*
and *what is still open for this feature?* — could only be answered by reading every title. And the
question that gates a release — *is anything blocking?* — could not be answered at all.

Every issue now carries up to four labels. Three are routing. One is a gate.

---

## The four axes

| axis | label | applies to | purpose |
|---|---|---|---|
| **Type** | `bug` · `enhancement` · `documentation` · `chore` | everything, exactly one | what kind of work |
| **Area** *(core)* | `area:<subsystem>` | MeshWeaver | which part of the framework |
| **Plugin** *(modules)* | `plugin:<Partition>` | plugin repos | which module owns it |
| **Feature** | `feature:<slug>` | everything with a specific subject | the thing itself, across repos |
| **Severity** | `sev:B` · `sev:H` · `sev:M` · `sev:L` | **`bug` only** | how bad |

`chore` means CI, build, dependencies or cleanup with no user-visible behaviour change.

**`feature:` is the axis that crosses repositories.** A defect in core and the plugin change that
depends on it share a feature slug and nothing else; that shared slug is the only way to see them
as one piece of work. It also exposes duplication that titles hide — the first pass found six
separately-filed issues that were one `RoutingGrain` back-pressure defect, one per activation.

---

## Severity, and why only bugs have it

| label | meaning |
|---|---|
| **`sev:B`** | **BLOCKING — the release cannot ship while this is open** |
| **`sev:H`** | **BLOCKING — a primary path broken but a workaround exists; an intermittent user-visible failure; or silently WRONG results anywhere** |
| `sev:M` | a secondary path broken, or a clear defect with an easy workaround |
| `sev:L` | cosmetic, a rare edge case, or developer-only |

**An `enhancement`, `documentation` or `chore` has no severity.** If something is not broken it
cannot block a release, and letting a feature request carry a blocking flag is how a release gate
rots into a wish-list.

`sev:B` and `sev:H` are the classes where *"we will do it next sprint"* is not an available answer
(maintainer, 2026-09-20: *"let's set bar of release to no high issues left"* — and, on M and L,
*"medium / low we don't care for release"*). `sev:M` and `sev:L` are a priority conversation and
never gate a cut.

---

## The gate

```
label:bug  label:sev:B  state:open   →   must be ZERO, in every repo of the product
label:bug  label:sev:H  state:open   →   must be ZERO, in every repo of the product
```

That is the whole release-readiness predicate, and it is enforced rather than remembered: the
`release.cut` standard in the Governance package requires `Gate.NoOpenIssues(repo, "sev:B")` **and**
`Gate.NoOpenIssues(repo, "sev:H")` for each of the seven gated repositories — 14 gates — so a release
proposal cannot reach `Ready` while one is open. (`Memex` and `MeshWeaver.Feedback` are deliberately
not gated: they ship no product code.) The pattern it uses
is the long-running check (`get Governance/Skill/long-running-check`); see
[Release Process](../ReleaseProcess) for what a release then is.

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

**GitHub is the ledger. The mesh holds the working state. Neither mirrors the other.**

| | holds | written by |
|---|---|---|
| **GitHub issues** | *what must be done* — the labelled queue, and the gate query | people and triage |
| **`Feedback/Feedback`** | *intake* — a finding as it arrives, before it is a work item | agents, users |
| **`Governance/Activity`** | *what is being done* — which label a thread is working, what it claimed, when it last looked | the control plane |

A mirror of the ledger would mean two writers over one set of rows, which imports every problem of
bi-directional synchronisation for no benefit: PRs already close GitHub issues natively, and
`Gate.NoOpenIssues` already reads GitHub directly, so the gate needs no copy. The one bridge runs
**one way** — a `Feedback` finding becomes a GitHub issue and records the reference — and the mesh
tracks the workers rather than the work, because an agent thread is not a GitHub user and cannot be
an assignee.

> ⚠️ `Hosting/Issue` is **not** part of this. It is fleet health — "no replicas ready", "not
> observed" — machine-written, self-resolving, one node per deployment × condition. It is not a
> work item and it carries no severity.

---

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
