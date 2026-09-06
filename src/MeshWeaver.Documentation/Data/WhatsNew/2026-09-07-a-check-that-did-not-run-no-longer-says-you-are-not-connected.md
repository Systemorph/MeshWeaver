---
Name: A check that could not run no longer says you are not connected
Category: Fix
Description: When the portal cannot check whether your mailbox is connected, it now says so instead of telling you that you never connected.
Icon: Sparkle
Order: -20260907
---

# A check that could not run no longer says you are not connected

Opening **Share ⇒ as email** starts a quick check: can this message go out from your own mailbox? If
the answer was no, the dialog said so plainly — *"Your Microsoft 365 mailbox is not connected yet"* —
and offered a Connect button.

The trouble was that the check had only two answers for three situations. "You have not connected"
and "the check did not finish" both arrived as *no*. So a brief hiccup — a slow moment, a connection
that dropped — showed people who **had** connected a panel telling them they had not, and invited
them through a setup step they had already done.

Now the check keeps the three situations apart. If it cannot reach an answer, the dialog says that:
it could not check just now, your draft is safe, and you can send either way. It no longer states
anything about your mailbox that it does not know.

Two things are deliberately unchanged. The Connect panel still appears, exactly as before, when the
check finishes and genuinely finds no connected mailbox — that sentence is true there, and it is the
one moment worth offering the setup. And pressing **Send** still asks which mailbox the message
should leave from whenever your own cannot be confirmed, rather than quietly sending from somewhere
else. Nothing sends from an identity the portal is not sure about.
