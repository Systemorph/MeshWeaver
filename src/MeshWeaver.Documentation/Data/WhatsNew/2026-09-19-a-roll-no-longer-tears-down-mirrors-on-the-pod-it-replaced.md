---
Name: A roll no longer tears down live views on the pod it replaced
Category: Fix
Description: When a pod went away, deliveries still addressed to it were reported to the sender as permanent failures — so every view, mirror and sync stream carrying its own recovery machinery tore itself down for a condition that was over seconds later on the surviving pod. Orleans names that condition in two ways and the router recognised neither. It does now, and a genuine defect is still reported as one.
Icon: Bug
Order: -20260919
---

# A roll no longer tears down live views on the pod it replaced

Every rolling deploy leaves a short window in which a message is still addressed to a pod that has
gone. The framework already handled that window in the right way — the delivery is retried, and each
retry re-resolves the target so a later attempt lands on the surviving pod. What went wrong was the
**verdict after the retries**, and that verdict is what decides whether the *sender* keeps going.

A sender told *"transient"* keeps its recovery machinery armed and resubscribes; a sender told
*"failed"* tears it down for good. So the classification of one window decided whether a live view,
a mirror or a sync stream came back after a roll — or stayed dead until the page was reloaded.

The router's rule for that verdict is a good one, and it is deliberately strict: **only a condition
that is a lifecycle transition by construction counts as transient**, and anything unrecognised stays
permanent, so a real defect is still reported as one. Three conditions qualified — the grain directory
mid-handoff, the host going away, the process's container being disposed. The last one was recognised
by two exception *types*, and the two ways Orleans actually reports a departed pod are neither of
them:

| what Orleans says | what it means |
|---|---|
| `Unable to connect to S10.244.2.223:11111:148812047 …` (host unreachable, connection refused) | nothing is listening at that address any more |
| `The target silo is no longer active: target was …:146524552, but this silo is …:146534005` | the pod restarted and reclaimed its address; callers still hold the old incarnation |

Both are statements about **one specific pod incarnation** — the address carries a generation stamp —
so neither can start working again, and the only cure is the re-resolve that the retry already
performs. Both are now classified as the lifecycle transition they are.

A timeout deliberately still counts as permanent, and the difference is the point: a timeout means
the target *accepted* the connection and did not answer, which is plausibly a wedge rather than a
restart, and telling a sender to keep retrying a wedge is a storm.

## Measured

Two production incidents, 191 and 3,959 occurrences, recorded across 27 pods over four weeks.
They are one root seen through two logs — one fingerprinted on the router's own verdict, the other on
Orleans' per-attempt addressing log, which the retries multiply. The full account, including the
shapes that are still open, is in
[A departed silo is not a delivery defect](/Doc/Architecture/ADepartedSiloIsNotADeliveryDefect).
