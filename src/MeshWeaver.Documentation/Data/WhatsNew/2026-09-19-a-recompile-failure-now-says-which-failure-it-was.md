---
Name: A recompile failure now says which failure it was
Category: Fix
Description: One log line reported every reason a NodeType release can fail to start — a missing node, a permission, a store outage, a host shutting down — through one sentence with the reason tucked inside a parameter. Four unrelated faults therefore arrived as one ticket that could never be closed. Each cause now has its own line naming its own class, decided where the failure happened rather than guessed from its wording.
Icon: Bug
Order: -20260919
---

# A recompile failure now says which failure it was

After content lands, the platform asks every affected NodeType to release — recompile — so the mesh
stops executing the assembly the change just invalidated. When one of those requests cannot even be
*started*, that was reported like this:

```
[Recompile] Release request for Store/Core failed: <the whole reason>
```

One sentence, for every possible reason. The reason itself sat inside a parameter, so from the
outside all of these looked like the same line:

- **there is no node at that path** — a release was asked for something that does not exist, which is
  not a fault at all;
- **the caller may not compile it** — the permission gate answering correctly;
- **the owning hub reached no verdict** — the write may still apply, and is not confirmed;
- **no initial state for the node ever arrived** — the write had nothing to diff against;
- **the host was shutting down** mid-write;
- **the data store could not be reached**.

Measured on the production incident this fix comes from: **326 occurrences over five weeks and
sixteen pods, filed as one issue**, whose retained evidence carried four of the causes above at once.
That is why it kept coming back — closing it against any single cause was followed within days by a
recurrence on a different one. And the worst case was invisible even to a careful reader: the words
that said *which* thing had been disposed sat on a second line that never reached the report at all.

Every class now has its own line, and the class is written into the message rather than hidden in a
parameter — so a reader sees the cause at a glance, and the same distinction reaches the ticket that
gets opened. The class is decided at the point the failure happens, from the typed error the owning
hub sent back, never by reading the sentence afterwards; a cause nothing recognises says exactly
that, instead of being filed under whichever class looks closest.

The path and the reason stay where they were, so everything already built on them keeps working, and
nothing about which failures are *reported* changed — only how well they can be told apart. The
causes themselves keep their own owners: naming them is what lets each be fixed and closed on its own
evidence instead of reopening one shared ticket forever.
