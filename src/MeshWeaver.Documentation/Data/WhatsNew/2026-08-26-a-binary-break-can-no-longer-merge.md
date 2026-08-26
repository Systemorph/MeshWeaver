---
Name: A change that breaks already-installed modules can no longer merge
Category: Fix
Description: The check that catches a public surface change breaking modules built earlier now blocks the merge instead of failing quietly beside it.
Icon: ShieldCheckmark
Order: -20260826
---

# A change that breaks already-installed modules can no longer merge

A module that is already installed does not contain a copy of the platform's code — it contains
*references* to it, by name. Move a public type to a different place and every module built before
that move is still asking for the old name, which no longer exists. Nothing in the platform's own
build notices, because the platform compiles perfectly well against its new layout; the failure only
appears later, in an installed module, at the moment it tries to use the thing that moved.

That is not hypothetical. It is how the MCP tools stopped working: four types moved, every compile
stayed green, and each tool call failed at the point of use.

A check for exactly this already existed and had just been extended to catch moved types as well as
changed ones. But it sat *beside* the merge gate rather than in it — it could report a failure while
the change merged anyway. It now blocks the merge, with a message naming the types and what to do:
leave a forwarder behind in the original location, so both older and newer modules keep working.

Where a forwarder genuinely cannot be used, the message says so rather than suggesting one, because
the honest answer there is to reconsider the move or republish everything together.
