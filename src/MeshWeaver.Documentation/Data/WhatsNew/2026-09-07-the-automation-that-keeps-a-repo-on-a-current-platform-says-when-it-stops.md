---
Name: The automation that keeps a repo on a current platform now says when it stops
Category: Fix
Description: A nightly job kept every satellite repository building against a recent platform. It had never once succeeded — and because it failed on a schedule rather than on anyone's pull request, nothing said so until an unrelated repository's course content went stale.
Icon: AlertUrgent
Order: -20260907
---

Each repository that ships modules pins **which** platform commit it compiles against, and a nightly
job is what keeps that pin from falling behind. That job had never worked. Not once, in either
repository that runs it — the pin lives inside a workflow file, and the identity the job pushes as
was never granted permission to touch one, so every attempt was refused at the last step.

The job was not sloppy about it. It refused to swallow the error, it went red honestly, and it did
so every night. But a nightly job hangs off no pull request and no review, so its red landed in an
empty room — **which, from outside, looks exactly like a job that had nothing to do.**

What that cost, on one morning: the pin froze, drifted for four hours, and then crossed the bound
that stops the repository. Every open pull request there went red at once, on a check none of their
changes could have caused. Nothing was published while that lasted, so a corrected course quiz
finished, merged and verified in another repository simply never reached the people taking the
course — and the session investigating *that* was looking at a quiz bug, three repositories away
from the cause.

Two changes. The job now asks for the permission it needs **up front**, so a missing grant is
reported by the step that wanted it instead of arriving four steps later as a push rejection about
something nobody requested. And when it fails, it now opens a tracking issue in the repository it
was supposed to help — kept current rather than repeated nightly, and closed automatically the
moment the job works again.

The last part matters more than it looks: clearing the symptom by hand moves the pin and leaves the
automation just as dead as before, which is how the same morning happened twice. The issue stays
open until the thing that was supposed to prevent it actually runs.
