---
Name: A node that is not there is reported at once
Category: Fix
Description: Opening something that is not there used to take thirty seconds and come back as a clock running out. The answer now arrives as soon as the lookup has finished — a verdict, not a timeout.
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

That is a **timeout** — the framework saying it ran out of time to find out — and it was being used
for a case where the lookup had finished thirty seconds earlier. (Which of the two clauses applies is
a separate question, and it is genuinely open: an empty answer from storage looks the same whether
the node was never written or nothing claims the partition it would live in. That has not changed
here, and the answer still names both possibilities.)

**Storage now decides when the question is answered**, and the shortcut is dropped the moment it
does. Ask for something that is not there and the answer arrives as soon as the lookup is done,
as a verdict about the address rather than a clock running out. The shortcut still hands over a node
when it has one warm, which is the only thing it was ever for; and if storage itself fails, that is
still reported as a failure — that part is real news about the node.

Visibly: a link or a bookmark pointing at something that has been deleted comes back now instead of
in half a minute, and a compile activity or a mirror that point-reads a companion node it does not
require stops paying thirty seconds to learn it is absent. In the portal's own logs, the
*"activation faulted … within 30s"* timeout is replaced by the resolution verdict for that address —
a different kind of statement, thirty seconds earlier.
