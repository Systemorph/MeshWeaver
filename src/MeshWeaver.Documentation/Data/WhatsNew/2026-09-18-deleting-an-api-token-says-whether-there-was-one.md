---
nodeType: WhatsNew
Name: Deleting an API token says whether there was one
Category: Fix
Description: "Removing a personal API token reported success even when the token was already gone, so a second click — or a mistyped path — looked exactly like a real deletion. It now reports what it actually removed."
Icon: Delete
Order: -20260918
---

Deleting a personal API token reported a successful removal **whether or not there had been a token
to remove**. Click Delete twice, or aim at a token a session on another device had already revoked,
and both attempts came back the same way: done.

Nothing was lost by it — the token really is gone in every one of those cases, which is why it went
unnoticed. What was lost was the ability to tell the two apart. A tidy-up that expected to clear a
handful of expired credentials reported the same outcome whether it cleared them or found nothing
there, and a delete aimed at the wrong path reported that it had removed something.

**The answer now says what actually happened**: a token that was there and is now gone reads as a
removal, and a path that held nothing reads as having removed nothing. Neither is an error — asking
for something to be gone when it is already gone is a request that has been granted — and the
platform has behaved this way since deleting something already deleted stopped being a failure. The
token screen simply had not been passing the answer along.

This is the same promise the platform makes everywhere else a delete can land twice: **a deletion
that found nothing to remove still records that it removed nothing.**
