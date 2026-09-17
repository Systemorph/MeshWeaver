---
Name: A web address that does not exist is no longer tried three times
Category: Fix
Description: An outbound call to a hostname that does not exist was retried as though the network had hiccupped — three failed attempts, each logged as a platform error. It now fails once, and a dead link in your data no longer files a bug report against the platform.
Icon: Globe
Order: -20260917
---

# A web address that does not exist is no longer tried three times

When the portal calls out to another site — an agent reading a page you asked about, for instance —
and the call fails, it tries again. That is right for a network that hiccupped, and wrong for the one
failure that will never improve: a **hostname that does not exist**.

Until now both were treated the same. A URL pointing at a domain that has been retired, or that was
mistyped, was attempted three times, and each attempt was recorded as a platform error. Those error
records are what the platform opens its own bug reports from — so a dead link in somebody's data
produced a bug report about the platform, which had nothing wrong with it.

Such a call now fails once and is recorded for what it is. Everything else keeps retrying exactly as
before, including the case that looks similar and is not: a name server that failed to *answer* is a
real hiccup, and those calls are still tried again.
