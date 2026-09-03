---
Name: Dead-Circuit Fan-Out Storm
Category: Architecture
Description: A closed browser tab left its owner fanning every change out to the dead address for 46 minutes — 300 to 1,169 refused deliveries a minute, zero evictions — because the one verdict the eviction acts on could not be produced. The measured 2026-09-03 case, the two correct fixes that cancelled each other, and the release tombstone that makes the verdict sayable.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 4h16v10H4z"/><path d="M8 20h8"/><path d="M12 14v6"/><path d="m9 8 3 3 3-3"/></svg>
---

# Dead-Circuit Fan-Out Storm

**Symptom, in the user's words:** *"no more access"*. **Symptom, in the logs:** one `[ROUTE]`
Warning a minute per dead address, each summarising hundreds of refusals logged at Debug:

```
[ROUTE] Directed delivery to pod hub 'portal/Wyf_5FmQ…' was refused: no silo in this cluster is
currently serving that hub. Transient — … so a retry is the correct response. … Message RawJson
(…) from mkleiner was NOT posted to the Orleans stream … Surfacing a transient DeliveryFailure to
the sender instead. 1169 earlier refusal(s) of this address since the last such line were logged at Debug.
```

## The measurement (memex, 2026-09-03)

| Fact | Value |
|---|---|
| Blazor circuit `Wyf_5FmQ…` closed | 15:54:21.986Z — the per-circuit portal hub WAS disposed (`Disposing per-circuit portal hub … on circuit close` fired) |
| First refusal aimed at `portal/Wyf_5FmQ…` | 15:54:23.5Z — **1.5 s later** |
| Last refusal | ~16:40Z — **46 minutes** after the tab closed |
| Rate | 300–1,169 refusals per minute, every minute |
| `routing reported subscriber … as unserved` (the eviction's one log line) | **0** on every pod |
| Same shape, other users, same day | rsalzmann 07:34–07:44 (11 min); mkleiner 09:30–09:43 (12 min) |

The sender in every line is the **owner** — the user's partition hub — pushing `DataChangedEvent`
frames to a subscriber that no longer exists. The two shorter storms ended when the server-side
stream's own idle release disposed it; the long one did not go idle because the resync loop
(`[SYNC_STREAM] Frame loss detected … requesting fresh snapshot`, 538×) kept it busy.

🚨 **The falsifiable check that nails the cause: refusals storming + zero eviction lines.** Had the
eviction merely been losing a race there would have been *some*. Zero means the path is gated off.

## Two correct fixes that cancelled each other

The owner-side eviction exists precisely for this — issues #2426/#2546: *"a subscriber whose
PROCESS died never sends an `UnsubscribeRequest`, so the owner fans every change out to the corpse
forever."* It acts on a `DeliveryFailure` the **router** stamps `TargetUnserved`, and it is gated —
deliberately, #2756 — against `ErrorType.ShuttingDown`, because that ErrorType is the platform's
*"come back and re-ask"* verdict: a subscriber that is merely mid-roll rides it out and re-arms
rather than re-subscribing, so evicting on it would destroy the server-side half of a subscription
whose other half is sitting still on purpose.

The pod-hub transport (see [Pod-Hub Delivery](/Doc/Architecture/PodHubDeliveryRollPlan)) answers a
dead address in `RoutingGrain.AnswerPodHubNotHere` with — always —
`postFailureToSender(reason, ErrorType.ShuttingDown, targetUnserved: true)`. Its reasoning is sound
for a hub that moves between pods: *the owner claims its address for as long as it is registered and
re-asserts the claim on every membership change, so a retry is the correct response.* The router
genuinely cannot tell a claim that has not landed yet from an address nobody will ever claim again;
`[PreferLocalPlacement]` re-creates the activation on the **caller's** silo either way, with no
local route and no memory.

So: the only leg that answers for a dead `portal/{circuitId}` always says `ShuttingDown`, and the
only guard in the eviction handler bails on exactly `ShuttingDown`. `Workspace.EvictClientSubscriptions`
— the whole of #2426/#2546 — was unreachable in production. Neither change was wrong. Together
they re-opened the storm the older one was written to stop, silently, behind a portal that still
answered HTTP 200.

## The fix: a release is a fact the cluster must keep, briefly

The missing knowledge exists — the owner *said goodbye*. Disposing a registration runs
`IPodHubGrain.Detach`. Before, `Detach` deactivated the activation on idle, which threw that fact
away. Now:

1. **`PodHubGrain.Detach` leaves a tombstone.** The activation stays alive for
   `ReleasedTombstoneLifetime` (10 min — at or past the sync stream's idle release, so any stream
   that would otherwise storm until that release meets its verdict first) and `Deliver` answers with
   `PodHubNotHereException { Released = true }`. A successful `Attach` clears it, so a process-level
   hub that re-registers under the same address (`mesh/{id}`, `portal/nodeops-…`) claims afresh
   exactly as before; a closed Blazor circuit id is never reused.
2. **`RoutingGrain.AnswerPodHubNotHere` answers a released refusal TERMINALLY:**
   `ErrorType.NotFound` + `TargetUnserved`. That is the shape `HandleTargetUnservedFailure` evicts
   on. Every other refusal keeps the transient shape, so the #2756 ride-out is untouched.
3. A peer that predates the field reads `Released == false` and degrades to the transient verdict —
   never to a wrong eviction.

After the tombstone expires a later delivery re-creates the activation on the caller and gets the
transient shape again — exactly the pre-existing behaviour. The tombstone narrows the window; the
eviction it makes reachable is what closes the loop.

## Where this is pinned

- `PodHubNotHereRefusalTest.AReleasedAddress_IsNackedTerminally_SoTheOwnerEvicts_AndNeverReachesTheStream`
  and `AnUnreleasedRefusal_IsStillTransient` — the router's two verdicts, side by side.
- `OrleansCrossSiloReplyTest.AReleasedPodHubAddress_AnswersTerminally_SoTheOwnerCanEvict` — two
  silos: register, claim, dispose, and the sender's `Observe` reads `NotFound` + `TargetUnserved`
  across the wire, with the never-claimed control arm still reading `ShuttingDown`.
- `UnservedVerdictEvictionTest.ATerminalUnservedVerdict_StillEvicts` — the owner evicts on that pair.

## What this does NOT explain, recorded honestly

Why the `UnsubscribeRequest` the circuit's own teardown posts did not end the server-side stream in
the first place. The disposal log fired; the storm began 1.5 s later. Either the request was never
posted, or it carried a `StreamId` the owner's registry no longer matched (a shape
`MeshNodeStreamCache` already documents). The pod that would have answered was deleted before its
log could be read. The eviction is the defence #2426 built for exactly that class of loss, and it is
now reachable; the primary teardown deserves its own repro.

## Related

- [Pod-Hub Delivery — the Transport Swap and its Roll Plan](/Doc/Architecture/PodHubDeliveryRollPlan)
  — the mechanism whose refusal this page changes.
- [Riding Out a ShuttingDown Address](/Doc/Architecture/RidingOutAShuttingDownAddress) — why the
  transient verdict must never evict, and why this fix adds a verdict instead of loosening that rule.
- [Error Propagation & Wedges](/Doc/Architecture/ErrorPropagationAndWedges) — a storm is a wedge in
  slow motion.
