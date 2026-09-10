---
Name: Platform releases no longer fork plugin module builds
Category: Fix
Description: A platform release now compiles every plugin module it composes in one shared workspace, preventing byte-distinct copies of the same dependency from blocking publication and leaving pages empty.
Icon: Checkmark
Order: -20260910
---

# Platform releases no longer fork plugin module builds

A platform release could compile related plugin modules separately even though they came from the
same source commit. Shared dependencies then arrived in the publication with the same name but
different binary identities. The safety gate correctly refused that publication, which stopped the
release wave and left downstream updates waiting.

The release now compiles all four composed plugin modules in one shared workspace and packages those
exact outputs. One source commit therefore has one producer for each shared assembly, so the
publication can be sealed and downstream updates can continue safely.

The container compiler now also supplies each project with the full transitive output of its local
project-reference graph, matching the .NET SDK. This keeps valid module chains such as Northwind's
Application → Model → Domain buildable inside that one workspace instead of forcing a second
compiler back into the release path.
