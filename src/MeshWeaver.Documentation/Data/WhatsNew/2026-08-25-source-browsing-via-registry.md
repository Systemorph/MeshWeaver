---
Name: Module source stays browsable when it leaves the database
Category: Feature
Description: On an adopt-only mesh the NodeType shell's Sources and Tests trees and its code view now read a module's source from the module's repository through the registry — no source nodes on the mesh, no GitHub credential on the consumer.
Icon: Code
Order: -20260825
---

# Module source stays browsable when it leaves the database

`Modules:ImportSourceNodes: false` stops a mesh from persisting a module's `Source/` and `Test/`
files as nodes — which would have left the NodeType shell's Sources and Tests trees empty, reading
like a module with no code at all.

They are not empty. On such a mesh the shell lists a type's compile inputs from the package's
manifest as served by the **registry** this mesh installs from, and opening a file reads its text
through the same registry, read-only, at exactly the URL the imported node would have answered.
The credential is the registry's — the same GitHub App that serves the package — so a private
repository is browsable exactly as far as the registry grants the package, and the consumer never
holds a GitHub credential. A mesh with no registry access says so in place of the tree instead of
showing nothing.

This is what makes retiring the existing source nodes safe later: browsing no longer depends on
them. Meshes that keep importing source (the default) are unchanged.
