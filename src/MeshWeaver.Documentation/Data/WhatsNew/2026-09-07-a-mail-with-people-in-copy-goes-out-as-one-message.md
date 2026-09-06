---
Name: A mail with people in copy goes out as one message
Category: Fix
Description: Copied recipients can now travel on the same message, so everyone sees who else received it and Reply-All reaches the group.
Icon: Sparkle
Order: -20260907
---

# A mail with people in copy goes out as one message

Mail sent from the portal could only ever be addressed to one person at a time. Sending to several
meant sending the message several times — one copy each, every one addressed to a single recipient.

That is not the same mail. Nobody could see who else had received it, so a client had no idea their
colleague was in the loop. Replying to everyone reached only the sender, and one conversation became
several unrelated ones. And a blind copy — a message someone receives without the other recipients
seeing it — had no way to be sent at all.

A message now carries its own recipients: **To**, **Cc** and **Bcc**, delivered as one mail. The
people in To and Cc can see each other, which is the whole point of copying someone in. The people in
Bcc receive it and nobody else knows, which is the whole point of that.

Where a message genuinely cannot be delivered that way, it is now **refused and reported** rather
than quietly sent to fewer people than were addressed. A mail that reaches half the people it names,
while telling the sender it went out, is worse than one that plainly did not go.
