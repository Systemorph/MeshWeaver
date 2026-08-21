---
Name: Storage backends load their native drivers
Category: Fix
Description: Snowflake and Cosmos ship native libraries that the module publish was deleting, so selecting either backend would have failed at its first call — modules can now carry and resolve native assets.
Icon: Sparkle
Order: -20260821
---

# Storage backends load their native drivers

Alternative storage backends ship as modules beside the portal rather than inside it. Their
native libraries — the pieces that are not .NET code — were being removed when the image was
assembled, because nothing could load them from a module folder anyway. That left Snowflake and
Cosmos looking installable while a deployment that actually selected one would have failed the
moment it called into the driver.

Modules can now carry native libraries, and the portal finds them at the moment they are needed,
picking the build that matches the machine it is running on. Nothing changes for a deployment on
PostgreSQL, which is every memex portal today; what changes is that the other backends are now
genuinely shippable.

This also removes the last blocker on moving image-only features — such as social share-card
rendering, which needs a graphics library — out of the base image and into optional modules.
