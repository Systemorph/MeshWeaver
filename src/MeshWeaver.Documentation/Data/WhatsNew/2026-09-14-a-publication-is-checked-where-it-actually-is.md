---
Name: A published update is checked where it actually is
Category: Fix
Description: The check that asks "has this content been published yet?" looked in the old, fixed location, which is briefly incomplete each time an update is published. It reported the update as missing and held the build that was waiting for it, even though the update was finished and in use.
Icon: Checkmark
Order: -20260914
---

# A published update is checked where it actually is

Before a repository of content is built, the platform checks that everything it builds **on top of**
has already been published. It is a simple question — is the prepared set there? — and the answer
gates the build.

Since updates began being published **whole** (each into its own folder, with a pointer saying which
one applies), that check was still looking at the old, fixed location instead of following the
pointer.

## What went wrong

A copy is still kept in the old location, for installations too old to follow a pointer. Publishing
an update refreshes that copy last — and refreshing it starts by **removing its "this set is
complete" marker**, so that nothing reads it half-replaced.

For the minute or so that takes, the old location has no marker while the update itself is finished
and already in use. The check looked there, found no marker, and answered **"this has not been
published"** — holding the build that was waiting for it, and reporting the one message that
normally means an upstream really is late. Anyone reading it went looking for a publication that had
in fact completed.

## What changes

The check now **follows the pointer**, like everything else that reads a published set, and asks
about the update that actually applies. Where there is no pointer — nothing has been published in the
new layout yet, or the pointer cannot be read — it falls back to exactly what it did before, so the
answer can only ever get *more* right.

It still refuses, loudly and in the same words, when a set genuinely has not been published. That is
covered both ways: the new case fails against the old check and passes against the new one, and the
"nothing published anywhere" case still refuses on both.
