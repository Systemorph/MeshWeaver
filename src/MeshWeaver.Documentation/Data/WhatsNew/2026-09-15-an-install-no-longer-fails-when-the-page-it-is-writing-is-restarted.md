---
Name: An interrupted install now says what happened, instead of going quiet for half a minute
Category: Fix
Description: When the platform restarted the area an install or import was writing into, creating a page went silent — the caller waited out its whole budget and was then told "the outcome is unknown". It is now told at once, and told which area went away.
Icon: ArrowSync
Order: -20260915
---

# An interrupted install now says what happened, instead of going quiet

Installing a plugin — or bringing a space up to date from its repository — writes hundreds of pages
into one area of the portal. While that is happening, the platform sometimes has to restart that
area: a new page type has just arrived and the area has to pick it up. That restart is normal and
almost always harmless.

Almost. If a page was being **created** at that exact moment, the creation had nobody left to
finish it and nobody left to report it, so it simply went quiet. Whoever was waiting waited out its
whole budget — half a minute — and was then told *"the create produced no response; the outcome is
unknown"*, which says nothing about what happened or where. The same interruption during a page
**update** had been handled for a long time, and reported immediately and by name.

**Creating a page now does the same.** An area that shuts down while it is still working on a
creation says so on the way out, names itself, and the answer reaches the caller in milliseconds
instead of at the end of a timeout. Installs and imports that are interrupted this way now fail
with a sentence you can act on — and the failure names the area to look at, which is the thing that
used to require reading a whole server log to work out.

**And the restart is now held off in more places.** The platform already knew not to restart an area
while a plugin install was writing into it. Importing a space from a repository does exactly the
same kind of writing — including installing new page types, which is what triggers the restart in
the first place — but had never said so, so the restart went ahead regardless. Every repository
import now holds the area for as long as it is writing, and releases it however it ends: on
success, on failure, and when you navigate away from it. That covers the scheduled imports, the
ones a green build triggers, and the **Re-import** button in a space's Git settings.

Nothing to do — plugin installs, repository imports and space updates all pick this up
automatically.
