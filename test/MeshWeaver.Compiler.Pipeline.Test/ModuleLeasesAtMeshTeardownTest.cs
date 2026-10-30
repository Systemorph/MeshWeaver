using System.Reactive.Linq;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// MeshWeaver#7577 — <b>a module generation kept loaded at mesh teardown says WHO held its lease.</b>
/// <c>ModuleContexts.Dispose</c> leaves a generation loaded while a lease on its context is held (the
/// leak-over-crash rule of <see cref="AlcLeaseRegistry"/>) and reports it. The production line carried
/// the generation and the count only, so a teardown on one pod could not say whether the holder was the
/// query fan-out, a kernel script session or an access rule.
///
/// <para>Both facts run through a fixture's REAL teardown (hosted-service stop, activity quiesce, mesh
/// dispose, the I/O-pool drain, then the container): a module that registers an
/// <see cref="IMeshQueryProvider"/> whose streams stay live, and a subscriber that never ends its
/// subscription.</para>
/// <list type="bullet">
/// <item>A live AUTOCOMPLETE — the fan-out leases the module's provider for the subscription, and the
/// autocomplete is not on the drainable Query pool, so its lease outlives the drain. The teardown keeps
/// the generation loaded and names the holder: <c>IMeshQueryProvider (registration 0) ×1</c>.</item>
/// <item>Control: a live QUERY, held the same way, holds the same lease while it runs — but it is
/// subscribed through the Query pool, whose drain disposes it, so the teardown releases the lease and
/// reports nothing. The verdict therefore depends on the holder, not on the module being present.</item>
/// </list>
/// </summary>
public sealed class ModuleLeasesAtMeshTeardownTest(ITestOutputHelper output)
{
    private const string Module = "MeshWeaver.Test.TeardownLease";
    private const string QueryHolder = "IMeshQueryProvider (registration 0) ×1";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    [Fact(Timeout = 180_000)]
    public async Task ALiveAutocompleteAtTeardown_KeepsTheGenerationLoaded_AndNamesItsHolder()
    {
        var fixture = new Fixture(output);
        CollectibleContextUnloads unloads;
        Exception? teardown = null;
        try
        {
            await fixture.InitializeAsync();
            unloads = fixture.Unloads;
            fixture.Hold(fixture.MeshService.Autocomplete("TeardownLeaseQueried", "a").Subscribe(_ => { }, _ => { }));
            await fixture.LeasedBy(QueryHolder);
        }
        finally
        {
            try { await fixture.DisposeAsync(); }
            catch (Exception exception) { teardown = exception; }
        }

        var faults = unloads.Faults.Select(f => f.Message).ToArray();
        faults.Should().ContainSingle(
            "the autocomplete's lease is not on the drainable pool and outlives the drain, so the teardown keeps the generation loaded");
        faults[0].Should().Contain($"module:{Module}#").And.Contain(
            $"held, by {QueryHolder}",
            "the teardown names the KIND of holder whose lease kept the generation loaded (MeshWeaver#7577), not only the count");
        teardown.Should().NotBeNull("the fixture surfaces a generation kept loaded at teardown as a failure");
    }

    [Fact(Timeout = 180_000)]
    public async Task ALivePooledQueryAtTeardown_IsReleasedByTheDrain_AndNothingIsKeptLoaded()
    {
        var fixture = new Fixture(output);
        CollectibleContextUnloads unloads;
        try
        {
            await fixture.InitializeAsync();
            unloads = fixture.Unloads;
            fixture.Hold(fixture.MeshService
                .Query<MeshNode>(MeshQueryRequest.FromQuery("path:TeardownLeaseQueried/answer").AsSystem())
                .Subscribe(_ => { }, _ => { }));
            await fixture.LeasedBy(QueryHolder);
        }
        finally
        {
            await fixture.DisposeAsync();
        }

        unloads.Faults.Should().BeEmpty(
            "the live query held the same lease, but the Query pool's drain disposed its subscription before the container ended");
    }

    private sealed class Fixture(ITestOutputHelper output) : MonolithMeshTestBase(output)
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "teardown-lease-" + Guid.NewGuid().ToString("N"));

        // Subscriptions the "client" never ends — deliberately never disposed: the teardown is the subject.
        private readonly System.Reactive.Disposables.CompositeDisposable held = new();

        protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
            base.ConfigureMesh(builder).InstallAssemblies(Write());

        private ModuleContexts Contexts => Mesh.ServiceProvider.GetRequiredService<ModuleContexts>();

        public CollectibleContextUnloads Unloads => Mesh.ServiceProvider.GetRequiredService<CollectibleContextUnloads>();

        public IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        public void Hold(IDisposable subscription) => held.Add(subscription);

        /// <summary>Completes once the module's context is leased by <paramref name="holder"/> — the
        /// registry's own record, re-read until it says so (the fan-out subscribes off the caller).</summary>
        public Task LeasedBy(string holder)
        {
            var context = (Contexts.Current(Module)
                           ?? throw new Xunit.Sdk.XunitException($"{Module} is not held by the module registry")).Context;
            return Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
                .Select(_ => Contexts.Leases.Holders(context))
                .Where(holders => holders.Contains(holder))
                .Take(1)
                .Should().Within(Budget)
                .Emit($"the live subscription leases the module's provider, recorded as '{holder}'",
                    cancellationToken: TestContext.Current.CancellationToken);
        }

        private string Write()
        {
            var compilation = CSharpCompilation.Create(Module, [CSharpSyntaxTree.ParseText(Source)], PlatformReferences.Platform(),
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

    // A provider whose streams stay LIVE after their first answer — the shape of a mailbox provider's
    // change feed: an Initial frame, then nothing until the subscriber ends the subscription.
    private const string Source = """
        using System;
        using System.Collections.Generic;
        using System.Reactive.Linq;
        using MeshWeaver.Mesh;
        using MeshWeaver.Mesh.Services;
        using Microsoft.Extensions.DependencyInjection;
        [assembly: MeshWeaver.Test.TeardownLease.Module]
        namespace MeshWeaver.Test.TeardownLease;
        public sealed class LiveProvider : IMeshQueryProvider
        {
            public string Name => "MeshWeaver.Test.TeardownLease.LiveProvider";
            public bool Matches(IReadOnlyList<string> queryNamespaces) => true;
            public IObservable<QueryResultChange<T>> Query<T>(MeshQueryRequest request, System.Text.Json.JsonSerializerOptions options) =>
                Observable.Return(new QueryResultChange<T> { ChangeType = QueryChangeType.Initial, Items = [] })
                    .Concat(Observable.Never<QueryResultChange<T>>());
            public IObservable<IReadOnlyCollection<QueryResult>> Autocomplete(
                string basePath, string prefix, System.Text.Json.JsonSerializerOptions options,
                AutocompleteMode mode = AutocompleteMode.RelevanceFirst,
                int limit = 10, string? contextPath = null, string? context = null) =>
                Observable.Return<IReadOnlyCollection<QueryResult>>([])
                    .Concat(Observable.Never<IReadOnlyCollection<QueryResult>>());
            public IObservable<T?> Select<T>(string path, string property, System.Text.Json.JsonSerializerOptions options) =>
                Observable.Return<T?>(default);
        }
        public sealed class ModuleAttribute : MeshNodeProviderAttribute
        {
            public override IEnumerable<MeshNode> Nodes =>
                [new MeshNode("TeardownLeaseProbeNode") { Name = "probe", NodeType = "Markdown" }
                    .WithGlobalServiceRegistry(services =>
                    {
                        services.AddSingleton<IMeshQueryProvider, LiveProvider>();
                        return services;
                    })];
        }
        """;
}
