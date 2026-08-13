---
Name: A page that loses its answer now asks again instead of hanging
Category: Fix
Description: When a page was reloaded at the exact moment its content was being refreshed behind the scenes, the request could go unanswered — leaving a spinner for a full minute and then a misleading "not found". It now gets a real answer and retries immediately.
Icon: ArrowClockwise
Order: -20260813
---

# A page that loses its answer now asks again instead of hanging

Occasionally a page would sit on a spinner for a full minute and then report that its content could
not be found — content that plainly existed, and that loaded fine on the very next attempt.

The cause was a moment of bad timing. Content in the platform is served by a small worker dedicated
to that item, and there are legitimate reasons for the platform to retire one and start a fresh
copy — most commonly right after the item's type has been rebuilt, so the next visitor gets the new
version rather than the old one. That swap is quick and normally invisible.

The problem was what happened to a request that arrived *during* the swap. The worker accepted it,
started fetching, and then went away mid-answer. Nothing was left to reply, and nothing said so —
so the person who asked simply waited, all the way to the one-minute limit, and was then told the
content did not exist. That last part was the most misleading piece: the platform reported what it
could see a minute later ("nobody is serving this"), not what had actually happened.

Now the hand-over is explicit. A request whose worker retires mid-answer is told exactly that —
"this item is being refreshed, ask again" — and the platform asks again straight away, landing on
the fresh copy. In practice the page loads normally instead of stalling, and the rare genuine
failure now says what went wrong rather than blaming a missing item.

The same fix removes a whole class of silent waits: any read that loses the thing it was reading
from now ends with an answer, never with silence.
