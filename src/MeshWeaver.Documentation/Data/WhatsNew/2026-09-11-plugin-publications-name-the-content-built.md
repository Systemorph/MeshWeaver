---
Name: Plugin publications name the content that was built
Category: Fix
Description: Plugin update notifications carry the actual content commit and the platform version used for the build.
Icon: Package
Order: -20260911
---

# Plugin publications name the content that was built

When the platform builds plugin content, its publication notification now identifies the
plugin repository's actual commit. It also includes the build platform's version for ordinary
pushes, where a release-dispatch payload may be absent.

This gives Memex an accurate publication record to use when evaluating plugin updates.
Missing or malformed provenance fails before the notification is sent.

See [Plugin publication provenance](/Doc/Architecture/PluginPublicationProvenance) for the
sender contract, fleet audit and executed regression.
