---
Name: A half-written email is no longer lost when you connect
Category: Fix
Description: Filling in a share-by-email form and then connecting Microsoft 365 emptied the form. Your draft is now kept, and sending works again.
Icon: Sparkle
Order: -20260813
---

# A half-written email is no longer lost when you connect

Sharing a document by email asked for the recipient, the subject and a message — and then, if your
Microsoft 365 mailbox was not connected yet, offered to connect it. Coming back from that sign-in,
the form was empty. Everything typed was gone, with no way to get it back.

Connecting has to leave the portal for Microsoft's sign-in page, and until now the half-written mail
only existed in the page you left. Now it is kept for you: what you type is saved as you go, and the
form fills itself back in when you return. Closing the tab by accident no longer costs you the
message either.

Two things changed alongside it, both about the same moment. If your mailbox is not connected, the
dialog says so **before** you start writing rather than after you press Send — so you are not asked
to authenticate at the point where it costs you the most. And the recipient list now shows each
person's email address next to their name, so two colleagues with similar names can be told apart
and you can see exactly where the mail is going.

Your draft is private to you: it is stored in your own space, so colleagues who can read the
document cannot read what you were writing about it. It is cleared once the mail is sent, when you
cancel, and after a week untouched — an old recipient will not resurface weeks later.

**Sending itself was also broken and is fixed.** Exporting or emailing a document failed outright
with a script error: the built-in export templates called a function that no longer exists outside
of our test suite. These templates are compiled while the portal runs, so no build could catch it —
they are now checked against the real portal on every change, which is what let this reach you.
