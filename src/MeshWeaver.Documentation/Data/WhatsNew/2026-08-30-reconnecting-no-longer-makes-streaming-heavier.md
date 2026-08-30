---
Name: Reconnecting no longer makes live text heavier
Category: Fix
Description: After a page's data connection was re-established, the server went back to resending each streamed text in full on every update instead of only the part that changed. Reconnections now keep the compact form.
Icon: ArrowSync
Order: -20260830
---

# Reconnecting no longer makes live text heavier

When text streams onto a page — an agent's answer, a live document — the server normally sends only
the piece that changed rather than the whole text again. A viewer says it can handle that compact
form when it first connects.

It said so only the *first* time. If the connection was re-established afterwards — the owner
restarted, the subscription was reclaimed, an update was missed and re-requested — the viewer never
repeated the claim, so the server quietly fell back to resending the full text on every update, for
the rest of that page's life. Nothing broke; it just got heavier the longer the text grew, and for
every viewer watching. Every reconnection now makes the same claim as the first connection.
