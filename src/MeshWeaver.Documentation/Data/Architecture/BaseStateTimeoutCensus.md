---
Name: Reading a Base-State Timeout
Category: Architecture
Description: "no initial state arrived for '…' within 30s" has two completely different causes with two completely different fixes, and until #1174 the log could not tell them apart. The census that does, and how to read one in production.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/><path d="M3.5 8.5h3"/></svg>
---

# Reading a Base-State Timeout

Every cross-hub `stream.Update` starts by reading a **base** — the state the lambda diffs against.
It reads this hub's **mirror** of the node (the client side of a `SubscribeRequest` to the owning
per-node hub), drops emissions that carry no node, and waits
[30 s](../WriteVerdictTotality) for one that does. When that wait runs out the caller gets:

```
System.TimeoutException: Update aborted: no initial state arrived for '{path}' within 30s.
Likely causes — (1) RLS silently rejected the prior CreateNode, (2) the path is misspelled /
points at a namespace no NodeType claims, (3) the node was deleted between create and update,
or (4) the per-node hub activated but its MeshDataSource didn't load the node from persistence.
```

That sentence lists four suspects and, on its own, **cannot pick between them**.

## The fork

There are exactly two shapes of silence, and they are different defects:

| what the mirror did | what it means | where to look |
|---|---|---|
| **produced NO change item at all** | the owning per-node hub never answered the subscribe | its ACTIVATION and ROUTING — a saturated owner action block, a delivery that never arrived |
| **produced N change items, none carrying the node** | the owner IS answering; its view of this path is EMPTY | the node's readability FROM THE OWNER — the initial read's identity/RLS, or a `MeshDataSource` that did not load it |

🚨 **The first row rules the node's content and its grants OUT, not in.** An absent node, and one the
reader may not see, both still produce a change item that carries no node — that is what the
null-filter exists for. So "no emission at all" is never suspects (1)–(3); it is the owner not being
reachable, or not getting a turn.

The base read now measures which row it is in and says so, appended to the caller's sentence:

```
… or (4) the per-node hub activated but its MeshDataSource didn't load the node from persistence.
The mirror produced 4 change item(s) inside the bound (last Version=0) and NONE of them carried
the node: the owner IS answering and its view of this path is EMPTY. …
```

The census is one counter per **subscription**, incremented UPSTREAM of the null-filter while the
`Timeout` stays DOWNSTREAM of it. That order is load-bearing twice over: downstream of the filter
the bound cannot fire at all (the inter-emission timer is reset by every dropped emission —
[Write Verdict Totality](../WriteVerdictTotality) records what that cost), and a census taken
downstream would read zero for *every* timeout, reporting row 1 for row 2 — the exact confusion it
exists to end.

**Nothing else is described.** A base read is bounded at 30 s, but the terminal that ends it is
routinely something else — an owner that never answered the `SubscribeRequest` inside the REQUEST
budget raises a `TimeoutException` of its own. Only the exception the base read itself raised
carries a measurement, so only that one gets a sentence; every other terminal keeps the message it
always had. Describing a foreign terminal in the language of the 30 s wait is what made the
boot-install failures claim a wait that never happened (#2387).

## Reading one in production

The caller's line is the SYMPTOM. Two more lines on the same pod, in the same second, are the
context, and both are shipped:

```
[UpdateQueue] ADVANCE_WITHOUT_HANDOFF path={path} seq={seq} bound=5000ms — the owner never
    acknowledged this write inside the bound …
[UpdateQueue] FAILED path={path} seq={seq} elapsedMs=30004
```

Same `seq` on both ⇒ the same write. `ADVANCE_WITHOUT_HANDOFF` at +5 s says the owner had not
acknowledged it even then, so the write was dispatched and simply never got a base — it did not sit
queued behind a predecessor. `seq` is the **cache-wide** write counter, not a per-path one; it dates
the write within the process, it does not count writes to that path.

Pull them from a running deployment with a `Logs` instance action on the control instance — never
`kubectl` ([Operating from the Portal](../OperatingFromThePortal)):

```json
{ "deployment": "<name>", "requestedAction": "Logs",
  "query": "UpdateQueue|no initial state arrived|<the path segment>", "sinceMinutes": 240 }
```

🚨 `query` is **filter text**, not LogQL — the action wraps it in
`{namespace="…"} |~ "(?i)<query>"`. Passing a whole LogQL expression double-wraps it, and because
the regex is an alternation the result still returns *some* lines, so the mistake reads as a
narrow answer rather than an error. Always include one term you KNOW is present as a positive
control, and check the recorded `logQl` on the action before believing a small count.

## What #1174 actually showed

`MeshWeaver.Graph.ActivityTracking` writes `{user}/_UserActivity/{encodedPath}` on every cold page
load. Measured 2026-09-14 on `memex-cloud`: 414 occurrences of this abort since 2026-08-10 across
57 pods, latest `2026-09-14T17:45:33Z`. Two facts the issue did not have:

- **It is not the deploy-roll starvation family.** The 2026-08-10 verdict closed it as a duplicate
  of a roll-window route-starvation issue because every occurrence then known lay inside one
  join-to-Ready window. The retained samples now span 2026-09-02 → 2026-09-14 on pods that had been
  up for many hours, so that explanation does not cover the population.
- **It concentrates on the HOTTEST paths.** Every retained sample is either the user's own login
  node `{user}/_UserActivity/{user}` — one author's is at version 6498 — or a page they revisit.
  Cold paths do not appear.

Which half of the fork that population is in was not decidable from any line the portal emitted,
which is why this page exists. The next occurrence says so in its own message.

## Related

- [Write Verdict Totality](../WriteVerdictTotality) — the sibling case: a base read that ENDS rather
  than times out, and used to answer nobody at all
- [MeshNode Stream Cache](../MeshNodeStreamCache) — the per-path update queue the `[UpdateQueue]` lines
  come from
- [Live Mirrors and the Change Feed](../LiveMirrorsAndTheChangeFeed) — why a written path's mirror is
  evicted after every write, and what that costs
- [Operating from the Portal](../OperatingFromThePortal) — the `Logs` and `Sample` instance actions
