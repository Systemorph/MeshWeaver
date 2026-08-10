---
Name: Clean shutdown no longer reports stream-teardown errors
Category: Fix
Description: Shutting the portal down could log "Error during shutdown of hub sync/…" faults when parallel stream teardowns raced each other. Teardown completions are now recognized as normal shutdown and land silently.
Icon: Sparkle
Order: -20260810
---

# Clean shutdown no longer reports stream-teardown errors

When the portal shut down, its synchronization streams were torn down in parallel. A stream
that finished tearing down could still receive the final "we're done" signal from a sibling
stream that was completing at the same moment — and instead of ignoring it, the stream faulted
with an error ("Error during shutdown of hub sync/…" / "Hub sync/… disposal faulted"). Nothing
was actually wrong: both streams were doing exactly what shutdown asked of them, and the fault
was pure noise in the error logs.

A stream's teardown now leaves it in a state that recognizes late shutdown signals as the
normal end of life they are: they land silently, no matter how the parallel teardowns
interleave. As a bonus, anything that subscribes to an already-closed stream now immediately
hears "this stream has ended" instead of waiting forever on a subscription that could never
speak.
