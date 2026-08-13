# MeshWeaver.Hosting.Grpc

gRPC transport for the MeshWeaver mesh. Hosts a gRPC service (`mesh.proto`) that lets
external clients — non-.NET clients, browser apps via gRPC-web, or other services — exchange
mesh messages and subscribe to streams.

## Features

- `MeshGrpcService` — the mesh message/stream surface as a gRPC service
- `GrpcConnectionRegistry` — tracks connected clients and routes replies
- `GrpcHostingExtensions` / `GrpcOptions` — hosting registration and configuration

## Links

- [MeshWeaver repository](https://github.com/Systemorph/MeshWeaver)
- [Documentation](https://memex.meshweaver.cloud/Doc)
