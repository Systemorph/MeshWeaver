---
Name: Email never reports "Sent" without sending
Category: Fix
Description: A portal set up to send mail but missing its mail sender used to mark every message Sent while nothing was delivered; it now refuses loudly and leaves the mail queued.
Icon: Sparkle
Order: -20260825
---

# Email never reports "Sent" without sending

If a portal was switched on for e-mail but had no mail sender installed, outbound messages and
invitations were marked **Sent** while nothing actually left the portal. Nothing looked wrong: the
message record said delivered, and no error appeared anywhere. The only way to find out was that
the mail never arrived.

That can no longer happen. On a portal in that state, e-mail now stops instead of pretending: the
message stays visibly **queued**, the failure is reported, and the cause is named. Once the mail
sender is installed, the queued messages go out on their own — nothing needs to be re-created or
re-sent by hand.

Portals that send mail normally, and portals with e-mail switched off, are unaffected.
