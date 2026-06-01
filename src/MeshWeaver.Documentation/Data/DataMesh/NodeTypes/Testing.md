---
Name: Testing Node Types
Category: Documentation
Description: End-to-end test node types with MonolithMeshTestBase — render layout areas, exercise request/response, run simulations against a real client.
---

A node type isn't "done" until it has a test that drives it. MeshWeaver ships `MonolithMeshTestBase`, which spins up a full monolith mesh in-process so your integration tests can behave exactly like a Blazor client: initialize a hub, request a layout area, and assert on the streamed response — no mocking, no seams.

This page covers two archetypes you'll write for every node type:

| Archetype | What it proves |
|---|---|
| **Layout-area rendering** | The `Details`, `Thumbnail`, `Overview`, and other views render correctly for a real node instance. |
| **Request/response** | A node-type-specific request is handled and produces the expected response — useful for simulations, computations, and any handler wired in via `config.WithHandler<TRequest>(...)`. |

---

## Test project layout

A typical test project follows this structure:

```
test/MeshWeaver.MyType.Test/
  MeshWeaver.MyType.Test.csproj
  TestPaths.cs                  # shared paths to samples/Graph
  MyTypeViewsTest.cs            # layout-area tests
  MyTypeRequestResponseTest.cs  # request/response tests
```

The `.csproj` needs a handful of standard references. Use `test/MeshWeaver.Acme.Test/MeshWeaver.Acme.Test.csproj` as your starting point:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <!-- Pre-copy samples/Graph so tests see the same tree as the portal -->
    <Content Include="..\..\samples\Graph\**">
      <Link>SamplesGraph\%(RecursiveDir)%(Filename)%(Extension)</Link>
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </Content>
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\MeshWeaver.Fixture\MeshWeaver.Fixture.csproj" />
    <ProjectReference Include="..\..\src\MeshWeaver.Hosting.Monolith\MeshWeaver.Hosting.Monolith.csproj" />
    <ProjectReference Include="..\..\src\MeshWeaver.Hosting.Monolith.TestBase\MeshWeaver.Hosting.Monolith.TestBase.csproj" />
    <ProjectReference Include="..\..\src\MeshWeaver.Hosting\MeshWeaver.Hosting.csproj" />
    <ProjectReference Include="..\..\src\MeshWeaver.Graph\MeshWeaver.Graph.csproj" />
    <ProjectReference Include="..\..\src\MeshWeaver.Layout\MeshWeaver.Layout.csproj" />
    <ProjectReference Include="..\..\src\MeshWeaver.Markdown\MeshWeaver.Markdown.csproj" />
    <ProjectReference Include="..\..\src\MeshWeaver.Mesh.Contract\MeshWeaver.Mesh.Contract.csproj" />
    <ProjectReference Include="..\..\src\MeshWeaver.Messaging.Hub\MeshWeaver.Messaging.Hub.csproj" />
  </ItemGroup>
</Project>
```

---

## Archetype 1 — layout-area rendering

The canonical reference is `test/MeshWeaver.Acme.Test/TodoViewsTest.cs:32`. Inherit `MonolithMeshTestBase`, override `ConfigureMesh` to point at `samples/Graph/Data`, call `AddGraph()` together with your type, then subscribe to `GetRemoteStream` against a `LayoutAreaReference`.

```csharp
public class MatrixViewsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var graphPath = TestPaths.SamplesGraph;
        var dataDirectory = TestPaths.SamplesGraphData;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Graph:Storage:SourceType"] = "FileSystem",
                ["Graph:Storage:BasePath"] = graphPath
            })
            .Build();

        return builder
            .UseMonolithMesh()
            .AddPartitionedFileSystemPersistence(dataDirectory)
            .ConfigureServices(services => services.AddSingleton<IConfiguration>(configuration))
            .AddGraph()
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas());
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    [Fact(Timeout = 20_000)]
    public async Task Inverse_ShouldRender()
    {
        var client = GetClient();
        var address = new Address("MathDemo/Matrix/Example");

        // Initialize the hub first — required for routing to hit the per-node hub.
        await client.AwaitResponse(new PingRequest(),
            o => o.WithTarget(address), TestContext.Current.CancellationToken);

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference("Inverse");

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(address, reference);
        var value = await stream.Timeout(TimeSpan.FromSeconds(5)).FirstAsync();

        value.Should().NotBe(default(JsonElement),
            "Inverse view should render for a Matrix instance");
    }
}
```

### Four things to keep in mind

> **`PingRequest` first.** Layout-area routing is per-hub. The first request to a fresh hub triggers address registration — without the ping, `GetRemoteStream` can race hub creation and time out.

> **`GetRemoteStream<JsonElement, LayoutAreaReference>`** returns a cold observable. `.Timeout(...).FirstAsync()` is the idiomatic way to get the first rendered payload.

> **Shared cache directory.** If your tests compile node types from `Source/`, pass a shared `.mesh-cache/` across test methods (see `SharedCacheDirectory` in `TodoViewsTest`). Otherwise each test pays the full Roslyn compile cost independently.

> **Collection fixtures.** Tests that share `samples/Graph` should share a fixture. Parallelism inside `xunit.runner.json` is disabled at the repo level.

### Control-typed assertion

`GetRemoteStream` yields `JsonElement` by default. To assert on the concrete control type, chain `GetControlStream`:

```csharp
var control = await stream
    .GetControlStream(reference.Area!)
    .Timeout(TimeSpan.FromSeconds(10))
    .FirstAsync(x => x is not null);

