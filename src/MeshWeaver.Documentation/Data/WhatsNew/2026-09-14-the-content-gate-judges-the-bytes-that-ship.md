---
Name: The content gate judges the bytes that ship, instead of quietly rebuilding them
Category: Fix
Description: The gate that checks a repository's content before publishing is meant to exercise the exact assemblies that are about to be shipped. When it declined one of them it silently fell back to rebuilding that piece from source — in an environment that has never once compiled anything successfully — and reported hundreds of unreadable errors instead of the one sentence that mattered. It no longer rebuilds anything.
Icon: Checkmark
Order: -20260914
---

# The content gate judges the bytes that ship, instead of quietly rebuilding them

Before a repository's content is published, a gate stands up a real mesh and runs it against the
assemblies that were just built for it — not a private rebuild. That distinction is the whole point:
a gate that rebuilds is checking bytes nobody will ever run.

There was a way back into rebuilding, and nothing was watching it. When the gate declined one
prepared assembly — because the sources had moved since it was built — it fell through to compiling
that one piece itself. The environment it compiled in has never successfully compiled anything: on
the run where this was found, ninety-six assemblies were accepted as prepared, two were declined,
and both of the rebuilds that followed failed. The same count on every other repository using this
gate: everything accepted, nothing rebuilt, ever.

What the failure looked like was hundreds of "could not be found" errors naming types the piece
legitimately depends on, which reads like the content is broken. What it actually meant was one
sentence printed two lines earlier: the prepared assembly was declined, and was judged **compatible**
— safe to use — a moment before being thrown away for a rebuild that could not succeed. The
publication then never completed, so roughly seven hundred commits of that repository reached no
running portal for two days.

**The gate now rebuilds nothing.** A prepared assembly that is still compatible is used, which is
what the gate exists to check. One that genuinely cannot be used — a real incompatibility, or a
missing build — is reported as exactly that, in one line, at the highest severity, instead of as a
wall of compiler errors about something else. Nothing that passed before starts failing: acceptance
was already the path every repository's gate took.
