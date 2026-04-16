# MeshWeaver.MemexTemplate.Pack

## Overview
MeshWeaver.MemexTemplate.Pack produces a `dotnet new` template NuGet package for scaffolding a Memex Portal solution. It auto-generates the template content from the live `memex/` source tree so the template always stays in sync with the current codebase.

## How It Works
Before every `Build` or `Pack`, an MSBuild target runs `generate-memex-template.cs` which copies and tokenizes the Memex portal projects (Monolith, Shared, Aspire microservices) into `dist/templates/`. The `.csproj` then packs that directory as NuGet template content.

## Usage
```bash
# Pack the template
dotnet pack tools/MeshWeaver.MemexTemplate.Pack

# Install and use
dotnet new install MeshWeaver.MemexTemplate
dotnet new memex -n MyPortal
```

## See Also
Refer to the [main MeshWeaver documentation](../../Readme.md) for more information about the Memex Portal and deployment options.
