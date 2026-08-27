---
Name: Half-configured email no longer blocks the portal from starting
Category: Fix
Description: A portal with mail switched on but its credentials unset now starts normally and reports exactly which key is missing.
Icon: Sparkle
Order: -20260827
---

# Half-configured email no longer blocks the portal from starting

Turning mail on (`Email:Enabled=true`) without finishing the credential — an unset `Email:TenantId`, for
instance — used to stop the whole portal from starting, not just mail. The check that protects against
undelivered mail asked the application to build its mail sender during startup, and that sender rejected
the incomplete credential, so an optional integration took the entire site down.

The check now reads the configuration directly, which cannot fail. A portal with mail half-configured
starts and serves normally; mail alone is switched off, with an error naming the exact keys still to set.
Anything already queued stays visibly unsent, and goes out by itself once the keys are set and the portal
restarts — mail settings are read at startup, so completing them takes effect on the next roll. Nothing
is ever marked as delivered when it was not.
