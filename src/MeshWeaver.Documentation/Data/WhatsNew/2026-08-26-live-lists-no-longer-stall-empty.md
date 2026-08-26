---
Name: Live lists no longer stall empty
Category: Fix
Description: A live children listing could open and never load — no error, no spinner resolution, just permanently empty — when the page opened it from certain code paths.
Icon: DocumentBulletListMultiple
Order: -20260826
---

# Live lists no longer stall empty

Views that show a live list of nodes — a folder's children, the notification bell, a conversation's
token-usage chip — open a query and then keep it open so the list updates as nodes are added.

Under a specific timing, that query could be started in a state where the work that reads the nodes
was queued behind the very code waiting for it. The list then never received its first result: no
error was raised and nothing timed out, so the view simply stayed empty for as long as it was open,
while opening the same content another way showed the nodes immediately.

The read now always runs on the spot rather than being queued, so a live list always receives its
first result and starts updating.
