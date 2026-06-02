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

<svg viewBox="0 0 760 370" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;" font-family="sans-serif" font-size="13">
  <defs>
    <marker id="arr" markerWidth="8" markerHeight="8" refX="7" refY="3.5" orient="auto">
      <path d="M0,0 L0,7 L8,3.5 Z" fill="currentColor" fill-opacity=".6"/>
    </marker>
    <marker id="arr-green" markerWidth="8" markerHeight="8" refX="7" refY="3.5" orient="auto">
      <path d="M0,0 L0,7 L8,3.5 Z" fill="#43a047"/>
    </marker>
    <marker id="arr-red" markerWidth="8" markerHeight="8" refX="7" refY="3.5" orient="auto">
      <path d="M0,0 L0,7 L8,3.5 Z" fill="#e53935"/>
    </marker>
  </defs>
  <text x="380" y="22" text-anchor="middle" font-size="15" font-weight="bold" fill="currentColor">Shared-Fixture Test Isolation — Two Required Halves</text>
  <rect x="20" y="38" width="720" height="62" rx="10" fill="#1e3a5f" stroke="#1e88e5" stroke-width="1.5"/>
  <text x="380" y="57" text-anchor="middle" font-size="12" fill="#90caf9">Shared Cluster Fixture  (Orleans TestCluster / ICollectionFixture — one silo, lifetime = test collection)</text>
  <rect x="60" y="68" width="180" height="24" rx="6" fill="#1e88e5"/>
  <text x="150" y="85" text-anchor="middle" fill="#fff" font-size="12">IStaticNodeProvider</text>
  <rect x="300" y="68" width="160" height="24" rx="6" fill="#5c6bc0"/>
  <text x="380" y="85" text-anchor="middle" fill="#fff" font-size="12">Persistence (per-cluster)</text>
  <rect x="520" y="68" width="180" height="24" rx="6" fill="#26a69a"/>
  <text x="610" y="85" text-anchor="middle" fill="#fff" font-size="12">Routing Service</text>
  <text x="190" y="130" text-anchor="middle" font-size="12" font-weight="bold" fill="#e53935">✗  Without fix</text>
  <rect x="40" y="140" width="300" height="120" rx="8" fill="none" stroke="#e53935" stroke-width="1.2" stroke-dasharray="5,3"/>
  <rect x="60" y="155" width="120" height="32" rx="8" fill="#b71c1c"/>
  <text x="120" y="176" text-anchor="middle" fill="#fff" font-size="12">Test A runs</text>
  <rect x="200" y="155" width="120" height="32" rx="8" fill="#b71c1c"/>
  <text x="260" y="176" text-anchor="middle" fill="#fff" font-size="12">Test B runs</text>
  <line x1="180" y1="171" x2="198" y2="171" stroke="#e53935" stroke-width="1.5" marker-end="url(#arr-red)"/>
  <rect x="60" y="205" width="260" height="42" rx="6" fill="#4a1010"/>
  <text x="190" y="222" text-anchor="middle" fill="#ff8a80" font-size="11">Stale grain state / wrong content</text>
  <text x="190" y="238" text-anchor="middle" fill="#ff8a80" font-size="11">"No route found" / MeshConfiguration pollution</text>
  <text x="560" y="130" text-anchor="middle" font-size="12" font-weight="bold" fill="#43a047">✓  With both halves</text>
  <rect x="420" y="140" width="320" height="120" rx="8" fill="none" stroke="#43a047" stroke-width="1.2" stroke-dasharray="5,3"/>
  <rect x="435" y="155" width="130" height="32" rx="8" fill="#1b5e20"/>
  <text x="500" y="168" text-anchor="middle" fill="#fff" font-size="11">Test A runs</text>
  <text x="500" y="181" text-anchor="middle" fill="#a5d6a7" font-size="10">creates nodes → tracked</text>
  <rect x="435" y="205" width="130" height="30" rx="8" fill="#33691e"/>
  <text x="500" y="225" text-anchor="middle" fill="#fff" font-size="11">DisposeAsync()</text>
  <line x1="500" y1="187" x2="500" y2="203" stroke="#43a047" stroke-width="1.5" marker-end="url(#arr-green)"/>
  <line x1="565" y1="171" x2="583" y2="171" stroke="#43a047" stroke-width="1.5" marker-end="url(#arr-green)"/>
  <rect x="585" y="155" width="135" height="32" rx="8" fill="#1b5e20"/>
  <text x="653" y="168" text-anchor="middle" fill="#fff" font-size="11">Test B runs</text>
  <text x="653" y="181" text-anchor="middle" fill="#a5d6a7" font-size="10">fresh routing + seed</text>
  <rect x="585" y="205" width="135" height="30" rx="8" fill="#33691e"/>
  <text x="653" y="225" text-anchor="middle" fill="#fff" font-size="11">DisposeAsync()</text>
  <line x1="653" y1="187" x2="653" y2="203" stroke="#43a047" stroke-width="1.5" marker-end="url(#arr-green)"/>
  <line x1="20" y1="278" x2="740" y2="278" stroke="currentColor" stroke-opacity=".2"/>
  <rect x="40" y="292" width="200" height="58" rx="8" fill="#263238"/>
  <text x="140" y="310" text-anchor="middle" fill="#80cbc4" font-size="12" font-weight="bold">Half 1 — Static Seed</text>
  <text x="140" y="327" text-anchor="middle" fill="currentColor" fill-opacity=".7" font-size="11">IStaticNodeProvider</text>
  <text x="140" y="342" text-anchor="middle" fill="currentColor" fill-opacity=".7" font-size="11">read-only, queried each activation</text>
  <rect x="280" y="292" width="200" height="58" rx="8" fill="#263238"/>
  <text x="380" y="310" text-anchor="middle" fill="#80cbc4" font-size="12" font-weight="bold">Half 2 — Hub Disposal</text>
  <text x="380" y="327" text-anchor="middle" fill="currentColor" fill-opacity=".7" font-size="11">DisposeAsync() per test</text>
  <text x="380" y="342" text-anchor="middle" fill="currentColor" fill-opacity=".7" font-size="11">DeactivateOnIdle → clean routing</text>
  <rect x="520" y="292" width="200" height="58" rx="8" fill="#263238"/>
  <text x="620" y="310" text-anchor="middle" fill="#80cbc4" font-size="12" font-weight="bold">Complement</text>
  <text x="620" y="327" text-anchor="middle" fill="currentColor" fill-opacity=".7" font-size="11">Path uniquification (Guid suffix)</text>
  <text x="620" y="342" text-anchor="middle" fill="currentColor" fill-opacity=".7" font-size="11">useful, but not a substitute</text>
</svg>

*The shared cluster fixture lives across the whole test collection; both halves are required to keep each test seeing a clean slate.*

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
