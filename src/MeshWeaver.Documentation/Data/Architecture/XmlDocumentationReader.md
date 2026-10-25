---
Name: The XML Documentation Reader
Category: Architecture
Description: How the platform reads XML-doc summaries for types and members — compiled NodeTypes included — with its own reader instead of Namotion.Reflection, no process-wide cache, no lock, and no pinned collectible generation.
---

# The XML Documentation Reader

Type and property descriptions shown in the portal — the data-model and domain-catalog views, the
edit forms, layout-area descriptions, the JSON schema handed to agents (`ContentJsonSchema`) — come
from the `<summary>` the author wrote in C#. Every read goes through the MESH's reader:

```csharp
hub.XmlDocs().Summary(typeof(MyContent));                        // a type
hub.XmlDocs().Summary(typeof(MyContent).GetProperty("Title")!);  // a member
typeRegistry.XmlDocs().Summary(member);                          // from a hub's ITypeRegistry
typeDefinition.XmlDocsSummary(member);                           // through a definition's registry
XmlDocs.DocumentationId(member);                                 // "P:Ns.MyContent.Title"
```

The implementation lives in `src/MeshWeaver.Messaging.Hub/Serialization/` (`XmlDocsReader`,
`XmlDocFile`, `DocCommentId`, and the `XmlDocs`/`XmlDocsExtensions` entry points). The old static
`XmlDocs.Summary(...)`, `ContentJsonSchema.Generate(type, options)` and
`ContentSchemaFacts.FromType(type, options)` remain as `[Obsolete]` forwarders for compiled callers
(in-mesh sources included); they own no cache, so each call parses the file again. It replaced the third-party Namotion.Reflection package, of which the platform used
exactly one capability, `GetXmlDocsSummary`.

## Why it was replaced

- **A process-wide static cache.** Namotion keeps one `XDocument` per assembly in a static
  dictionary — the shape the [No Static State](../NoStaticState) rule forbids. It was keyed by the
  assembly's NAME, so a recompiled NodeType (same name, new collectible generation) kept serving the
  first build's docs for the process lifetime.
- **A global lock to make it safe.** Concurrent first reads of the same cached `XDocument` tore its
  object graph (`AccessViolationException` in `XContainer.GetElements` → SIGSEGV, exit 139), so every
  lookup in the platform was serialised behind one `Lock`.
- **The #613 teardown crash ran through it.** The FutuRe.Test teardown SIGSEGV dumped inside the
  doc reader (`ToXmlDocsContent → StringBuilder.ToString`) while compiled-NodeType load contexts
  unloaded (Systemorph/MeshWeaver#613; see the notes in `TypeDefinition`'s constructor and
  `HostedHubsCollection.CloseCreation`). The structural fixes — the lazy description and the
  creation freeze — stay; the reader no longer holds shared mutable XLinq state on top of them.
