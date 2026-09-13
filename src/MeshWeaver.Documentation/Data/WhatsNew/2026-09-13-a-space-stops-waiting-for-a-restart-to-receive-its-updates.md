---
Name: A Space stops waiting for a restart to receive its updates
Category: Fix
Description: A Space synced from a repository used to stop receiving updates silently — its own record said "up to date" — until the installation was restarted. The updates now arrive as soon as they are ready, and an installation that genuinely cannot receive them says so publicly instead of looking idle.
Icon: Checkmark
Order: -20260913
---

# A Space stops waiting for a restart to receive its updates

A Space can be kept in step with a repository: when the repository's code passes its checks, the
Space receives the new content. There is a safety rule in the middle of that, and it is a good one —
the content is only taken once the platform has finished preparing a version of it that *this*
installation can actually run. A Space never receives content built for a platform it is not on.

The rule was right. The moment it was checked was not.

## What went wrong

The check ran when the repository's checks went green — which is *before* the platform finished
preparing the content. So the answer was always "not ready yet", correctly, and the Space waited.

The preparation then finished a few minutes later, and **nothing looked again**. Worse, the next
green run did not rescue it either: that run asked about a *newer* change, which had not been
prepared yet either. Each update pushed the answer one step further out of reach.

The practical effect was that a Space could stop receiving updates entirely and only start again
when the installation was restarted. On two installations this ran for nine hours across several
released versions. Nothing looked wrong from the outside: the Space's own record read *up to date,
nothing pending*, because "we decided not to take this one" left no trace at all. Two separate
investigations read that record as healthy before the Space's activity history — which had simply
stopped — gave it away.

## What changes

**The content arrives when it is ready, not when something else happens to ask.** The platform
already announces each finished preparation. That announcement is now what releases a waiting
Space, so the wait lasts as long as the preparation does — usually a few minutes — instead of until
the next restart. Nothing polls and nothing retries on a schedule; the Space simply reacts to the
announcement.

**A Space that is waiting says so on its own record.** Its state reads *Held*, with the reason in
plain words — which version this installation has prepared, and which one the repository has moved
to. "Waiting for preparation" and "up to date" are no longer the same thing to read.

**And an installation that cannot receive updates at all now says so publicly.** Its status page
carries a new `publication-seal` entry naming every repository whose updates are being held and how
long this installation has been holding them. It prints its reading **whether or not anything is
wrong**, because "nothing is being held" and "nobody is looking" must not be the same silence. It
turns amber only once a hold outlives the longest a preparation is allowed to take — so the ordinary
few-minute wait stays quiet, and a genuine stall is visible.

## What this does not change

**The safety rule is untouched.** A Space still never receives content prepared for a platform this
installation is not running. Nothing here relaxes that; it only stops the question being asked at
the one moment it could never be answered yes.

**One case still needs a person.** If an installation is running an older platform than the one the
content is being prepared for, no amount of re-asking helps — there is genuinely nothing prepared
for it. That case is now clearly reported instead of being invisible, but resolving it means
updating the installation.
