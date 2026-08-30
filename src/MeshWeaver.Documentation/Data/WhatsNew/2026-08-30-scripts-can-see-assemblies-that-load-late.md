---
Name: Scripts and completions can see assemblies that loaded late
Category: Fix
Description: The script reference set froze whatever happened to be loaded when the portal built its first one, so a later-loading assembly stayed invisible for the life of the process — completions silently short a symbol, and a script referencing it will not compile.
Icon: Bug
Order: -20260830
---

# Scripts and completions can see assemblies that loaded late

A symbol could be missing from script completions, and a script referencing it could fail to
compile, **for no reason visible in your code** — while the same script worked in another portal
process. Restarting sometimes fixed it, which made it look like a cache.

## What was happening

The script reference set — the surface every script and every completion is compiled against — was
built once per process and then **frozen**. It was built from the list of assemblies loaded at that
moment, and .NET loads assemblies **lazily**: an assembly enters the process the first time
something needs it.

So whatever had not been loaded when the process built its first reference set stayed **invisible
for the life of that process**. Which assemblies those were came down to what ran first — a
load-order lottery, decided differently on every start.

The same lottery had already been noticed and half-fixed for plugin assemblies; it was never the
whole story, because it applies to ordinary assemblies too.

## What changed

The expensive part — reading each assembly's metadata — is still done once and shared, because it
is genuinely costly and its result never changes for a given file. But the **list** is now composed
fresh on every request, so an assembly that loaded a moment ago is included. Later calls are
dictionary lookups against the shared cache, so the cost of being correct is negligible next to the
compilation itself.

## How this was verified

Not by re-running the intermittent test that exposed it. The regression test loads an assembly
**after** the first reference set is built and asserts the next one can see it — impossible against
the frozen implementation, so it fails every time rather than occasionally. Putting the freeze back
makes it fail, naming the assembly that went missing.
