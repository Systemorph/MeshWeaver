---
Name: Real-time connections no longer ask the message router to check their token
Category: Fix
Description: Validating the API token for a SignalR connection ran on the one router every portal message passes through, competing with the traffic it exists to dispatch.
Icon: Sparkle
Order: -20260809
---

# Real-time connections no longer ask the message router to check their token

Every message in a portal — a page opening, an edit saving, a chat round running — passes through a
single router whose only job is to decide where each message goes. When a client opened a real-time
connection, the check that turns its API token into an identity was asked of *that router*, on the
same one-at-a-time queue it uses to dispatch everything else. Under load that is how a burst of
connections ends up delaying page loads and chat replies that have nothing to do with them.

Token checks for real-time connections now run on a hub of their own, so the router only routes. The
equivalent path for the gRPC transport was already fixed; this brings SignalR in line with it.

The problem was already visible in the logs as errors — it was among the largest sources of red lines
in production, which is how it was found.
