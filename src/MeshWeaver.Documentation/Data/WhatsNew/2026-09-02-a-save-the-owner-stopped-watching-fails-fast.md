---
Name: A save that the owner stopped watching now fails fast instead of hanging
Category: Fix
Description: When the stream a save was waiting on ended without answering, the save sat for 31 seconds and then reported the owner unreachable. It now gets a prompt, retryable verdict that says what happened.
Icon: Warning
Order: -20260902
---

# A save that the owner stopped watching now fails fast instead of hanging

When you save content that lives on another hub, the owner of that content confirms the write: it
watches for its own commit to come back on its stream, makes it durable, and only then tells your
side "saved". Your side waits for that answer.

There was one way for that answer never to come. If the stream the owner was watching *ended* —
which happens when the owner's cached view of the node is evicted while the owner itself keeps
running — the watcher simply stopped, without reporting anything. Nothing had failed as far as it
could tell; it had just run out of things to watch. Your side then waited out its whole confirmation
window (31 seconds) and reported the owner as unreachable, even though the owner had been alive the
entire time and may well have committed the write. On the portal this looked like a text-area save
that neither succeeded nor failed for half a minute.

The watcher now reports that case the moment it happens: a verdict that says the owner's stream
ended before the write's confirmation arrived, marked as safe to retry — and the retry is automatic,
because re-applying a write that already landed changes nothing. The same gap existed one level in,
where the durable flush is awaited, and in the generic path used by non-content data hubs; all three
are closed together. A verdict of this kind is now distinguishable from a genuine timeout, so a slow
owner and a stopped watcher no longer read the same in the log.
