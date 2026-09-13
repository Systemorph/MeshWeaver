---
Name: API token checks no longer run through the message router
Category: Fix
Description: Validating a Bearer token on an installation without the browser shell sent the check from — and its answer back to — the component whose only job is routing. It now goes through the dedicated read path, like every other bounded read.
Icon: Checkmark
Order: -20260913
---

# API token checks no longer run through the message router

Inside an installation, one component is the **router**: it exists to pass messages between all the
others, and nothing else. Work handed to it directly competes with that job, and under load the
installation stops answering.

Every request that arrives with an API token has that token checked. The check is a small
round-trip: ask the component that owns the token record whether the token is real, and wait for the
answer. Which component *sends* that question depends on how the installation is running — and on an
installation running **without the browser shell**, or for any request that arrives outside a browser
session, it was the router.

## What changes

The check is now sent from the installation's **dedicated read path** — a component that handles
nothing else, so the only thing it ever does is deliver the answer to a read someone is waiting for.

That is the same path every other one-off read already uses, and it was chosen over the write path
deliberately: the write path processes one node change at a time, so an answer arriving in the middle
of a bulk import or an install would wait behind all of it. A person is waiting on this one.

## What this does not change

**Nothing about tokens, and nothing about who can use one.** Same check, same record, same verdict,
same failure handling — an infrastructure fault still reports as retryable rather than as an invalid
token.

**Installations with the browser shell already behaved this way.** There the check was already sent
from the page's own component; for those the change resolves to exactly what was happening before.
