---
Name: NuGet Packages
Category: Documentation
Description: Reference NuGet packages directly from interactive markdown code cells using the #r "nuget:..." directive.
---

Interactive markdown in MeshWeaver is backed by a [.NET Interactive](https://github.com/dotnet/interactive) `CSharpKernel`. That gives every code cell the same package-management directive used by Polyglot Notebooks: `#r "nuget:PackageId, Version"`. MeshWeaver resolves the package in-process using the public `NuGet.Protocol` / `NuGet.Packaging` / `NuGet.Resolver` libraries (no .NET SDK, no MSBuild), adds its assemblies to the kernel's compilation references, and it becomes usable immediately — no restart.

## Basic usage

Put a `#r "nuget:..."` directive on the first line(s) of a code cell, then write code that depends on it:

```csharp --render HumanizeExample --show-code
#r "nuget:Humanizer, 2.14.1"
using Humanizer;
"hello_world_framework".Humanize()
```

When this article is rendered, MeshWeaver:

1. Submits the cell to the kernel as a single `SubmitCode` command.
2. .NET Interactive's package-management extension sees the `#r "nuget"` directive, calls `api.nuget.org`, restores the package and its transitive dependencies into the local NuGet cache.
3. Adds every resolved assembly to the kernel's metadata references via `CSharpKernel.AddAssemblyReferences(...)`.
4. Compiles and runs the remaining code against the augmented reference set.

The return value flows back as a `ReturnValueProduced` event and is rendered into the `--render` area.

## Pinning versions

Always pin a specific version. Leaving the version off means the kernel resolves "latest" at render time, which makes articles non-reproducible and risks pulling in a breaking change.

```csharp
#r "nuget:Humanizer, 2.14.1"   // good — reproducible
#r "nuget:Humanizer"             // avoid — latest-at-render
```

## Multiple packages

One directive per line. Order does not matter; all directives in the cell are resolved before any code runs.

```csharp --render MultiPackage --show-code
#r "nuget:Humanizer, 2.14.1"
#r "nuget:Markdig, 0.37.0"
using Humanizer;
using Markdig;
var heading = "hello_world".Humanize(LetterCasing.Title);
Markdown.ToHtml($"# {heading}\n\nGenerated.")
```

## Sharing packages across cells

Once a package has been resolved in any cell of an article, it stays available for every later cell in the same kernel session. You do not need to repeat the `#r` directive — only the `using` statements.

```csharp --render SharedFirst --show-code
#r "nuget:Humanizer, 2.14.1"
using Humanizer;
"first_cell".Humanize()
```

```csharp --render SharedSecond --show-code
// Humanizer is already loaded from the cell above.
using Humanizer;
DateTime.UtcNow.AddMinutes(-5).Humanize()
```

## What happens on failure

If the package id is unknown, the version does not exist, or the kernel cannot reach nuget.org, the cell produces a `CommandFailed` event with the restore error. The failure is rendered inline so the author sees what went wrong without having to tail server logs:

```csharp
#r "nuget:ThisPackageDoesNotExist, 1.0.0"
"unreachable"
```

## Also in node types

The same directive works at the top of any `Source/*.cs` file in a node type. See [NuGet Packages in Node Types](NodeTypeWithNuGet) for the end-to-end walkthrough.

## Deployment — no SDK required

The restore is a library operation on `NuGet.Protocol` + `NuGet.Packaging` + `NuGet.Resolver`. It does not shell out to `dotnet restore` and does not need MSBuild, so the ACA image is the plain `mcr.microsoft.com/dotnet/aspnet` runtime. Requirements are minimal:

- Outbound HTTPS to `api.nuget.org` (default ACA egress allows it).
- A writable cache directory. The Aspire AppHost sets `NUGET_PACKAGES=/tmp/nuget-cache` for the portal resource.

## Related

- [Interactive Markdown](InteractiveMarkdown) — how code cells and `--render` areas work
- [NuGet Packages in Node Types](NodeTypeWithNuGet) — same directive in `Source/*.cs`
- [Data Modeling](DataModeling) — referencing your own schema types from a code cell