var stack = control.Should().BeOfType<StackControl>().Subject;
stack.Areas.Should().NotBeEmpty();
```

See `TodoViewsTest.CreateArea_WithTypeParam_ShouldRenderCreateForm` for a complete example with form-field assertions.

---

## Archetype 2 — request/response and simulation

When your node type registers a custom handler — for example `config.WithHandler<RunSimulationRequest>(HandleRunSimulation)` in `Source/MyHub.cs` — test it the same way the UI would invoke it: `client.AwaitResponse<TResponse>(request, o => o.WithTarget(address))`. This exercises the real routing and serialization path, not a mocked seam.

```csharp
[Fact(Timeout = 15_000)]
public async Task RunSimulation_ReturnsExpectedYield()
{
    var client = GetClient();
    var address = new Address("MathDemo/Matrix/Example");

    var response = await client.AwaitResponse<SimulationResult>(
        new RunSimulationRequest(trials: 1_000),
        o => o.WithTarget(address),
        TestContext.Current.CancellationToken);

    response.Message.Yield.Should().BeApproximately(0.042, 0.001);
}
```

---

## Node types without a samples folder

If your node type lives entirely inside `Source/` under `samples/Graph/Data/MyNamespace/MyType/`, the layout-area test above already exercises the whole pipeline: load the node, compile `Source/`, register handlers, render. Nothing extra is needed.

If your node type ships as a compiled assembly — a typed record in the portal itself rather than a dynamic node — the pattern is identical. Just skip the `samples/Graph` pre-copy and register the type via `builder.AddMyType()` in `ConfigureMesh`.

---

## NuGet-referenced node types

A node type that adds `#r "nuget:..."` at the top of its `Source/*.cs` compiles identically under `MonolithMeshTestBase`. The test's compilation path hits the same `MeshNodeCompilationService` and the same `INuGetAssemblyResolver` that the portal uses. The only prerequisite is network access to `api.nuget.org` during the test run — the resolver caches packages, so subsequent test methods in the same process are instant.

See `test/MeshWeaver.Graph.Test/NodeTypeWithNuGetCompilationTest.cs` for a narrowly-scoped compilation test against `MathNet.Numerics`.

---

## Running the tests

```bash
# One test project
dotnet test test/MeshWeaver.Acme.Test/MeshWeaver.Acme.Test.csproj --no-restore

# Filter to a specific class or method
dotnet test test/MeshWeaver.Acme.Test --filter "FullyQualifiedName~TodoViewsTest"
dotnet test test/MeshWeaver.Acme.Test --filter "FullyQualifiedName~TodoViewsTest.Details_ShouldRenderTodoItem"

# Whole solution
dotnet test
```

`xunit.runner.json` at the repo root enforces sequential execution (`parallelizeAssembly: false`, `maxParallelThreads: 1`). Expect each method to take 1–5 s on a cold Roslyn compile; subsequent methods in the same class share the compilation cache and complete in milliseconds.

---

## Related

- [Creating Node Types](CreatingNodeTypes) — how to build the thing you're testing
- [NuGet Packages](NodeTypeWithNuGet) — `#r "nuget:..."` in `Source/*.cs`
- [Interactive Markdown](InteractiveMarkdown) — test interactive markdown too
