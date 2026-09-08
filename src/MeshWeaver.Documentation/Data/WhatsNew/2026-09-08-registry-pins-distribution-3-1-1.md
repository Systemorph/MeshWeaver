---
Name: The fleet registry pins distribution 3.1.1
Category: Fix
Description: The container registry now runs distribution 3.1.1 — on 3.0.0 the workload-identity storage credential was signed as a shared key and the registry pods never became ready.
Icon: Box
Order: -20260908
---

# The fleet registry pins distribution 3.1.1

The container registry the deployment chart can stand up now pins CNCF distribution 3.1.1
instead of 3.0.0. On 3.0.0 the registry authenticated to blob storage with an empty shared key
even when configured to use the pod's workload identity, so its storage health check failed and the
registry pods never became ready. From 3.1.0 on the identity is used as configured; the example
values, the design page and the chart's own hint name the new pin.
