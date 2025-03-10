# MeshWeaver.Hosting

## Overview
MeshWeaver.Hosting provides the foundational infrastructure for hosting MeshWeaver instances. It includes base classes and configuration capabilities that enable different hosting scenarios.

## Core Components

### MeshConfiguration
The central configuration class for setting up a MeshWeaver instance. Used by specific hosting implementations to configure their services.

## Hosting Options

### Monolithic Hosting
For single-process deployments, use [MeshWeaver.Hosting.Monolith](../MeshWeaver.Hosting.Monolith/README.md).

### Orleans Hosting
For distributed deployments, use [MeshWeaver.Hosting.Orleans](../MeshWeaver.Hosting.Orleans/README.md).

## Integration
- Base classes and interfaces for hosting implementations
- Works with MeshWeaver client libraries
- Supports different deployment scenarios through specific hosting packages

## See Also
Refer to the [main MeshWeaver documentation](../../Readme.md) for more information about hosting options and configuration.
