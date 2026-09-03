---
Name: Automatic updates now migrate the database before they roll
Category: Fix
Description: An automatic update could roll the platform to a build that needed a newer database schema without updating the schema, leaving the new version unable to start while the old one kept serving; the schema is now migrated first, and an update whose migration fails is refused rather than applied.
Icon: Sparkle
Order: -20260903
---

# Automatic updates now migrate the database before they roll

When the platform updates itself, the new build sometimes needs a change to the database first.
Until now the automatic update only swapped the running software and left the database as it was.
When the two disagreed, the new version refused to start — correctly — but the old version kept
answering, so from the outside nothing looked wrong. Two portals sat in that state for most of a
day on 2026-09-03: current software that could not start, older software still serving, and on one
of them a slowdown the newer build had already fixed.

An automatic update now runs the database migration first and waits for it to finish before it
touches the running software. If the migration fails or does not complete, the update is refused
and the reason is recorded, instead of rolling forward onto a database it cannot use. Installations
that have not yet been granted permission to run the migration this way still update as before and
say so plainly in their logs.
