---
Name: A module brings its own dependencies again
Category: Fix
Description: Installed modules no longer fail to load with "Could not load file or assembly" — a module package now carries the libraries it needs instead of assuming the portal happens to have them.
Icon: Sparkle
Order: -20260901
---

# A module brings its own dependencies again

A module — the AI engine, an import format, a provider connector — arrives at your portal as a
small package of compiled files. Whatever it needs to run and does not bring with it, the portal
has to already have. So the package has to be honest about what it needs.

For a short window it was not. The build had started deciding "the portal already has this
library, so the package need not carry it" by looking at *one particular portal* — the one that
built it, which happened to have the module compiled in and therefore had all of that module's
libraries lying around. On that portal everything worked. Anywhere else the same package landed
without its own dependencies, and the first thing that touched them failed with

```
Could not load file or assembly 'Microsoft.Agents.AI' …
```

That is not a message anyone can act on: it names a library nobody asked for, on a machine that
installed a package the publisher said was complete. It surfaced as installs that would not come
up, and as red builds in projects that had changed nothing and merely *used* a published package.

A package now carries the libraries its own code pulls in, always — and leaves out only what
travels with every portal by definition (the .NET runtime itself). That is the same rule the older
build path used, so a package's contents no longer depend on which builder produced it. Installs
that were failing on a missing library work on the next published version, with nothing to change
on your side.
