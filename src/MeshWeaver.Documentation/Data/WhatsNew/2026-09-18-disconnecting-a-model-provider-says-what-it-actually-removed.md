---
nodeType: WhatsNew
Name: Disconnecting a model provider says what it actually removed
Category: Fix
Description: "Disconnecting an AI model provider always reported that its stored credential had been removed, even when there was none left to remove — and the message only ever appeared in English. Both are fixed."
Icon: PlugDisconnected
Order: -20260918
---

Disconnecting a model provider in the chat view always said the same thing: *"… disconnected — its
stored credential was removed."* It said it whether or not there had been a credential to remove.

Click Disconnect twice, or disconnect on one device after already doing it on another, and the
second attempt read exactly like the first — a confident report about work that had not happened.
Nothing was broken by it, which is precisely why it would have gone unnoticed: the provider really is
disconnected either way. What was missing was the ability to tell the two apart.

**The message now follows the outcome.** A credential that was there and is now gone is reported as
a removal; when there was nothing stored, the line says so instead. "Disconnected" is true in both
cases and stays.

These messages were also hard-coded English, so a German viewer saw them in English regardless of
their language setting. They now come from the platform's text catalogue like every other message in
that view, and read in the viewer's own language.
