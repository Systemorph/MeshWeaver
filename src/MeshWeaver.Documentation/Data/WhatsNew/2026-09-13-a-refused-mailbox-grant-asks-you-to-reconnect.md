---
Name: A refused mailbox grant asks you to reconnect instead of "ask me again in a moment"
Category: Fix
Description: When Microsoft refuses the Executive Assistant's stored grant (revoked, expired, password changed, consent withdrawn), the assistant now hands you the reconnect link, and the link runs the consent dialog. Before, it answered "I could not determine … ask me again in a moment" forever, and the reconnect link bounced you back as already connected.
Icon: KeyRound
Order: -20260913
---

# A refused mailbox grant asks you to reconnect instead of "ask me again in a moment"

If Microsoft refused the Executive Assistant's stored refresh token — the grant was revoked, it
expired, your password changed, or consent was withdrawn — every mail, calendar and Teams tool
answered *"I could not determine whether your mailbox and calendar are connected just now … please
ask me again in a moment"*. Asking again redeemed the same dead token and got the same refusal, and
the reconnect link, reading a stored credential, bounced you back as already connected.

A refusal that names the grant is now read as what it is: the assistant answers with the reconnect
link, the credential is marked so the link actually runs Microsoft's consent dialog, and the dead
token is not offered to Microsoft again. A token endpoint that is merely down still gets the honest
"could not check just now", and is retried next time.

Design: [Executive Assistant credential reads](/Doc/Architecture/ExecutiveAssistantCredentialReads).
