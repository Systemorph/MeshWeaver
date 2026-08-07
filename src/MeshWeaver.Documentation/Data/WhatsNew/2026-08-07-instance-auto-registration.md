---
Name: New installations register themselves on first startup
Category: What's New
Description: A registration key from the admin surface lets a fresh MeshWeaver installation register itself at the plugin registry on first boot — no token copying, plugins available immediately.
Icon: Sparkle
---

# New installations register themselves on first startup

Standing up a new MeshWeaver installation no longer involves copying access tokens between
systems. A platform admin mints a reusable registration key once, puts it in the deployment
scaffold, and every new installation registers itself at the plugin registry the first time it
starts — receiving and safely storing its own access key automatically.

Combined with the default plugin grants introduced alongside, a brand-new installation reaches a
filled Plugin Catalog with no manual steps: boot, self-register, see the platform plugins.
Registration keys can expire and be revoked at any time from the Instance grants administration
tab — revoking one stops future registrations without affecting installations already registered.
