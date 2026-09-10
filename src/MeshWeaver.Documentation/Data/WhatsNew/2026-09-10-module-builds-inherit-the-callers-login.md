---
Name: Module builds no longer stop before they start
Category: Fix
Description: Shared module checks once again start for repositories using registry credentials, while platform releases retain their short-lived workload identity.
Icon: Checkmark
Order: -20260910
---

# Module builds no longer stop before they start

A shared module build now inherits the registry-login permissions chosen by its caller. Repositories
using their existing registry credentials can start their checks normally, while the platform release
continues to use its short-lived workload identity without giving that capability to every caller.
