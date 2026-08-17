---
Name: Registry bundles adopt again
Category: Fix
Description: Plugin bundles served by a registry portal recorded a stale framework identity since the surface-identity change, so installers silently recompiled everything instead of adopting the prebuilt assemblies — they adopt again.
Icon: Sparkle
Order: -20260817
---

# Registry bundles adopt again

The registry's bundle endpoint recorded the framework identity as a raw assembly fingerprint —
correct until the platform switched to the surface-based identity, after which every bundle it
served carried a mismatched identity and the installing portal's safety gate declined it. The
install still worked, but silently fell back to compiling every type instead of adopting the
prebuilt assemblies. The endpoint now records the same resolved identity the adoption gate
compares, so registry installs adopt prebuilt assemblies again.
