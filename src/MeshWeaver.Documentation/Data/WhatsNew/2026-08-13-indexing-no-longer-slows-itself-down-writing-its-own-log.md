---
Name: Indexing and repository import no longer slow themselves down writing their own progress log
Category: Fix
Description: Re-indexing a large collection got slower the longer it ran, because each file's progress line rewrote the entire log. The detail is now written once per collection, with a periodic heartbeat so a long run still shows life.
Icon: TopSpeed
Order: -20260813
---

# Indexing and repository import no longer slow themselves down writing their own progress log

Re-indexing a content collection, or importing a repository, writes a line of progress for
everything it touches. Recording one of those lines means rewriting the whole progress log,
so a run that touched a thousand files rewrote a thousand-line document a thousand times.
The work of *reporting* the job grew faster than the job itself — a long run spent more of
the server's time on its own log than on the indexing, and everything else sharing that
server slowed down with it.

The per-file detail is now collected and written **once per collection**, which makes the
cost grow in step with the number of files instead of with its square. Nothing is lost: every
file is still named in the log, in order, and still lands before the run reports finished.
A long walk also emits a short "N files processed" line as it goes, so you can still see it
is alive.

Repository import got the same treatment for files it cannot parse. Those used to be reported
one write at a time — and, because import parses several files at once, those writes competed
with each other on the same record. They are now reported together, in a single entry that
still names every file that was skipped and why.
