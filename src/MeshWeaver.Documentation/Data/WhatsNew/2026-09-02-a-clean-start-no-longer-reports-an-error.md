---
Name: A clean start no longer reports an error
Category: Fix
Description: Starting the portal wrote one Error-level line per built-in activity type about a subscription that had "faulted" — a routine start-up step reporting itself. Start-up is now quiet.
Icon: Sparkle
Order: -20260902
---

# A clean start no longer reports an error

Every time the portal started, its log carried one line at **Error** level for each built-in
activity type, saying a subscription had faulted and was being re-established. Nothing was
actually wrong: the lines described a preparation step the platform runs at start-up, watching
itself.

That step is worth explaining, because it is useful. At start-up the platform walks every content
type it knows how to store and teaches itself to read that type — so a portal can display a kind of
content correctly even when it does not yet hold a single item of it. To do that it builds a
throwaway workspace per type, reads what it needs from it, and throws it away in the same breath.

The throwaway workspace has no content of its own, and never will. Some types also bring along a
watcher whose job, on a real node, is to notice when someone asks the node to stop or restart. On
the throwaway there is nothing to watch, so the watcher immediately failed, said so at the severity
operators alert on, and promised to try again a second later — against something that had already
been discarded.

The preparation step now leaves those watchers out, and a read of a throwaway workspace's own
content simply answers "there is nothing here" rather than failing. A normal start writes no error
lines, so an error in the log is once again a reason to look.
