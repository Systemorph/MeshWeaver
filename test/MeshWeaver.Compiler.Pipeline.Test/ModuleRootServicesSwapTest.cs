using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
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

/// <summary>A PLATFORM interface (it lives in this test assembly, in the default load context) that
/// the test module implements and registers as a root service.</summary>
public interface ILiveGreeter
{
    /// <summary>Says which generation answered.</summary>
    string Greet();
}

/// <summary>A platform singleton that resolves the module's greeter ONCE and caches it — the consumer
/// shape that pins a generation unless the root holds a forwarding proxy.</summary>
public sealed class CachingGreeterConsumer(ILiveGreeter greeter)
{
    /// <summary>Asks the cached greeter.</summary>
    public string Ask() => greeter.Greet();
}

/// <summary>Where the module's hosted service writes its start and stop — a platform singleton.</summary>
public sealed class HostedServiceJournal
{
    private ImmutableList<string> entries = [];

    /// <summary>Everything written, in order.</summary>
    public ImmutableList<string> Entries => entries;

    /// <summary>Appends one entry.</summary>
    public void Write(string entry) => ImmutableInterlocked.Update(ref entries, e => e.Add(entry));
}

/// <summary>
/// 🚨 <b>Root services, converted (policy <c>module-live-update-default</c>, slice 4).</b> A module that
/// registers ROOT services — the most common reason the 37 modules measured restart-required — is now
/// swapped live: its registrations run against a copy of the root collection and are served from a
/// scope of its own; the root holds only forwarders.
///
/// <para>The module here registers what the shipped ones do: a platform interface it implements (the
/// <c>IChatClientFactory</c> shape), options over a type it owns (<c>AddOptions&lt;T&gt;().Configure</c>),
/// and a hosted service (the <c>OpenAICompatibleModelSync</c> shape). A platform singleton caches the
/// interface — the consumer that would pin the old generation without a forwarding proxy.</para>
/// </summary>
public sealed class ModuleRootServicesSwapTest : MonolithMeshTestBase
{
    private const string Module = "MeshWeaver.Test.LiveServices";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);

    private readonly string root = Path.Combine(Path.GetTempPath(), "live-services-" + Guid.NewGuid().ToString("N"));
    private string? v1Path;
    private string V1 => v1Path ??= Write("g1", ModuleSource(1));

    public ModuleRootServicesSwapTest(ITestOutputHelper output) : base(output)
    {
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder)
            .ConfigureServices(s => s
                .AddSingleton<HostedServiceJournal>()
                .AddSingleton<CachingGreeterConsumer>())
            .InstallAssemblies(V1);

    private ModuleContexts Contexts => Mesh.ServiceProvider.GetRequiredService<ModuleContexts>();

    private ModuleGeneration HeldGeneration(string module) =>
        Contexts.Current(module) ?? throw new Xunit.Sdk.XunitException($"{module} is not held by the module registry");
    private ModuleLiveUpdater Updater => Mesh.ServiceProvider.GetRequiredService<ModuleLiveUpdater>();

    [Fact(Timeout = 180_000)]
    public async Task AModuleWithRootServices_SwapsLive_AndEveryConsumerFollowsTheNewGeneration()
    {
        var ct = TestContext.Current.CancellationToken;
        var consumer = Mesh.ServiceProvider.GetRequiredService<CachingGreeterConsumer>();
        var journal = Mesh.ServiceProvider.GetRequiredService<HostedServiceJournal>();
        HeldGeneration(Module).Services.Should().NotBeNull(
            "the module's root services must be served from a scope of its own — the conversion under test");
        HeldGeneration(Module).RootServiceBlockers.Should().BeEmpty();
        consumer.Ask().Should().Be("hello v1 (options v1)");
        journal.Entries.Should().Equal(["start v1"], "the host started the module's hosted service once, through its forwarder");
        var weakN = WeakContextOf(Module);

        var outcome = await Updater.Swap(Write("g2", ModuleSource(2)), "test: root services").Timeout(Budget).Await(ct);

        outcome.Kind.Should().Be(ModuleSwapKind.Live, outcome.Reason);
        consumer.Ask().Should().Be("hello v2 (options v2)",
            "a platform singleton that CACHED the module's service must follow the swap — the root holds a forwarding proxy");
        journal.Entries.Should().Equal(["start v1", "stop v1", "start v2"],
            "the old generation's hosted service is stopped and the new one's started, in that order");

        var drained = await CollectibleUnloadDrain.WaitUntilCollectedAsync(Mesh.ServiceProvider.GetRequiredService<CollectibleContextUnloads>());
        drained.Collected.Should().BeTrue($"no root registration may pin generation N ({drained})");
        weakN.IsAlive.Should().BeFalse();
    }

    /// <summary>
    /// The NEGATIVE CONTROL for the forwarding: the root's forwarders were laid out at boot, so an N+1
    /// that forwards a DIFFERENT set of platform services cannot be swapped live — refused by name, N
    /// keeps serving.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AnUpdateThatChangesTheRootServiceShape_IsRefused_AndNKeepsServing()
    {
        var ct = TestContext.Current.CancellationToken;
        var consumer = Mesh.ServiceProvider.GetRequiredService<CachingGreeterConsumer>();

        var outcome = await Updater.Swap(Write("g2", ModuleSource(2, extraGreeter: true)), "test: shape").Timeout(Budget).Await(ct);

        outcome.Kind.Should().Be(ModuleSwapKind.RestartRequired);
        outcome.Reason.Should().Contain("changed shape");
        consumer.Ask().Should().Be("hello v1 (options v1)", "never a half-swapped state");
    }

    [Fact]
    public void AClassTypedRootServiceTheModuleImplements_IsABlocker_ThatTheGuardNames()
    {
        var path = Write("gx", ModuleSource(1, classService: true));
        using var contexts = new ModuleContexts();
        var generation = contexts.Load(path);
        contexts.Commit(generation);
        var contributions = ModuleContributions.Of(generation.Assembly);

        contributions.MeasuredLiveUpdateBlockers().Should().Contain(b => b.Contains("class-typed root service"));
        ModuleLiveUpdateGuard.Violation(Module, contributions).Should().NotBeNull()
            .And.Contain("class-typed root service");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private WeakReference WeakContextOf(string module) => new(HeldGeneration(module).Context);

    private static string ModuleSource(int version, bool extraGreeter = false, bool classService = false) => $$"""
        using Microsoft.Extensions.DependencyInjection;
        using Microsoft.Extensions.Options;
        [assembly: MeshWeaver.Test.LiveServices.Module]
        namespace MeshWeaver.Test.LiveServices;
        public sealed class GreeterOptions { public string Version { get; set; } = "unset"; }
        public sealed class Greeter(IOptions<GreeterOptions> options) : MeshWeaver.Graph.Test.ILiveGreeter
        {
            public string Greet() => "hello v{{version}} (options " + options.Value.Version + ")";
        }
        public sealed class Journaling(MeshWeaver.Graph.Test.HostedServiceJournal journal) : Microsoft.Extensions.Hosting.IHostedService
        {
            public System.Threading.Tasks.Task StartAsync(System.Threading.CancellationToken ct) { journal.Write("start v{{version}}"); return System.Threading.Tasks.Task.CompletedTask; }
            public System.Threading.Tasks.Task StopAsync(System.Threading.CancellationToken ct) { journal.Write("stop v{{version}}"); return System.Threading.Tasks.Task.CompletedTask; }
        }
        public class Journal2 : MeshWeaver.Graph.Test.HostedServiceJournalBase { }
        public sealed class ModuleAttribute : MeshWeaver.Mesh.MeshNodeProviderAttribute
        {
            public override System.Collections.Generic.IEnumerable<MeshWeaver.Mesh.MeshNode> Nodes =>
                [new MeshWeaver.Mesh.MeshNode("LiveServicesProbe") { Name = "v{{version}}", NodeType = "Markdown" }
                    .WithGlobalServiceRegistry(services =>
                    {
                        services.AddOptions<GreeterOptions>().Configure(o => o.Version = "v{{version}}");
                        services.AddSingleton<MeshWeaver.Graph.Test.ILiveGreeter, Greeter>();
                        {{(extraGreeter ? "services.AddSingleton<MeshWeaver.Graph.Test.ILiveGreeter, Greeter>();" : "")}}
                        {{(classService ? "services.AddSingleton<MeshWeaver.Graph.Test.HostedServiceJournalBase, Journal2>();" : "")}}
                        services.AddHostedService<Journaling>();
                        return services;
                    })];
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

/// <summary>A platform CLASS a module may derive from — the class-typed root service shape.</summary>
public class HostedServiceJournalBase
{
}
