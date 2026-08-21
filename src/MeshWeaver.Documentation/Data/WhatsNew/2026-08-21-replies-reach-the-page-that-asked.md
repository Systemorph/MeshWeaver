---
Name: Answers now reach the page that asked for them
Category: Fix
Description: Replies travelling between portal servers are delivered directly instead of being broadcast and hoped for, so a page no longer waits a minute for an answer that was produced and lost.
Icon: Sparkle
Order: -20260821
---

# Answers now reach the page that asked for them

When the portal runs on more than one server, a page's request can be answered by a different server than the one serving the page. The answer used to travel back over an internal broadcast channel — and that channel had no way to report a failure. If the bookkeeping that says who is listening had been lost, which happened on every deployment, the answer was produced, accepted for delivery, and quietly discarded. The page waited its full minute and gave up, with nothing in the logs to explain it.

Answers are now sent directly to the server that asked, over the same reliable mechanism every other message between servers already used. A delivery that cannot be made is reported rather than absorbed, and the previous route is kept as a fallback so nothing is disrupted while a deployment is half-rolled.
