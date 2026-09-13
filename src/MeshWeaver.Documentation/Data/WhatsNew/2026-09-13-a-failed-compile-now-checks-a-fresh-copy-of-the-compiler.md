---
Name: A failed compile now checks a fresh copy of the compiler
Category: Feature
Description: When a NodeType compile fails inside the compiler itself, the diagnostics now also try the same source through a freshly loaded, never-used copy of the compiler, so the report says whether the process or the shared compiler is what cannot emit.
Icon: Bug
Order: -20260913
---

# A failed compile now checks a fresh copy of the compiler

Rarely, a build host reaches a state in which every NodeType compile fails inside the compiler
with the same internal error. The existing diagnostics could already rule out the reference set
and the shape of the source, but every check ran through the one copy of the compiler the process
had been using all along — so "the process cannot emit" and "this copy of the compiler cannot
emit" could not be told apart, and the report pointed at the runtime by default.

The diagnostics now add one more check: the same tiny source is compiled through a second copy of
the compiler loaded from fresh bytes into its own isolated context and unloaded again afterwards.
If that copy emits, the report says the fault lives in the shared compiler's state and where to
look; if it fails the same way, the report says the process genuinely cannot emit, and names the
failing frame to file. The check runs only on a compile that has already failed this way, never on
a normal build.
