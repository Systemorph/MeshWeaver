---
Name: A recycled page asks again instead of giving up
Category: Fix
Description: A view that loaded while its page was being recycled used to report a permanent failure. It now reports a transient one, so the page comes back on its own instead of needing a reload.
Icon: ArrowSync
Order: -20260906
---

# A recycled page asks again instead of giving up

When a page is recycled — a node is republished, a hub restarts, an app updates — the connection
behind an open view is torn down and rebuilt. A view that happened to load during that short window
could hit the connection just as it was going away, and what came back was an error that said *this
failed permanently*. It had not: the page was seconds away from being available again. The view
stayed blank until you reloaded it by hand.

Those moments are now reported for what they are — **temporary**. A view that lands in a recycle
window is told to ask again, and comes back on its own.

The same correction reaches the surfaces around it. A form submitted while its page was closing used
to fail with nothing to show for it; it now tells you the connection was closed and that nothing was
submitted, so you know to send it again rather than wondering whether it went through. A data
binding whose page has gone simply stops delivering values instead of taking the rest of the view
down with it.

Nothing changes for a page that is working normally: every one of these paths behaves exactly as it
did before while the connection is live.
