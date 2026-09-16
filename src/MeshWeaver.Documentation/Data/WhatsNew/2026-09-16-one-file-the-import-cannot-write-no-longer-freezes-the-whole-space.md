---
Name: One file the import cannot write no longer freezes the whole Space
Category: Fix
Description: A single source file the store refused made every later import of that Space answer "already up to date" without looking — so a Space could be left missing a file, with no way to repair it and nothing anywhere naming which file was missing. Imports now remember the refusal against that one file, keep importing everything else, and say which node did not land and why.
Icon: RefreshCircle
Order: -20260916
---

# One file the import cannot write no longer freezes the whole Space

When a Space is synced from a repository, the import writes each file into the mesh one node at a
time. Nearly always they all land. Occasionally one cannot — a file that breaks a rule the mesh
enforces, or that the database genuinely will not store. That case has always been isolated: the
offending file is reported and every other file still imports.

What was wrong is what happened **next time**. The import concluded, from that one file, that *this
Space's content cannot be imported* — and recorded it that way. Every later import of the same
content then answered:

> Skipped — an earlier FULL import already recorded this exact content … so the partition was not
> re-read.

Which was not true. The import had lost a file, and the sentence said the content was recorded. From
then on the Space was frozen: nothing was re-read, nothing could be repaired, and pressing **Update**
did nothing but repeat the same sentence.

## What that looked like when it happened

A source file in an internal Space contained a single invisible character that PostgreSQL cannot
store in a text column. One node out of forty-one did not land. Five node types that referenced it
stopped compiling, among them the one that runs every operational action, so for an evening no
action on that portal would run at all.

The only thing anyone could see was a compile error on a symbol whose file is plainly in the
repository. The import had reported *"41 node(s)"* with a count of failures and **no path at all** —
and it had done so in the same ordinary, green-looking line a completely successful import writes.

## What changes

**The refusal is remembered against the file, not the Space.** The next import skips exactly that
one node — so it is not retried pointlessly, which is what the old behaviour was protecting against
— and evaluates everything else normally. A Space that has drifted from what the repository says can
be repaired again, including by the obvious route of pressing Update.

**An import that loses a file says which one, and why.** The path and the reason now appear in the
sync activity, in the import summary, and in the result the platform passes on. *"Refused"* on its
own could not distinguish a character the database will not accept from a rule your content breaks
from a permissions problem — three different problems with three different fixes.

**And it no longer looks green.** An import that could not write everything is now reported as a
warning, and one that failed outright as an error, so the sync activity's own status reflects what
happened rather than reading as a success.

If the file really cannot be stored, the fix is still to correct it in the repository — the platform
will not invent content it was refused. The difference is that the rest of the Space keeps working
while you do, and that you can see which file to correct.

Background, with the measurements: [A Content Verdict Is Per
Node](/Doc/Architecture/AContentVerdictIsPerNode).
