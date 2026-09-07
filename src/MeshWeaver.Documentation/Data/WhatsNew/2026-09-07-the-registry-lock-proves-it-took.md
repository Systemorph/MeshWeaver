---
Name: The nightly registry lock now proves it worked
Category: Fix
Description: The job that protects the images CI depends on used to report success as soon as the registry accepted its request. It now reads the setting back and confirms it actually changed — so "protected" can no longer mean "asked politely".
Icon: ShieldCheckmark
Order: -20260907
---

# The nightly registry lock now proves it worked

Behind the scenes, a nightly job protects the container images this platform's build and deployment
pipelines depend on, so that the registry's own housekeeping cannot delete them out from under a
running deployment. Protecting one means flipping a single setting on the image.

Until now the job decided it had succeeded the moment the registry **accepted the request**. That is
not the same thing as the setting having changed — an accepted request that quietly has no effect
looks identical from the outside, and the job would have gone on reporting a number of protected
images while the housekeeping could still remove every one of them.

It now reads the setting back after every write and requires it to actually say "protected". A write
that is accepted but does not take is reported as a failure, naming the image and what still pins
it. So is a read that cannot answer: not being able to confirm something is not the same as having
confirmed it, and the job no longer treats the two alike.

This matters more than it sounds, because the writing half of that job had never actually run — the
runs so far were all report-only. The first real one is the measurement that counts, and it can no
longer pass without having done the work.
