---
Name: A deployment's plugin credentials stop vanishing on upgrade
Category: Fix
Description: The credentials a portal uses to reach its plugin catalogue could only be attached by hand, and the next routine update removed them again — which no longer merely disabled the catalogue, it re-enabled a source that could not authenticate. They are now supplied from the secure store like every other secret.
Icon: Sparkle
Order: -20260813
---

# A deployment's plugin credentials stop vanishing on upgrade

A portal reaches its plugin catalogue with two credentials: one that identifies the portal to the
catalogue it subscribes to, and one that lets it read repositories on your behalf. Both were
described in the deployment configuration as though they came from the secure store — but nothing
actually put them there. In practice the only way to supply either was to attach it to the running
portal by hand, and the next routine update removed it again, with nothing reporting that anything
had been lost.

That already made catalogues quietly stop working. It now costs more than that. A portal with **no**
catalogue configured deliberately falls back to reading plugins straight from their source
repositories — sensible, because with no catalogue there is no other supply. So a portal that
*loses* its catalogue credential no longer just goes quiet: it switches to a route it has no
credential for, and starts failing repeatedly against repositories it cannot open.

Both credentials are now read from the secure store, the same way the portal's other secrets
already are. They are declared once, they survive every redeployment, and a portal that is
entitled to read private plugin content no longer drifts back into a state where it cannot.

Nothing changes about who may see what. A catalogue or repository nobody granted access to stays
inaccessible; what changes is that access already granted stops being lost by accident.
