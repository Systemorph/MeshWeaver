---
Name: A long-running script is no longer stopped after 15 quiet minutes
Category: Fix
Description: A script that was still working 15 minutes after anything was last sent to it was stopped mid-run, silently — no result, no error, and the run never finished. Scripts now run for as long as they work, and an idle kernel is still cleaned up 15 minutes after its last run ends.
Icon: Timer
Order: -20260915
---

# A long-running script is no longer stopped after 15 quiet minutes

A script that ran for a long time could simply **stop**. Not fail: stop. Its output stopped growing,
no error appeared, and the run never reached *Done* or *Failed*. Anything waiting on it waited until
its own watchdog gave up. An approved operation request did exactly that on 2026-09-15: it reported
its last line at 10:45, and nothing after.

The cause was the kernel's idle clean-up. A kernel that nobody uses is closed after 15 minutes, so an
abandoned one does not hold memory for ever. "Nobody uses it" was measured as "nothing has been sent
to it for 15 minutes". A running script sends the kernel nothing, though. It works on its own, and
its progress lines travel the other way. So a script still busy 15 minutes after its last input
looked idle, and the clean-up closed the kernel under it.

## What changed

**Idle now means "nothing sent to it *and* nothing running".** While a submitted script has not
answered, the clean-up keeps the kernel and checks again one window later. When the script finishes,
fails or is stopped, the 15 minutes start again from that moment.

Two limits stay in place, so a kernel cannot be kept forever:

- **A finished kernel is still closed 15 minutes after its last run.** That behaviour is unchanged.
- **A kernel whose executor has died is closed.** A dead executor can never answer, so the script it
  was running no longer counts as working.

## What you will see

Long scripts, such as a migration, a partition drain or a provisioning run, now finish or fail with
their own result. They no longer go quiet part-way through. Nothing needs to be re-installed: the fix
is in the platform and arrives with the next update of your deployment.
