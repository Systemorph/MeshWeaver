---
Name: Invitations take effect the moment someone signs up
Category: Fix
Description: An invitation to a space or a group could sit dormant until the portal was next restarted, so a newly signed-up colleague saw nothing. Deferred invitations now act the instant the account appears.
Icon: Sparkle
Order: -20260813
---

# Invitations take effect the moment someone signs up

When you invite someone who does not have an account yet, the platform stores the invitation and
waits. The instant that person signs up, the stored invitation is supposed to act: it grants them
the role you chose, adds them to the group you picked, and pins the space to their dashboard.

That waiting step had stopped working. The invitation was written correctly and kept safely, but
the background service that watches for the new account could no longer read its own list of
outstanding invitations — so as far as it was concerned there were none, and nothing ever fired.
Nothing failed and nothing was logged as an error; the invitation simply sat there.

The effect depended on timing, which is why it looked so inconsistent. A restart of the portal did
re-examine every outstanding invitation and catch up, so most invitations eventually landed and
looked fine in hindsight. But anyone who signed up *between* restarts got no access at all until
the next one — they arrived at a portal where the space they had been invited to was not there.
Invitations that wait for something other than a sign-up — a scheduled time, or a piece of work
finishing — were never caught up by a restart and so never ran at all.

Both are fixed. Outstanding invitations are readable again, and they act immediately: sign-up,
scheduled time, and completed-work triggers all fire while the portal is running. Nothing about who
gets what has changed — the same role, on the same space or group, arrives when it should instead
of hours later.
