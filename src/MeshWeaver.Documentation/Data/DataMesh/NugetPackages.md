---
Name: NuGet Packages
Category: Documentation
Description: Reference NuGet packages directly from interactive markdown code cells using the #r "nuget:..." directive.
---

Interactive markdown in MeshWeaver is backed by a [.NET Interactive](https://github.com/dotnet/interactive) `CSharpKernel`. That gives every code cell the same package-management directive used by Polyglot Notebooks: `#r "nuget:PackageId, Version"`. The package is resolved by the NuGet client libraries *in-process* — no `dotnet` CLI, no .NET SDK on the container, no restart. The resolved assemblies are added to the kernel's compilation references and become usable immediately.

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

## How this fits with dynamic compilation

Interactive markdown and dynamic node compilation (`MeshNodeCompilationService`, `ScriptCompilationService`) are different paths. `#r "nuget"` support described here covers interactive markdown only. The dynamic node compiler builds MeshNode assemblies from runtime assemblies and does not currently accept `nuget:` references — it takes its reference set from `TRUSTED_PLATFORM_ASSEMBLIES` plus an explicit list.

## No .NET SDK on the container

The runtime image (`mcr.microsoft.com/dotnet/aspnet`) is enough. NuGet restore in .NET Interactive is a library operation — `NuGet.Protocol` and `NuGet.Packaging` talk HTTPS to `api.nuget.org` and unpack `.nupkg` files into the local cache. The `dotnet restore` CLI (which needs the SDK) is never invoked. When deploying to Azure Container Apps, make sure:

- Outbound HTTPS to `api.nuget.org` is permitted (ACA's default egress policy allows it).
- A writable cache directory is available. The default (`$HOME/.nuget/packages` or `%USERPROFILE%\.nuget\packages`) works on ACA; set `NUGET_PACKAGES` if you need a specific path.

## Related

- [Interactive Markdown](InteractiveMarkdown) — how code cells and `--render` areas work
- [Data Modeling](DataModeling) — referencing your own schema types from a code cell
