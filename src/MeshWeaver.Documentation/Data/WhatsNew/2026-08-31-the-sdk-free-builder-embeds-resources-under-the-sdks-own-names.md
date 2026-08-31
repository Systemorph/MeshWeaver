---
Name: The SDK-free builder embeds resources, under the SDK's own names
Category: Feature
Description: build-project compiles a project with no dotnet SDK, and it now carries <EmbeddedResource> items into the assembly under the manifest names the real SDK would produce — every rule established by measuring the SDK rather than recalling it, and every construct whose name could not be matched refused out loud.
Icon: Package
Order: -20260831
---

# The SDK-free builder embeds resources, under the SDK's own names

`mw-plugin-test build-project` compiles a `.csproj` with no dotnet SDK, no NuGet restore and no
platform checkout, against the assemblies of a MeshWeaver image. Until now it **refused**
`<EmbeddedResource>` outright, and that refusal was the single biggest coverage gap: of the 54
non-test projects in `MeshWeaver.Plugins/src`, **19 failed on nothing else** — most of them on one
line, `<EmbeddedResource Include="Data\**\*.md">`.

The refusal was not timidity. **A wrong manifest-resource name is the quietest defect this builder
could ship.** The assembly compiles, the emit is verified, the DLL loads — and
`Assembly.GetManifestResourceStream(name)` returns `null` at run time, in some other process, weeks
later. Nothing goes red anywhere in between. Core's own `MeshWeaver.Messaging.Hub.csproj` already
carries a comment describing that exact outcome for its `strings.*.json`: *"the build succeeds, the
main assembly carries ZERO manifest resources, every lookup falls through to the key-fallback path,
and the UI renders raw `chat.new` tokens."*

So the rules were **measured, not remembered**. Each one was established by building a probe project
with the real .NET SDK and reading the manifest-resource table back out of the emitted PE. They are
not guessable:

- The **directory** is mangled into identifiers and the **file name is not** —
  `Data\with-dash\Three.md` becomes `…Data.with_dash.Three.md`, while `Weird-File.Name.md` keeps its
  hyphen and its dot.
- A leading digit is **prefixed**, not replaced: `9digits` → `_9digits`.
- A dot inside a directory name is a **separator**: `Dot.9Dir` → `Dot._9Dir`.
- A segment that reduces to a single underscore is **doubled** — sibling directories `--` and `_`
  both become `__`, and a project with both fails the real SDK build with `CS1508`.
- `$(RootNamespace)` defaults to the **project** name, never `$(AssemblyName)`; empty means no prefix
  at all.
- A file outside the project with no `Link` **loses its directory entirely**.

## What it refuses, and why that is the feature

Where a rule could not be matched exactly, the construct is refused **by name**, with its own
`--accept` token — because a plausible-looking wrong name is worse than no build:

- **`.resx` / `.restext`** — the name is reproducible, the *content* is not: it needs resgen.
- **A culture in the file name** — the SDK routes it into a **satellite assembly** and out of the
  main one, and an explicit `LogicalName` does **not** rescue it. `WithCulture="false"` is the
  project-side fix. (An explicitly declared `Culture` beats even that — also measured, also the
  opposite of what the two names suggest.)
- **`DependentUpon`**, **`ManifestResourceName`**, and embedding **the build's own output**.

## The number, and how to read it

Over the same 54 projects and the same pinned portal image: **9 green before, 10 after — and 19
resource refusals down to 0.**

That looks like a poor return until you notice what a refusal does. **It stops the load, so it masks
every gap behind it.** Those 19 projects were not "19 projects away from green"; they were 19
projects about which nothing further was known. Closing the gap turned one green and revealed the
real blocker for the other eighteen — ten want SDK source generators, three want Razor, three want an
additional library, one wants protoc. The previous sweep counted 3 source-generator failures; the
true figure was 14.

**In a builder designed to refuse rather than guess, a failure ranking is a ranking of FIRST
refusals.** Removing the top entry does not add its count to the green column. It redistributes it —
and that is the honest way to read every such table, including the next one.
