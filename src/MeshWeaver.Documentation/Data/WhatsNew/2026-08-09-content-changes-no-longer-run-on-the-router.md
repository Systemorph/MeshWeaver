---
Name: Content changes no longer run on the portal's message router
Category: Fix
Description: Creating, moving or deleting content from inside a page, a thread or a compile step ran on the one router every portal message passes through.
Icon: Sparkle
Order: -20260809
---

# Content changes no longer run on the portal's message router

Every message in a portal — a page opening, an edit saving, a chat round running — passes through a
single router whose only job is to decide where each message goes. When a change was made from
inside something that already lives in the mesh (a chat thread creating its own notification, a code
node recording a compile step), the resulting create or delete was executed *by that router*, on the
same one-at-a-time queue it uses to dispatch everything else. Under load that is how a burst of
saves ends up delaying page loads and chat replies that have nothing to do with it.

Those changes now run on a hub of their own, so the router only routes. The same applies to
validating an API token for a real-time connection, which was also being asked of the router.

Both were already visible in the logs as errors; they were the largest single source of red lines in
production, which is how the pattern was found.
