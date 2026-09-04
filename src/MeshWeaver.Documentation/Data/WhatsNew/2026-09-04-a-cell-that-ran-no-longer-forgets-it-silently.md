---
Name: A cell that ran no longer forgets it silently
Category: Fix
Description: Running a code cell records when it ran, who ran it, and which run produced the output on screen. That record could quietly fail to save — leaving a cell that had just executed still reading as never run, with nothing anywhere saying so. It now saves in a case where it used to vanish, and when it genuinely cannot, that is reported instead of swallowed.
Icon: Bug
Order: -20260904
---

# A cell that ran no longer forgets it silently

Press **Run** on a code cell and two separate things are written. The run itself goes to its own
activity — the transcript you watch scroll past in the output pane. Separately, a short note is
written back onto the cell: *when* it last ran, *who* ran it, *which* activity holds the output, and
a fingerprint of the exact code that was submitted. That note is what lets the cell say "Last
executed 10 minutes ago" on your next visit, and what lets it warn you when you have edited the code
since the output below it was produced.

The note is a separate save, and a save can fail. When it did, nothing said so. The cell went back
to reading as though it had never been run at all — indistinguishable from a cell nobody has ever
touched — and the only trace was a low-priority line in the server log that no alert was watching.

Two things change.

**One case that used to lose the note now keeps it.** Depending on how a cell's content arrived at
the server, the save could quietly decide there was nothing to write and report success anyway.
Nothing was saved, nothing failed, and nothing was logged — the run had happened and the cell simply
did not know. That path no longer exists: the save either writes the note or reports that it could
not, and it can no longer overwrite a cell with a blank one while trying.

**When the note genuinely cannot be saved, that is now an alertable fault.** It is recorded once, at
error level, naming the cell, naming where the run's transcript actually is, and stating plainly
that the run itself succeeded — so nobody goes hunting a script failure that never happened.

Nothing about running cells has changed, and no output that used to appear has moved. What changed
is that "this cell has never been run" is now a statement the cell has actually earned.

One thing is still open: opening a cell after a page reload, you cannot tell "never run" apart from
"ran, and the note did not save". The run is not lost — its activity records which cell produced it
— but the cell has no pointer back to it. Reconnecting the two is tracked separately.
