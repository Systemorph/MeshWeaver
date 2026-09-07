---
Name: Conditional Writes Across Hubs
Category: Architecture
Description: A stream.Update lambda on a node this hub does not own runs on the caller's MIRROR and ships a merge patch of the fields it changed — so a field the lambda decided NOT to change is absent from the patch, and a concurrent write to that field survives. The rule for expressing an intent whose condition must hold at APPLY time.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M7 16V4m0 0L3 8m4-4l4 4"/><path d="M17 8v12m0 0l4-4m-4 4l-4-4"/></svg>
---

# Conditional Writes Across Hubs

`workspace.GetMeshNodeStream(path).Update(current => modified)` is the only mesh-node mutation API, and it is safe against concurrent writers **for the fields it writes**. This page is about the fields it *decides not to* write, which is a different question and has bitten twice.

## The mechanic

Where the write lands decides how the lambda is evaluated:

| the node | path taken | when the lambda runs | what travels |
|---|---|---|---|
| **owned** by this hub | `UpdateOwn` | on the owner's serialised write path | the node |
| **not owned** by this hub | `IMeshNodeStreamCache.Update` → `UpdateRemote` | on **this hub's mirror** | an **RFC 7396 merge patch** |

For the second row the lambda's output is diffed against the caller's base — `ComputeMergePatchDiff(currentNode, updatedNode)` in `MeshNodeStreamHandle` — and only the members that actually changed are sent. The owner then merges that patch.

That is exactly what makes concurrent writers safe: two candidates each removing *their own* key from a map produce two disjoint patches, and both land. It is also what makes the failure below possible.

## 🚨 The failure: an absent member is not "leave it as I found it"

Under RFC 7396 a member the patch does not carry is **left untouched on the owner**. From the writer's side that looks identical to "I decided not to change it" — but the two mean different things the moment anyone else writes that member.

So a lambda shaped like this is unsafe on a non-owned node:

```csharp
// ❌ the condition is evaluated on THIS hub's mirror; the patch is applied later, on the owner
stream.Update(node =>
{
    var state = node.ContentAs<MyState>(options);
    var itIsMine = state.ClaimedBy == me && state.Status is Status.Planning;
    return node with { Content = state with
    {
        Registrations = state.Registrations.Remove(me),      // own key — merge-safe
        ClaimedBy = itIsMine ? null : state.ClaimedBy,       // 🚨 a CONDITION, not a value
    }};
});
```

When the mirror has not yet seen a claim the owner already granted, `itIsMine` is **false**, the patch carries no `claimedBy` at all, and the grant *survives the release that was meant to undo it*. Nothing errors. The write "succeeds". The field simply keeps the other writer's value, forever.

Note what is **not** wrong here: the removal on the line above is fine, because it is a *value* the lambda always writes under a key it owns. The unsafe part is the field whose presence in the patch depends on state the caller cannot see.

## The rule

> **On a node you do not own, a lambda may write values. It may not decide, from its own mirror, that a field needs no write — unless that decision is correct for every state the owner might be in.**

A useful test: *if the field I am leaving alone had been changed by someone else a millisecond ago, would my patch still be right?* If the answer is no, the condition has to move.

## What to do instead — state a fact, let the owner act

The condition has to be evaluated where the state is current, and that is the **owning hub**. AGENTS.md already prescribes the shape for this and calls it out as the answer to state-machine semantics:

> Set a `RequestedX` field and let the owning hub's watcher react.

So the caller writes something **unconditional and true regardless of the owner's state**, under a key it owns — and the owner, whose lambda *is* serialised against current state, decides what that implies:

```csharp
// ✅ the candidate states a FACT under its own key — unconditional, merge-safe
stream.Update(node => node with { Content = state with
{
    Registrations = state.Registrations.Remove(me),
    StoodDown = (state.StoodDown ?? Empty).SetItem(me, DateTime.UtcNow),
}});

// ✅ …and the owner, which sees the real state, draws the conclusion
Update(node => ReleaseStoodDownClaim(node, options, now));
```

Three properties make this work, and all three are worth copying:

- **The fact is written unconditionally**, so it is present in the patch whatever the mirror showed.
- **It is keyed by the writer**, so it is merge-safe against every other writer of the same map.
- **It is consumed, not accumulated** — the owner removes the entry when it acts on it, the writer removes its own when the fact stops being true, and anything left over ages out on a bound the record already has. A fact nobody consumes is a leak.

Reach for this only when a condition genuinely must hold at apply time. Most writes are values, and `stream.Update` handles those exactly as it appears to.

## What NOT to do

- **A read-back-and-retry loop** after the write, to "converge" the field. That is a poller recovering from a state that should not happen — AGENTS.md names it explicitly ("a watchdog / timer / poller that resubscribes or retries to recover from a state that shouldn't happen") — and it hides the ordering defect rather than removing it.
- **Widening the condition to "always write it"**. `ClaimedBy = null` unconditionally clears somebody else's live claim, which is worse than the bug.
- **A compare-and-set on a version**. It makes each half safe on its own; it cannot order two writers, and the two halves here live on different records.
- **Moving the work to a bespoke request/response type.** Mesh-node mutation has one API; a verb-shaped message races the same way and adds a surface. See [CQRS and Content Access](/Doc/Architecture/CqrsAndContentAccess).

## Where this has actually happened

**Build-claim arbitration**, twice, and the second time is the clearer statement of the shape.

A follower that has seen the build's GO stands down by calling `WithdrawBuildClaim`. Its second half — hand back a claim we were granted but never started — is conditional on `ClaimedBy` naming us. The arbiter grants on the node it owns.

- The first ordering — the grant published *after* the stand-down — was closed at the arbiter's publish point: it refuses a winner that is no longer a candidate, and hands the lock back.
- The second — the stand-down *decided* before the grant and *applied* after it — could not be closed there, because the publication was legitimate when it happened. The patch simply carried no `claimedBy`, and the build stayed locked to a process whose driver had already completed. The takeover rule then defended that holder **by design**, because the process really was alive: no builder, no bake, no pod reaching ready.

The measured residue was a single terminal state — a holder at `Planning` with nobody queued and the GO already published — differing from the healthy state in exactly one field. It is worth remembering how *quiet* that is: no exception, no log, no failed write, and a symptom (a rollout that never becomes ready) several layers away from the cause.

## Related

- [Data Access Patterns](/Doc/Architecture/DataAccessPatterns) — the one mutation API and its read counterparts.
- [CQRS and Content Access](/Doc/Architecture/CqrsAndContentAccess) — why a read for a specific node never goes through a query.
- [Asynchronous Calls](/Doc/Architecture/AsynchronousCalls) — the cold-observable rule that decides whether a write happens at all.
- [Access Context Propagation](/Doc/Architecture/AccessContextPropagation) — the other thing that travels with a cross-hub write.
