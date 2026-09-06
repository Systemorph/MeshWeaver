---
Name: A server running an old add-on now says so
Category: Fix
Description: When an add-on updated itself, the servers that were already running kept the previous version until their next restart — and reported themselves completely up to date while they did. They now say which add-ons they are behind on, so nobody is left wondering why the same page behaves differently on two visits.
Icon: ArrowSync
Order: -20260906
---

# A server running an old add-on now says so

The portal runs on several servers at once, and add-ons update themselves in the background. An
update is written once, for everyone, and each server picks it up the next time it restarts. That
is deliberate: swapping code underneath a running server is how you get half-updated behaviour.

The gap was what happened in between. A server that had not restarted yet kept the previous version
of the add-on — correctly — and reported itself **completely up to date** while it did. The check
behind that report asked only *"is this add-on running here at all?"*, and the answer was yes; it
had simply never been taught to ask *"is it the version everyone else agreed on?"*.

So two servers could serve the same portal from two different sets of add-ons, for as long as
neither restarted, with nothing anywhere saying so. On one visit a page would look one way, on the
next visit another, and every health signal read green. It also confused the portal's own build
machinery: a page type rebuilt on one server, then rebuilt again on the other, each undoing the
other's work — and where an add-on was missing from one server entirely, a page type that had been
working simply stopped.

Each server now compares what it is actually running against what has been published, version by
version, and reports the add-ons it is behind on by name. The remedy is unchanged and was always
the same one — a restart — but it is now a stated to-do instead of something you had to guess at.

Two things it deliberately does **not** do. It never asks for a restart it cannot promise will
help: if an add-on's newly published files are missing, it says *re-install* instead, because no
number of restarts would fix that. And where a server genuinely cannot tell which version it loaded,
it stays quiet rather than raising an alarm nothing can clear.
