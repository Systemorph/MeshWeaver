---
Name: Test State Isolation in MeshWeaver
Category: Architecture
Description: How to keep tests independent in a shared-cluster fixture — pre-seed pattern via IStaticNodeProvider plus per-test hub disposal. Required for Orleans tests, recommended for any shared-mesh fixture.
Icon: Beaker
---

# Test State Isolation: Static Seed + Hub Disposal

When tests share a mesh fixture (the canonical case is Orleans `TestCluster` — one silo per `xUnit` collection, dozens of tests against it), naive `builder.AddMeshNodes(...)` seed data and runtime-created nodes accumulate across tests. Test A creates `User/Roland/_Thread/x` → grain caches its config → Test B activates a different node at the same path → reads stale state from Test A → fails for reasons unrelated to its own logic.

The fix has two halves and both are required:

1. **Pre-seeded test state lives in `IStaticNodeProvider`**, not `MeshConfiguration.Nodes`.
2. **Hubs created during a test are disposed at test teardown**.

Skip either half and the failures move around but don't go away.

## Why `AddMeshNodes` is wrong for shared fixtures

`MeshBuilder.AddMeshNodes(...)` adds entries to `MeshConfiguration.Nodes` — a hub-startup snapshot that grain activation falls back to when persistence misses. That works in single-test isolation but in a shared cluster it has two problems:

- `MeshConfiguration.Nodes` is a `Dictionary` keyed by path. The fixture loads it once. If a test mutates a node at the same path (via `CreateNodeRequest` → persistence, then `UpdateNodeRequest`), the next test sees the **mutated** version on grain activation, not the original seed.
- The fallback is sync — there's no notification when persistence "catches up". A grain that activated against `MeshConfiguration.Nodes` keeps that node in memory until deactivation, even if persistence later disagrees.

`IStaticNodeProvider`, by contrast, is queried on every grain activation (`MessageHubGrain.OnActivateAsync` line 43-45) and serves immutable read-only definitions. Tests can't pollute it because writes never go there — `CreateNodeRequest` flows to persistence, which is per-cluster and gets cleaned up between tests (next section).

```csharp
public sealed class MyTestSeedProvider : IStaticNodeProvider
{
    public IReadOnlyList<MeshNode> GetStaticNodes() => [
        new MeshNode("Roland", "User") { Name = "Roland", NodeType = "User",
            Content = new UserProfile { ... } },
        // NodeType definitions for the test (see "NodeType definitions" below)
        new MeshNode("readable") {
            Name = "Readable",
            AssemblyLocation = typeof(MyTestSeedProvider).Assembly.Location,
            HubConfiguration = c => c.AddMeshDataSource()
        },
    ];
}

// In the silo configurator
hostBuilder
    .UseOrleansMeshServer()
    .ConfigureServices(services =>
        services.AddSingleton<IStaticNodeProvider, MyTestSeedProvider>());
```

Static providers MUST satisfy the `HandleCreateNodeRequest` bare-node rule (every entry has either `NodeType` set or `Content` set — see `MeshExtensions.cs HandleCreateNodeRequest`). For NodeType definitions specifically, set `AssemblyLocation` so `NodeTypeService.EnrichWithNodeTypeAsync` short-circuits the dynamic-compilation lookup that would otherwise log `"NodeType definition not found at path 'X'"`.

## Why per-test disposal is required

Even with static seed, tests still create runtime nodes (the `CreateThread_*` family, the `CreateNode_*` family). Each runtime node spawns a per-node hub through routing; the hub holds an `ActionBlock` queue, an `IWorkspace`, a `MeshDataSource` subscription, and (in Orleans) a `MessageHubGrain` activation. Without explicit disposal these accumulate for the lifetime of the shared cluster fixture — typically the entire test class collection.

Symptoms:
- Test A passes in isolation but fails when run after Test B (state pollution).
- Grain activation succeeds but reads wrong content (cached InstanceCollection from a prior test's writes).
- "No route found" warnings in the second test for nodes that weren't supposed to exist (the first test's per-node hub is still registered with the routing service).

Disposing at test end cleanly tears down the per-node hub — the `ActionBlock` completes, the subscription unwires, and (in Orleans) `Hub.RegisterForDisposal(_ => DeactivateOnIdle())` triggers grain deactivation (`MessageHubGrain.cs:105`). The next test sees a fresh routing table at the same path.

