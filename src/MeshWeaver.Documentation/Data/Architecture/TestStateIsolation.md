---
Name: Test State Isolation in MeshWeaver
Category: Architecture
Description: How to keep tests independent in a shared-cluster fixture — pre-seed via IStaticNodeProvider, per-test hub disposal, and path uniquification. Required for Orleans tests; recommended for any shared-mesh fixture.
Icon: Beaker
---

# Test State Isolation: Static Seed + Hub Disposal

When tests share a mesh fixture — the canonical case is an Orleans `TestCluster` with one silo per xUnit collection — seed data and runtime-created nodes accumulate across tests without deliberate cleanup. Test A creates `User/Roland/_Thread/x`, the grain caches its config, Test B activates a different node at the same path, reads stale state from Test A, and fails for reasons entirely unrelated to its own logic.

The fix has two halves, and **both are required**:

1. Pre-seeded test state lives in `IStaticNodeProvider`, not `MeshConfiguration.Nodes`.
2. Hubs created during a test are disposed at test teardown.

Skip either half and the failures move around but never go away.

---

## Why `AddMeshNodes` Is Wrong for Shared Fixtures

`MeshBuilder.AddMeshNodes(...)` adds entries to `MeshConfiguration.Nodes` — a hub-startup snapshot that grain activation falls back to when persistence misses. In a single-test setup that's fine. In a shared cluster it has two problems:

- `MeshConfiguration.Nodes` is a `Dictionary` keyed by path, loaded once at fixture startup. If a test mutates a node at that path (via `CreateNodeRequest` → persistence, then `UpdateNodeRequest`), the next test sees the **mutated** version on grain activation — not the original seed.
- The fallback is synchronous. A grain that activated against `MeshConfiguration.Nodes` keeps that node in memory until deactivation, even if persistence later disagrees.

`IStaticNodeProvider`, by contrast, is queried on **every** grain activation (`MessageHubGrain.OnActivateAsync` lines 43–45) and serves immutable, read-only definitions. Tests cannot pollute it because writes never flow there — `CreateNodeRequest` goes to persistence, which is per-cluster and cleaned up between tests.

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

> **Rule:** Static providers must satisfy the `HandleCreateNodeRequest` bare-node rule — every entry must have either `NodeType` or `Content` set (see `MeshExtensions.cs HandleCreateNodeRequest`). For NodeType definitions, also set `AssemblyLocation` so `NodeTypeService.EnrichWithNodeTypeAsync` short-circuits the dynamic-compilation lookup that would otherwise log `"NodeType definition not found at path 'X'"`.

---

## Why Per-Test Disposal Is Required

Even with a clean static seed, tests still create runtime nodes (the `CreateThread_*` family, `CreateNode_*`, and so on). Each runtime node spawns a per-node hub via routing. That hub holds an `ActionBlock` queue, an `IWorkspace`, a `MeshDataSource` subscription, and — in Orleans — a `MessageHubGrain` activation. Without explicit disposal, these accumulate for the lifetime of the shared cluster fixture, which is typically the entire test collection.

**Symptoms of missing disposal:**

- Test A passes in isolation but fails when run after Test B (state pollution).
- Grain activation succeeds but reads wrong content — a cached `InstanceCollection` from a prior test's writes.
- `"No route found"` warnings in the second test for nodes that were never supposed to exist (the first test's per-node hub is still registered with the routing service).

Disposing at test end tears down the per-node hub cleanly: the `ActionBlock` completes, the subscription unwires, and in Orleans `Hub.RegisterForDisposal(_ => DeactivateOnIdle())` triggers grain deactivation (`MessageHubGrain.cs:105`). The next test sees a fresh routing table at the same path.

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

> **Tip:** When a test creates a whole subtree under a unique partition, issue a `DeleteNodeRequest(rootPath, Recursive = true)` instead of disposing each path individually — that drops persistence and the hub in one round trip.

---

## When This Pattern Is Required vs. Optional

| Fixture shape | Required? |
|---|---|
| Orleans `[Collection(...)]` with `SharedOrleansFixture` | **Required** — the silo lives across all tests in the collection |
| Monolith `MonolithMeshTestBase` per test (default) | Optional — the base class disposes the mesh at teardown anyway |
| Any `ICollectionFixture<>` that builds the mesh once | **Required** — same reason as Orleans |
| Per-test `[Fact]` that constructs its own builder | Not needed — the mesh is born and dies inside the test |

If you are not sure which category your test falls into, check the class declaration. A `[Collection(name)]` attribute over a fixture that implements `IAsyncLifetime` and builds the mesh once means a shared cluster — use this pattern.

---

## Path Uniquification: Useful Defence, Not a Replacement

Adding a `Guid` suffix to every test's node IDs (`$"thread-{Guid.NewGuid():N}"`) makes paths unique but doesn't solve the underlying problem in three cases:

- The grain at the unique path activates and pulls config from `MeshConfiguration.Nodes`, which still resolves by NodeType, not path.
- The shared persistence accumulates orphaned nodes that slow subsequent tests.
- Two tests racing to create different paths under the same partition can step on each other's partition-store initialisation.

Path uniquification is a useful complement to the static-seed + dispose pattern, not a substitute for it. Use both.

---

## NodeType Definitions in Tests

Tests that register a custom NodeType hit the same trap as runtime nodes. Placing a bare entry in `MeshConfiguration.Nodes` — `builder.AddMeshNodes(new MeshNode("readable") { Name = "Readable" })` — provides no `HubConfiguration` and no `AssemblyLocation`. When the per-node hub for a node of that type activates, `NodeTypeService.EnrichWithNodeTypeAsync` falls through the cached-config path, attempts a dynamic compile, fails (no `Source/` subtree exists), logs `NodeType definition not found at path 'readable'`, and the hub spins up with a default config that lacks `AddMeshDataSource` — so `GetDataRequest` returns `"No handler found"`.

The fix is the same: register the NodeType definition through `IStaticNodeProvider`, and on the static node set both `HubConfiguration` (so the per-node hub gets the right wiring) and `AssemblyLocation` (so the type lookup short-circuits without compilation).

```csharp
new MeshNode("readable") {
    Name = "Readable",
    AssemblyLocation = typeof(MyTestSeedProvider).Assembly.Location,
    HubConfiguration = c => c.AddMeshDataSource(s => s.WithContentType<ReadableContent>())
}
```

---

## Verification Checklist

After applying both halves, confirm isolation is solid:

- **Run the full test class twice in a row.** The second run should show the same green count as the first.
- **Check for `[Warning] NodeType definition not found at path '...'`** — should be empty.
- **Check for `No route found for ... → <path>` warnings** on paths a previous test created — should be empty.

If any of these appear, you either missed a runtime-created path in the dispose loop or a static seed entry in the provider.

---

## Cross-References

- [Writing Tests](WritingTests) — the broader test-authoring guide; this page covers the shared-fixture special case.
- [Debugging Message Flow](DebuggingMessageFlow) — what to grep when a test fails because a previous test polluted state.
- [Asynchronous Calls](AsynchronousCalls) — disposal must respect the actor model: never `await` a dispose chain inside a hub handler.
- [Satellite Node Patterns](SatelliteNodePatterns) — `IStaticNodeProvider` is also the right place for satellite NodeType configs where no runtime mutation is expected.
