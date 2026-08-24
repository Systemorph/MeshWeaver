---
Name: FutuRe sample pages no longer stuck on compile errors
Category: Fix
Description: Six sample NodeTypes that could never compile are now ordinary pages, and a new guard keeps the shape out of the tree.
Icon: Sparkle
Order: -20260824
---

# FutuRe sample pages no longer stuck on compile errors

The FutuRe sample's per-business-unit Line of Business and Transaction Mapping
pages were declared as NodeTypes without any source code, so every deployment
parked them on a compilation-error overlay. They now render as ordinary
documentation pages, keeping their governance prose and their child nodes. A
new build-time guard fails any pull request that ships a NodeType naming a
content type its sources cannot provide, so the broken shape cannot return.
