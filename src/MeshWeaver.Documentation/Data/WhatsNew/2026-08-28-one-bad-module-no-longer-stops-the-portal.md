---
Name: One incompatible add-on no longer stops the portal starting
Category: Fix
Description: A single add-on built against a different platform version could prevent the whole portal from starting; now only that add-on is skipped.
Icon: Sparkle
Order: -20260828
---

# One incompatible add-on no longer stops the portal starting

An add-on that runs a background task could stop the entire portal from starting if it had been
built against a different version of the platform. Every new instance of the portal would fail at
launch, so an update could not roll out at all until someone intervened by hand.

Now only the affected add-on is skipped, and the reason is reported. The portal starts and serves
without that one feature instead of not starting at all.
