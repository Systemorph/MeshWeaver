# MeshWeaver.BusinessRules.Generator

MeshWeaver.BusinessRules.Generator is the source generator component for the MeshWeaver.BusinessRules framework. It generates the implementation code for business rule scopes at compile time.

## Overview

The generator provides:
- Compile-time implementation of `IScope<TIdentity, TState>` interfaces
- Property caching and evaluation logic
- Scope proxy generation
- Dependency injection support

## Setup

### Project Configuration

```xml
<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
        <!-- Enable source generation -->
        <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
        <CompilerGeneratedFilesOutputPath>obj\Generated</CompilerGeneratedFilesOutputPath>
    </PropertyGroup>

    <ItemGroup>
        <!-- Add the generator as an analyzer -->
        <ProjectReference 
            Include="MeshWeaver.BusinessRules.Generator" 
            OutputItemType="Analyzer" 
            ReferenceOutputAssembly="false" />
        
        <!-- Add the runtime library -->
        <ProjectReference 
            Include="MeshWeaver.BusinessRules" />
    </ItemGroup>
</Project>
```


## Related Projects

- [MeshWeaver.BusinessRules](../MeshWeaver.BusinessRules/README.md) - Core business rules framework
- MeshWeaver.Data - Data management
- MeshWeaver.Messaging.Hub - Message routing
