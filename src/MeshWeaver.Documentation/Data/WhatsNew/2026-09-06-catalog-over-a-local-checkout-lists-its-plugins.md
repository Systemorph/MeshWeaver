---
Name: A catalog over a local checkout lists its plugins
Category: Fix
Description: A plugin catalog pointed at a local checkout showed "No installable packages found." while its own update watcher listed those same packages — the node can now declare its repository format, and both readers honour it.
Icon: PackageImport
Order: -20260906
---

A `PluginCatalog` node pointed at a local node-repo checkout showed **"No installable packages
found."** even though the repository was full of them.

The catalog node could not say which format its repository used, so the browse page always read it
as a `package.json` manifest repo — while the update watcher, reading the very same node, treated it
as a node repo. One record, two readers, opposite answers.

The node now carries a `Format` field — the same values and the same `node-repo` default as the
`PluginCatalog:Sources:N:Format` configuration knob — and both readers resolve it through one shared
rule. A catalog over a local checkout lists its plugins; a `package.json` repo opts in with
`Format: package-json`.
