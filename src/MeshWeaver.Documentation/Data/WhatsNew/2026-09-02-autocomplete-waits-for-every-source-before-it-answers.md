---
Name: Autocomplete waits for every source before it answers
Category: Fix
Description: A suggestion list could go out before the slower sources had answered — most visibly, results from another partition were missing when the portal was busy and present when it was not. The list now waits until every source has actually finished, and says so when one of them never does.
Icon: Search
Order: -20260902
---

# Autocomplete waits for every source before it answers

Typing `@` gathers suggestions from several places at once: what is nearby in the node you are
editing, what the search index knows, what other partitions hold. Some of those answer in a
millisecond; the ones that reach across partitions take longer.

The list used to be sent out as soon as it had stopped changing for a moment. That reads as
"everything has answered", and most of the time it was — but a moment of quiet is not the same
thing as being finished. On a busy portal the slower sources simply arrived after the pause, and
the list you saw was the one assembled without them. Nothing reported this: the suggestions
carried a flag meaning *this is the complete list*, and it was set whether or not it was true. The
symptom people met was a name that was there yesterday and missing today, with no way to tell
which of the two was right.

Suggestions now go out when every source has finished, which each of them reports for itself.
Waiting for the slowest costs nothing when they are all fast, and when one of them is slow the
list is simply a little later — and correct.

A source that never finishes at all can no longer hold your suggestions hostage either: after two
seconds the best list so far is sent anyway, and it is now honestly marked as incomplete, with the
misbehaving source named in the portal's log so it can be fixed rather than absorbed.
