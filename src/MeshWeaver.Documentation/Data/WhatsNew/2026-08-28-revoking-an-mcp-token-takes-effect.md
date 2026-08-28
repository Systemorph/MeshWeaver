---
Name: Revoking an MCP token now takes effect
Category: Fix
Description: A revoked back-connection token kept being handed to the co-hosted CLI until the portal restarted.
Icon: Sparkle
Order: -20260828
---

# Revoking an MCP token now takes effect

The token that lets the co-hosted assistant reach your mesh was remembered for as long as the portal
ran. If you revoked it, it kept being handed out anyway — the revocation only really took hold when
the portal next restarted.

Revoking now works immediately: the token is checked before it is reused, and a revoked one is
replaced. If the check itself cannot be completed — the store is briefly unreachable, say — the
existing token is kept rather than replaced, so a passing glitch does not quietly fill your token
list with new ones.
