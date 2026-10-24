# MeshWeaver.ServiceProvider

## Overview
MeshWeaver's own dependency-injection container, `MeshServiceProvider`, providing hierarchical service resolution across the application: the mesh's root, one level per hub holding only that hub's own registrations, and module containers. See `Doc/Architecture/HubServiceLayers`.

## Architecture
The service provider implements a layered dependency injection approach where each [MessageHub](../MeshWeaver.MessageHub/README.md) instantiates its own DI container. This modular design allows MessageHubs to inject additional services independently, creating isolated service scopes while maintaining access to application-wide services.

## See Also
Refer to the [main MeshWeaver documentation](../../Readme.md) for more information about the overall project.
