---
Name: Why a page can keep showing the old version after an update
Category: Feature
Description: A new documentation page explains why an update that has been built, shipped and rolled out can still be invisible on a page you already had open — and what the Recycle action does and does not change about it.
Icon: ArrowSync
Order: -20260917
---

# Why a page can keep showing the old version after an update

Every node in the mesh is served by its own small worker, which is started the first time something
asks for that node and then keeps running. It reads what it needs — the node's type, the compiled
code behind that type — **once, when it starts**, and holds on to it. New content you or anyone else
writes still reaches it immediately; that is why editing and chat never need anything special. But a
new *build* of the code behind a node does not, because the worker never looks again.

So an update can be finished everywhere it is measured — built, published, installed, showing in the
portal's own version line — and a node whose worker was already running keeps answering exactly as it
did before, with nothing anywhere reporting a problem. The remedy is the **Recycle** action on that
node: it stops the worker, and the next visit starts a fresh one that reads everything again.

The new page [Stale State Until a Recycle](/Doc/Architecture/StaleStateUntilRecycle) writes this
down in one place: what a worker holds on to and what stays live, when the portal offers you a
recycle by itself and when it deliberately does not, and — just as important — the cases where a
recycle genuinely changes nothing, so a second and a third one are not worth your time.
