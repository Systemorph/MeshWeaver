---
Name: Search stops coming up empty after a deployment changes its AI keys
Category: Fix
Description: On installations that supply their AI keys through the chart's legacy escape hatch, the database migration received none of them, so everything written before the change stayed unsearchable by meaning — and the migration reported success anyway. It now receives the same keys the portal does.
Icon: Search
Order: -20260907
---

# Search stops coming up empty after a deployment changes its AI keys

Semantic search — finding a page by what it *means* rather than by the words it literally contains
— only works for content that has been indexed. Content written **before** an installation had an
AI provider gets indexed by a catch-up pass that runs during a database update. That pass needs the
installation's AI key.

On installations that supply that key through the chart's older, by-name mechanism, **the update
never received it**. Not a wrong key — no key. The catch-up pass saw "no provider configured",
skipped every single page, and the update finished by reporting success. Nothing in the run said the
index was empty.

The visible effect is the one that is hardest to attribute: search keeps working, but it quietly
falls back to matching literal words. A page you know exists is not found by a phrase that describes
it. Nothing is broken, nothing is logged as an error, and there is no obvious moment when it started.

## What changes

**The database update now receives exactly the same secrets the portal receives.** Both delivery
mechanisms, not just the newer one: whichever way an installation supplies its keys, both parts of
the deployment get the same set. Where the keys come from a secure vault, the update also attaches
that vault directly, so it reads the key that is current *now* rather than a copy some other part of
the system happened to leave behind.

**An installation that already worked keeps working, unchanged.** Installations that do not use the
older mechanism render exactly the same deployment as before — byte for byte, verified against the
three shipped configurations.

**The gap cannot silently reopen.** A check now fails the build if one part of the deployment carries
the environment's secrets and another does not, or if a secret source is attached without the vault
that keeps it current. The previous check could not see this case at all.

## What you may need to do

If your installation's semantic search has been returning nothing for older content, the catch-up
pass ran without a key at some point. It runs again on the next update and indexes what it missed —
no re-install, nothing to clean up.
