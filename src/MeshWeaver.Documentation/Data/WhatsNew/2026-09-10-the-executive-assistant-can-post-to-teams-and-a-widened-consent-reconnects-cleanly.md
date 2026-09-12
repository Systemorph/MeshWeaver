---
Name: The Executive Assistant can post to Teams, and a widened consent reconnects cleanly
Category: Feature
Description: The assistant's Microsoft connection now carries the permissions to post to your Teams channels and chats, and a connection granted before new permissions were added takes you through the consent dialog again instead of bouncing you back.
Icon: Sparkle
Order: -20260910
---

# The Executive Assistant can post to Teams, and a widened consent reconnects cleanly

The consent the Executive Assistant asks for when you connect your Microsoft account now includes
posting to Teams channels and chats, next to reading them. Whether the assistant may actually post
is a decision each installation makes (`Teams:AgentSend`); the permission is asked for once so that
turning posting on later does not send everybody through the consent dialog again.

Adding a permission also used to leave existing connections in a loop: Microsoft refuses to renew a
connection for more than it was granted, the assistant said it could not check your mailbox, and the
reconnect link — seeing a stored connection — bounced you straight back without ever showing the
dialog. A connection granted for fewer permissions than the current version needs is now recognised
as one that has to be renewed: the assistant hands you the reconnect link, and the link runs the
dialog. Your mailbox and calendar keep working throughout; only the new permissions are missing
until you reconnect.
