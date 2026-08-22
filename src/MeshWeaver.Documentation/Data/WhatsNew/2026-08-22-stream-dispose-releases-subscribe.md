---
Name: Closing a view releases its subscription immediately
Category: Fix
Description: A layout area or page that is closed while still loading no longer leaves a request hanging against the server for 30 seconds.
Icon: Sparkle
Order: -20260822
---

# Closing a view releases its subscription immediately

When a page or layout area opens, it subscribes to the data it needs. If you navigate away — or the
render is cancelled — before the answer arrives, that subscription should be dropped straight away.

It was not. The release was queued behind the full shutdown of the subscription's internal machinery,
which takes an unbounded number of steps and runs while the host is already tearing down. In practice
the request kept waiting on the server for up to thirty seconds after nobody was listening any more.

It is now released the moment the view lets go. Nothing else changes for anyone who stays on the page.
