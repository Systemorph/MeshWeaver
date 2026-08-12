---
Name: Background services no longer run their work on the mesh router
Category: Fix
Description: Plugin installs, log-incident filing and one-shot node reads now run on a dedicated work hub instead of the mesh router, keeping routing responsive and removing a steady stream of router-traffic error logs.
Icon: Sparkle
Order: -20260810
---

# Background services no longer run their work on the mesh router

The mesh router's only job is to route messages — but several background services (the plugin
default-install seed, log-incident filing, GitHub sync writes, and one-shot node reads) were
issuing their work directly on it. Every such operation competed with routing itself and produced
a steady stream of router-traffic error logs (hundreds per day on a busy portal).

All of these now issue their work on the dedicated off-router work hub, so routing stays
responsive under load and the error stream disappears. The router-traffic detector also no longer
flags the router's own undeliverable-message notices, so the remaining reports point at genuine
problems only.
