---
Name: Your language no longer depends on when you signed in
Category: Fix
Description: Signing in while the portal was still warming up could leave you with an English interface and a missing delete button for the whole session; both now correct themselves as soon as the answer is available.
Icon: Sparkle
Order: -20260810
---

# Your language no longer depends on when you signed in

The portal reads your display language and time zone from your profile once,
when your session starts. If it asked at a moment when the user directory
could not answer yet — the seconds after a restart, a slow first query, a
storage hiccup — it fell back to English and UTC.

The fallback itself was fine. What was not fine is that nothing ever asked
again. "The directory cannot answer yet" was stored as though it were the
answer, so a German-speaking user who happened to sign in during that window
read an English portal, with timestamps in UTC, until they reloaded the page.
Nothing on screen suggested why, and reloading is not an obvious remedy for a
problem that does not look like one.

Now an unanswered lookup stays an open question. The user directory is kept up
to date by a live subscription, so the moment it can answer, your session picks
up your real profile — your language, your time zone, your name — on its own.
No reload, and nothing to notice beyond the interface being in the language you
chose.

The same correction applies to the delete button on search results. Whether you
may delete something is checked per result, and a check that had not come back
within ten seconds was recorded as "no". That answer stuck for the rest of the
session, so a slow moment could cost you the delete option on items you own,
with no way to ask again. The check now waits for a real answer instead of
inventing one at the ten-second mark; until it arrives, no delete button is
offered, exactly as before.
