---
Name: Timestamps show your own time zone again
Category: Fix
Description: The portal stopped finding most users' profiles, so their time zone and language silently fell back to UTC and English. It reads the full user directory again.
Icon: Clock
Order: -20260821
---

# Timestamps show your own time zone again

If your profile says `Europe/Zurich`, every timestamp in the portal is supposed to be shown in
Zurich time. For most people it had quietly gone back to UTC — an hour or two out, and on a late
evening a whole day out — and the portal was serving English chrome to users whose profile asks for
German.

The setting was never lost. What broke was the lookup that turns your sign-in into your profile.

The portal keeps an index of every user, and asks it "who is this email?" once per sign-in; your
time zone and your language ride along on the answer. That index was being filled with a *search*
rather than a *listing*, so it only ever contained the fifty most recently edited profiles. Everyone
else came back as "no such user" — a perfectly confident answer, with no error anywhere — and the
portal fell back to what it could read off the sign-in alone: your name and your email, but no time
zone and no language.

It also got worse by itself. A profile's timestamp only moves when the profile is edited, so the
longer you went without touching yours, the more certainly you had dropped off the list.

The index now reads the whole directory, and reads it as the platform rather than as whoever
happened to open the portal first after a restart. It also survives a couple of internal shapes a
profile can arrive in that used to make it skip a user entirely.

Nothing you stored changed: times are still kept in UTC and only converted for display, and your
time zone and language preferences are exactly as you left them. Sign in again — or reload — and
the clock is yours.
