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
<svg viewBox="0 0 760 200" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;" font-family="sans-serif" font-size="13">
  <defs>
    <marker id="np-arrow" markerWidth="8" markerHeight="8" refX="6" refY="3" orient="auto">
      <path d="M0,0 L0,6 L8,3 z" fill="#90a4ae"/>
    </marker>
  </defs>
  <rect x="0" y="0" width="760" height="200" rx="12" fill="#1a2030" opacity="0.6"/>
  <rect x="20" y="60" width="130" height="80" rx="10" fill="#1e3a5f" stroke="#1e88e5" stroke-width="1.5"/>
  <rect x="20" y="60" width="130" height="34" rx="10" fill="#1e88e5"/>
  <rect x="20" y="80" width="130" height="14" fill="#1e88e5"/>
  <text x="85" y="82" text-anchor="middle" fill="#fff" font-weight="bold" font-size="13">Code Cell</text>
  <text x="85" y="108" text-anchor="middle" fill="#cfd8dc" font-size="11">#r "nuget:Pkg, 1.0"</text>
  <text x="85" y="124" text-anchor="middle" fill="#cfd8dc" font-size="11">+ C# source</text>
  <rect x="195" y="60" width="130" height="80" rx="10" fill="#1b3a2a" stroke="#43a047" stroke-width="1.5"/>
  <rect x="195" y="60" width="130" height="34" rx="10" fill="#43a047"/>
  <rect x="195" y="80" width="130" height="14" fill="#43a047"/>
  <text x="260" y="82" text-anchor="middle" fill="#fff" font-weight="bold" font-size="13">CSharpKernel</text>
  <text x="260" y="108" text-anchor="middle" fill="#cfd8dc" font-size="11">SubmitCode</text>
  <text x="260" y="124" text-anchor="middle" fill="#cfd8dc" font-size="11">command</text>
  <rect x="375" y="60" width="150" height="80" rx="10" fill="#2a1e3a" stroke="#8e24aa" stroke-width="1.5"/>
  <rect x="375" y="60" width="150" height="34" rx="10" fill="#8e24aa"/>
  <rect x="375" y="80" width="150" height="14" fill="#8e24aa"/>
  <text x="450" y="82" text-anchor="middle" fill="#fff" font-weight="bold" font-size="13">NuGet Restore</text>
  <text x="450" y="108" text-anchor="middle" fill="#cfd8dc" font-size="11">api.nuget.org</text>
  <text x="450" y="124" text-anchor="middle" fill="#cfd8dc" font-size="11">+ local cache</text>
  <rect x="565" y="60" width="175" height="80" rx="10" fill="#1e2a3a" stroke="#f57c00" stroke-width="1.5"/>
  <rect x="565" y="60" width="175" height="34" rx="10" fill="#f57c00"/>
  <rect x="565" y="80" width="175" height="14" fill="#f57c00"/>
  <text x="652" y="82" text-anchor="middle" fill="#fff" font-weight="bold" font-size="13">Result</text>
  <text x="652" y="108" text-anchor="middle" fill="#cfd8dc" font-size="11">AddAssemblyReferences</text>
  <text x="652" y="124" text-anchor="middle" fill="#cfd8dc" font-size="11">ReturnValueProduced</text>
  <line x1="150" y1="100" x2="193" y2="100" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#np-arrow)"/>
  <line x1="325" y1="100" x2="373" y2="100" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#np-arrow)"/>
  <line x1="525" y1="100" x2="563" y2="100" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#np-arrow)"/>
</svg>
*NuGet resolution pipeline: the `#r` directive triggers an in-process restore; resolved assemblies are added to the kernel's references before the remaining code compiles and runs.*

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
