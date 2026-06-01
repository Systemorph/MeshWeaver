---
Name: NuGet Packages
Category: Documentation
Description: Load any NuGet package directly from an interactive markdown code cell using the #r "nuget:..." directive — no SDK, no restart.
---

Interactive markdown in MeshWeaver is backed by a [.NET Interactive](https://github.com/dotnet/interactive) `CSharpKernel`. That gives every code cell the same package-management directive used by Polyglot Notebooks: `#r "nuget:PackageId, Version"`. MeshWeaver resolves the package in-process using `NuGet.Protocol`, `NuGet.Packaging`, and `NuGet.Resolver` — no .NET SDK, no MSBuild — adds its assemblies to the kernel's compilation references, and the package is usable immediately without restarting the portal.

## Basic usage

Place one or more `#r "nuget:..."` directives at the top of a code cell, then write code that depends on them:

```csharp --render HumanizeExample --show-code
#r "nuget:Humanizer, 2.14.1"
using Humanizer;
"hello_world_framework".Humanize()
```

When MeshWeaver renders a cell like this, it:

1. Submits the entire cell to the kernel as a single `SubmitCode` command.
2. .NET Interactive's package-management extension sees the `#r "nuget"` directive, calls `api.nuget.org`, and restores the package and its transitive dependencies into the local NuGet cache.
3. Adds every resolved assembly to the kernel's metadata references via `CSharpKernel.AddAssemblyReferences(...)`.
4. Compiles and runs the remaining code against the augmented reference set.

The return value flows back as a `ReturnValueProduced` event and is rendered into the `--render` area.

## Pinning versions

> **Always pin a specific version.** Omitting the version resolves "latest" at render time, making articles non-reproducible and risking silent breaking changes.

```csharp
#r "nuget:Humanizer, 2.14.1"   // good — reproducible
#r "nuget:Humanizer"             // avoid — resolves latest at render
```

## Multiple packages

List one directive per line. Order does not matter — all directives in the cell are resolved before any code runs:

```csharp --render MultiPackage --show-code
#r "nuget:Humanizer, 2.14.1"
#r "nuget:Markdig, 0.37.0"
using Humanizer;
using Markdig;
var heading = "hello_world".Humanize(LetterCasing.Title);
Markdown.ToHtml($"# {heading}\n\nGenerated.")
```

## Sharing packages across cells

Once a package is resolved in any cell of an article, it stays available for every later cell in the same kernel session. You only need the `using` statement — the `#r` directive is not required again:

```csharp --render SharedFirst --show-code
#r "nuget:Humanizer, 2.14.1"
using Humanizer;
"first_cell".Humanize()
```

```csharp --render SharedSecond --show-code
// Humanizer is already loaded from the cell above — no #r needed.
using Humanizer;
DateTime.UtcNow.AddMinutes(-5).Humanize()
```

## What happens on failure

If the package ID is unknown, the version does not exist, or the kernel cannot reach `nuget.org`, the cell produces a `CommandFailed` event. The error is rendered inline — the author sees exactly what went wrong without tailing server logs:

```csharp
#r "nuget:ThisPackageDoesNotExist, 1.0.0"
"unreachable"
```

## Also in node types

The same `#r "nuget:..."` directive works at the top of any `Source/*.cs` file in a node type. See [NuGet Packages in Node Types](NodeTypeWithNuGet) for the end-to-end walkthrough.

## Deployment — no SDK required

The restore is a pure library operation. It does not shell out to `dotnet restore` and does not require MSBuild, so the ACA image stays as the lean `mcr.microsoft.com/dotnet/aspnet` runtime. The two requirements are:

| Requirement | Detail |
|---|---|
| Outbound HTTPS | `api.nuget.org` — default ACA egress allows this |
| Writable cache directory | The Aspire AppHost sets `NUGET_PACKAGES=/tmp/nuget-cache` for the portal resource |

## Related

- [Interactive Markdown](InteractiveMarkdown) — how code cells and `--render` areas work
- [NuGet Packages in Node Types](NodeTypeWithNuGet) — same directive inside `Source/*.cs` files
- [Data Modeling](DataModeling) — referencing your own schema types from a code cell
