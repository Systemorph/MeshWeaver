---
Name: A read that timed out no longer blames the hub it was waiting for
Category: Fix
Description: When an interactive read runs out of time, the error now says whether the node it was waiting for was simply still starting — instead of reporting it as unreachable and pointing every investigation at the wrong cause.
Icon: ClockAlert
Order: -20260910
---

# A read that timed out no longer blames the hub it was waiting for

An interactive read — the one an image on a page makes, among others — gives up after ten seconds
and reports that the node it was reading did not answer. The wording said more than the read could
know: *"the owning hub never answered"*. Most of the time that was simply untrue. The node had never
been opened in this process, opening it is what the read had just triggered, and it answered a few
seconds later — every later read of anything under that node then served in a fraction of a second.

The message also carried a line claiming the node was not present in this process at all. That line
was a constant: it asked the wrong component, which never holds any node, so it printed the same
verdict whether or not the node was sitting right there.

Both are corrected. The error now states only what it observed — that no answer arrived inside its
budget — and, when the node **is** in this process and has not finished starting, says exactly that
in words. So an occurrence can be read off one line instead of being filed against a completely
different fault, which is what happened twice before this was found.

Nothing about the timing changed: the same read still succeeds the moment the node finishes
starting, and a genuinely unreachable node still fails the same way it always did. What is new is
that the two are no longer written the same.

The full elimination — three causes, what separates them, and the decision still open on the third
— is in [The /api/content 503](/Doc/Architecture/ContentRoute503).
