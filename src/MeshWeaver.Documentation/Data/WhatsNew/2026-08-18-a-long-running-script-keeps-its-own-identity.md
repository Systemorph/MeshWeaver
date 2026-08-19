---
Name: A long-running script keeps its own identity
Category: Fix
Description: A script that logged past the activity window could silently continue as the system account; it now stays the user who submitted it.
Icon: ShieldError
Order: -20260818
---

# A long-running script keeps its own identity

An activity log keeps only the newest slice of a run's output on the node itself and files the
older lines away into a segment beside it. That filing step is a privileged write, and it was
opening its system-identity scope on the thread that happened to be logging — the script's own
thread — while closing it somewhere else entirely.

The result was that a script which produced enough output to trigger the filing carried on as the
system account for the rest of its run. Anything it read afterwards was read with system rights
rather than the submitter's, which for a document export meant an embedded area the requester was
not allowed to see could still be resolved into the exported file.

The privileged scope is now opened and closed around the write alone, so nothing after it inherits
the elevation. Scripts, exports and any other activity that outgrows the log window now read
exactly what their submitter is allowed to read.
