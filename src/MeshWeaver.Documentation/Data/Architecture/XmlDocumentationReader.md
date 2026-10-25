---
Name: The XML Documentation Reader
Category: Architecture
Description: How the platform reads XML-doc summaries for types and members — compiled NodeTypes included — with its own reader instead of Namotion.Reflection, no process-wide cache, no lock, and no pinned collectible generation.
---

# The XML Documentation Reader

Type and property descriptions shown in the portal — the data-model and domain-catalog views, the
edit forms, layout-area descriptions, the JSON schema handed to agents (`ContentJsonSchema`) — come
from the `<summary>` the author wrote in C#. Every read goes through ONE entry point:

```csharp
MeshWeaver.Messaging.Serialization.XmlDocs.Summary(typeof(MyContent));          // a type
MeshWeaver.Messaging.Serialization.XmlDocs.Summary(typeof(MyContent).GetProperty("Title")!); // a member
MeshWeaver.Messaging.Serialization.XmlDocs.DocumentationId(member);              // "P:Ns.MyContent.Title"
```

The implementation lives in `src/MeshWeaver.Messaging.Hub/Serialization/` (`XmlDocs`, `XmlDocFile`,
`DocCommentId`). It replaced the third-party Namotion.Reflection package, of which the platform used
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

**Locating the file.** `{AssemblyName}.xml` beside `Assembly.Location`; for an assembly with no
location (loaded from a stream, or a single-file host) the application base directory and the
current directory, each also under `bin/`. A compiled NodeType is emitted as `{node}.dll` with
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

**The cache.** `ConditionalWeakTable<Assembly, XmlDocFile>`. The callers are display-metadata reads
that hold a `Type` and no hub or service provider, so a mesh-owned singleton is not reachable from
them; a `ConditionalWeakTable` does not keep its key alive, so an entry lives exactly as long as its
assembly and an unloaded NodeType generation is collected with its docs. Keying by the `Assembly`
instance (not its name) gives each recompiled generation its own docs.
`XmlDocsCollectibleContextTest` pins both, with a negative control: a strong reference to the
assembly in the cache makes the generation-collected assertion fail.

**Rendering** matches what the platform has always shown (Namotion's formatting-free mode):
`<see>`/`<seealso>` render as their text, else the `langword`, else the cref's last name segment
(`T:Ns.Type` → `Type`), else the `href`; `<paramref>` as its name; every other element (`<c>`,
`<para>`, `<b>`, `<list>`, …) as its own rendered content, trimmed; `<typeparamref>` renders as
nothing. The first line's indentation is removed from every line.

**Documentation IDs** follow the compiler — nested types joined with `.`, a generic type's arity,
a method's arity, type and method generic parameters, constructed generics in braces, arrays,
by-ref, `#ctor`/`#cctor`, `~ReturnType` on conversion operators, `#` for an explicit
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
declaring type (a positional record). An `<inheritdoc/>` without `cref` resolves from the base class
chain, then the interfaces (by interface map for a method); with `cref` it resolves the referenced
ID in the member's assembly, its type hierarchy's assemblies, or the assembly that defines the
referenced type. Namotion only handled the immediate base and matched by name alone, so an
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

- Read docs only through `XmlDocs`. Do not add another reader or a second cache.
- Keep the read OFF hub construction: `TypeDisplayMetadata.Description` is lazy for the #613 reason
  above — the lookup still reflects over the type.
