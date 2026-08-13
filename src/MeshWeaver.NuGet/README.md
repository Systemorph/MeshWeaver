# MeshWeaver.NuGet

In-process NuGet package resolution for MeshWeaver. Resolves `#r "nuget:..."` directives in
scripts, notebooks, and dynamically compiled node types against configured feeds — at
runtime, inside the portal process.

## Features

- `NuGetDirectiveParser` — extracts package references from source
- `INuGetAssemblyResolver` / `NuGetAssemblyResolver` — resolves packages to metadata references for Roslyn compilation
- `INuGetPackageCache` with `FileSystemNuGetPackageCache` — download-once package caching
- `ResolvedPackageSet` — the resolved closure handed to the compiler

## Links

- [MeshWeaver repository](https://github.com/Systemorph/MeshWeaver)
- [Documentation](https://memex.meshweaver.cloud/Doc)
