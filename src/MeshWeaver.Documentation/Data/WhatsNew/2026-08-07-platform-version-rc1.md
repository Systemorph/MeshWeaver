---
Name: The platform version is now 3.0.0-rc1
Category: Feature
Description: Builds carry a release-candidate version, so portals always update to the newest one.
Icon: Sparkle
Order: -20260807
---

# The platform version is now 3.0.0-rc1

Builds are now versioned `3.0.0-rc1` instead of a bare `3.0.0`. Version numbers are
compared piece by piece, and under those rules the old numbering sorted every new
build *below* an older preview release — which meant a portal could have decided the
old preview was the newer one and stopped updating itself.

The release-candidate label restores the correct order, so portals always move forward
to the newest build. Nothing changes in how the product looks or behaves.
