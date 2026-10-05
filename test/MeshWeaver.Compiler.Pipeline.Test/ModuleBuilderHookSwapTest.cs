using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using MeshWeaver.Domain;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>The builder hook, decomposed (policy <c>module-live-update-default</c>).</b> The builder hook
/// (<c>BuilderConfigurations</c>) is how MeshWeaver.AI and a dozen other modules contribute: arbitrary
/// calls on the <see cref="MeshBuilder"/>. It is now run against a CAPTURE builder and split into the
/// surfaces a swap can re-bind — its nodes, root services, mesh-hub type registrations, per-node-hub
/// configuration, mesh types — so a module contributing that way updates live. What it touches that a
/// swap cannot re-apply is a named blocker (the negative control).
/// </summary>
public sealed class ModuleBuilderHookSwapTest : MonolithMeshTestBase
{
    private const string Module = "MeshWeaver.Test.LiveHook";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);

    private readonly string root = Path.Combine(Path.GetTempPath(), "live-hook-" + Guid.NewGuid().ToString("N"));
    private string? v1Path;
    private string V1 => v1Path ??= Write("g1", ModuleSource(1));

    public ModuleBuilderHookSwapTest(ITestOutputHelper output) : base(output)
    {
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder)
            .ConfigureServices(s => s.AddSingleton<HostedServiceJournal>().AddSingleton<CachingGreeterConsumer>())
            .InstallAssemblies(V1);

    private ModuleContexts Contexts => Mesh.ServiceProvider.GetRequiredService<ModuleContexts>();

    private ModuleGeneration HeldGeneration(string module) =>
        Contexts.Current(module) ?? throw new Xunit.Sdk.XunitException($"{module} is not held by the module registry");

    [Fact(Timeout = 180_000)]
    public async Task AModuleContributingThroughTheBuilderHook_SwapsLive()
    {
        var ct = TestContext.Current.CancellationToken;
        var held = HeldGeneration(Module);
        held.Contributions!.BuilderHooks.Blockers.Should().BeEmpty("the hook must decompose — the conversion under test");
        held.Contributions.LiveUpdateBlockers().Should().BeEmpty();
        var consumer = Mesh.ServiceProvider.GetRequiredService<CachingGreeterConsumer>();
        consumer.Ask().Should().Be("hook v1");
        (await ReadNode("LiveHookProbe").Timeout(Budget).Await(ct))!.Name.Should().Be("v1",
            "a node the builder hook added is served");
        MeshTypeName("HookMessage")!.Assembly.Should().BeSameAs(held.Assembly,
            "the mesh hub's type registry carries the hook's type registration");
        var weakN = new WeakReference(held.Context);
        held = null;

        var outcome = await Mesh.ServiceProvider.GetRequiredService<ModuleLiveUpdater>()
            .Swap(Write("g2", ModuleSource(2)), "test: builder hook").Timeout(Budget).Await(ct);

        outcome.Kind.Should().Be(ModuleSwapKind.Live, outcome.Reason);
        consumer.Ask().Should().Be("hook v2", "the hook's root service follows the swap");
        (await ReadNode("LiveHookProbe").Timeout(Budget).Await(ct))!.Name.Should().Be("v2",
            "the hook's node is served from the new generation");
        MeshTypeName("HookMessage")!.Assembly.Should().BeSameAs(HeldGeneration(Module).Assembly,
            "the running mesh hub's type registry now maps the name to the new generation's type");

        var drained = await CollectibleUnloadDrain.WaitUntilCollectedAsync(Mesh.ServiceProvider.GetRequiredService<CollectibleContextUnloads>());
        drained.Collected.Should().BeTrue($"generation N must be collected ({drained})");
        weakN.IsAlive.Should().BeFalse();
    }

    /// <summary>The NEGATIVE CONTROL: a hook that adds a query routing rule — which the mesh reads once
    /// — does not decompose; the blocker is named, and the guard demands the conversion.</summary>
    [Fact]
    public void AHookThatAddsAQueryRoutingRule_IsABlocker_ThatTheGuardNames()
    {
        using var contexts = new ModuleContexts();
        var generation = contexts.Load(Write("gx", ModuleSource(1, routingRule: true)));
        contexts.Commit(generation);
        var contributions = ModuleContributions.Of(generation.Assembly);

        contributions.MeasuredLiveUpdateBlockers().Should().Contain(b => b.Contains("query routing rules"));
        ModuleLiveUpdateGuard.Violation(Module, contributions).Should().NotBeNull().And.Contain("query routing rules");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private Type? MeshTypeName(string name) =>
        Mesh.Configuration.TypeRegistry.GetType(name);

    private static string ModuleSource(int version, bool routingRule = false) => $$"""
        using Microsoft.Extensions.DependencyInjection;
        [assembly: MeshWeaver.Test.LiveHook.Module]
        namespace MeshWeaver.Test.LiveHook;
        public sealed record HookMessage(string Text);
        public sealed class Greeter : MeshWeaver.Graph.Test.ILiveGreeter { public string Greet() => "hook v{{version}}"; }
        public sealed class ModuleAttribute : MeshWeaver.Mesh.MeshNodeProviderAttribute
        {
            public override System.Collections.Generic.IEnumerable<System.Func<MeshWeaver.Mesh.MeshBuilder, MeshWeaver.Mesh.MeshBuilder>> BuilderConfigurations =>
            [
                builder => builder
                    .AddMeshNodes(new MeshWeaver.Mesh.MeshNode("LiveHookProbe") { Name = "v{{version}}", NodeType = "Markdown" })
                    .ConfigureServices(services => services.AddSingleton<MeshWeaver.Graph.Test.ILiveGreeter, Greeter>())
                    .ConfigureHub(config => { config.TypeRegistry.WithType(typeof(HookMessage), "HookMessage"); return config; })
                    .ConfigureDefaultNodeHub(config => config)
                    .AddAutocompleteExcludedTypes("LiveHookType")
                    {{(routingRule ? ".AddQueryRoutingRule(_ => null)" : "")}}
            ];
        }
        """;

    private string Write(string generation, string source)
    {
        var references = PlatformReferences.Platform()
            .Add(MetadataReference.CreateFromFile(typeof(ILiveGreeter).Assembly.Location));
        var compilation = CSharpCompilation.Create(Module, [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
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
