---
Name: Test suite no longer stalls a release
Category: Fix
Description: A busy-wait in three delegation tests could hang a whole test run, blocking releases from shipping.
Icon: Sparkle
Order: -20260826
---

# Test suite no longer stalls a release

Three tests that wait for a delegated sub-thread used a retry loop that only paused between
attempts when the underlying read failed. When the read instead returned "nothing yet", the loop
retried tens of thousands of times a second, pinned a processor and never finished — taking the
whole test run down with it and, on twelve occasions in a single day, blocking the build that ships
new versions. The three tests now wait for the sub-thread to appear instead of polling for it, and a
new build check refuses any future test that reintroduces the same loop.
