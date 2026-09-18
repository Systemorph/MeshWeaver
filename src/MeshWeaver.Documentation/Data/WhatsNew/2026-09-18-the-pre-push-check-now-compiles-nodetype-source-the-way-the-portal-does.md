---
Name: The pre-push check now compiles NodeType source the way the portal does
Category: Fix
Description: The check that runs before code reaches the portal used to compile each of a NodeType's files on its own, while the portal joins them into one. Warnings that only appear once the files are joined were therefore invisible to it — reported later, by the build, as a count against the NodeType with no file and no line, against content whose owner could not reproduce them. The check now joins them the same way and names the file and line of every diagnostic.
Icon: CheckmarkCircle
Order: -20260918
---

# The pre-push check now compiles NodeType source the way the portal does

A NodeType's C# lives in the mesh, not in a project, and the portal compiles it when it loads the
node. Several files can belong to one NodeType — the type's own `Source`, its `Test` folder, a
snippet shared from another module — and **the portal joins them into a single piece of code**
before handing it to the compiler, lifting every `using` line to the top.

The check that runs before that source reaches a portal did not. It handed the compiler each file
separately, which is a perfectly valid way to compile them and **not the one that ships**.

## Why that gap was invisible rather than merely different

Several things in C# apply to *a file*, from where they are written to the end of it. The important
one here is the line that switches on null-safety checking. In the joined form, a file that turns it
on turns it on for **every file that follows** — including files whose author never asked for it,
and including generated code. Compiled one file at a time, it reaches nothing past its own file.

So the check saw a different program from the one the portal runs, and the difference went only one
way: **the check was clean, and the later build was not.** Worse, the build reports those in
aggregate — a code and a count, no file and no line — and they were recorded against the
*NodeType's* name. Nobody who owned that NodeType could reproduce them, and a note explaining that
the debt was genuine and belonged to that content was, twice in one file, simply wrong: both times
the fault was in generated code, and once it was an invisible stray character a generator had
written into the middle of a file.

## What changed

The check now builds **one piece of code per NodeType, joined in the same order, with the `using`
lines lifted and de-duplicated exactly as the portal does it** — and the two are now compared
against each other automatically, so they cannot quietly drift apart again.

Two things follow. **Diagnostics the check could not see before are now reported before the push**,
which is the point: they were always real, and they were always going to be reported — just later,
to somebody else, about the wrong thing. And **every diagnostic now names the file and the line it
came from**, instead of a count attributed to the NodeType as a whole.

One class of finding is fixed rather than reported, because it was never the content's to fix: the
byte-order mark some generators write into the middle of a file. It is invisible in an editor, it
is harmless in a file compiled on its own, and in the joined form it hides the `using` line behind
it and breaks the compile. The check now strips it on reading, the same way the portal's own file
reader does.
