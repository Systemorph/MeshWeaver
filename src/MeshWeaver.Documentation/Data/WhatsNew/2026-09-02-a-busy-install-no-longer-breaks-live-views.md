---
Name: A busy install no longer breaks live views
Category: Fix
Description: Fixed a recovery check that mistook a slow answer for a missing one and cut off live views in the middle of a large install.
Icon: Sparkle
Order: -20260902
---

# A busy install no longer breaks live views

When a page is showing live data and one update goes missing on the way to it, the page asks
the server to send the whole picture again. If those requests are asked for repeatedly and
nothing ever comes back, the page gives up and says so, which is what lets it reconnect
instead of sitting on a spinner forever.

The check for "nothing ever comes back" was reading the wrong signal. The server
acknowledged each request the instant it arrived — before it had actually produced the fresh
copy — so a page that asked while the server was busy was told "acknowledged" three times in
a few milliseconds and concluded that its answers were being lost. They were not: they were
queued behind the very work that made the server busy.

During a large import or package install that is exactly what happens, so a perfectly
healthy view was being cut off mid-install and had to be re-established. The server now
acknowledges a request only once it has sent the answer, so "acknowledged with nothing
behind it" means what it says. A view whose answer is merely slow now waits and catches up;
a view whose answers really are being lost still gives up and reconnects, exactly as before.
