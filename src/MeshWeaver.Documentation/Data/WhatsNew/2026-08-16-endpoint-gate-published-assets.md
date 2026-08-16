---
Name: Portal startup fixed on published deployments
Category: Fix
Description: The endpoint safety check no longer mistakes precompressed static assets for route conflicts, which prevented the portal from starting.
Icon: Sparkle
Order: -20260816
---

# Portal startup fixed on published deployments

A safety check that guards against modules accidentally overriding each other's web routes was
too strict: on deployed (published) portals it also flagged the platform's own static files, which
are legitimately served in several compressed variants, and refused to start the portal. The check
now only triggers when a module's route is actually involved in a conflict, so deployments start
normally while the protection against real route conflicts stays in place.
