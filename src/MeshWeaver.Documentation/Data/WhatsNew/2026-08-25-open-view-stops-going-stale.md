---
Name: An open page stops going stale after a change made elsewhere
Category: Fix
Description: A page you were looking at could freeze on the content it had when you opened it — an agent edit or an edit from another tab saved fine, but the open view kept showing the old text until you reloaded. It now notices and catches up on its own.
Icon: ArrowSync
Order: -20260825
---

# An open page stops going stale after a change made elsewhere

Leave a page open, ask an agent in the side panel to change it, and the change would save — the tool
reported the new version, asking again confirmed it, reopening the page showed it — while the page
in front of you kept showing the old text. Not for a moment: indefinitely. The only ways back were a
full browser reload or recycling the node. Several people concluded their edit had been lost and
made it a second time.

The page was not broken and had not failed to render. It was reading from a live connection to the
node whose *other end had quietly been closed* — released while the page sat idle, or closed when
someone else stopped watching. The server closed its half and told the page nothing at all, so the
page went on showing the last thing it had ever been sent, with no error and nothing to notice.

Now that closing is announced. A page whose live feed has been closed underneath it learns so
immediately and re-establishes it, so the next change — and every change after — arrives the way it
always should have. Nothing polls and nothing retries on a timer: the closing itself is the signal.
