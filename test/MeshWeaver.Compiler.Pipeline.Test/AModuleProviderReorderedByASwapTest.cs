using System.Reactive.Linq;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
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
/// Review of #7231 (MeshWeaver#7218) — <b>a provider name the query fan-out learned is trusted only for the
/// generation that answered it.</b> The fan-out learns each module provider's name from the first instance a
/// subscription leases, so it can leave out a duplicate registration without constructing it. A same-shape
/// live swap that REORDERS the module's providers — <c>[A, B]</c> to <c>[B, A]</c> — puts a different provider
/// at each position. Trusting the names learned from the old generation, the second position was skipped as
/// a duplicate of the name the first one now answers by, and provider A was absent from every later query.
///
/// <para>Negative control: with the learned name trusted across generations, the query for A's node after
/// the swap answers without it.</para>
/// </summary>
public sealed class AModuleProviderReorderedByASwapTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Module = "MeshWeaver.Test.ReorderedProviders";
    private const string APath = "ReorderedA/answer";
    private const string BPath = "ReorderedB/answer";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);

    private readonly string root = Path.Combine(Path.GetTempPath(), "reordered-providers-" + Guid.NewGuid().ToString("N"));

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder).InstallAssemblies(Write("g1", aFirst: true));

    private ModuleContexts Contexts => Mesh.ServiceProvider.GetRequiredService<ModuleContexts>();

    private ModuleGeneration Held() =>
        Contexts.Current(Module) ?? throw new Xunit.Sdk.XunitException($"{Module} is not held by the module registry");

    // The SAME fan-out before and after the swap: it is built once, so what it learned survives the swap.
    private IObservable<bool> Answers(IMeshQueryCore query, string path) =>
        query.Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{path}").AsSystem(), Mesh.JsonSerializerOptions)
            .Where(change => change.ChangeType == QueryChangeType.Initial)
            .Select(change => change.Items.Any(node => node.Path == path));

    [Fact(Timeout = 180_000)]
    public async Task AfterASwapReordersTheProviders_BothStillAnswer()
    {
        var ct = TestContext.Current.CancellationToken;
        var query = Mesh.ServiceProvider.GetRequiredService<IMeshQueryCore>();

        // Before the swap: each provider answers, and the fan-out has learned both names.
        await Answers(query, APath).Where(answered => answered).Should().Within(Budget)
            .Emit("provider A answers its node before the swap", cancellationToken: ct);
        await Answers(query, BPath).Where(answered => answered).Should().Within(Budget)
            .Emit("provider B answers its node before the swap", cancellationToken: ct);

        SwapIn(Write("g2", aFirst: false));

        await Answers(query, APath).Should().Within(Budget)
            .Match(answered => answered, "provider A still answers after the swap moved it to the second position", cancellationToken: ct);
        await Answers(query, BPath).Should().Within(Budget)
            .Match(answered => answered, "provider B still answers after the swap moved it to the first position", cancellationToken: ct);
    }

    /// <summary>
    /// The learned names never pin a generation (review of #7231). A fan-out the test keeps alive learns both
    /// names on g1; the module is swapped live and NO query runs afterwards, so nothing re-learns or replaces
    /// what the fan-out holds. The retired g1 context must still be collected. Negative control: with the
    /// learned name keyed to the <see cref="ModuleGeneration"/> itself, the idle fan-out keeps g1 loaded.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task ARetiredGeneration_IsCollected_WhileAnIdleFanOutThatLearnedFromItStaysAlive()
    {
        var ct = TestContext.Current.CancellationToken;
        var fanOut = new MeshWeaver.Hosting.Persistence.Query.MeshQuery(
            Mesh.ServiceProvider.GetServices<IMeshQueryProvider>(), Mesh);
        await Answers(fanOut, APath).Where(answered => answered).Should().Within(Budget)
            .Emit("the fan-out leases provider A on g1 and learns its name", cancellationToken: ct);
        await Answers(fanOut, BPath).Where(answered => answered).Should().Within(Budget)
            .Emit("the fan-out leases provider B on g1 and learns its name", cancellationToken: ct);
        var weakG1 = WeakContextOfHeld();

        var outcome = await Mesh.ServiceProvider.GetRequiredService<ModuleLiveUpdater>()
            .Swap(Write("g2", aFirst: true), "test: retire g1 under an idle fan-out").Timeout(Budget).Await(ct);
        outcome.Kind.Should().Be(ModuleSwapKind.Live, outcome.Reason);

        var drained = await CollectibleUnloadDrain.WaitUntilCollectedAsync(
            Mesh.ServiceProvider.GetRequiredService<CollectibleContextUnloads>());
        drained.Collected.Should().BeTrue($"the idle fan-out must not pin the retired generation ({drained})");
        weakG1.IsAlive.Should().BeFalse("nothing the fan-out learned holds g1's load context");
        GC.KeepAlive(fanOut);
    }

    // In its own frame, so no local of the test method roots the generation.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private WeakReference WeakContextOfHeld() => new(Held().Context);

    // A same-shape live swap: two IMeshQueryProvider registrations, as before, in the other order.
    private void SwapIn(string path)
    {
        var old = Held();
        var stage = new ModuleSwapStage();
        var fresh = Contexts.LoadStaged(path, stage);
        var contributions = ModuleContributions.Of(fresh.Assembly);
        var services = ModuleServices.Probe(fresh.Name, fresh.Context, contributions.ServiceConfigurations, old.Services!.Prefix);
        services.Blockers.Should().BeEmpty();
        services.Stage = fresh.Context.Stage;
        services.InheritForwarderLayout(old.Services);
        fresh.Services = services;
        Contexts.SetContributions(fresh, contributions);
        stage.Add(fresh);
        Contexts.BuildBeforePublish(fresh).Should().BeNull();
        Contexts.CommitAll(stage);
        Held().Should().BeSameAs(fresh);
    }

    private static string Provider(string name, string folder) => $$"""
        public sealed class {{name}} : IMeshQueryProvider
        {
            private static readonly MeshNode Answer = new("answer", "{{folder}}") { Name = "answered by {{name}}", NodeType = "Markdown" };
            public string Name => "MeshWeaver.Test.ReorderedProviders.{{name}}";
            public bool Matches(IReadOnlyList<string> queryNamespaces) => true;
            public IObservable<QueryResultChange<T>> Query<T>(MeshQueryRequest request, System.Text.Json.JsonSerializerOptions options) =>
                Observable.Return(new QueryResultChange<T>
                {
                    ChangeType = QueryChangeType.Initial,
                    Items = (request.Query ?? "").Contains("{{folder}}") && Answer is T answer ? [answer] : [],
                });
            public IObservable<IReadOnlyCollection<QueryResult>> Autocomplete(
                string basePath, string prefix, System.Text.Json.JsonSerializerOptions options,
                AutocompleteMode mode = AutocompleteMode.RelevanceFirst,
                int limit = 10, string? contextPath = null, string? context = null) =>
                Observable.Return<IReadOnlyCollection<QueryResult>>([]);
            public IObservable<T?> Select<T>(string path, string property, System.Text.Json.JsonSerializerOptions options) =>
                Observable.Return<T?>(default);
        }
        """;

    private static string Source(bool aFirst) => $$"""
        using System;
        using System.Collections.Generic;
        using System.Reactive.Linq;
        using MeshWeaver.Mesh;
        using MeshWeaver.Mesh.Services;
        using Microsoft.Extensions.DependencyInjection;
        [assembly: MeshWeaver.Test.ReorderedProviders.Module]
        namespace MeshWeaver.Test.ReorderedProviders;
        {{Provider("AProvider", "ReorderedA")}}
        {{Provider("BProvider", "ReorderedB")}}
        public sealed class ModuleAttribute : MeshNodeProviderAttribute
        {
            public override IEnumerable<MeshNode> Nodes =>
                [new MeshNode("ReorderedProvidersProbe") { Name = "probe", NodeType = "Markdown" }
                    .WithGlobalServiceRegistry(services =>
                    {
                        services.AddSingleton<IMeshQueryProvider, {{(aFirst ? "AProvider" : "BProvider")}}>();
                        services.AddSingleton<IMeshQueryProvider, {{(aFirst ? "BProvider" : "AProvider")}}>();
                        return services;
                    })];
        }
        """;

    private string Write(string generation, bool aFirst)
    {
        var compilation = CSharpCompilation.Create(Module, [CSharpSyntaxTree.ParseText(Source(aFirst))], PlatformReferences.Platform(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        using var buffer = new MemoryStream();
        var result = compilation.Emit(buffer);
        result.Success.Should().BeTrue(string.Join(Environment.NewLine,
            result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        var directory = Path.Combine(root, "modules", $"{Module}@{generation}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Module + ".dll");
        File.WriteAllBytes(path, buffer.ToArray());
        return path;
    }
}
