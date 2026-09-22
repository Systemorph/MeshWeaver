---
Name: Reading a Module's Stamp
Category: Architecture
Description: Reading ONE assembly attribute through reflection resolves the type of ALL of them, so a module whose attribute closure is incomplete killed the read with a nine-frame reflection stack naming nothing. The metadata read that fixes it — and the measurement that says why "refuse an argument that is not a module" is not available as a fix.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M20.59 13.41l-7.17 7.17a2 2 0 0 1-2.83 0L2 12V2h10l8.59 8.59a2 2 0 0 1 0 2.82z"/><line x1="7" y1="7" x2="7.01" y2="7"/></svg>
---

# Reading a Module's Stamp

`InstalledModuleAssembly.Version` is [the one reader](/Doc/Architecture/DependencyRecordFloor) of a module build's
ordered version — the `min:<version>` floor every compiled NodeType's dependency record states. It
asks for one attribute, `AssemblyInformationalVersionAttribute`, which is a corelib type sitting in
the module's own metadata.

It used to ask through reflection, and that is a much bigger question than it looks.

## Reading one attribute costs you all of them

`Attribute.GetCustomAttributes(Assembly, Type)` has to resolve the **declaring type of every
assembly-level attribute record** in order to test it against the filter. One of those types living
in an assembly this process cannot bind is a `FileNotFoundException` thrown out of
`System.Reflection.CustomAttribute.FilterCustomAttributeRecord` — for an attribute nobody asked
about, while the value that *was* asked for is untouched a few bytes away.

Measured 2026-09-18, `mw-plugin-test compile … --module <a plain assembly>`:

```
mw-plugin-test: FATAL — System.IO.FileNotFoundException: Could not load file or assembly
'System.ClientModel, Version=1.14.0.0, …'.
   at System.ModuleHandle.ResolveType(…)
   … seven more reflection frames …
   at MeshWeaver.Mesh.InstalledModuleAssembly.VersionOf(Assembly assembly)
```

Exit 70, and nothing in it names the module, the `--module` argument, or the fact that an
*unrelated* attribute is what could not be resolved. The same property is read by
`NodeTypeCompilationHelpers.ModuleVersionsOf` over every installed module on a live portal, so this
was never the tester's crash alone — it was one landed module with a private dependency away from
being everyone's.

**The fix is to read the stamp out of the metadata**, which resolves no type at all: same
attribute, same string, same bytes, and an incomplete attribute closure is simply not this
property's business. An assembly with no `Location` — loaded from bytes, or inside a single-file
bundle — has no file to read and keeps the reflection path; a module is file-backed by
construction, so the hazardous path is unreachable for one.

## 🚨 "Refuse an argument that is not a module" is not available as a fix

The obvious reading of the crash is that a non-module was passed where a module was expected, and
the obvious remedy is to validate the argument: *"`<path>` is not a module assembly (no module
attribute) — `--module` expects …"*. **Measure it before you build it.** Over the 24 modules
declared by MeshWeaver.Plugins whose binaries were on hand (2026-09-18):

| | Count |
|---|---:|
| carry an assembly-level attribute deriving from `MeshNodeProviderAttribute` | 23 |
| carry no assembly-level attribute at all | **1 — `MeshWeaver.Maps`** |

`Maps/index.json` names `"module": "MeshWeaver.Maps"`, CI composes it as
`--module /ext/modules/MeshWeaver.Maps/MeshWeaver.Maps.dll`, and the assembly is a plain library —
a control and a configuration record, no attribute. A gate refusing "no module attribute" would
refuse a real, shipped module, and it would do so in the lane that bakes content.

**Module-ness is a DEPLOYMENT fact, not a property of the bytes.** It is what a deployment's
`Modules:Assemblies` says it is; the attribute is how a module *contributes*, not how it is
*identified*. So `--module` composes the assembly it is given — that is the whole contract — and
nothing downstream may treat "carries a module attribute" as the definition of a module.

The crash was never about module-ness either way: a module with a private dependency throws the
same stack, and most plain assemblies (`MathNet.Numerics`, `AngleSharp`) never throw at all — they
compose silently, which is correct.

## What a composition failure looks like now

Two further changes, both in the bake lane's `LoadExternalModules`:

- **The module's identity is materialised inside the per-module unit** — name, MVID and version
  together, in the same `try` as the load, and printed on the composition line
  (`bake: module X mvid=… version=1.60.0 — composed into the reference set`). The version used to
  be read much later, under `BakeHost`, where a failure had lost every trace of which `--module`
  argument it belonged to.
- **The translation catches everything.** It was `when (ex is IOException or
  BadImageFormatException)` — the two shapes `Assembly.LoadFrom` throws — and the shape that
  actually reached a caller was neither. A filter there is not an exemption for the rest, it is a
  message covering the failures somebody already thought of. Nothing is swallowed: the cause rides
  as `InnerException` and the run still fails.

And the `compile` verb now returns the refusal as its report's `FatalError` rather than letting it
escape to the last-resort handler, so a bad `--module` reads like every other bake refusal:

```
compile: FATAL — bake: module '/tmp/notanassembly.dll' could not be composed —
BadImageFormatException: Bad IL format. … modules passed with --module must exist at the absolute
path given, with their dependencies beside them …
```

Exit 1, no stack. (`mw-plugin-test build` already routed it this way; only `compile` did not.)

## The rule

> **Never ask reflection for one assembly-level attribute of an assembly you did not build.** You
> are asking it to resolve the type of every attribute on that assembly, and you will be told about
> the one you do not care about. Read the metadata.

Related: [The Dependency Record Floor](/Doc/Architecture/DependencyRecordFloor) · [Module Versioning](/Doc/Architecture/ModuleVersioning) ·
[Module Build Architecture](/Doc/Architecture/ModuleBuildArchitecture)
