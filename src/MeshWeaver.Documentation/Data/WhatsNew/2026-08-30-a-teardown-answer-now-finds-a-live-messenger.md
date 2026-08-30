---
Name: A teardown answer now finds a live messenger
Category: Fix
Description: When a hub went away mid-write, the "the owner is disposing, retry" answer was posted through the parent — and skipped entirely if the parent was itself disposing its children, which is exactly when it happens. The caller then waited out its whole budget in silence. The answer now walks up until it finds a hub that can still carry it.
Icon: ArrowUpRight
Order: -20260830
---

# A teardown answer now finds a live messenger

When a per-node hub is torn down with a write still in flight, it owes the writer an answer: *the
owner is disposing, the patch was not applied, safe to retry against the fresh activation.* That
answer exists precisely so a caller does not sit in silence.

It was posted through the hub's parent — but only while the parent was not itself disposing its
hosted children. That condition is false in exactly one situation: **when the parent is disposing
its hosted children**, which is the moment a whole batch of child hubs goes down with writes in
flight. So the answer was skipped at the only time it was ever needed, and the caller got the
silence the answer exists to replace.

The reasoning behind that limit was written down: *"during a whole-mesh teardown the parent is past
that mark too, the post is skipped, and nobody is waiting."* But **"nobody is waiting" is an
assumption the code cannot check**. A caller whose wait outlives the start of a teardown is still
waiting, and it is the caller who most needs an answer that is guaranteed not to get one.

It is also not usually a whole-mesh teardown. In the reported cases the parent was disposing its
children while *its* parent was still running normally — one more level up was all that was ever
needed. It was measured twice, in two different repositories and two unrelated subsystems, with the
same outcome: a writer burning its full 31-second budget on a write whose owner had gone away.

The answer now walks up the chain and goes through the first hub that can still post. When nothing
in the chain can — the genuine whole-mesh teardown the old comment described — that is now **logged**
rather than dropped, because a caller that will hang deserves a line saying so. Silence used to be
indistinguishable from an answer that was never produced at all.
