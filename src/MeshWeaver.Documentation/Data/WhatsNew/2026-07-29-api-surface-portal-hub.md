---
Name: More reliable REST, MCP and gRPC access
Category: Fix
Description: API, MCP and gRPC calls now run on a proper portal session, so unauthenticated calls no longer stall and SDK clients keep their subscriptions across reconnects.
Icon: Sparkle
Order: -20260729
---

# More reliable REST, MCP and gRPC access

Calls arriving over the REST API or MCP without a resolvable session — an anonymous request, or a client that sends no session header — were handled on the mesh's internal routing layer instead of a real portal session. Those calls could hang until they timed out rather than returning an answer. Every API and MCP call now runs on a proper portal session, and the same applies to the GitHub sync operations started from MCP.

For gRPC, validating an API token now happens on a portal session too. Previously a failure in that step was quietly treated as "not signed in", so a valid token could silently lose its identity and the caller would see permission errors instead of a clear failure.

The Python and TypeScript SDKs now reuse one connection identity for the lifetime of the process instead of generating a new one on every connect. Reconnecting keeps the subscriptions and live streams you already set up, rather than leaving them behind on an abandoned session. Set `MESHWEAVER_CLIENT_ID` if you want that identity to stay the same across restarts as well.
