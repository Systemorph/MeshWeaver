---
Name: An update no longer interrupts what you were doing
Category: Fix
Description: When a new version is deployed, the page you have open keeps working until you are finished with it, instead of disconnecting mid-task.
Icon: Sparkle
Order: -20260813
---

# An update no longer interrupts what you were doing

Deploying a new version used to end your session. The new version started up, took over, and the
old one was given fifteen seconds before it was stopped — which was enough for the network to
notice, and nowhere near enough for the page you had open. Anything still in progress simply
stopped: the page reported that it could not reach the server, and whatever you were part-way
through was gone.

That fifteen-second window was never meant to protect your session. It exists so the load balancer
has time to stop sending *new* visitors to the old version. Protecting the work already in flight
was a separate job, and it was missing.

Now the old version waits for you. After the network has been redirected, it keeps serving the
pages that are already open and only shuts down once the last one has been closed — up to a
generous ceiling, so a forgotten browser tab cannot hold a deployment open indefinitely. New
visitors go to the new version immediately, as they always did, so nothing about the update slows
down. The difference is only that finishing your work is no longer a race against it.

This matters more than it used to. Updates now follow a successful build within minutes rather
than waiting for a daily check, so they happen far more often — and an interruption that was once
rare enough to shrug at would otherwise have become a routine annoyance.
