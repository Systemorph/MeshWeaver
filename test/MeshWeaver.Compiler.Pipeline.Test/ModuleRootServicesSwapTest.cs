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
                .AddSingleton<CachingGreeterConsumer>()
                .AddScoped<CircuitBoundState>())
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
        var keyed = Mesh.ServiceProvider.GetRequiredKeyedService<ILiveGreeter>("keyed-greeter");
        keyed.Greet().Should().Be("hello v1 (options v1)");
        journal.Entries.Should().Equal(["start v1"], "the host started the module's hosted service once, through the single ModuleHostedServicesHost");
        var weakN = WeakContextOf(Module);

        var outcome = await Updater.Swap(Write("g2", ModuleSource(2)), "test: root services").Timeout(Budget).Await(ct);

        outcome.Kind.Should().Be(ModuleSwapKind.Live, outcome.Reason);
        consumer.Ask().Should().Be("hello v2 (options v2)",
            "a platform singleton that CACHED the module's service must follow the swap — the root holds a forwarding proxy");
        keyed.Greet().Should().Be("hello v2 (options v2)",
            "a KEYED root service is forwarded under the module's own key and follows the swap too");
        OwnKeyedGreeting().Should().Be("own v2",
            "a KEYED registration of a type the module DECLARES is answered by the registration source, which "
            + "must read the CURRENT generation's scope on every resolve — never one cached at boot");
        journal.Entries.Should().Equal(["start v1", "stop v1", "start v2"],
            "the old generation's hosted service is stopped and the new one's started, in that order");

        var drained = await CollectibleUnloadDrain.WaitUntilCollectedAsync(Mesh.ServiceProvider.GetRequiredService<CollectibleContextUnloads>());
        drained.Collected.Should().BeTrue($"no root registration may pin generation N ({drained})");
        weakN.IsAlive.Should().BeFalse();
    }

    /// <summary>
    /// The keyed stand-in path of <see cref="ModuleOwnedRootSource"/>: a KEYED registration of a type the
    /// module itself DECLARES is never forwarded into the root (route ModuleOwned), so the root answers it
    /// only through the registration source, which must find the declaring generation for a type whose
    /// ONLY registration is keyed. Asked by its key on the root, it resolves from the module's container.
    /// </summary>
    [Fact]
    public void AKeyedServiceOfATypeTheModuleDeclares_ResolvesOnTheRootByItsKey()
    {
        OwnKeyedGreeting().Should().Be("own v1",
            "the root holds no descriptor for a module-declared keyed type; the source must answer it by key");
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

    /// <summary>
    /// MeshWeaver#6847 — the root's forwarders hold the BOOT generation's registration index. An N+1 that
    /// forwards the very same platform services but inserts a registration of its OWN type ahead of them
    /// (WhatsApp 1.4.2 added its inbox reader that way) moves every forwarded index, so a live swap would
    /// make the root resolve the wrong registration of N+1 — measured in production as
    /// "mw-module-service:10 … has not been registered" on every hub activation. Refused by name; N keeps
    /// serving through the same proxy. Its negative control is the hosted-service swap above: an addition
    /// AFTER the forwarded registrations still swaps live.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AnUpdateThatMovesAForwardedRegistrationsIndex_IsRefused_AndNKeepsServing()
    {
        var ct = TestContext.Current.CancellationToken;
        var consumer = Mesh.ServiceProvider.GetRequiredService<CachingGreeterConsumer>();
        var greeter = Mesh.ServiceProvider.GetRequiredService<ILiveGreeter>();

        var outcome = await Updater.Swap(Write("g2", ModuleSource(2, ownedFirst: true)), "test: a module-owned registration inserted first")
            .Timeout(Budget).Await(ct);

        outcome.Kind.Should().Be(ModuleSwapKind.RestartRequired, outcome.Reason);
        outcome.Reason.Should().Contain("changed shape");
        consumer.Ask().Should().Be("hello v1 (options v1)", "never a half-swapped state");
        greeter.Greet().Should().Be("hello v1 (options v1)", "the boot proxy still resolves N's registration at its own index");
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

    /// <summary>
    /// Resolves the module-declared <c>IOwnGreeter</c> by its key on the root, through the HELD
    /// generation's type, and asks it — in its own frame so no reference to that generation's type
    /// or instance outlives the call.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private string? OwnKeyedGreeting()
    {
        var type = HeldGeneration(Module).Assembly.GetType("MeshWeaver.Test.LiveServices.IOwnGreeter")
                   ?? throw new Xunit.Sdk.XunitException("IOwnGreeter is not in the held module");
        var greeter = Mesh.ServiceProvider.GetRequiredKeyedService(type, "own-keyed");
        return (string?)type.GetMethod("Greet")!.Invoke(greeter, null);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private WeakReference WeakContextOf(string module) => new(HeldGeneration(module).Context);

    /// <summary>
    /// A new generation that ADDS a background service still swaps live: the root holds no
    /// per-registration forwarder for hosted services, so the shape is unchanged — the shape of the
    /// real MeshWeaver.AI update behind the 2026-10-05 incident, which added exactly one.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AnUpdateThatAddsAHostedService_SwapsLive_AndStartsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var journal = Mesh.ServiceProvider.GetRequiredService<HostedServiceJournal>();
        journal.Entries.Should().Equal(["start v1"]);

        var outcome = await Updater.Swap(Write("g2", ModuleSource(2, extraHosted: true)), "test: added hosted service")
            .Timeout(Budget).Await(ct);

        outcome.Kind.Should().Be(ModuleSwapKind.Live, outcome.Reason);
        journal.Entries.Should().Equal(["start v1", "stop v1", "start v2", "start extra v2"]);
    }

    /// <summary>
    /// A diagnostic names what a module's proxy forwards to NOW (MeshWeaver#6391). The proxy survives
    /// a live swap, and a swap keeps route, service type and lifetime but may change the implementing
    /// class — so the label is read from the current generation's registration, never remembered.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AProxysLabel_NamesTheCurrentGenerationsImplementation_AfterALiveSwap()
    {
        var ct = TestContext.Current.CancellationToken;
        var greeter = Mesh.ServiceProvider.GetRequiredService<ILiveGreeter>();
        ModuleServiceProxy.Label(greeter).Should().StartWith($"Greeter [module {Module}, registration ");

        var outcome = await Updater.Swap(Write("g2", ModuleSource(2, greeterClass: "RenamedGreeter")), "test: renamed implementation")
            .Timeout(Budget).Await(ct);

        outcome.Kind.Should().Be(ModuleSwapKind.Live, outcome.Reason);
        greeter.Greet().Should().Be("hello v2 (options v2)", "the same proxy now forwards to generation 2");
        ModuleServiceProxy.Label(greeter).Should().StartWith($"RenamedGreeter [module {Module}, registration ",
            "the label follows the swap; a name taken at boot would still say 'Greeter', the retired class");
    }

    /// <summary>
    /// 🚨 <b>Built before published</b> (MeshWeaver#6542): every shared instance of N+1's container is
    /// built BEFORE the swap commits. A singleton whose constructor throws therefore refuses the swap —
    /// nothing of N+1 was ever reachable, N keeps serving every consumer, and N's hosted service was
    /// never stopped. Its negative control is the live swap above: the same N+1 without the throwing
    /// singleton commits.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AnUpdateWhoseSingletonCannotBeBuilt_IsRefusedBeforeItCommits_AndNKeepsServing()
    {
        var ct = TestContext.Current.CancellationToken;
        var consumer = Mesh.ServiceProvider.GetRequiredService<CachingGreeterConsumer>();
        var journal = Mesh.ServiceProvider.GetRequiredService<HostedServiceJournal>();
        var serving = HeldGeneration(Module).Location;

        var outcome = await Updater.Swap(Write("g2", ModuleSource(2, brokenSingleton: true)), "test: a singleton that cannot be built")
            .Timeout(Budget).Await(ct);

        outcome.Kind.Should().Be(ModuleSwapKind.Failed, outcome.Reason);
        outcome.Reason.Should().Contain("could not be built before the swap commits")
            .And.Contain("broken by design in v2", "the constructor's own sentence is what an operator acts on");
        HeldGeneration(Module).Location.Should().Be(serving, "a generation that could not be built is never made current");
        consumer.Ask().Should().Be("hello v1 (options v1)", "the cached proxy still forwards to N");
        OwnKeyedGreeting().Should().Be("own v1", "the root's module-type stand-ins still answer from N");
        journal.Entries.Should().Equal(["start v1"], "N's hosted service was never stopped, and N+1's never started");
    }

    /// <summary>
    /// MeshWeaver#6556 — the Radzen shape on a LIVE SWAP. N+1 adds services that belong to a circuit:
    /// a scoped one over circuit state, a singleton whose constructor takes circuit state, and a
    /// singleton that takes that one — all module types, so the forwarded shape stays the same. None is a process-lifetime
    /// instance the root reaches, so none is built before the commit, and the swap goes live. Its
    /// negative control is the refused swap above: a root-reachable singleton that throws still refuses it.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AnUpdateWithCircuitBoundServices_SwapsLive()
    {
        var ct = TestContext.Current.CancellationToken;
        var consumer = Mesh.ServiceProvider.GetRequiredService<CachingGreeterConsumer>();

        var outcome = await Updater.Swap(Write("g2", ModuleSource(2, circuitBound: true)), "test: circuit-bound services")
            .Timeout(Budget).Await(ct);

        outcome.Kind.Should().Be(ModuleSwapKind.Live, outcome.Reason);
        consumer.Ask().Should().Be("hello v2 (options v2)", "N+1 is current and serves");
    }

    private static string ModuleSource(int version, bool extraGreeter = false, bool classService = false, bool extraHosted = false, string greeterClass = "Greeter", bool brokenSingleton = false, bool circuitBound = false, bool ownedFirst = false) => $$"""
        using Microsoft.Extensions.DependencyInjection;
        using Microsoft.Extensions.Options;
        [assembly: MeshWeaver.Test.LiveServices.Module]
        namespace MeshWeaver.Test.LiveServices;
        public sealed class GreeterOptions { public string Version { get; set; } = "unset"; }
        public sealed class {{greeterClass}}(IOptions<GreeterOptions> options) : MeshWeaver.Graph.Test.ILiveGreeter
        {
            public string Greet() => "hello v{{version}} (options " + options.Value.Version + ")";
        }
        public sealed class Journaling(MeshWeaver.Graph.Test.HostedServiceJournal journal) : Microsoft.Extensions.Hosting.IHostedService
        {
            public System.Threading.Tasks.Task StartAsync(System.Threading.CancellationToken ct) { journal.Write("start v{{version}}"); return System.Threading.Tasks.Task.CompletedTask; }
            public System.Threading.Tasks.Task StopAsync(System.Threading.CancellationToken ct) { journal.Write("stop v{{version}}"); return System.Threading.Tasks.Task.CompletedTask; }
        }
        public interface IOwnGreeter { string Greet(); }
        public sealed class OwnGreeter : IOwnGreeter { public string Greet() => "own v{{version}}"; }
        public sealed class Unbuildable { public Unbuildable() => throw new System.InvalidOperationException("broken by design in v{{version}}"); }
        public sealed class CircuitDialog { public CircuitDialog(MeshWeaver.Graph.Test.CircuitBoundState state) => state.Subscribe(); }
        public sealed class CircuitBoundSingleton { public CircuitBoundSingleton(MeshWeaver.Graph.Test.CircuitBoundState state) => state.Subscribe(); }
        public sealed class TakesACircuitBoundSingleton { public TakesACircuitBoundSingleton(CircuitBoundSingleton inner) { } }
        public class Journal2 : MeshWeaver.Graph.Test.HostedServiceJournalBase { }
        public sealed class ExtraJournaling(MeshWeaver.Graph.Test.HostedServiceJournal journal) : Microsoft.Extensions.Hosting.IHostedService
        {
            public System.Threading.Tasks.Task StartAsync(System.Threading.CancellationToken ct) { journal.Write("start extra v{{version}}"); return System.Threading.Tasks.Task.CompletedTask; }
            public System.Threading.Tasks.Task StopAsync(System.Threading.CancellationToken ct) { journal.Write("stop extra v{{version}}"); return System.Threading.Tasks.Task.CompletedTask; }
        }
        public sealed class ModuleAttribute : MeshWeaver.Mesh.MeshNodeProviderAttribute
        {
            public override System.Collections.Generic.IEnumerable<MeshWeaver.Mesh.MeshNode> Nodes =>
                [new MeshWeaver.Mesh.MeshNode("LiveServicesProbe") { Name = "v{{version}}", NodeType = "Markdown" }
                    .WithGlobalServiceRegistry(services =>
                    {
                        {{(ownedFirst ? "services.AddSingleton<IOwnGreeter, OwnGreeter>();" : "")}}
                        services.AddOptions<GreeterOptions>().Configure(o => o.Version = "v{{version}}");
                        services.AddSingleton<MeshWeaver.Graph.Test.ILiveGreeter, {{greeterClass}}>();
                        services.AddKeyedSingleton<MeshWeaver.Graph.Test.ILiveGreeter, {{greeterClass}}>("keyed-greeter");
                        services.AddKeyedSingleton<IOwnGreeter, OwnGreeter>("own-keyed");
                        {{(extraGreeter ? "services.AddSingleton<MeshWeaver.Graph.Test.ILiveGreeter, " + greeterClass + ">();" : "")}}
                        {{(classService ? "services.AddSingleton<MeshWeaver.Graph.Test.HostedServiceJournalBase, Journal2>();" : "")}}
                        services.AddHostedService<Journaling>();
                        {{(extraHosted ? "services.AddHostedService<ExtraJournaling>();" : "")}}
                        {{(brokenSingleton ? "services.AddSingleton<Unbuildable>();" : "")}}
                        {{(circuitBound ? "services.AddScoped<CircuitDialog>(); services.AddSingleton<CircuitBoundSingleton>(); services.AddSingleton<TakesACircuitBoundSingleton>();" : "")}}
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
