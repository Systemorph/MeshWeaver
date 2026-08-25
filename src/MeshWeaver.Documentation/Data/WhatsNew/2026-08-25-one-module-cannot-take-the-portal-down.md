---
Name: A single incompatible add-on can no longer take the portal down
Category: Fix
Description: One add-on that does not match the running version is now skipped and reported, instead of stopping the whole portal from starting.
Icon: Sparkle
Order: -20260825
---

# A single incompatible add-on can no longer take the portal down

When an installed add-on was built against a different version of the platform than the one your
portal is running, the portal could fail to start at all — and because this happened before logging
began, there was nothing in the logs to explain why.

Now the portal starts anyway. The add-on that does not match is skipped, and it is reported clearly
rather than quietly ignored: it is named at startup, and it shows up as a problem on the portal's
health status instead of the portal claiming everything is fine. That matters because a silently
missing add-on looks exactly like one that is working — the feature is simply gone.

The remedy is unchanged and is now stated in the message itself: an add-on and the platform have to
move together, never one on its own.
