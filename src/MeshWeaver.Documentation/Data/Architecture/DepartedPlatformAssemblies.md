---
Name: Departed Platform Assemblies
Category: Architecture
Description: An assembly that leaves the platform for a module breaks every OTHER module that binds it — at load time, invisibly to every compile gate. The break shape, the six-night reproducer that established it, and why "those are the platform" is one answer per host rather than one answer.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 7h11v10H3z"/><path d="M14 10h4l3 3v4h-7z"/><path d="m17 3-2 4h4l-2 4"/></svg>
---

# Departed Platform Assemblies

**An assembly that leaves the platform for a module breaks every OTHER module that binds it — at
LOAD time, invisibly to every compile gate.**

Both halves compile. The platform builds without it. The depending module builds against it — it is
right there in the same repository. Nothing anywhere asks whether the assembly it moved *into* is in
the closure of every module that binds it, and the failure surfaces as a module that loads and
contributes nothing.

## 1. The reproducer that established it

MeshWeaver.Manufacturing's scheduled run, red every night 2026-09-04 → 09-09:

```
[MeshWeaver.Mesh.IncompatibleModule] Module 'MeshWeaver.AI' did not install against this platform
build and is CONTRIBUTING NOTHING. It requires
'MeshWeaver.ContentCollections.Indexing.ChunkPosition
 (MeshWeaver.ContentCollections.Indexing — this deployment carries no such platform assembly)'

[MeshWeaver.Mesh.IncompatibleModule] Module 'MeshWeaver.Markdown.Collaboration' … requires
'MeshWeaver.Blazor.BlazorView`2 (MeshWeaver.Blazor — …no such platform assembly)'
```

**Two modules, two departed assemblies, one shape** — which is what makes it a rule rather than an
incident. The cascade from there is worth knowing, because none of it names the cause:

| step | what is seen |
|---|---|
| AI contributes nothing | its `BuiltInAgentProvider` / `BuiltInSkillProvider` never run |
| so `Agent` / `Skill` resolve to nothing | `Install of 'Store' failed: NodeType(s) not registered: Skill` |
| `Store/Plugin` is declared BY `Store` | every package rooted on it fails: *"NodeType 'Store/Plugin' is not registered"* |
| 36 installs refused | `GATE FAILED — the bake declares 103 NodeType(s), NONE of which this run installed` |

A reader starting at the bottom finds a bake-consumption failure; three levels up is a module that
could not load. The first three explanations tried on that issue — missing `Agent`/`Skill` packages,
a stale catalog, an identity skew — were all wrong, and each was reached by stopping at the first
line that fit.

## 2. Why the compile gates cannot see it

The cross-repo pair gate covers *a public type leaving `src/`*, and #2941 (*"The indexing pipeline,
the map control and Cornerstone leave the content surface"*) presumably satisfied it — nothing
failed to compile, because the consumer moved with the assembly. What no gate covers is the
**runtime closure**: whether the bundle that now owns the assembly is loaded wherever a module
binding it is loaded.

This is the tenth break shape in AGENTS.md's list in all but name, and the one whose evidence
arrives latest — a green build, a green publish, and a module that silently does nothing.

## 3. "Those are the platform" is one answer PER HOST

`DepsClosure` decides what a bundle carries with this rule:

> stopping at (and never bundling) `MeshWeaver.*` nodes: **those are the platform**, they ship in the
> consumer's `/app` by construction

The exclusion list is MeshWeaver.Plugins' `src/platform-shipped.txt`. Both rest on "the platform"
being one thing. It is not — the two hosts that load these bundles are built from **different
repositories**:

| host | built from | carries `Blazor` / `Indexing`? |
|---|---|---|
| `memex-portal-ai` | **Plugins** — `Memex.Portal.Gui/.Distributed/.Monolith`, alongside `src/MeshWeaver.Blazor*` and `src/MeshWeaver.ContentCollections.Indexing.*` | **yes** |
| `mw-plugin-test` | **core** — `tools/MeshWeaver.PluginTester`, whose whole closure is seven core projects, none reaching Blazor | **no — neither assembly exists in core at all** |

and `platform-shipped.txt`'s own comments record it being measured *twice against the portal*
(images `ci.7755` / `ci.7794`, and the three `Memex.Portal.*` closures). **It is right for the host it
was measured against and wrong for the other**, and because they are built from different
repositories no re-measurement of a single image reconciles them.

🚨 This is determinable **statically**, from the two reference sets — the investigation that found it
first proposed extracting both images, which was unnecessary.

## 4. The trap in the obvious fix

Deleting the two roster lines makes the tester work and reintroduces on the portal exactly what the
roster exists to prevent. Its own comment:

> omitting them is what makes the Indexing bundle carry a second copy of an assembly `/app` already
> has, which is the **same-identity duplicate this whole file exists to prevent**

— i.e. [#3732](https://github.com/Systemorph/MeshWeaver/issues/3732)'s two-producer defect, whose
live `heldReason` reads *"the loader keeps whichever it sees first, so the other's NodeTypes are
declined at adoption"*.

So it is a decision between two real costs, and it belongs to whoever owns the module split:

1. **A per-host roster** — pack per consumer. `module-owned-platform.sh` already takes the host as an
   argument (`<src> [<platform-app-dir>]`); what is missing is that the pack resolves ONE answer for
   hosts that do not agree.
2. **Carry it and let `/app` win** — which is precisely the trade `DepsClosure`'s *diamond* paragraph
   already argues for non-`MeshWeaver.*` packages: *"the default load context resolves `/app`'s copy
   first whenever the platform carries one … and the module's copy takes over the moment the platform
   stops carrying it… shedding a dependency from the platform (the very point of the module split)
   would silently break every landed module that relied on it."* That is this failure, one
   assembly-name convention over.
3. **Make the tester's closure match the portal's** — which changes what the gate gates.

## 5. What to check when it happens again

- `IncompatibleModule … CONTRIBUTING NOTHING` naming an assembly, in the boot or install log — that
  IS the diagnosis; everything downstream of it is cascade.
- Whether the named assembly exists in the repository the LOADING host is built from.
- Whether `platform-shipped.txt` names it — if so, the bundle omitted it on purpose.
- A green push run proves nothing here: `push` pins the image to the wave the modules came from, so
  only a release-follow poll exercises the pairing.

Related: [#3221](https://github.com/Systemorph/MeshWeaver/issues/3221) (module closure — the sibling
question), [#3732](https://github.com/Systemorph/MeshWeaver/issues/3732) (the two-producer half),
[#3768](https://github.com/Systemorph/MeshWeaver/issues/3768), and
[#3583](https://github.com/Systemorph/MeshWeaver/issues/3583) (the same asymmetry from the source
side).
