using System.Runtime.CompilerServices;

// ─────────────────────────────────────────────────────────────────────────────────────────────
// THE BINARY CONTRACT THIS ASSEMBLY OWES EVERY MODULE COMPILED AGAINST AN EARLIER PLATFORM
// ─────────────────────────────────────────────────────────────────────────────────────────────
//
// A module is a plain assembly that binds platform types BY SIMPLE ASSEMBLY NAME, and the module
// lane's only gate is a SEMVER FLOOR — never MVID equality — precisely so "a landed module keeps
// loading across ordinary platform updates" (Doc/Architecture/Modules → the skip rules). That
// sentence is a promise about BINARY compatibility, and it is the promise #2370 broke.
//
// #2283 moved `MeshOperations`, `MeshExportManifest`, `MeshExportFileEntry` and `NodeReadOutcome`
// out of this assembly into MeshWeaver.Mesh.Operations. The change was verified by BUILDING the
// plugins repo's MeshWeaver.Mcp against the branch — which proves SOURCE compatibility and says
// nothing at all about the module that was already published. That module's IL holds
//
//     TypeRef  MeshWeaver.AI.MeshOperations   scope: AssemblyRef MeshWeaver.AI
//
// so when the platform rolled, every single MCP tool call died in the McpMeshPlugin constructor
// with `TypeLoadException: Could not load type 'MeshWeaver.AI.MeshOperations' from assembly
// 'MeshWeaver.AI, Version=3.0.0.0'` — the whole /mcp surface, for every external client.
//
// These forwarders are what makes the move survivable: the CLR resolves that TypeRef through this
// assembly's ExportedType table into MeshWeaver.Mesh.Operations, yielding the SAME type identity.
// A forwarder cannot rename, so the full type NAME (namespace included) is frozen by this file —
// which is why those types still declare `namespace MeshWeaver.AI` in an assembly that is not AI.
//
// 🚨 Deleting a line here re-breaks every module built before #2283. `scripts/check-type-forwards.py`
// refuses that, and MovedTypeBinaryContractTest proves the forwarders actually resolve at runtime.
// If this assembly ever leaves the platform repo (#2276), the forwarders leave WITH it.
// Fully qualified deliberately: `NodeReadOutcome` is ambiguous under this project's global
// usings (MeshWeaver.Mesh has a read-classifier type of the same simple name), and a
// forwarder that pointed at the wrong one would compile and forward the wrong identity.
[assembly: TypeForwardedTo(typeof(MeshWeaver.AI.MeshOperations))]
[assembly: TypeForwardedTo(typeof(MeshWeaver.AI.MeshExportManifest))]
[assembly: TypeForwardedTo(typeof(MeshWeaver.AI.MeshExportFileEntry))]
[assembly: TypeForwardedTo(typeof(MeshWeaver.AI.NodeReadOutcome))]

// ─────────────────────────────────────────────────────────────────────────────────────────────
// The SAME class again, found by replaying the gate across v3.0.0-rc7 → main (#2398).
// #2276 moved the platform's credential-protection and MCP-back-connection contracts out of this
// assembly into MeshWeaver.Mesh.Contract, renaming their namespaces on the way. Three of them have
// a PROVEN module consumer on Systemorph/MeshWeaver.Plugins@main:
//
//   IMcpBackConnection     MeshWeaver.AI.ClaudeCode/{ClaudeCodeChatClient,ClaudeCodeHarness}.cs
//                          MeshWeaver.AI.Copilot/{CopilotChatClient,CopilotHarness}.cs
//   McpConnectionInfo      MeshWeaver.AI.ClaudeCode/ClaudeCodeChatClient.cs
//   IProviderKeyProtector  MeshWeaver.Blazor.Portal/Chat/ThreadChatView.razor.cs
//
// Their SOURCE was updated in the same wave, so `landed-modules-gate` is green and a REBUILT
// bundle is correct. What that says nothing about is a deployment still running a bundle
// published BEFORE the wave: it holds the old TypeRef and dies exactly like #2370 the moment its
// platform rolls. These forwarders are what makes that ordering irrelevant.
[assembly: TypeForwardedTo(typeof(MeshWeaver.AI.IMasterKeyProvider))]
[assembly: TypeForwardedTo(typeof(MeshWeaver.AI.ConfigMasterKeyProvider))]
[assembly: TypeForwardedTo(typeof(MeshWeaver.AI.IProviderKeyProtector))]
[assembly: TypeForwardedTo(typeof(MeshWeaver.AI.ProviderKeyProtector))]
[assembly: TypeForwardedTo(typeof(MeshWeaver.AI.Connect.IMcpBackConnection))]
[assembly: TypeForwardedTo(typeof(MeshWeaver.AI.Connect.McpConnectionInfo))]
