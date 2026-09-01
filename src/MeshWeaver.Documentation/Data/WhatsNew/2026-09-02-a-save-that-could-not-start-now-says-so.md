---
Name: A save that could not start now says so
Category: Fix
Description: A save that lost its connection to the node before it could begin used to wait forever without ever reporting anything — it now fails straight away and says the change did not land, so it can be made again.
Icon: Bug
Order: -20260902
---

# A save that could not start now says so

Saving anything in the mesh works the same way underneath: read the node as it stands, work out what
changed, send just that change to whoever owns the node, and wait to be told it was committed. Every
answer you can get — saved, refused, denied, or "the owner never replied" — is worked out **after**
that first read comes back.

Which left one gap, and it was the worst-behaved kind. If the connection to the node went away
*before* that first read produced anything, the save had nothing to work from — so it never sent
anything, and there was therefore nothing to be told. It did not fail. It did not succeed. It simply
never answered, and it never would, because the deadline that would eventually have complained was
only set once the change had been sent. Nothing appeared in the log, because as far as the system was
concerned that piece of work had finished long ago.

You would notice it as a spinner that never stops, an edit that never confirms, or — most often —
several things saving at once where all but one finished. That last shape is what makes it easy to
mistake for something else: connections to a node are shared, so when one of several simultaneous
saves tidied the shared connection away, whichever one had not yet had its first read was the one
left hanging. Nine times out of ten everything is fast enough that it never happens; on a loaded
machine it happens just often enough to look random.

**Now that first read has to answer one way or the other.** If it ends without producing the node,
the save stops there and reports plainly that the change did not land and should be made again —
which is the honest answer, since nothing was ever sent. Whatever was waiting on that save gets on
with its life: the queue behind it moves, and anything counting saves in flight stops counting.

Two things deliberately did not change:

- **A save that changes nothing still succeeds.** Re-saving an unchanged page, or an edit that turns
  out to be identical to what is already there, completes normally without writing anything. "Nothing
  changed" is an outcome, not a reason to go quiet.
- **Nothing waits any longer than it did.** No timer was lengthened and nothing is retried. The only
  difference is that an outcome which previously reached nobody now reaches you.
