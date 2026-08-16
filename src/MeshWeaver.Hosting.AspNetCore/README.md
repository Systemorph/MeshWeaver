# MeshWeaver.Hosting.AspNetCore

ASP.NET Core host integration for MeshWeaver modules — the endpoint-contribution hook.

A module assembly declares HTTP endpoints via `MeshEndpointProviderAttribute`; the host maps every
installed module's contributions with `app.MapMeshModuleEndpoints()`. Contributed routes are
authenticated by default (a route is anonymous only where the module explicitly opts out), each
endpoint is stamped with `MeshModuleEndpointMetadata`, and at `ApplicationStarted` the host refuses
to serve if a module-contributed endpoint collides with another registration on the same
(verb, pattern) — a silently shadowed route is indistinguishable from a passing one.

Part of the [MeshWeaver](https://github.com/Systemorph/MeshWeaver) platform. See
`Doc/Architecture/Modules` in a running portal for the module lane end to end.
