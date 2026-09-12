---
Name: A check that can decide nothing is not a check
Category: Fix
Description: An install pinned to "no automatic updates" was still running a full self-update check on every platform or module publication anywhere in the fleet — 158 of them in five hours on memex, each one rewriting the same "updates are disabled" sentence onto Admin/UpdatePolicy from both replicas at once. The fleet-paced triggers now stop while the policy is None; the ones this install paces itself keep running, so the record still says what it always said.
Icon: Clock
Order: -20260909
---

`Admin/UpdatePolicy` on memex stood at **version 62,671** on the morning of 2026-09-09, for a
content decision that had not changed since 2026-09-07. Reading five hours of both replicas' logs
explains every one of those versions:

```
[SelfUpdate] check (BuildCompletion): updates are disabled on this install
             (Admin/UpdatePolicy = None); the registry was not listed.        ×158
[MergeGuard] Admin/UpdatePolicy: refused stale/reordered cross-hub write
             to 'lastCheckedAt'                                               ×14
[UpdateRemote] OWNER_NACK_REENQUEUE … code=Conflict                           ×10
[UpdateQueue] FAILED path=Admin/UpdatePolicy seq=576 elapsedMs=10015          ×2
```

Nothing user-visible broke — the contested field is a timestamp — but two replicas were writing one
leaf on the same event, roughly once every two minutes, in the update queue that also carries user
writes, and the resulting log volume buried the lines that mattered: the same window's real signal
was **4 lines in 212**.

## What was wrong

The check's rate was not this install's to choose. `BuildCompletion` ticks once per publication
**anywhere in the fleet** — that is the point of it, and under a policy that can act it is exactly
right: an event beats a poll, and #2494 exists because this service used to have neither.

Under `None` it decides nothing, and the code already said so twice. `RunOnce` returns
`UpdatesDisabled` before it lists a single tag, and `SelfUpdateVerdict.MayRestartAfter` answers
`false` for that outcome — so the module-restart half is not taken either. The entire effect of such
a check was a log line and a stamp repeating a sentence the node already carried.

## What it does now

While the policy is `None`, the two **fleet-paced** triggers — `BuildCompletion` and
`ModuleSetProposed` — are not decision points and do not run a check. The three this install paces
itself still do:

- **Startup**, so the record always carries the disabled verdict and the time this pod established
  it;
- **PolicyChange**, so enabling updates never waits for the next publication; and
- **the safety net**, so `LastCheckedAt` keeps moving on this service's own hourly period and a
  checker that has actually died still reads as a stale stamp rather than a frozen one.

That is the whole change: what stops is the repetition, at a rate nobody here chooses, of an answer
already on the node. On the measured window it takes a pinned install from ~158 checks in five
hours to about five.

## Why this is not the silence that was removed

An earlier version of this service dropped **every** check under `None` with a bare `Where`, and
that was the defect [#2553](https://github.com/Systemorph/MeshWeaver/issues/2553) was filed about:
an install an administrator had deliberately pinned and an install whose updater was broken both
left the record empty, so nobody could tell them apart and memex sat three builds behind for seven
hours. The two changes are one line apart and point in opposite directions, so the rule is pinned as
a truth table over every trigger × every policy, and the suite asserts both halves — the fleet-paced
triggers stop, **and** the disabled verdict, the moving stamp and the policy-change wake-up all
survive.

See [Self-Update Target Selection](@/Doc/Architecture/SelfUpdateTargetSelection) for how a check that
does run picks what to roll to.
