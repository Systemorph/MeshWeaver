---
Name: An install no longer fails when the area it is writing is restarted underneath it
Category: Fix
Description: Installing a plugin, or importing a space from a repository, could fail with a timeout when the platform restarted the very area the install was writing into. The install now notices the restart, waits for the fresh one and carries on, instead of giving up with "the outcome is unknown".
Icon: ArrowSync
Order: -20260915
---

# An install no longer fails when the area it is writing is restarted underneath it

Installing a plugin — or bringing a space up to date from its repository — writes hundreds of pages
into one area of the portal. While that is happening, the platform sometimes has to restart that
area: a new page type has just arrived and the area has to pick it up. That restart is normal and
almost always harmless.

Almost. If a page was being **created** at that exact moment, the creation had nobody left to
finish it and nobody left to report it, so it simply went quiet. The install waited for an answer
that was never coming and eventually gave up — *"the create produced no response"*, or, on a bad
day, a ten-minute timeout with nothing to say about what had happened. The same interruption during
a page **update** had been handled for a long time: the update was told the area had restarted, it
tried again against the fresh one, and it succeeded. Creating a page did not get that treatment.

**Now it does.** When an area shuts down owing an answer about a page it was creating, it says so
before it goes, and the install retries against the restarted area — the same way updates already
did. A page that had in fact been created is recognised as existing and updated instead, so nothing
is ever written twice. What used to be a failed install is now a pause of a few hundred
milliseconds.

**And the restart is now held off in more places.** The platform already knew not to restart an area
while a plugin install was writing into it. Importing a space from a repository does exactly the
same kind of writing — including installing new page types, which is what triggers the restart in
the first place — but had never said so, so the restart went ahead regardless. Repository imports
now hold the area for as long as they are writing, and release it however they end: on success, on
failure, and when you navigate away from them.

Nothing to do — plugin installs, repository imports and space updates all pick this up
automatically.
