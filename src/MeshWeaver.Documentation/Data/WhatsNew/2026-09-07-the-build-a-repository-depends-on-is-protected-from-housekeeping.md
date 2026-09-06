---
Name: The build a repository depends on is protected from housekeeping
Category: Fix
Description: Naming a deleted build the morning after was only half an answer. Every build anything still depends on is now marked as protected each night, two hours before the housekeeping runs, so it is skipped rather than cleared away.
Icon: LockClosed
Order: -20260907
---

# The build a repository depends on is protected from housekeeping

Each content repository, and each running deployment, is tied to one specific known-good build of
the platform. Those builds live in an image registry, and the registry clears out old ones on a
schedule so that storage does not grow without limit. The two arrangements disagreed: a repository
is meant to stay on its chosen build for weeks, while the housekeeping kept only the newest handful
and counted "old" by how many *newer* builds existed. The more often the platform was rebuilt, the
sooner someone's chosen build was cleared away.

On 5 September three repositories stopped at the same moment because that had happened. A daily
sweep now names a missing build the morning after — but naming it is only half an answer, because by
then it is gone.

The registry's housekeeping already skips anything explicitly marked as protected. So a job now
runs each night, two hours before the housekeeping, reads every build that anything still depends
on, and marks each of them. Nobody has to remember to add one: the list is read fresh each night
from the repositories themselves and from the deployment settings, which are two separate places
and had to both be looked at — the build a deployment names is written down quite differently from
the build a repository's checks name, and a job that read only one of them would have left the other
kind unprotected while reporting success.

It reports how much it examined, not just what it found, so "everything is protected" and "nothing
was looked at" can be told apart. If any repository cannot be read, or any entry cannot be matched
to a build, the run fails and says which — a partial reading must not be mistaken for a complete one.

**It only ever adds protection; it never removes it.** Removing protection is the one direction that
can lose something for good, and it is least safe in exactly the situation where the reading is
incomplete, because "nothing depends on this any more" and "I could not read the thing that depends
on it" look identical from the outside. The job works out which protections look unused and lists
them for a person to review, and that is where it stops unless someone deliberately turns the other
half on. The first live run showed why: three of the five protections that had been placed by hand
were for a build that a *pending* change will start using, and an automatic clean-up would have
removed exactly the ones being kept ready.

The housekeeping configuration itself lives in the registry rather than in any repository, so a copy
of it — and the reasoning behind it, from both incidents — is now kept alongside the job, together
with a way to check that the two still agree.
