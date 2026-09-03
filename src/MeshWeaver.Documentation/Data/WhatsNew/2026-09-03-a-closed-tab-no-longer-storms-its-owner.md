---
Name: A closed tab no longer keeps the server busy for it
Category: Fix
Description: Closing a browser tab could leave the server pushing every change to it for up to an hour, refusing each one and slowing everything it touched; the server now learns the tab is gone on the first push and stops.
Icon: Sparkle
Order: -20260903
---

# A closed tab no longer keeps the server busy for it

When you closed a tab, the server did clean up your session — but the parts of the system that had
been sending you live updates were told only that your session "could not be reached right now",
the same message they get while the platform is being updated. They treated it as temporary, as they
should during an update, and kept trying: hundreds of refused deliveries a minute, for as long as
three quarters of an hour, for a single closed tab. Users saw the platform slow down and, for the
person whose tab it was, sessions that kept dropping.

Now a closed session is remembered as closed for a short while, so the first attempt to reach it is
answered with "this session is gone" rather than "not right now". The sender stops at once. Sessions
that are genuinely just moving during an update still get the "not right now" answer and still wait
for it, exactly as before.
