# MeshWeaver.Hosting.SignalR

Server-side SignalR hosting for MeshWeaver. Exposes the SignalR hub endpoint through which
browser clients (including the Blazor portal's JS interop) and external .NET clients
(`MeshWeaver.Connection.SignalR`) attach to the mesh.

## Features

- `SignalRConnectionHub` — the hub endpoint carrying mesh messages
- `SignalRConnectionRegistry` — connection lifetime and routing
- `SignalRHostingExtensions` — one-call registration on the host

## Links

- [MeshWeaver repository](https://github.com/Systemorph/MeshWeaver)
- [Documentation](https://memex.meshweaver.cloud/Doc)
