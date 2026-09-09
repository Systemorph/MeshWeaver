---
Name: Official releases stay protected without deployment pins
Category: Fix
Description: The image-protection job now retains official release images even when no deployment currently pins them, preserving releases whose support period has not been established as ended.
Icon: ShieldCheckmark
Order: -20260909
---

A release must remain available throughout its support period, including when every current
installation has moved to a newer version. Official release tags now keep their images protected
from cleanup independently of deployment pins. An unknown support date is not treated as permission
to remove the release.
