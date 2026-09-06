---
Name: A test that respected its time limit is no longer reported as hung
Category: Fix
Description: When an in-mesh test ran out of time, the build report could say the test never returned and its thread had been abandoned — even though the test had stopped exactly when it was asked to.
Icon: Timer
Order: -20260906
---

# A test that respected its time limit is no longer reported as hung

Every in-mesh test gets a time budget, and a test that runs out of it is told to stop. A
well-written test takes that hint: it stops waiting, throws, and the build report says so by name —
*the case budget expired and the case ended on its cancellation token*.

The other verdict is much more serious. *Did not return — a hung case; the thread is abandoned and
the build continues* means the test ignored the request entirely and the build carried a live,
unstoppable thread on for the rest of the run. Those two lines are supposed to tell you which kind
of test you wrote.

Under load, the build could print the second line for a test that had actually done the first. The
runner asked the test to stop only a fraction of a second before it stopped waiting for it, so
everything the test does on the way out — unwinding, running its own cleanup, and the operating
system finally reaping the thread — had to fit into that fraction. On a busy build machine it did
not, and a co-operative test was reported as an abandoned one.

The two questions are now asked in order: the runner waits out the whole budget, *then* asks the
test to stop, *then* gives it a proper window to finish stopping. A test that co-operates is named
as such however loaded the machine is, and a test that genuinely ignores the request is still
reported as hung — which is the distinction the report exists to draw.
