---
Name: The plugin registry answers from memory
Category: Fix
Description: Fetching a plugin bundle or the catalog with an instance key could answer "temporarily unavailable" on a busy registry, once a minute, per key — every satellite build gate and bake depended on that answer. The registry now keeps each registered instance in memory and reads it live, so only the very first request for an instance ever waits on the store.
Icon: Key
Order: -20260902
---

# The plugin registry answers from memory

Every installation that pulls plugins presents an **instance key**. Until now the registry looked
that key up afresh — three round-trips to the nodes that describe the instance, its index and its
grant — on every request that missed a one-minute cache, with ten seconds to get each answer. On a
registry under load those ten seconds were not always enough, and the caller was told
`503 Instance-key resolution is temporarily unavailable`. Correct, but useless: the fleet's build
gates and bakes fetch through exactly that endpoint, and three of them failed on it in one day.

Now the registry holds each instance, its index entry and its grant as **live mirrors** — the same
mechanism every page in the portal uses to stay current. The first request for an instance still
waits for the mirror to fill (and still answers *temporarily unavailable* if that takes longer than
ten seconds — nothing was widened), but every request after it reads memory. A disabled instance or
a changed plan is seen by the **next** request on **every** replica, not after a cache window.

Nothing changes for callers: an unknown key is still refused the way it always was, and a 503 still
carries `Retry-After`. Design and evidence:
[Instance-Key Resolution](/Doc/Architecture/InstanceKeyResolution).
