# Testing — the platform's xunit estate, in-mesh

Every `Testing/<Suite>` NodeType is a former `test/<Project>.Test` xunit project whose accepted files run as
`[MeshFact]` cases in the samples gate's mesh (`MeshWeaver.Testing.InMesh`, #4184). The suites are GENERATED —
`python3 .github/scripts/generate-in-mesh-suites.py` runs every file of the project through
`.github/scripts/convert-xunit-to-inmesh.py` and lays `InMeshTestBase.cs` and `XunitShims.cs` in from
`.github/scripts/in-mesh/`; edit the generator, the converter or the templates and regenerate, never a suite by hand.
A refused file stays on xunit and is named, with its reason, in the suite's description: that list is the
inventory of the facilities still missing (maintainer, 2026-09-13: "pls refactor 100% to this shape").

## The suites (2026-09-13)

| Suite | From | Converted | Refused | Cases |
|---|---|---|---|---|
| `Testing/CompilerPipeline` | `test/MeshWeaver.Compiler.Pipeline.Test` | 56 | 35 | 500 |
| `Testing/ContentCollections` | `test/MeshWeaver.ContentCollections.Test` | 1 | 11 | 1 |
| `Testing/Data` | `test/MeshWeaver.Data.Test` | 20 | 59 | 116 |
| `Testing/DeploymentContract` | `test/MeshWeaver.Deployment.Contract.Test` | 1 | 3 | 3 |
| `Testing/Graph` | `test/MeshWeaver.Graph.Test` | 100 | 109 | 843 |
| `Testing/Hosting` | `test/MeshWeaver.Hosting.Test` | 49 | 54 | 306 |
| `Testing/Layout` | `test/MeshWeaver.Layout.Test` | 18 | 42 | 178 |
| `Testing/MessagingHub` | `test/MeshWeaver.Messaging.Hub.Test` | 31 | 49 | 126 |
| **total** | | **276** | **362** | **2073** |

## Runtime, 2026-09-13 (local gate, `--seed` over the bake)

| Suite | compile | render | tests |
|---|---|---|---|
| `Testing/ContentCollections` | ok | ok | **ok** (1/1) |
| `Testing/CompilerPipeline`, `Graph`, `Hosting`, `Layout`, `MessagingHub`, `Data` | ok | ok | no verdict within the gate's 120 s per-area deadline — the runner needs a per-suite budget or sharding (`.github/samples-gate.allow`, one-way) |
| `Testing/DeploymentContract` | ok | ok | 1 case reads a file by repository path — needs an in-mesh fixture |

## Projects that stay on xunit whole

A suite compiles against `FrameworkBuildIdentity.ContentSurfaceAssemblies` — what every portal ships. A test project
whose subject is not on that surface cannot run in-mesh at all:

| Project | Subject | Why |
|---|---|---|
| `test/MeshWeaver.Cli.Test` | `MeshWeaver.Cli` | not a content-surface assembly
| `test/MeshWeaver.ContainerImages.Test` | `MeshWeaver.ContainerImages` | not a content-surface assembly
| `test/MeshWeaver.Documentation.Test` | `MeshWeaver.Documentation` | not a content-surface assembly
| `test/MeshWeaver.Hosting.Orleans.Test` | `MeshWeaver.Hosting.Orleans` | not a content-surface assembly
| `test/MeshWeaver.PluginTester.Test` | `MeshWeaver.PluginTester` | not a content-surface assembly
| `test/MeshWeaver.Portal.E2E.Test` | the portal in a browser | every file drives Playwright |
| `test/Memex.Portal.Shared.Test` | `Memex.Portal.Shared` | a host project, not a platform assembly |
| `test/MeshWeaver.Testing.Xunit.Test` | the xunit adapter | tests xunit itself |

## What a refusal means, by facility

- **pre-boot service substitution** — `Configure(Host|Client|Mesh)` overrides, `GetHost(config)`, `Services.Add…`: the
  xunit fixture builds its own host; in-mesh the host is the live mesh. The largest group.
- **lifecycle** — `IAsyncLifetime`, `InitializeAsync`/`DisposeAsync`: the runner has no per-class hook yet.
- **guards** — `.ToTask(` (ObservableToTaskBridgeGuard) and an awaited mesh read (HubReachableAsyncGuard): `samples/` is a
  gated root, so the converter refuses rather than ship a site the guards would red.
- **hosts** — Playwright, the Orleans silo, ASP.NET (`WebApplication`, `DefaultHttpContext`), Postgres, a second process.
- **quoted source** — a file whose string constants contain `using` lines: the in-mesh compile hoists every using-shaped
  line (`CombineSources`), which guts the string.
- **typed Observe** — `hub.Observe(request)` is untyped in-mesh; a test reading a typed `Message` off it needs a hand port
  (`HAND_PORT` in the generator).
