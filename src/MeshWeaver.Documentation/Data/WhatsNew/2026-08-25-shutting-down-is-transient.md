---
Name: Pages stop failing when something restarts behind them
Category: Fix
Description: A page or action that arrived while its target was restarting gave up instead of waiting a moment.
Icon: Sparkle
Order: -20260825
---

# Pages stop failing when something restarts behind them

Parts of the platform restart routinely — a document type is recompiled, a workspace is recycled
after an install. Anything arriving during those couple of seconds is supposed to be told "this is
coming back, ask again", so the page waits a moment and then works.

Some of those messages were being told the opposite: that the thing they wanted had failed for
good. Whatever asked then stopped trying, even though the target was serving again a second later.
Because it depended on which of two answers arrived first, it looked random — the same action would
work, then not, then work again.

Restarting is now reported as temporary everywhere, so the caller waits and retries as intended. A
genuinely missing item is still reported as missing, so nothing retries forever.