```csharp
public class MyOrleansTest(SharedOrleansFixture fixture, ITestOutputHelper output)
    : OrleansTestBase(output), IAsyncLifetime
{
    private readonly List<string> _createdPaths = [];

    private async Task<string> CreateThreadAsync(string contextPath, string text)
    {
        var node = ThreadNodeType.BuildThreadNode(contextPath, text, "Roland");
        var path = await CreateNodeAsync(client, node, contextPath, ct);
        _createdPaths.Add(path);   // ← track for teardown
        return path;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var path in _createdPaths)
        {
            // Triggers DeactivateOnIdle on the grain; routing forgets the address.
            fixture.Mesh.GetHostedHub(new Address(path), HostedHubCreation.Never)?.Dispose();
        }
        _createdPaths.Clear();
    }
}
```

For deeper cleanup (the test created a whole subtree under a unique partition), issue a `DeleteNodeRequest(rootPath, Recursive = true)` instead of disposing each path — that drops persistence + hub in one round trip.

## When this pattern is required vs. optional

| Fixture shape | Required? |
|---|---|
| Orleans `[Collection(...)]` with `SharedOrleansFixture` | **Required** — silo lives across all tests in the collection |
| Monolith `MonolithMeshTestBase` per test (default) | Optional — base class disposes the mesh in teardown anyway |
| Any `ICollectionFixture<>` that builds the mesh once | **Required** — same reason as Orleans |
| Per-test `[Fact]` that constructs its own builder | Not needed — the mesh is born and dies inside the test |

If you're not sure, look at the test's class declaration: `[Collection(name)]` over a fixture that implements `IAsyncLifetime` and builds the mesh once = shared cluster = use this pattern.

## Path uniquification — the cheap-but-not-enough partial fix

Adding a `Guid` suffix to every test's node ids (`$"thread-{Guid.NewGuid():N}"`) makes the *paths* unique but doesn't help when:
- The grain at the unique path activates and pulls config from `MeshConfiguration.Nodes` (which still resolves by NodeType, not path).
- The shared persistence accumulates orphaned nodes that slow subsequent tests.
- Two tests racing to create different paths under the same partition step on each other's partition store init.

Path uniquification is a useful defence-in-depth on top of the static-seed + dispose pattern, not a replacement for it. Use both.

## NodeType definitions in tests

Tests that register a custom NodeType (validator tests, content-shape tests) hit the same trap as runtime nodes: `builder.AddMeshNodes(new MeshNode("readable") { Name = "Readable" })` puts the type definition into `MeshConfiguration.Nodes` with no `HubConfiguration` and no `AssemblyLocation`. When the per-node hub for a node of that type activates, `NodeTypeService.EnrichWithNodeTypeAsync` falls through the cached-config path, attempts a dynamic compile, fails (no Source/ subtree exists), logs `NodeType definition not found at path 'readable'`, and the hub spins up with the default config — which doesn't include `AddMeshDataSource`, so `GetDataRequest` returns "No handler found".

The fix is the same: register the NodeType definition through `IStaticNodeProvider`, and on the static node set both `HubConfiguration` (so the per-node hub gets the right wiring) and `AssemblyLocation` (so the type lookup short-circuits without compilation).

```csharp
new MeshNode("readable") {
    Name = "Readable",
    AssemblyLocation = typeof(MyTestSeedProvider).Assembly.Location,
    HubConfiguration = c => c.AddMeshDataSource(s => s.WithContentType<ReadableContent>())
}
```

## Verification

After applying both halves:
- Run the full test class twice in a row — second run should see the same green count as the first.
- Check the trace log for `[Warning] NodeType definition not found at path '...'` — should be empty.
- Check for `No route found for ... → <path>` warnings on paths a previous test created — should be empty.

If any of these appear, you missed a runtime-created path in the dispose loop or a static seed in the provider.

## Cross-references

- [Writing Tests](WritingTests) — the broader test-authoring guide; this doc is the shared-fixture special case.
- [Debugging Message Flow](DebuggingMessageFlow) — what to grep when a test fails because a previous test polluted state.
- [Asynchronous Calls](AsynchronousCalls) — disposal must respect the actor model: don't `await` the dispose chain inside a hub handler.
- [Satellite Node Patterns](SatelliteNodePatterns) — `IStaticNodeProvider` is also the right place for satellite NodeType configs (no runtime mutation expected).