- **Wrong IDs.** Namotion's documentation IDs dropped a generic method's arity suffix (so it read
  the NON-generic overload's docs), spelled by-ref parameters `System.Type&` instead of
  `System.Type@`, and failed on members of generic type definitions — those members read empty.

## How it works

**Locating the file.** `{AssemblyName}.xml` beside `Assembly.Location`, and nowhere else: a
located assembly whose sidecar is missing reads as undocumented, because probing elsewhere could hand
a collectible generation another generation's same-named file. Only an assembly with NO location
(loaded from a stream, or a single-file host) probes the application base directory and the current
directory, each also under `bin/`. A compiled NodeType is emitted as `{node}.dll` with
assembly name `DynamicNode_{node}`, and `EmitPipeline` writes `DynamicNode_{node}.xml` beside it,
so the first probe finds it. Dynamic assemblies have no docs. Namotion additionally probed the NuGet
cache and the SDK's reference packs on a developer machine; that is deliberately NOT carried over —
no deployed image has those directories, so it only showed summaries locally (the measured effect:
the `<inheritdoc/>` of `IHostedService.StartAsync` on a handful of hosted services) that production
never showed.

**Parsing once, into immutable data.** The file is loaded once per assembly, every `<member>` is
rendered to text immediately, and the `XDocument` is dropped. What remains is an
`ImmutableDictionary<string, XmlMemberDoc>` of plain strings: summary, the `<param>` texts, and
whether the member carries `<inheritdoc/>` (and its `cref`). Nothing read concurrently is a live
XLinq tree, so there is no lock.

**The cache is owned by the mesh.** One `XmlDocsReader` per mesh, created by the root
`TypeRegistry` of the mesh's registry chain beside its `TypeDisplayMetadataCache` and handed down to
every hub's registry — never a `static`, so neither a snapshot nor a cached MISS outlives the mesh
(`XmlDocsTest.TheReader_IsOnePerMesh_SharedDownTheRegistryChain`). Inside it the per-assembly
snapshots sit in a `ConditionalWeakTable<Assembly, XmlDocFile>`, which does not keep its key alive,
so an unloaded NodeType generation is collected with its docs; keying by the `Assembly` instance
(not its name) gives each recompiled generation its own docs. `XmlDocsCollectibleContextTest` pins
both, with a negative control: a strong reference to the assembly in the cache makes the
generation-collected assertion fail. Every call site reaches the reader through a hub, a hub's
`ITypeRegistry`/service provider, or an `ITypeDefinition`; a definition built outside any registry
(the public `TypeDefinition` constructor) owns a reader of its own.

**Per-mesh, so kept small and shared where it can be.** A process running many meshes — a test
host above all — parses the same files once per mesh, and disposed meshes can stay reachable for a
while (the HTTP client factory's handler lifetime holds a mesh's service provider for about two
minutes). A heap dump of `Memex.Portal.Shared.Test` (MeshWeaver.Plugins run 40214543622, which the
test infrastructure's 6 GiB memory watchdog aborted) showed 88 live readers, 1,032,764
`XmlMemberDoc` entries and 835 MB of strings, about 10 MB of rendered text per mesh. So the
snapshot keeps only what a lookup can read: a member with no summary, no `<inheritdoc/>` and no
`<param>` is left out (a lookup treats it exactly as absent); `<param>` texts are kept on TYPE
entries only, because the positional-record fallback is the one place they are read; and the texts
and IDs of an assembly that can never unload are interned with `string.Intern`, so every mesh's
snapshot points at one copy of each string (`XmlDocsTest.TwoMeshes_ShareOneCopyOfEachSummaryText`).
A collectible NodeType assembly's texts are not interned: the runtime's intern pool would keep them
alive after its load context unloads. Interning shares immutable VALUES, never a cache entry, so
no snapshot and no cached miss is visible to another mesh.

**The first read is synchronous.** A cold lookup reads and parses the file on the caller's thread,
exactly as Namotion did. It is not warmed through the IO pool: `IIoPool` lives in
`MeshWeaver.Mesh.Contract`, which depends on the assembly the reader lives in, and a warm-up the
synchronous description then waited for would be a blocking bridge. Moving it off the render path
means making the description a data-bound value rather than a string read during rendering.

**Rendering** matches what the platform has always shown (Namotion's formatting-free mode):
`<see>`/`<seealso>` render as their text, else the `langword`, else the cref's last name segment
(`T:Ns.Type` → `Type`; the `X:` kind prefix is parsed, so a global-namespace `T:GlobalType` renders
`GlobalType` where Namotion rendered `T:GlobalType`), else the `href`; `<paramref>` as its name; every other element (`<c>`,
`<para>`, `<b>`, `<list>`, …) as its own rendered content, trimmed; `<typeparamref>` renders as
nothing. The first line's indentation is removed from every line.

**Documentation IDs** follow the compiler, and `XmlDocsCompilerParityTest` proves it against the
compiler itself: it compiles a fixture covering every member shape with XML documentation and
requires every ID Roslyn wrote to be produced by reflection over the same assembly. They follow the compiler — nested types joined with `.`, a generic type's arity,
a method's arity, type and method generic parameters, constructed generics in braces, arrays,
by-ref, `#ctor`/`#cctor`, `~ReturnType` on conversion operators (`op_Implicit`, `op_Explicit`, `op_CheckedExplicit`), `#` for an explicit
implementation's dots, index parameters for an indexer:

```text
T:Ns.Outer`1.Inner`1
M:Ns.T.M``1(``0,System.Int32@,System.String[],System.Int32[0:,0:],System.Nullable{System.Int32})
M:Ns.Outer`1.Inner`1.Take(`0,`1,Ns.Outer{`0}.Inner{`1})
M:Ns.T.op_Implicit(Ns.T)~System.Int32
M:Ns.T.System#IDisposable#Dispose
P:Ns.T.Item(System.String,System.Int32)
```

A constructed generic type or a member of one reads the generic definition's docs.

**Fallbacks.** A property with no summary of its own reads the `<param>` of the same name on its
declaring type (a positional record) — at every hop, so a cref to such a property reads it too. A
constructed generic method reads, implements and overrides as its generic definition. An `<inheritdoc/>` without `cref` resolves from the base class
chain, then the interfaces (by interface map for a method); with `cref` it resolves the referenced
ID in the member's assembly, its type hierarchy's assemblies, or the assembly that defines the
referenced type, then reads the referenced MEMBER like any other — so its own `<inheritdoc/>`
continues in its declaring type's context. Through an interface map when the declaring type has one
(methods directly, properties and events through their accessors), so an explicit `IB.Name` reads
`IB`'s summary even when `IA` also declares `Name` — open generic definitions included, which
`GetInterfaceMap` answers too. Only a type the runtime refuses a map for falls back to a name match,
restricted to EXACTLY the interface an explicit implementation's qualifier names (`IFoo` is not
`IFoo<int>`, `Outer<T>.IA` is not `Outer<T>.IB`). Signatures compare generic arity
as well as parameters (`M()` is not `M<T>()`). An override finds the base member it overrides by its virtual SLOT (`GetBaseDefinition`), not by
parameter types: constructing a generic base can make two overloads look identical (`Base<int>.M(T)`
and `M(int)`). Members are compared by METADATA identity (module and
token, a constructed generic method as its definition), never by reference: the same member reached
through a subclass, a constructed type or an interface map is a different `MemberInfo` object. Each
lookup memoizes every member and base-chain walk it resolves, so a chain of undocumented
`<inheritdoc/>` overrides is visited once per member rather than exponentially, and a member already
open on the stack (a cref cycle) reads empty instead of overflowing it
(`XmlDocsInheritDocTest`, `XmlDocsCompilerParityTest`, which also reads every inherited member through
the declaring type, a subclass, the open definition, a constructed type and the interface). Namotion only handled the immediate base and matched by name alone, so an
overloaded base member made it read nothing.

## Parity evidence

Before the package was removed, both readers were run over every type and every declared member of
six platform assemblies (Messaging.Hub, Data, Layout, Domain, Mesh.Contract, Graph — 72,373 lookups):

| Outcome | Count |
|---|---|
| Identical text | 71,712 (17,194 non-empty) |
| Text only from the new reader (Namotion's ID or inheritdoc failed) | 641 |
| Different text — Namotion read the non-generic overload of a generic method | 6 |
| Text only from Namotion — NuGet-cache `<inheritdoc/>` of framework interfaces (dev machine only) | 14 |

`XmlDocsTest` hard-codes Namotion's output for representative members (method, field with
`<see cref>`/`<c>`, record-param fallback with `<paramref>`, private nested record, constructed and
nested generic types, constructor, `<inheritdoc/>` from an interface) and pins the IDs of every
member shape.

## Rules

- Read docs only through the mesh's `XmlDocsReader` (`hub.XmlDocs()`). Do not add another reader, a second cache, or a `static` one.
- Keep the read OFF hub construction: `TypeDisplayMetadata.Description` is lazy for the #613 reason
  above — the lookup still reflects over the type.
