---
Name: Chat works on the local mesh
Category: Fix
Description: The local sidecar now carries the AI engine, so starting a chat thread from the mobile/web shells works instead of failing silently.
Icon: Sparkle
Order: -20260826
---

# Chat works on the local mesh

Starting a chat from the mobile or web shell against the local mesh used to do nothing: the
sidecar did not know the Thread node type, so every send was refused behind the scenes and the
conversation never appeared. The local mesh now ships the AI engine, so threads are created under
your own space, your message is queued and answered, and configuring a model provider is done the
same way as on a portal.
