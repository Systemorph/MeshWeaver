# MeshWeaver.Connection.SignalR

Client-side SignalR transport for MeshWeaver. Connects an out-of-process .NET message hub
(a desktop app, a service, a test host) to a running mesh over the portal's SignalR endpoint,
so it can exchange messages with in-mesh hubs as if it were local.

## Features

- `SignalRClientExtensions` — fluent configuration to attach a SignalR connection to a `MessageHubConfiguration`
- Automatic serialization of mesh messages over the SignalR wire
- Pairs with `MeshWeaver.Hosting.SignalR`, which hosts the server side of the same connection

## Links

- [MeshWeaver repository](https://github.com/Systemorph/MeshWeaver)
- [Documentation](https://memex.meshweaver.cloud/Doc)
