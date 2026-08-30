---
Name: No test project runs near its own kill switch
Category: Fix
Description: Two test projects were scheduled as single units taking ~70% of the 8-minute wall-clock cap each project runs under, so one slow runner killed them and produced a red naming tests that had not failed. Both are split, and a guard now does the arithmetic instead of a comment asking someone to.
Icon: DocumentSplitHint
Order: -20260830
---

# No test project runs near its own kill switch

Every test project in CI runs under an 8-minute wall-clock cap. The cap is deliberate: it turns a
hang into a bounded, attributable failure instead of a shard that eats its whole budget.

But a project that *legitimately* takes 5½ minutes is one slow runner away from hitting it, and what
comes out then is not a clean "timed out" — it is a red listing tests that were still running when
the host was killed. Those tests did not fail. Every occurrence therefore costs a full investigation
before it can be dismissed, and re-running to see whether it repeats is exactly what this codebase
forbids, because that habit is how real races get buried.

`MeshWeaver.PluginCatalog.Test` was in that position: 315, 320, 315 seconds on three consecutive
runs — and then 480 seconds and killed, with the identical tree passing at 320 seconds on the very
next run. Five tests were blamed for a kill.

## Why it was not caught

The scheduler already had a split rule, but it is about **balance** — split a project when it is the
long pole. PluginCatalog was never the long pole; it passed that rule comfortably. Headroom against
the cap is an independent property, and nothing was checking it.

Both projects are now split into parts that land on different runners — PluginCatalog into two
(~174 s each) and Hosting.Monolith into three (~221 s each) — and the shard loads stay balanced.

The lasting part is that the check is now mechanical. A guard reads the cap out of the workflow (so
it cannot disagree with the budget it guards), divides each project's measured weight by its part
count, and fails when any scheduled unit exceeds 60% of the cap — naming the project and pointing at
the parts column. It found the second of the two projects on its first run.
