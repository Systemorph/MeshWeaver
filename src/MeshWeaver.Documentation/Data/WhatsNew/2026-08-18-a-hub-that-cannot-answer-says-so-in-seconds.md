---
Name: A hub that cannot answer says so in seconds
Category: Fix
Description: Reads whose target hub is unreachable, still starting, or broken now give up in ten seconds with a retryable answer, instead of holding the page for a full minute and then reporting a server error.
Icon: Timer
Order: -20260818
---

# A hub that cannot answer says so in seconds

A cover image that would not load, a form field that spun and then popped an error, a video that
never started — each of them, on a bad minute, took exactly **sixty seconds** to fail, and then
failed as if the page were broken rather than as if something were temporarily away.

Sixty seconds was never anybody's decision. It is the framework's last-resort reply ceiling, the
number that has to be generous enough to cover a cold module compiling itself for the first time.
A read that set no budget of its own simply inherited it — so an image fetch and a first-time
compile were given the same patience, and when nothing came back the failure said only *"no
response received"*, naming neither the file nor the node.

Interactive reads now carry their **own** ten-second budget — the same one node reads have always
used — and report in their own terms: *this node did not answer, within this budget, and here is
what the two ends looked like while we waited*. The answer a browser gets is **503 Service
Unavailable**, which means "ask again", instead of a 500 that means "this is broken". Caches and
retries treat those very differently, and only one of them was true.

Three related habits changed with it, all of them about telling apart facts that used to collapse
into one:

- **A hub that could not start** is now reported as *unavailable* — retry-worthy — rather than as an
  unclassified fault. It is an availability fact about the target, and the next visit re-runs the
  whole thing from scratch.
- **A node that genuinely is not there** answers as a plain, immediate *not found*, identical to a
  refusal, instead of a server error.
- **A form field bound to a node that is slow to reach** now draws empty after the budget and
  **keeps waiting**, so a value that arrives late still fills it in. Previously the field could wait
  for the life of the page with nothing logged at all.

There is one more fix underneath. When a module's hub failed to construct — a genuine bug in that
module — the platform's own error handling dereferenced the missing hub and reported *"Object
reference not set to an instance of an object"*, burying the real exception it had already written
down a moment earlier. That report now names the module, its type, and where the actual cause is
recorded.
