# MeshWeaver.Reflection

## Overview
MeshWeaver.Reflection provides a collection of extension methods and utilities to simplify common reflection tasks in C#. This library extends the standard reflection capabilities with convenient helper methods and performance optimizations.

## Features

### Type Extensions
- Anonymous type detection
- Nullable type handling
- Type inheritance and interface inspection
- Attribute handling
- Property and method reflection helpers

### Member Info Extensions
- Attribute inspection and caching
- Property override detection
- Virtual property analysis
- Interface declaration inspection

### Reflection Helpers
- Property accessor detection
- Constant value extraction
- Generic type constraint validation
- Type signature analysis

### Performance Optimizations
- Cached attribute lookups
- Optimized type comparisons
- Efficient member access

## Usage Examples

```csharp
// Check if type is anonymous
bool isAnonymous = type.IsAnonymous();

// Get all string constants from a type
var constants = type.GetStringConstants();

// Check if property overrides base class
bool isOverride = propertyInfo.IsOverride();

// Get custom attributes with inheritance
var attributes = memberInfo.GetCustomAttributesInherited<T>();
```

## Integration
The library is used throughout the MeshWeaver ecosystem to provide efficient reflection capabilities where needed.

## See Also
Refer to the [main MeshWeaver documentation](../../Readme.md) for more information about the overall project architecture.
