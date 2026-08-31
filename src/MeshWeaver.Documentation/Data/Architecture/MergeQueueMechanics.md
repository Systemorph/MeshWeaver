---
Name: Merge Queue Mechanics
Description: "Why a merge queue can accept entries for hours and land nothing, and the four readings that mislead you while it happens — an ejection outranks a PR's own green, every mutation restarts every build, and the diligent responses are the ones that make it worse."
---

# Merge queue mechanics

Core `main` merges through a GitHub merge queue. It works well and is nearly invisible — until it
stops landing anything, at which point almost every instinct that serves you elsewhere makes it
worse. On 2026-08-30 core `main` landed **nothing for over two and a half hours** while six to
eight entries sat queued, no PR was red, and every session involved was acting reasonably.

This page is what that cost to learn.

## The queue tests a tree that does not exist anywhere else

A PR's CI runs against **its merge base**. The queue builds **`PR + main@now`**, speculatively
stacked with the other entries. Those are different trees, and only the second one is the tree that
lands.

Everything below follows from that one fact.

## 1. 🚨 An ejection outranks the PR's own green

When the queue ejects an entry, **the PR page still shows green** — legitimately, because its own
run really did pass against its own base. So the natural response is to re-queue it, which
reinstates the same failure, and the loop closes.

Measured: one PR's only run passed **all six shards** against a merge base that was `behind_by = 6`.
Its green attested to a tree from ninety minutes earlier. It was re-queued after every ejection, by
different people, each of whom checked and saw green.

```bash
gh api repos/OWNER/REPO/compare/main...<head> --jq '.behind_by'
```

One call. **Non-zero means the green describes a tree that no longer exists.** Rebase and re-run
before re-queueing, or the queue is the only thing that will ever discover the problem — one
~25-minute cycle at a time.

## 2. 🚨 Every queue mutation restarts every in-flight build

Enqueue, dequeue, force-push to a queued branch, or an ejection — each rebuilds the speculative
stack and **restarts every build in it**, not only the entry you touched.

With several sessions each doing one reasonable thing, the clock never runs out:

```
every merge_group build:  created 22:47:15 … 22:49:57      now 22:51:33      cycle ≈ 25 min
```

All of them minutes old, all restarted together, repeatedly. **The fastest way to drain a stuck
queue is to stop touching it** and let one full cycle complete undisturbed. If mutations are
needed, batch them into one window rather than trickling them.

**This is not the [#888 cancellation shape](../ReadingCiSignals).** Check `conclusion`
on the `merge_group` runs: *zero* `cancelled` means the stack is rebuilding correctly and simply
never getting a quiet window. Same symptom, opposite cause, opposite remedy.

## 3. Individually-clean PRs can be `UNMERGEABLE` in the queue

Two entries that are each `MERGEABLE/CLEAN` against `main` can conflict **with each other** in the
stack. The queue reports `UNMERGEABLE` on one of them; its PR page still says clean.

**A local integration merge is the cheap oracle** — merging the candidate set locally surfaces this
in about forty seconds, against a full queue cycle per discovery. It also lets the conflict be
resolved *deliberately* rather than rediscovered by the stack.

🚨 A dependent PR queued **ahead** of the PR it depends on makes that dependency permanently
unmergeable. Order matters, and the queue will not tell you.

## 4. A branch carrying an already-merged commit looks clean and cannot merge

If a branch is cut from a worktree whose `HEAD` already contains a commit that has since landed
(squashed, so the sha differs), the speculative merge sees content already on `main` arriving
again. The PR reads `CLEAN`; the queue ejects it; nothing explains why.

`gh pr view --files` compares against a stale base and hides it. **The tell:**

```bash
git log --oneline origin/main..HEAD     # lists a commit you thought had landed
```

Fix by resetting to `origin/main` and cherry-picking only the intended commit.

## 5. One flaky-looking test can hold the whole queue

Every merge-group build contains every entry, so **one failing test ejects an entry per cycle** and
each ejection restarts the stack. The queue then churns indefinitely while no individual PR is at
fault.

Two readings will mislead you here, and both were tried:

- *"It is load"* — the queue runs ~5 speculative builds × 6 shards ≈ 30 concurrent jobs, so
  blaming contention is irresistible. It was wrong: the same test failed on a plain
  `pull_request` run with the queue empty.
- *"It is deterministic"* — also wrong: an earlier queue build on byte-identical content passed.

The truth was a **dual-terminal-writer split-brain**: one writer pinned a terminal status while the
folded value said otherwise, and which one the response surfaced depended on an unrelated
write-back race. Neither "flake" nor "deterministic" described it, and neither would have led
anywhere.

**When the queue ejects on a test, read the `.trx` from the ejection run** — the CI summary keeps
only the test name. The assertion text and the duration are what separate a timeout from a wedge
from a wrong-value assertion, and they are the whole diagnosis.

## What to do when nothing is landing

1. **Look for motion before cause.** `created` timestamps on the `merge_group` runs. All of them
   minutes old and restarting ⇒ churn, not blockage.
2. **Check `conclusion` on those runs.** Failures ⇒ something in the stack is red; zero failures
   and zero cancellations ⇒ you are in a restart loop.
3. **If it is a restart loop, declare a hold across sessions.** Nothing enqueued, dequeued or
   pushed for one full cycle.
4. **If an entry is red, read its `.trx`**, and treat its own green as evidence about its merge
   base only.
5. **If entries conflict, resolve locally** — or land one combined PR, which is one entry, one
   build, no stack, and no mutual ejection.

## Related

- [Reading CI signals](../ReadingCiSignals) — skipped and absent checks read as
  satisfied; a required context counts only when it reads literally `=SUCCESS`.
- [Guards and unknown states](../GuardsAndUnknownStates) — a classifier with no
  representation for "I cannot tell" picks the nearest bucket, which is how both wrong readings in
  §5 were reached.
- [Bounds must be ordered](../BoundsMustBeOrdered) — why a failure at exactly its bound
  is not evidence that the bound was too short.
