---
Name: The CI bake publication actually seals
Category: Fix
Description: Publishing the CI-compiled content bundles to the portals' storage always failed at the final completeness marker, so portals never adopted a published bake and kept recompiling at boot. The seal now lands and published bakes are picked up.
Icon: Seal
Order: -20260817
---

# The CI bake publication actually seals

The lane that copies CI-compiled content bundles onto the portals' shared storage marks a finished
publication with a completeness marker, written last — and portals only read sealed publications.
The upload tool silently treated the marker's extensionless name as a folder, so the seal failed
on every publication, every bundle set stayed torn, and each booting portal kept recompiling
content it should have adopted.

What you notice: after a platform update, published content bakes now land sealed and portals warm
up from them instead of rebuilding — the "no re-compilation wave" behavior shipping updates
promised now actually happens end to end. (Found and verified live while wiring the node repos'
bake publications; the same fix ships in each node repo's vendored copy of the publish step.)
