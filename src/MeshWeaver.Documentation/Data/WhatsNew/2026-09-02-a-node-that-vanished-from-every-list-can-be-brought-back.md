---
Name: A node that vanished from every list can be brought back
Category: Fix
Description: A handful of skills were still there and still worked if you knew their address, but had dropped out of every list and search — and could not be corrected, because the thing that was wrong about them was the thing deciding who was allowed to correct it. There is now a repair that finds them and puts them back.
Icon: Bug
Order: -20260902
---

# A node that vanished from every list can be brought back

Every node carries a note saying which node it belongs to. Usually it points at itself — that is what
makes it a thing in its own right rather than an attachment to something else. Lists and search are
built on exactly that: *show me the things that point at themselves.*

A small number of nodes ended up pointing somewhere else by accident. Nothing about them looked
broken. They were live, complete, and worked perfectly if you went straight to their address. They
had simply stopped answering "yes" to the question every list asks, so they were missing from all of
them — search, the contents of the page they lived on, everything. No error, no warning, nothing to
notice except an absence, which is the hardest thing to spot.

**And they could not be corrected by hand.** Permission to change a node is decided by that same
note. A node pointing into somewhere it does not belong had its permission question answered by
somewhere it does not belong — so trying to fix the note was refused for not having rights over the
place the broken note pointed at. The thing that was wrong was the thing guarding it.

**There is now a repair that works around that properly.** It runs with the platform's own authority
rather than a person's, so the guard does not apply, and it goes through the normal way of changing a
node so live pages see the correction immediately rather than after a restart.

Two things about it are worth knowing:

- **It finds the affected nodes rather than being handed a list.** The original report named seven.
  It turned out there was an eighth nobody had spotted, and two of the seven were broken in a
  slightly different way than the report described. A repair working from the list would have missed
  all three. This one recognises the *condition*, so it finds whatever is actually there — including,
  in future, anything the list-writers never saw.
- **It never deletes anything.** Some of these come in pairs — two copies of the same thing, each
  pointing at the other. Both get put back to pointing at themselves, and both are kept. Whether one
  of the pair is a duplicate that should go is a judgement about your content, not something a repair
  should decide on its own; it is reported so a person can look.

Running it twice is harmless — the second pass finds nothing, because the repair is simply not a
thing that can be done twice. And on a system with nothing wrong it looks at everything, changes
nothing, and says so.

There is also a report-only mode that finds and lists the affected nodes without touching anything,
so the state of a system can be checked before deciding to change it.
