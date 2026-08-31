---
Name: Saving no longer times out when the answer arrives too fast
Category: Fix
Description: Occasionally a save would wait half a minute and then report the owner unreachable, even though the write had gone through. The confirmation was arriving faster than the sender had prepared to receive it, and was thrown away; the listener is now in place before the request is sent.
Icon: Sparkle
Order: -20260831
---

# Saving no longer times out when the answer arrives too fast

When part of the mesh saves a change to data owned elsewhere, it sends the change across and waits
for the owner to confirm. Rarely — about once in several full test runs, and by the same mechanism
in production — that wait ran a full thirty-one seconds and then failed with "owner unreachable",
even though the owner had applied the change and confirmed it immediately.

The confirmation was being thrown away. The sender posted the request first and only then set up the
listener for the answer — and an owner that is already warm answers in under a millisecond, so on a
busy machine the confirmation could arrive in the gap and find nobody listening. Unclaimed answers
are discarded by design; the sender then waited out its entire budget for a reply that had already
come and gone.

The reading half of the platform had this exact fault, found and fixed earlier with a test that
forces the bad timing on purpose. The writing half had the same shape and now has the same fix: the
listener is registered before the request is posted, so however fast the answer comes back, someone
is there to receive it. The same forced-timing test now covers writes too, in both directions — the
old order provably loses the answer, the new order provably keeps it.
