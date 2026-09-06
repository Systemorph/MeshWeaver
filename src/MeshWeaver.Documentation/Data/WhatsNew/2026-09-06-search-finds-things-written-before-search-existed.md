---
Name: Search finds things that were written before search did
Category: Fix
Description: Turning on semantic search used to leave every page and node created beforehand invisible to it, because the one job that fills in their missing vectors could not reach the embedding key. It can now, so switching search on covers the whole history instead of only what you write next.
Icon: Checkmark
Order: -20260906
---

# Search finds things that were written before search did

A node's search vector is computed when the node is written. That is the right moment for
everything written *after* semantic search is switched on — but it means that on the day you enable
it, every page, document and node that already existed has no vector at all.

The platform has always had an answer for this: a backfill that runs with the database migration,
walks every schema, and fills in the missing vectors. The problem was that the backfill could not
reach the embedding key. The portal received it; the migration did not. So the migration ran, said
`no embedding provider configured — skipped`, and finished green having embedded nothing.

That is the worst shape a gap can take. Nothing failed. The deployment was healthy, search was
switched on, new content was indexed correctly — and the entire back catalogue silently stayed
lexical-only, findable if you typed a word it literally contained and invisible otherwise. On one
portal that was 1 242 documentation pages, of which 0 were embedded.

The migration now reads the same key vault secrets the portal does. Enabling semantic search covers
what you already have, not just what you write next.

## What you will notice

- Turning on semantic search now makes existing content searchable by meaning, not only by
  substring — with no extra step and no re-import.
- If the key genuinely is not available yet, the migration still says so out loud rather than
  quietly skipping.

Nothing changes for a portal that has always had an embedding provider configured: the backfill only
touches rows whose vector is missing, so it is a no-op there.
