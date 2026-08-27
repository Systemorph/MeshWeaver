---
Name: Stale NodeTypes now say what actually went stale
Category: Fix
Description: After a platform update, NodeTypes waiting to rebuild are reported as framework-stale instead of blaming a module change, and a genuine dependency drift now names the dependency.
Icon: Sparkle
Order: -20260827
---

# Stale NodeTypes now say what actually went stale

After a platform update, every NodeType has to be rebuilt against the new platform. That is normal
and harmless. Until now the portal described those NodeTypes as having a changed module or toolchain
dependency, which read as a fault in your content when nothing about your content had changed.

Types waiting for an ordinary post-update rebuild are now reported as framework-stale, which is the
state that has always been documented as benign. The message that blames a changed dependency is now
reserved for the case where the platform did NOT move and something a type binds genuinely did — and
in that case it names the dependency that moved, instead of leaving you to guess.

This is a reporting change only: exactly the same NodeTypes are rebuilt, at the same time, as before.
