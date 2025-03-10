# MeshWeaver.BusinessRules

MeshWeaver.BusinessRules provides a framework for defining and managing complex business rules by separating state management from rule definitions using interfaces and source generation.

## Overview

The library provides:
- Interface-based rule definitions
- Scope management for rule isolation
- Source generation for rule implementations
- State management abstractions

## Key Concepts

### IScope Interface
The foundation for defining business rule scopes:

```csharp
// Base scope interface
public interface IScope
{
    TScope GetScope<TScope>(object identity) where TScope : IScope;
}

// Generic scope interface with identity and state
public interface IScope<out TIdentity, out TState> : IScope
{
    TIdentity Identity { get; }
    TState GetStorage();
}

```

## Setup

### Project Configuration
To use business rules in your project, add the source generator:

```xml
<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
        <!-- Enable source generation -->
        <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
        <CompilerGeneratedFilesOutputPath>obj\Generated</CompilerGeneratedFilesOutputPath>
    </PropertyGroup>

    <ItemGroup>
        <!-- Add the source generator -->
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

## Usage Examples

### Defining Business Rules
```csharp
// Define a rule scope with GUID identity
public interface IRandomScope : IScope<Guid, object>
{
    double RandomNumber { get; }
}

// The generator creates implementation classes
public class RandomScopeProxy : ScopeBase<IRandomScope, Guid, object>, IRandomScope
{
    private static readonly Random Random = new();
    private readonly Lazy<double> _randomNumber;

    public RandomScopeProxy(Guid identity, ScopeRegistry<object> state) 
        : base(identity, state)
    {
        _randomNumber = new(() => Random.NextDouble());
    }

    public double RandomNumber => _randomNumber.Value;
}
```

### Using Scopes
```csharp
// Create a scope registry
var registry = serviceProvider.CreateScopeRegistry<object>(null);

// Get a scope instance
var randomScope = registry.GetScope<IRandomScope>(Guid.NewGuid());

// Access scope properties (values are cached)
var number = randomScope.RandomNumber;
var sameNumber = randomScope.RandomNumber; // Returns same value

// Get related scope
var otherScope = randomScope.GetScope<IRandomScope>(Guid.NewGuid());
```


## Features

1. **Scope Management**
   - Generic scope types with identity and state
   - Scope registry for instance management
   - Cached property evaluation
   - Cross-scope references

2. **Rule Generation**
   - Compile-time code generation
   - Type-safe rule implementations
   - Performance optimization
   - Dependency injection support

3. **State Management**
   - Typed state containers
   - State isolation per scope
   - State sharing between scopes
   - Immutable state objects

4. **Rule Composition**
   - Rule chaining
   - Rule aggregation
   - Cross-scope rules
   - Rule prioritization

## Integration

### With Dependency Injection
```csharp
services.AddMessageHub(hub => hub
    .ConfigureServices(services => services
        .AddBusinessRules(typeof(Program).Assembly)
    )
);

// Create scope registry
var registry = serviceProvider.CreateScopeRegistry(initialState);
```

## Source Generation

The BusinessRules.Generator creates:
- Scope implementation classes
- Property caching logic
- State management
- Dependency injection support


## Related Projects

- MeshWeaver.BusinessRules.Generator - Source generator
- MeshWeaver.Data - Data management
- MeshWeaver.Messaging.Hub - Message routing
