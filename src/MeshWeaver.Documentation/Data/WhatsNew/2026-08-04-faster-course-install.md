---
Name: Installing a course is faster
Category: What's New
Description: Course and plugin installs now write their nodes to the database in batches instead of one at a time.
Icon: Sparkle
---

# Installing a course is faster

Installing a course copies every lesson, exercise and solution into your own space. Until now each
of those was written to the database on its own, one round-trip at a time, which is why a large
course took a while to appear.

Those writes are now grouped and sent together, so the same install finishes in a fraction of the
database chatter. Nothing about the result changes — the same nodes land in the same order, and an
install that fails still records nothing rather than claiming to have half-worked.
