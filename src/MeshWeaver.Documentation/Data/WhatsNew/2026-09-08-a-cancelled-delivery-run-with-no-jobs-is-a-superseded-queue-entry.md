---
Name: A cancelled delivery run with no jobs is a superseded queue entry
Category: Feature
Description: When merges land faster than delivery runs finish, the runs list fills with "cancelled" delivery runs. Each of those had zero jobs and was replaced by a newer commit's run before it started — the run that was executing was never touched. The reading, and the three lines that keep a live delivery alive, are now written down and guarded.
Icon: DocumentBulletList
Order: -20260908
---

During a merge burst the delivery workflow's run list looks alarming: run after run marked
**cancelled**, one per merge. Twice now that reading has held a roll — once when it was a real
defect, and once, on 2026-09-08, when it was not.

## What was actually happening

GitHub keeps exactly one *waiting* run per concurrency group. When a newer commit's delivery run
arrives while one is already waiting, the waiting one is cancelled and the newer one takes its
place. The cancelled run never ran a single job. The run that was **executing** — building images,
sealing a publication — is never touched.

Measured over one hour: seven delivery runs cancelled, each one to two seconds after the next
arrival was created, all with zero jobs; the two runs that were executing ran to their seal. The
newest commit always held the slot, so nothing was starved. What needed attention was a different
conclusion in the same list — a *failure* on a run that did execute.

## What is now written down and held

- [Reading CI Signals](/Doc/Architecture/ReadingCiSignals) carries the measured table, the reading
  recipe (`jobs: 0`, and the cancellation instant equal to the next arrival's creation), and how to
  tell this apart from an account-budget kill or a hand.
- The delivery workflow's own comment says what "queued" means in this lane, instead of claiming
  that waiting runs queue up behind each other.
- A guard test holds the three lines the shape depends on: the run in flight is never cancelled for
  any event, the hourly reconcile keeps its own lane, and deliveries share one group on the ref —
  with a control arm that fires on each plausible "tidy-up".
