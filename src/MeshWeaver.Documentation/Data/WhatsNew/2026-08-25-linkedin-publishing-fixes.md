---
Name: LinkedIn connecting, profiles and scheduled posts work again
Category: Fix
Description: Connecting LinkedIn no longer fails before sign-in, connected profiles stay readable, and scheduled posts publish — or say why they did not.
Icon: Sparkle
Order: -20260825
---

# LinkedIn connecting, profiles and scheduled posts work again

Connecting a LinkedIn account asked for a permission most deployments are not approved for, and LinkedIn refused the whole sign-in before it began — so nobody new could connect at all, even though posting never needed that permission. It is now requested only where a deployment opts in, and a refusal lands back on the profile page saying what happened instead of on the home page saying nothing.

Connecting also used to leave the profile unreadable: the profile page showed nothing, and posts written against it could not be approved. The same flaw quietly damaged a post every time it was published or its engagement figures were refreshed. Connected profiles and published posts now keep their identity, and a profile an earlier version damaged is repaired the next time it is connected.

Scheduled posts could pass their slot without publishing and without a word of explanation. They publish now, and when something genuinely stands in the way — no connected account, no author profile, a missing permission — the post itself says so in plain language and names the action to take. Re-scheduling a post after fixing the cause works, instead of being silently ignored forever.

Publication timestamps are also recorded correctly again, rather than being shifted by the server's time zone.
