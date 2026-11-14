---
Name: Closed Hierarchies and Unions
Category: Architecture
Description: When the platform may use a C# 15 `closed` hierarchy or a `union` (internal, never persisted or serialized, never read by in-mesh C#, never extended by a module), how to write the switch so the compiler actually checks it, and the types converted so far.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="3" width="18" height="18" rx="2"/><path d="M3 12h18"/><path d="M12 3v18"/></svg>
---

# Closed Hierarchies and Unions

> **The rule in one sentence:** a C# 15 `closed` hierarchy or `union` is allowed only for a type that
> is **internal**, **never persisted or serialized**, **never read by in-mesh C#** and **never extended
> by a module**. Everything else stays open: string constants for a vocabulary, an ordinary record
> hierarchy for an extensible type.

## What the language gives

A `closed` class (or record) can be derived from only inside its declaring assembly, so the set of
direct descendants is fixed at compile time. A `switch` **expression** that names every direct
descendant is exhaustive and needs no default arm. One that misses a case is `CS8509`, which our
`-warnaserror` build turns into an error. A `closed` class is implicitly `abstract`, cannot also be
`sealed`, `static` or explicitly `abstract`, and closedness is **not transitive**: mark an
intermediate level `closed` too if the check should reach further down
([What's new in C# 15](https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-15)).
A `union Pet(Cat, Dog, Bird)` gives the same exhaustiveness over a set of case types, plus an implicit
conversion from each (same page).

🚨 **Only a switch EXPRESSION is checked.** Measured with SDK 11.0.100: a switch expression that omits
a case reports *"CS8509: The switch expression does not handle all possible values of its input type
(it is not exhaustive). For example, the pattern '…CompletedEmpty' is not covered."* The same omission
in a switch **statement** compiles silently. So a converted call site whose arms are statements
selects an `Action` with a switch expression and invokes it, rather than keeping an `if` chain or a
`switch` statement:

```csharp
Action reply = outcome switch
{
    DetachedReplyOutcome<bool>.Emitted(true) => evaluate,
    DetachedReplyOutcome<bool>.Emitted(false) => RefuseUnauthorized,
    DetachedReplyOutcome<bool>.Faulted(var error) => () => RefuseUndetermined(error),
    DetachedReplyOutcome<bool>.CompletedEmpty => () => RefuseUndetermined(null),
};
reply();
```

A conversion removes the default arm (a throwing `_ =>`, a hard cast, or the silent fall-through
at the end of an `if` chain), because the compiler now proves there is no other case.

## Why the four conditions

1. **Internal.** A public closed type is part of the surface a satellite or a module binds. Adding a
   case to it breaks every consumer's switch under `-warnaserror`, in repositories that core's CI
   never builds.
2. **Never persisted or serialized.** System.Text.Json writes a union's active case directly, without
   a `$type` discriminator
   ([.NET 11 libraries](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/libraries)).
   That bypasses our polymorphic `$type` convention, and an untyped payload is the
   silent null that `.As<T>()` / `.ContentAs<T>()` exist to prevent.
3. **Never read by in-mesh C#.** NodeType sources compile at runtime in the portal, and no
   `dotnet build` sees them ([NodeType Compilation](../NodeTypeCompilation)). A public type that in-mesh
   code switches on may change only additively. `NodeReadStatus` and `PermissionCheckOutcome` are
   such types: MeshWeaver.Plugins NodeType sources switch on them today.
4. **Never extended by a module.** A set that a module, satellite or deployment extends is an open
   vocabulary ([Open Vocabularies Are String Constants](../OpenVocabulariesAsStringConstants)).
   `UiControl`, `WorkspaceReference`, the stream messages and the data-source bases are therefore
   never candidates.

**Unions additionally wait** until the portal's runtime compiler is proven to compile C# 15
(`EmitPipeline.CreateParseOptions()` sets no language version). A `closed` hierarchy that meets the
four conditions is invisible to in-mesh code, so that question does not arise for it.

## Converted so far

| Type | Project | Was | Default arm removed |
|---|---|---|---|
| `DetachedReplyOutcome<T>` → `Emitted` / `Faulted` / `CompletedEmpty` | `MeshWeaver.Mesh.Contract` | `(bool HasValue, T? Value, Exception? Error)`; "completed empty" was "neither field set" | the fall-through at the end of each of the five upsert call sites' `if` chains |
| `CodeTreeNode` → `CodeTreeLeaf` / `CodeTreeFolder` | `MeshWeaver.Graph` | abstract class + `(CodeTreeFolder)node` cast | the cast, which threw for any third kind |
| `RunEvent` (five cases) | `MeshWeaver.Testing.InMesh` | abstract record + switch statement | `default: return state with { Emit = false }` |

**Not converted, and why:** `NodeReadOutcome`, `PermissionCheckOutcome`, `NodeCopyOutcome` and
`DependencyRecordOutcome` are public. The first two are switched on by in-mesh code, so they may only
gain a closed hierarchy additively, keeping every existing member and factory. That is a separate,
paired change.

## The other C# 14 and 15 idioms, and where they apply

| Feature | Source | Use in core |
|---|---|---|
| `field` keyword | [C# 14](https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-14) | A validating `init` accessor over a private backing field becomes `get; init => field = …;` with the default as a property initializer (`MeshOperationOptions`, `MeshConfiguration.ClientHostedAddressTypes`, `WebhookTarget.AcceptedEvents`). The public signature is unchanged. Beware a member already named `field` (write `@field` or `this.field`) |
| Null-conditional assignment | [C# 14](https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-14) | `x?.P = v;` replaces `if (x is not null) x.P = v;`. The right side runs only when `x` is non-null. `++`/`--` are not allowed |
| Extension members | [C# 14](https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-14) | Extension properties and static extension members. Most of core's extension clusters are public and called from in-mesh C#, so a conversion is a surface change to sweep first, not a mechanical edit |
| Collection expression arguments `[with(…), …]` | [C# 15](https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-15) | Capacity or comparer in a collection expression. Core builds most collections immutably through `ImmutableX.Create(comparer, …)`, which already says this |
| Labeled `break`/`continue` | [C# 15](https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-15) | Replaces a flag or a `goto` out of nested loops (style rule IDE0410). No `goto` remains in `src/` |
| Extension indexers | [C# 15](https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-15) | No current need |

## Verifying a conversion

For each converted type, delete one arm locally, build with `-c Release -warnaserror`, and expect
`CS8509` naming the missing case. Restore the arm. A conversion whose deleted arm still builds was
written as a switch statement, and is not checked.

See also: [Open Vocabularies Are String Constants](../OpenVocabulariesAsStringConstants),
[Write Verdict Totality](../WriteVerdictTotality) (why `DetachedReplyOutcome` exists),
[NodeType Compilation](../NodeTypeCompilation).
