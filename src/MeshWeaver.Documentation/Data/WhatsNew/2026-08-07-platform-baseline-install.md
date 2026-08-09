---
Name: The standard agents and skills are part of every installation
Category: Fix
Description: The agents and skills MeshWeaver ships are now installed and published on every installation automatically, and an installation whose catalog went missing repairs itself on the next start.
Icon: Bot
Order: -20260807
---

# The standard agents and skills are part of every installation

The agents and skills that come with MeshWeaver — the Assistant and its specialists, the standard
skills behind `/code`, `/slide` and the rest — are part of what an installation *is*. Until now they
arrived only where somebody had wired them up by hand, and an installation that lost them had no way
back: the agent picker simply came up empty for everyone, with nothing an administrator could click
to fix it.

Both catalogs are now part of every installation's standard content. They install on first start,
they are published so that everyone can read them — including people who are not administrators and
visitors who are not signed in at all — and they are checked again on each restart, so an
installation that has lost them repairs itself the next time it starts. An installation already
carrying them does nothing: the check costs one catalog listing and writes nothing.

Publication is done by the installer itself rather than by a hand-placed setting, which is what
makes it survive an update. An installation that already had its own access policy keeps it
untouched.

Operators who curate their own content can opt out. Leaving the setting alone keeps the standard
catalogs — which is what almost every installation wants, since the platform's own features assume
they are there.
