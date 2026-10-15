using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// MeshWeaver#6793 (review) — <b>a module's query provider is leased for the SUBSCRIPTION, not for the
/// call.</b> A provider returns a COLD observable and the query fan-out subscribes to it later, through
/// its query pool. A lease taken around the call and released when it returned left the subscription
/// running with no lease at all, so a retirement landing in between disposed the generation's services
/// under the query.
///
/// <para>The module here registers an <see cref="IMeshQueryProvider"/> (forwarded to the root as a proxy)
/// over a module-owned singleton (<c>Fence</c>) that the generation's container disposes. The provider's
/// observable calls <see cref="RetireSwitch"/> when it is SUBSCRIBED, and the test's armed action retires
/// the module's generation right there — the race, made deterministic. Negative control: the lease taken
/// around the call only (the first revision of #6801) — the retirement then disposes the fence at once,
/// the provider faults, and the query answers without the module's node.</para>
/// </summary>
public sealed class ModuleQueryProviderLeasedAcrossRetirementTest : MonolithMeshTestBase
{
    private const string Module = "MeshWeaver.Test.LeasedQuery";
    private const string QueriedPath = "LeasedQueried/answer";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);
    private readonly string root = Path.Combine(Path.GetTempPath(), "leased-query-" + Guid.NewGuid().ToString("N"));

    public ModuleQueryProviderLeasedAcrossRetirementTest(ITestOutputHelper output) : base(output)
    {
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder)
            .ConfigureServices(s => s.AddSingleton<RetireSwitch>())
            .InstallAssemblies(Write());

    private ModuleContexts Contexts => Mesh.ServiceProvider.GetRequiredService<ModuleContexts>();
    private RetireSwitch Switch => Mesh.ServiceProvider.GetRequiredService<RetireSwitch>();

    [Fact(Timeout = 180_000)]
    public async Task ARetirementWhenTheQueryIsSubscribed_IsDeferredUntilTheQueryEnds()
    {
        var ct = TestContext.Current.CancellationToken;
        var generation = Contexts.Current(Module)
                         ?? throw new Xunit.Sdk.XunitException($"{Module} is not held by the module registry");
        var retired = new ReplaySubject<bool>();
        Switch.Arm("subscribe", () => Contexts.Retire(generation, Budget).Subscribe(retired));

        await Mesh.ServiceProvider.GetRequiredService<IMeshService>()
            .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{QueriedPath}").AsSystem())
            .Where(change => change.Items.Any(node => node.Path == QueriedPath))
            .Should().Within(Budget)
            .Emit("the module's provider answers on live services: the retirement that started when its "
                  + "query was subscribed waits for the subscription's lease",
                cancellationToken: ct);

        Switch.Journal.Should().NotContain("fault", "the provider never ran on a disposed generation");
        (await retired.Timeout(Budget).Await(ct)).Should().BeTrue("the retirement unloads once the query released its lease");
        Switch.Journal.Should().Contain("disposed", "the generation's services were disposed after the query ended");
    }

    private const string Source = """
        using System;
        using System.Collections.Generic;
        using System.Reactive.Linq;
        using MeshWeaver.Mesh;
        using MeshWeaver.Mesh.Services;
        using Microsoft.Extensions.DependencyInjection;
        [assembly: MeshWeaver.Test.LeasedQuery.Module]
        namespace MeshWeaver.Test.LeasedQuery;
        public sealed class Fence(MeshWeaver.Graph.Test.RetireSwitch retire) : IDisposable
        {
            public bool Disposed { get; private set; }
            public void Dispose() { Disposed = true; retire.Write("disposed"); }
        }
        public sealed class QueryProbe(Fence fence, MeshWeaver.Graph.Test.RetireSwitch retire) : IMeshQueryProvider
        {
            private static readonly MeshNode Answer =
                new("answer", "LeasedQueried") { Name = "answered by the module", NodeType = "Markdown" };
            public string Name => "MeshWeaver.Test.LeasedQuery.QueryProbe";
            public bool Matches(IReadOnlyList<string> queryNamespaces) => true;
            public IObservable<QueryResultChange<T>> Query<T>(MeshQueryRequest request, System.Text.Json.JsonSerializerOptions options) =>
                Observable.Defer(() =>
                {
                    if (!request.Query.Contains("LeasedQueried"))
                        return Observable.Return(new QueryResultChange<T> { ChangeType = QueryChangeType.Initial, Items = [] });
                    retire.Fire("subscribe");
                    if (fence.Disposed)
                    {
                        retire.Write("fault");
                        return Observable.Throw<QueryResultChange<T>>(
                            new ObjectDisposedException(nameof(Fence), "queried under a disposed generation"));
                    }
                    return Observable.Return(new QueryResultChange<T>
                    {
                        ChangeType = QueryChangeType.Initial,
                        Items = Answer is T answer ? [answer] : [],
                    });
                });
            public IObservable<IReadOnlyCollection<QueryResult>> Autocomplete(
                string basePath, string prefix, System.Text.Json.JsonSerializerOptions options,
                AutocompleteMode mode = AutocompleteMode.RelevanceFirst,
                int limit = 10, string? contextPath = null, string? context = null) =>
                Observable.Return<IReadOnlyCollection<QueryResult>>([]);
            public IObservable<T?> Select<T>(string path, string property, System.Text.Json.JsonSerializerOptions options) =>
                Observable.Return<T?>(default);
        }
        public sealed class ModuleAttribute : MeshNodeProviderAttribute
        {
            public override IEnumerable<MeshNode> Nodes =>
                [new MeshNode("LeasedQueryProbeNode") { Name = "probe", NodeType = "Markdown" }
                    .WithGlobalServiceRegistry(services =>
                    {
                        services.AddSingleton<Fence>();
                        services.AddSingleton<IMeshQueryProvider, QueryProbe>();
                        return services;
                    })];
        }
        """;

    private string Write()
    {
        var references = PlatformReferences.Platform()
            .Add(MetadataReference.CreateFromFile(typeof(RetireSwitch).Assembly.Location));
        var compilation = CSharpCompilation.Create(Module, [CSharpSyntaxTree.ParseText(Source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        using var buffer = new MemoryStream();
        var result = compilation.Emit(buffer);
        result.Success.Should().BeTrue(string.Join(Environment.NewLine,
            result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        var directory = Path.Combine(root, "modules", $"{Module}@g1");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Module + ".dll");
        File.WriteAllBytes(path, buffer.ToArray());
        return path;
    }
}
