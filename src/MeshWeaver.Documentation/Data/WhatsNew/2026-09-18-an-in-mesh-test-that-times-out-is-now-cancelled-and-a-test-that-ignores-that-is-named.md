---
Name: An in-mesh test that times out is now cancelled, and a test that ignores that is named
Category: Fix
Description: "When an in-mesh test case ran past its time limit, the runner reported the failure and moved on while the case's work kept running inside the portal. The runner now cancels the case at its limit through the mesh's I/O pool, hands every case a cancellation token to observe, and waits a short grace for it to stop. A case that stops is reported as cancelled; one that keeps running is reported as having ignored its token, so the defect is visible in the verdict rather than hidden by the timeout."
Icon: Bug
Order: -20260918
---

# An in-mesh test that times out is now cancelled, and a test that ignores that is named

In-mesh test suites run their cases one after another against the live mesh, each under a time
limit. Until now, a case that ran past its limit was reported as failed, and the runner went on to
the next case, but nothing told the case to stop: whatever it was waiting on kept running in the
same portal process, competing with every case that followed and with the mesh itself.

This is the same defect the xunit analyzer warns about with `xUnit1069` on the compiled test
suites: a timeout that fires while the work it started keeps running. Moving a test in-mesh used
to remove the warning without removing the defect.

## What changed

- Each case runs as one unit of work on the mesh's `Tests` I/O pool, which links the case's
  cancellation token to its subscription. When the limit elapses, the runner's timeout cancels that
  token.
- Every case can observe the token, either through `MeshTestContext.CancellationToken` or by
  declaring a trailing `CancellationToken` parameter that the runner fills in.
- After cancelling, the runner waits a short grace for the case to unwind. The verdict then says
  which of two things happened: "cancelled and unwound", or "the case IGNORED its cancellation
  token: still running after it was cancelled" with the remedy spelled out.

- The pool does not order the cases; the runner does. It has room for cases that ignore their
  cancellation, so one such case no longer blocks every case after it. If ignoring cases do fill the
  pool, the next case is reported at once as not run, naming the cases that are still running,
  instead of timing out half a minute later without ever having executed.

The runner cannot stop what a case started without the case's cooperation. What it can do is
refuse to let that go unnoticed, which is what the verdict now does.
