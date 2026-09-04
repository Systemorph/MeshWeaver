---
Name: A compile that could not run now says what to do about it
Category: Fix
Description: When a compile aborts because the whole process has lost the ability to produce an assembly, the message it leaves behind used to end with an instruction nobody could carry out — fetch a crash dump that this kind of failure never produces. It now reports what was actually measured inside the failing process, and names a next step that can be taken.
Icon: Bug
Order: -20260904
---

# A compile that could not run now says what to do about it

Very occasionally a portal or a test host loses the ability to build **any** assembly at all. It is
not your code: from that moment every compile in that process fails the same way, including ones
that were succeeding a second earlier, while everything that only needs to *check* code carries on
returning correct errors and warnings. MeshWeaver already recognises this and refuses to blame the
NodeType — the type is left as **unavailable**, not as broken, so the next healthy process picks it
up again.

What it did not do well was explain itself. The diagnostic it left behind ended with *"capture a
core dump"* — and this particular failure can never produce one. A dump is written when a process is
killed by the operating system; this process is not killed, it keeps running until its host gives up
on it. So the one concrete instruction in the message was a dead end, every time, for a week.

## What changes

**The message reports a measurement instead of asking for an artifact.** At the moment the failure
happens, the process now re-runs the exact read the failure came from — twice, by two different
routes — and records what each answered. That is the evidence the crash dump was being asked for,
taken in the place where it is available: inside the failing process, at the instant it fails, in the
log that is already collected.

**And it names a step that can actually be taken.** Instead of the impossible one, the message now
says which setting to re-run with, and warns that a single clean re-run proves nothing at this
failure's rate — so nobody reads one quiet run as a fix.

Nothing about a healthy compile changes, and nothing about the way a genuinely broken NodeType is
reported changes: real compile errors are still real compile errors.
