---
Name: Cross-partition shared sources now compile
Category: What's New
Description: Multi-query searches now route each query to its owning partition, so NodeTypes can consume shared source libraries from other Spaces.
Icon: Sparkle
---

# Cross-partition shared sources now compile

A NodeType can declare `shared=@OtherSpace/...` source queries to reuse a code
library published in another Space. Previously, on partitioned PostgreSQL
deployments the whole bundle of source queries was searched only in the first
query's Space, so shared sources from a different Space silently came back
empty and the type failed to compile against them.

Multi-query searches now route every query to the Space that owns it and merge
the results, so cross-partition shared-source libraries (for example a shared
business-rules scope library) work exactly like same-Space ones — no need to
copy the files into your own Space anymore.
