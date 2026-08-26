---
Name: Signing in moments after a restart no longer strands you on UTC and English
Category: Fix
Description: The wait that repairs your identity while the portal's user directory is still filling could miss the very moment it filled, and then wait a full minute for an event that was never coming again.
Icon: Globe
Order: -20260825
---

# Signing in moments after a restart no longer strands you on UTC and English

When the portal has just started — a deploy, a restart, a scale-up — its directory of users is still
filling. A sign-in that arrives in that window cannot be answered yet: "I have no record of this
email" and "I have not finished reading the records" look identical from the outside, and the portal
deliberately refuses to guess between them, because the first is what drives onboarding. So it waits
for the directory to finish, and then answers.

The wait had a gap in it. It took its reading of the directory *before* it started listening for the
directory to change — a handful of instructions apart, but the announcement fires exactly once, when
the first full snapshot lands, and it is not repeated. If the snapshot landed inside that gap the
announcement went to nobody, and nothing would ever fire again: a mesh that has finished writing has
nothing left to announce. The wait then ran out its full minute and gave up.

What you saw when it happened: your profile did not reach your session. The portal kept the details
that came with the sign-in itself, which carry no time zone and no language — so every timestamp
rendered in UTC and every string in English, regardless of what your profile says, until you
reloaded. Nothing was logged, and nothing on screen suggested anything had gone wrong.

The wait now subscribes first and reads second, so a snapshot that lands while it is taking its
reading is still seen. Ordinary sign-ins — the overwhelming majority, where the directory has been
ready for hours — are unchanged: they are answered from the first reading and never wait at all.
