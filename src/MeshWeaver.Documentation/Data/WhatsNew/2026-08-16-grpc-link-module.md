---
Name: The mesh gRPC link rides the module lane
Category: Feature
Description: The gRPC mesh transport — Python/Node foreign participants AND the React GUI's browser connection — is now the MeshWeaver.Hosting.Grpc module, switched by one Modules:Assemblies line. Default-on everywhere.
Icon: Sparkle
Order: -20260816
---

# The mesh gRPC link rides the module lane

The gRPC mesh transport — the endpoint Python and Node workers connect through, and the same one
the React frontend uses for its live browser connection — now ships as the
`MeshWeaver.Hosting.Grpc` module instead of being hard-wired into the portal. It stays **on by
default in every deployment**, because the React GUI depends on it; a deployment with no React
frontend and no foreign-language workers can now switch the whole surface off with one
`Modules:Assemblies` line. Nothing changes for users: connections, tokens, and the trusted
co-deployed gates work exactly as before.
