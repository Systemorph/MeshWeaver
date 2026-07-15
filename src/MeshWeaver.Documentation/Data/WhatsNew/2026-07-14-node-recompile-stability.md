---
Name: More reliable node recompilation
Category: What's New
Description: Recompiling code/scope nodes no longer leaks unloaded assemblies or risks a rare crash under load.
Icon: Sparkle
---

# More reliable node recompilation

Compiling and recompiling code and scope nodes now cleanly releases the previous
version's compiled assembly. Previously the runtime's dependency-injection cache kept a
hidden reference to each unloaded assembly, which slowly grew memory over many recompiles
and, under concurrent activity, could trigger a rare hard crash. Portals that frequently
edit and recompile nodes will be more stable and use less memory over time.
