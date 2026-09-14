---
Name: Starting a data source no longer leaves a spare hub behind
Category: Fix
Description: Every data source used to mint a second, unreachable sync/ hub when it started — one per source per partition, kept alive until its node hub died. It is gone, and the test that measured it now waits for the host to finish starting before it counts.
Icon: Broom
Order: -20260914
---

# Starting a data source no longer leaves a spare hub behind

When a hub with data started, each of its data sources opened its primary `EntityStore` stream — the
one every read and write of that source flows through — and then, in the same breath, reduced that
stream to its own full reference and threw the result away. The reduce was not free: it built a
second `SynchronizationStream` with its own hosted `sync/` hub, registered on the primary stream for
disposal, that no caller could ever reach and that lived for as long as the node hub did. One spare
hub per data source per partition, on every hub that carries data.

**The spare hub is no longer created.** Starting a data source opens its primary stream and nothing
else. Reads, writes and the initialization gate are unchanged — they never used the discarded stream.

It was found through a test, not a heap: `ReadPathStreamMintingTest` counts the `sync/` hubs five
uncached reads leave behind and once read **six**. The sixth was this spare, minted on the hub's init
turn a few milliseconds after the first data frame the test had been waiting on — so it landed inside
the measurement on a loaded CI runner and outside it everywhere else. The test now takes its baseline
after the host reports started, and a new test pins the population a start leaves behind at exactly
one hub per source. The investigation is written up in
[The Read Path Minted a Hub Per Read](/Doc/Architecture/ReadPathStreamMinting), §8.
