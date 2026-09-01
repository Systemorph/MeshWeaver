---
Name: An install that cannot read its update policy no longer stops updating for six hours
Category: Fix
Description: A slow first read of Admin/UpdatePolicy used to leave a portal with no update checks at all until the six-hour retry came round; it now retries in seconds, because until that read lands the install is not slow, it is inert.
Icon: ArrowSync
Order: -20260901
---

# An install that cannot read its update policy no longer stops updating for six hours

Every self-update trigger — the startup pass, a build completion, an admin changing the policy, and
the safety net that exists to bound a dead event channel — waits for the install to have actually
**read** its `Admin/UpdatePolicy` node. That gate is deliberate: a portal must never decide to roll
itself under a policy it only guessed at, which is how a pinned install once rolled on every pod
start.

The gate was right; the cadence behind it was not. When that first read faulted — on a busy portal
the boot-time `SubscribeRequest` can exceed its minute — the retry was paced by `RetryInterval`,
the six-hour value meant for re-establishing a watch that has already produced a value. But before
the first read there is nothing to re-establish and nothing retained: the install performs **no
checks at all**, and the safety net cannot help, because it is behind the same gate. One slow read
therefore bought six hours of an install that looked perfectly healthy and had quietly stopped
asking whether a newer release existed.

That is not a hypothesis. On 1 September a portal was found serving `rc8.ci.6829` while the
registry had reached `rc9.ci.7231` — roughly four hundred builds — with its pinned module set
advanced past its image, so modules reported themselves as "contributing nothing", memory sat at
89%, and other repositories' CI gates failed against its bundle endpoints. Two pods of the *same*
build settled it: the one that logged `policy stream faulted; re-establishing in 06:00:00` had run
zero checks, while its sibling, whose read succeeded, had run two.

Establishing the policy now has its own cadence (`SelfUpdate__PolicyEstablishRetryInterval`,
30 seconds by default). Once the policy **has** been read, a later fault keeps the six-hour
interval exactly as before — the last value is retained and checks keep running, so that fault
costs freshness rather than the feature. The log line now says which of the two states it is in,
so the difference between "this install is pacing itself" and "this install is inert" is readable
rather than inferred.
