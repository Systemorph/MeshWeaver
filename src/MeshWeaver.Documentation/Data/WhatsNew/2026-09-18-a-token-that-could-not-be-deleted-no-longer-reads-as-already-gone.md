---
nodeType: WhatsNew
Name: A token that could not be deleted no longer reads as already gone
Category: Fix
Description: "When deleting a personal API token was refused, the screen said the token had already been removed — so a credential that still worked looked like one that was gone. The refusal is now reported as a refusal, with the reason."
Icon: Delete
Order: -20260918
---

When removing a personal API token was **refused** — because the account lacked the right to remove
it, or a rule protected it — the token screen reported that the token **had already been removed**.

That is the worst of the three things it could have said. The token was still there and still
working: anything holding that credential kept authenticating with it. Someone tidying up an old
integration would read *"already gone"*, tick it off, and leave a live key in place believing they
had revoked it.

**A refusal now reads as a refusal, and says why.** The message names the reason the removal was
turned down, so the next step — asking for the right permission, or finding the rule that protects
the token — is visible instead of hidden behind a reassuring sentence.

The three outcomes are now genuinely distinct on screen: the token was there and is now gone; there
was nothing there to remove; or the removal was refused and the token is still live. Only the first
two are quiet, and only the first claims anything was deleted.

This completes the change made earlier the same day, which taught the screen to tell a real removal
from a no-op. That fix could not distinguish a refusal from a no-op, because the refusal never
reached it.
