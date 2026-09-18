---
Name: Portal update compatibility
Category: Fix
Description: Portal updates measure the assemblies shipped by the portal, including its seeded modules.
Icon: ArrowSync
Order: -20260916
---

Portal updates no longer report an assembly as missing just because it is absent from the smaller test host. The release publication measures the actual portal image and its seeded modules; unreadable assemblies still fail verification.
