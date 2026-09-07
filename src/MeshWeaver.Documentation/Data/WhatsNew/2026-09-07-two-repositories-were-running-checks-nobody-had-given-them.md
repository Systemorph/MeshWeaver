---
Name: Two repositories stopped skipping checks nobody knew they skipped
Category: Fix
Description: A repository that copied a shared CI lane instead of calling it opts out of every check that lane gains afterwards — quietly, and long after the copy was made. Two repositories were doing that, one of them the largest. Both now call the lane.
Icon: ShieldCheckmark
Order: -20260907
---

# Two repositories stopped skipping checks nobody knew they skipped

The platform keeps one copy of the checks every content repository has to pass, and each repository
*calls* it. A repository that instead copies the steps into its own configuration gets an identical
result on the day it does so — and then stops getting anything the shared copy gains afterwards.

Nobody decides to skip the new check. The copy decides, months of commits later. And from inside
the repository that made the copy there is nothing to see: **a repository that forked a lane before
a check was written looks exactly like one that passes it.**

## What was found

Two of the shared checks are the fleet's, not any one repository's: *no build job may run longer
than 45 minutes*, and *every secret a pull-request job uses is checked for before anything needs
it*. That second one exists because a missing secret otherwise surfaces deep in a build, in a
message that names no secret at all.

Both live inside the shared lane. Neither existed when the older copies were made. So:

- **the plugin catalogue — the largest repository, with the most build lanes and the most
  secrets — was running the secret check nowhere at all.** Running it for the first time named three
  findings immediately.
- the manufacturing repository was running neither.

Both now call the shared lane. Each finding is either fixed or written down with the reason it is
safe, and the written-down form expires by itself: the check refuses an entry once the thing it
excused is gone.

## The part that was less obvious

Calling the lane is necessary and **not sufficient**. Each repository pins the exact version of the
lane it calls, so a repository whose pin is older than a check calls the lane and still does not run
that check.

Measured across every satellite repository: three of them are in that position today. The
repository whose pin is current is also the only one with nothing to report — which is what makes
that a measurement rather than a guess. A pin that nobody moves is not a tidiness problem; it is a
coverage one, and it now reads that way in the documentation and in each build's own summary.

## And the checks were made to fail first

Every check touched here was deliberately broken and observed to go red before being fixed and
observed green — including one whose first version could **not** have failed on the thing it names,
because it was reading a neighbouring value instead of its own. A check that has only ever seen its
passing path is not a check.
