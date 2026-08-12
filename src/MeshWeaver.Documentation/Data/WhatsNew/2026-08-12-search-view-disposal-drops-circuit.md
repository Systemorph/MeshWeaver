---
Name: Closing a search page no longer drops the connection
Category: Fix
Description: Navigating away from search while results were still arriving could disconnect the page.
Icon: Sparkle
Order: -20260812
---

# Closing a search page no longer drops the connection

Leaving a search or catalog page at the moment fresh results arrived could break the page's
connection to the server, showing the reconnecting overlay for no reason. Tidying up the page's live
subscriptions collided with results still coming in.

Views now tear their subscriptions down in a way that tolerates late arrivals, so leaving a page is
never disruptive. The same change also stops a subscription that arrives during teardown from being
left running for a page that is already gone.
