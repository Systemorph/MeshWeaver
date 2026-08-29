---
Name: Comments and track changes ship as a module
Category: Feature
Description: Document collaboration is now an installable module rather than part of the platform image, so a deployment can carry it or leave it out.
Icon: Sparkle
Order: -20260829
---

# Comments and track changes ship as a module

Inline comments, the comment composer and the tracked-change view model are no longer compiled
into the platform. They ship as the **MeshWeaver.Collaboration** module, required by the
pre-installed **Essentials** package — so every portal still gets them exactly as before, and a
deployment that publishes read-only content can now leave them out instead of carrying a comment
composer it never shows.

Nothing changes for existing content. Comments and suggestions written before this release keep
working, keep deserializing, and keep delegating their permissions to the document they hang off:
the comment and tracked-change records, their type-registry entries, the satellite access rule and
the `_Comment` / `_Tracking` → `annotations` routing all stay in the platform. A mesh that later
drops the module keeps its data — it simply stops offering the comment UI.

The one visible seam is where a document page admits the section. A node type now says it accepts
module contributions rather than naming comments directly, which is what lets the feature arrive
from outside the image without changing which pages show it.
