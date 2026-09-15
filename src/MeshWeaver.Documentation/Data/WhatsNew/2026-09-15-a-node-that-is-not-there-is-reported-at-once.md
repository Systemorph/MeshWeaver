---
Name: A node that is not there is reported at once
Category: Fix
Description: Opening something that no longer exists used to take thirty seconds and then blame the storage layer. The answer now comes back immediately, and says the node is not there.
Icon: Timer
Order: -20260915
---

# A node that is not there is reported at once

Every mesh node is served by its own hub, and the hub is built the first time somebody asks for the
node. Building it starts with one question: *which node is this?* Two things answer it at the same
time — storage, which is authoritative, and the process's own node cache, which is a shortcut for a
node somebody already had open.

When the node genuinely is not there, storage says so in milliseconds. The shortcut cannot say
anything at all: the way it asks is by sending a request **to the very hub that is being built**, so
it is waiting on the answer it is supposed to be helping to produce. That is harmless in itself, and
it was already arranged so that its failure could not become the node's failure.

What was not arranged is the other half. The answer was only considered final once **both** had
finished — and the shortcut only ever finishes by giving up on its own request, after a minute. The
minute never fitted inside the thirty seconds allowed for identifying the node, so a node that was
simply not there took the full thirty seconds and then came back as:

> No MeshNode emitted for '…' within 30s. Either the node does not exist or no query provider claims
> its partition.

That message is what the framework says when it does not know — one of those two clauses is a
missing node, the other is a piece of storage not answering — and it was being used for a case where
the framework knew perfectly well, thirty seconds earlier, that the node was missing.

**Storage now decides when the question is answered**, and the shortcut is dropped the moment it
does. Ask for something that is not there and you get the answer straight away, saying so; the
shortcut still hands over a node when it has one warm, which is the only thing it was ever for. If
storage itself fails, that is still reported as a failure — that part is real news about the node.

Visibly: a link or a bookmark pointing at something that has been deleted comes back now instead of
in half a minute, and a compile activity or a mirror that point-reads a companion node it does not
require stops paying thirty seconds to learn it is absent. In the portal's own logs, the
*"activation faulted … within 30s"* line stops standing in for *"there is no such node"*.
