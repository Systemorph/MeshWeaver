---
Name: A local install registers without a key
Category: Feature
Description: memex-local registry no longer asks for a registration key — an un-keyed registration is an open one, which memex.meshweaver.cloud enrols into the free tier; a key is now only how an install lands on a higher plan from its first boot.
Icon: Sparkle
Order: -20260830
---

# A local install registers without a key

`memex-local registry https://memex.meshweaver.cloud` used to refuse without a registration key
minted by a platform admin. That put an admin between every Homebrew install and its first
useful boot, for the one plan everybody starts on.

The key is optional now. Without one the install registers **openly**: it presents only its
instance id, memex.meshweaver.cloud enrols it into its default plan — the free tier — and it
comes up with everything that plan covers and the platform baseline, modules included. Moving it
up is a platform admin's edit of the instance's grant on the registry, or a key minted for that
plan — the admin's decision either way, never the install's. `memex-local registry status` says
which kind of registration an install made, and `memex-local verify` names the two ways an
un-keyed first boot can end up with nothing: a registry that accepts no open registrations, and
one that granted the instance nothing yet.
