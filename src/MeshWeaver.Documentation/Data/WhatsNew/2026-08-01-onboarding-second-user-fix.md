---
Name: Onboarding works again for every new user, and platform-admin grants are correctly scoped
Category: Fix
Description: New users no longer hit a failed submit at the end of onboarding, the invitation gate holds again, and platform admins are scoped to the Admin partition instead of silently becoming data superusers.
Icon: Sparkle
Order: -20260801
---

# Onboarding works again for every new user, and platform-admin grants are correctly scoped

Signing up on a portal that already had users could fail at the final step: the platform mistook **every** new user for the very first one, tried to grant them platform admin, and the security guard (correctly) refused the mis-scoped grant — failing the whole submit. The same confusion also let users self-onboard past the invitation gate.

Both checks are fixed at the root:

- **"Is this the first user?"** now asks the question that actually matters — *does any platform-admin grant exist yet?* — with a path-scoped query that works on partitioned storage (the old check matched nothing, ever). Config-seeded admins count, so a seeded deployment never mints a second bootstrap admin, and the invitation / closed-registration gates hold as configured.
- **The username-taken check** now probes the real partition root, so picking an existing username is rejected instead of silently overwriting that user's profile.
- **Platform-admin grants** (first-user bootstrap and config-seeded) are now written scoped to the Admin partition (`MainNode = "Admin"`), the shape the access guard enforces. A repair migration rescopes existing platform-admin grants the same way: admins keep all platform-management capabilities, and no longer carry standing superuser access to every partition's data — emergency cross-partition access remains an explicit break-glass elevation.
