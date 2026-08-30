---
Name: A configuration or secret change now restarts the portal
Category: Fix
Description: helm upgrade with a changed ConfigMap or Secret rolls the portal pods, so the new setting is actually running — before, the release reported deployed while the pods kept the old configuration
Icon: Sparkle
Order: -20260830
---

# A configuration or secret change now restarts the portal

Changing a portal setting or secret and running `helm upgrade` (or `memex-local up`) now rolls the portal pods, one at a time and gracefully, so the setting you changed is the one the portal runs with. Before, the release reported "deployed" and the rendered ConfigMap and Secret carried the new value, but the running pods — which read configuration only at start — kept the old one until something else happened to restart them. That is how an open-registration key delivered to memex.meshweaver.cloud still answered 401 for an hour after the deploy. An upgrade that changes nothing still restarts nothing.
