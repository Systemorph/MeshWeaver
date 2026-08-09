---
Name: One stuck message no longer stops the whole portal from talking to itself
Category: Fix
Description: A single delivery that never finished could hold the portal's message router and silently queue up everything else behind it.
Icon: Sparkle
Order: -20260809
---

# One stuck message no longer stops the whole portal from talking to itself

Everything in a portal — opening a page, saving an edit, running a chat round — is a message handed
to one router that decides where it goes. That router handled one message at a time, and it did the
delivery work itself. So a single delivery that got stuck was enough to hold the router, and every
other message in the portal simply waited. Nothing reported it: the portal answered pages, looked
healthy, and quietly stopped doing anything that needed a message delivered.

In production this happened once and lasted 37 hours. A single delivery sat unfinished for six
hours with more than five hundred messages queued behind it, and it only came to light later as
"this portal is running an old version" — the update it needed was one of the messages in that
queue. Restarting the portal cleared it, which is part of why it stayed hidden for so long.

The router now hands each delivery off and moves straight on to the next one, so a delivery that
gets stuck costs only itself. A delivery that cannot be completed now says so — the sender is told
it failed instead of waiting forever — and a portal whose deliveries stop finishing reports it
loudly in the log rather than leaving it to be discovered days later.
