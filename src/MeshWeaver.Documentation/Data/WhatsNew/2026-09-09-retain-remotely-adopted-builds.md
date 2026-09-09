---
Name: Retain builds still adopted by remote portals
Category: Fix
Description: Instance reports identify older adopted builds so central cleanup can protect them.
Icon: Sparkle
Order: -20260909
---

# Retain builds still adopted by remote portals

Instance reports now include older builds still adopted by their modules, alongside the platform they currently run. Central bundle retention includes these reported builds in its protected references. An incomplete or legacy adoption report blocks cleanup until a complete report arrives. The remote receiver must also support these fields; adding the contract does not enable deletion.
