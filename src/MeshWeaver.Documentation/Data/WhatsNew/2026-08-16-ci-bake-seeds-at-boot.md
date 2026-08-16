---
Name: Deployments start faster — content is compiled once on CI and reused
Category: Feature
Description: Framework builds are now commit-deterministic, so the NodeType assemblies CI compiles are adopted by every portal at boot instead of being recompiled on each pod.
Icon: Sparkle
Order: -20260816
---

# Deployments start faster — content is compiled once on CI and reused

Every deployed build used to recompile all of its dynamic content on startup, because each CI
build carried a unique timestamp that made its compiled assemblies unrecognizable to any other
build — even of the exact same code. Builds are now deterministic per commit: CI compiles the
shipped content once, publishes the result, and every portal built from that commit picks it up
at boot instead of doing the work again.

What you notice: after a platform update, pages backed by dynamic content are warm right away —
the new pod no longer spends its startup re-baking hundreds of node types, and the first visitor
no longer pays for a compile.
