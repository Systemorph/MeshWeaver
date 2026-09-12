---
Name: Module compatibility follows the APIs you use
Category: Feature
Description: The module and compiler documentation states the API-based compatibility rule, with regression coverage for version differences, removed APIs and preservation of a working module.
Icon: 🔗
Order: -20260909
---

Modules should keep working across platform and dependency releases when the APIs they use remain
compatible. A version or build-identity difference alone does not establish incompatibility, and
identical version numbers cannot make a removed API safe.

The [adoption policy](@/Doc/Architecture/ModuleAdoptionPolicy),
[plugin mechanism](@/Doc/Architecture/Plugins) and
[compiler lifecycle](@/Doc/Architecture/NodeTypeCompilation) now state this rule together. The
regression tests use real compiled contracts and the real metadata/landing paths. They cover
compatible APIs across versions, removed types with unchanged versions, and an incompatible
upgrade preserving the working module. The docs also distinguish type checks from member checks
and explain why a NodeType bake cache miss normally means recompilation, not a platform hold.
