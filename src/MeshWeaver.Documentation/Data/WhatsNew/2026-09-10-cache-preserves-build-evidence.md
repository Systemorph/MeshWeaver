---
Name: Cached builds retain their compatibility evidence
Category: Fix
Description: Reloading an unchanged compiled module preserves its recorded input fingerprint.
Icon: Sparkle
Order: -20260910
---

# Cached builds retain their compatibility evidence

Reloading an unchanged compiled module now preserves the input fingerprint recorded when it was built. The fingerprint is retained only when the assembly identity and its other dependency records still agree. This prevents a cache reload from discarding evidence used to check a module's compatibility.
