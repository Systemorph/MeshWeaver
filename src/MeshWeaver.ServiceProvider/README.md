# MeshWeaver.ServiceProvider

## Overview
MeshWeaver's dependency injection system built on Autofac, providing hierarchical service resolution across the application.

## Architecture
The service provider implements a layered dependency injection approach where each [MessageHub](../MeshWeaver.MessageHub/README.md) instantiates its own DI container. This modular design allows MessageHubs to inject additional services independently, creating isolated service scopes while maintaining access to application-wide services.

## See Also
Refer to the [main MeshWeaver documentation](../../Readme.md) for more information about the overall project.
