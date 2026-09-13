---
Name: Module updates stop being held back by a check that could not read them
Category: Fix
Description: A safety check that stands a module down when a newer commit is already on its way could never actually read which module it was looking at, so it stood every module down instead. For eight hours no module update reached anyone.
Icon: Bug
Order: -20260913
---

# Module updates stop being held back by a check that could not read them

When several builds of the same repository are in flight at once, the build lane asks one question
right before it hands a module over: *is a newer commit already on its way that would rebuild this
module anyway?* If the answer is yes, this build stands the module down and lets the newer one
publish it — and if the lane **cannot tell**, it deliberately publishes nothing, because shipping an
already-stale module is worse than shipping it a few minutes later.

That check could not read which module it was being asked about. It requested the module's build
description from the lane's own bookkeeping using a lookup that only reaches *fields inside* that
description — never the description itself. The request could therefore never succeed, and it
quietly returned the empty placeholder it had been given as a fallback. The next step looked at the
empty placeholder, correctly said *"this names no module"*, and "cannot tell" did the rest.

The result on 2026-09-12: for roughly eight hours, **no module update reached the registry at all**.
Every build was green up to that point, every module was built and tested, and every single one was
held back. Nothing in the logs named the module that had gone missing, because the message saying so
was being fed into the next command as if it were data.

## What changes

- The lane can now ask for a module's whole build description directly, and there is **no fallback**:
  a module the lane cannot describe is a **failure that names the module and the stage it stopped
  in**, never an empty placeholder passed quietly to the next step.
- Failure messages from that bookkeeping tool now go to the error channel instead of the output
  channel, so they appear in the log and name the module rather than being swallowed by whatever
  reads the output next.
- Both behaviours are pinned by the tool's own self-test, which runs on every build.

You do not need to do anything. Module updates flow again on the normal schedule.
