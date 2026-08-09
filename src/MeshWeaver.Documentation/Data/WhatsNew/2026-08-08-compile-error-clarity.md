---
Name: A compile problem now tells you whether it is your code or a timeout
Category: Fix
Description: The "this page can't be displayed" overlay now says whether the compiler rejected your code or a lookup simply timed out — and links the full compile log.
Icon: Sparkle
Order: -20260808
---

# A compile problem now tells you whether it is your code or a timeout

When a page could not be built, it always said the same thing: *"There was a
compilation error in this item's code… Please correct the code."* That was
right only some of the time. A registration lookup that timed out after three
seconds, a build that had not settled yet, an assembly the node could not fetch,
and a type built by a previous platform version all produced that identical
sentence — sending you to edit source the compiler had never even read.

Those cases now say what actually happened, and lead with **"No code change is
needed for this."** followed by what to do instead (usually: wait, or Recompile).
A genuine compiler error is unchanged — it still shows the diagnostics and still
asks you to correct the code, because there it is the right advice.

Two more things came with it:

- **The broken page links its compile log.** The "View full compile log →" link
  used to exist only on the type's own page; now the item's page carries it too,
  which matters most on a timeout — the log is the only record of how far the
  build got.
- **"Could not be determined" is now a state of its own.** A build whose state
  never came back is no longer recorded as a failure, so nothing downstream —
  the type's page, the status badge, the `GetDiagnostics` answer an agent reads —
  reports a healthy type as broken.
