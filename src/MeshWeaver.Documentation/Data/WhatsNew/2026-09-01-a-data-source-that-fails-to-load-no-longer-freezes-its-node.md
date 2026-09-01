---
Name: A data source that fails to load no longer freezes its node
Category: Fix
Description: A node whose data failed to load could stop answering entirely instead of reporting the error, leaving every page and request on it waiting for its full timeout.
Icon: Sparkle
Order: -20260901
---

# A data source that fails to load no longer freezes its node

When a node's data failed to load, it was supposed to say so immediately: every request got a
clear "initialization failed — here is why" answer within milliseconds, and the page showed the
real reason.

Occasionally it did the opposite. The node recorded the failure internally and then went silent:
requests were held back waiting for a load that had already given up, and callers waited out
their whole budget — up to 30 seconds — before getting a generic timeout with no explanation.
Whether it happened came down to which of two things finished first on a busy machine, so the
same node could behave correctly one moment and freeze the next.

The two are no longer in a race. A data load that fails now always reaches the code that reports
it, so the node reports the error immediately every time, and the message you see names the
actual problem instead of a timeout.
