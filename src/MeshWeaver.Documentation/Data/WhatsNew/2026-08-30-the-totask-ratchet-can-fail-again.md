---
Name: The ToTask ratchet can fail again — it was carrying 1,535 allowances for code that no longer exists
Category: Fix
Description: The guard that keeps observable-to-Task bridges out of the codebase budgeted 1,548 call sites while only 2 remained, so it would have passed a thousand new violations. Re-seeded to the real inventory, and memex/ joins src/ at zero tolerance.
Icon: Bug
Order: -20260830
---

# The ToTask ratchet can fail again

`ObservableToTaskBridgeGuard` is the ratchet that keeps `.ToTask(` — the bridge that resumes an
awaiter **inline on the signalling thread** — from coming back. It reads a seeded inventory of
known call sites and fails on a new file, a raised count, or a raised total.

It was **seeded before the conversion waves landed and never re-seeded afterwards**. So it
budgeted **1,548** allowances against an actual **2**, and listed 19 `memex/` files that had since
gone to zero. A ratchet with 1,535 allowances of slack does not ratchet: someone could have added
a thousand new bridges and every gate would have stayed green.

## What changed

- The inventory is re-seeded to what actually remains: **two** deliberate negative controls in
  `DisposalWaitBridgeTest`, which must keep using the old bridge because measuring what it does
  *is* their purpose. `TotalBudget` drops from 1,548 to 2.
- **`memex/` moves from the ratcheted roots to the production roots** — zero tolerance, no allow
  file, no line anyone can add — which is what the allow file's own header instructed should
  happen once a wave emptied a root.

## Two more ratchets were carrying the same rot

The same sweep found allow-list entries for files that **no longer exist** — every one of them
moved to `MeshWeaver.Plugins` with the AI and Orleans exits, and the entry stayed behind as free
budget:

| ratchet | stale entries | budget |
|---|---|---|
| `BlockingBridgeSites` | 4 files (Content, 2 × Orleans, PostgreSql) | 46 → 38 |
| `ImpersonationScopeSites` | 1 file (`MeshWeaver.AI/ChatClientCredentialResolver`) | 77 → 75 |

An allow entry goes stale the moment its subject's move merges, and a stale entry is
indistinguishable from a live one — it just quietly raises the ceiling.

## How this was verified

Not by the guard passing — a guard that cannot fail passes too. By **injecting a violation and
watching it fail**: one `.ToTask(` added to a `memex/` file (which the old configuration would have
absorbed silently) and one added to a `test/` file outside the inventory. Both went red, naming the
file. Then both were reverted and the guard went green again.
