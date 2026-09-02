---
Name: A type no longer records a build it did not run
Category: Fix
Description: A node type's "last built" time was being moved forward by things that had not built anything — handing back bytes that already existed, or simply reaching the next step of a build already under way. Two internal safety checks read that time as proof, and could be satisfied without a build ever running.
Icon: ClockDismiss
Order: -20260902
---

# A type no longer records a build it did not run

Each node type keeps two timestamps: when its last build **started**, and when it last **succeeded**.
They look like bookkeeping, and mostly they are — until something reads them as evidence. Two things
do, and both were being told a story that had not happened.

**Handing back bytes is not building them.** When something asks a type where its compiled code
lives, the usual answer is already on the shelf: it was built weeks ago, the result is in the store,
and nothing needs to run. That answer was nonetheless recorded as a *fresh success*. Two internal
safety checks exist precisely to tell "this type genuinely rebuilt" from "this type is showing me
the same result as before" — and they do it by requiring the success time to have moved. Handing
back existing bytes moved it, so both could be satisfied without a build ever running. One of them
guards a case that matters: a type whose compiled code has gone missing from the shared store. It
would have reported the repair as done.

**And a build that is under way starts only once.** The "started" time was written twice per build:
once at the moment the build actually began, and again a step later, after the build's log entry had
been created — a step allowed up to ten seconds. The second write pushed the recorded start forward
past that gap, so any source file changed inside it looked *older* than the build. That is exactly
the signal the platform uses to notice a build that read half-applied changes, and it was being
erased by the write that followed it.

**Both timestamps now describe what actually happened.** A success time is written only by a real
build; a start time is written once, by the moment the build starts. Nothing you see changes — type
pages, build history and compile logs carry the same information — but the platform can now tell a
genuine rebuild from a repeat answer, and can once again see when a build read source that was being
replaced underneath it.

There is a second benefit worth naming. A node type's record is watched by everything that uses the
type, and every change to it is stored and broadcast. Because these two timestamps always looked new,
a write that otherwise repeated the stored record word for word could not be recognised as a
no-change write, and went through in full. Those writes now settle to nothing, which is what they
always were.
