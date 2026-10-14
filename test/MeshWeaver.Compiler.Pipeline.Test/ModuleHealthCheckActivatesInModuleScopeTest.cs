using System.Reactive.Linq;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>A module's health check activates in the MODULE's scope</b> — the regression that kept every
/// control image out of acceptance from core #6127 on: <c>/health</c> answered 500 with
/// <c>Unable to resolve service for type 'MeshWeaver.SelfUpdate.Aks.FleetWatchHeartbeat' while attempting
/// to activate 'MeshWeaver.SelfUpdate.Aks.FleetWatchHealthCheck'</c>.
///
/// <para>The shape is the shipped one (MeshWeaver.SelfUpdate.Aks): the module registers a singleton of a
/// type it declares, a hosted service that resolves it, and <c>AddHealthChecks().AddCheck&lt;TCheck&gt;</c>
/// whose check takes that singleton in its constructor. Since #6127 the module's own types live only in
/// its scope; the check's <see cref="HealthCheckRegistration"/> is forwarded to the root and the ROOT's
/// <c>HealthCheckService</c> calls its factory with a root provider — which cannot see the module-owned
/// dependency. The host (here: the test mesh) has its own <c>AddHealthChecks()</c>, exactly as the portal
/// does, so the root's health-check service is the one that activates the check.</para>
///
/// <para>Negative control: <c>ModuleServiceForwarding.AddForwarders</c> without the
/// <c>HealthCheckServiceOptions</c> case — the factory then throws the measured exception.</para>
/// </summary>
public sealed class ModuleHealthCheckActivatesInModuleScopeTest : MonolithMeshTestBase
{
    private const string Module = "MeshWeaver.Test.LiveHealth";
    private readonly string root = Path.Combine(Path.GetTempPath(), "live-health-" + Guid.NewGuid().ToString("N"));

    public ModuleHealthCheckActivatesInModuleScopeTest(ITestOutputHelper output) : base(output)
    {
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder)
            .ConfigureServices(s => { s.AddHealthChecks(); return s; })
            .InstallAssemblies(Write("g1", 1));

    [Fact]
    public void AModuleHealthCheck_WhoseDependencyTheModuleOwns_IsActivatedByTheRootHealthService()
    {
        var generation = Mesh.ServiceProvider.GetRequiredService<ModuleContexts>().Current(Module);
        generation.Should().NotBeNull("the module must be held by the module registry");
        generation!.Services.Should().NotBeNull("its root services are served from a scope of its own — the #6127 conversion under test");

        var options = Mesh.ServiceProvider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;
        var registration = options.Registrations.SingleOrDefault(r => r.Name == "live_health_probe");
        registration.Should().NotBeNull("the module's AddCheck must reach the ROOT's health-check options");

        // Exactly what DefaultHealthCheckService does: the factory, called with the ROOT's provider.
        var check = registration!.Factory(Mesh.ServiceProvider);

        check.Should().NotBeNull();
        check.GetType().FullName.Should().Be($"{Module}.ProbeCheck",
            "the check must be activated — with the module-owned beat injected from the MODULE's scope");
        var beatOfCheck = check.GetType().GetProperty("BeatId")!.GetValue(check);
        var moduleBeat = Mesh.ServiceProvider.GetRequiredService<ModuleContexts>().ModuleScope(Module)!
            .GetRequiredService(generation.Assembly.GetType($"{Module}.Beat")!);
        beatOfCheck.Should().Be(moduleBeat.GetType().GetProperty("Id")!.GetValue(moduleBeat),
            "ONE instance: the check reads the same singleton the module's hosted service feeds, not a second one");
    }

    private const string CheckName = "live_health_probe";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);

    private ModuleContexts Contexts => Mesh.ServiceProvider.GetRequiredService<ModuleContexts>();

    /// <summary>Runs the module's check exactly as <c>/health</c> does: the ROOT's health-check service.</summary>
    private Task<HealthReport> Probe(CancellationToken ct) =>
        Mesh.ServiceProvider.GetRequiredService<HealthCheckService>().CheckHealthAsync(r => r.Name == CheckName, ct);

    /// <summary>
    /// 🚨 MeshWeaver#6766 — after a LIVE SWAP the check still activates, from the NEW generation's
    /// container. The root's health-check options are built ONCE, while generation 1 is current; the
    /// factory they captured used to be generation 1's <c>GetServiceOrCreateInstance&lt;ProbeCheck&gt;</c>,
    /// rebound only to the CURRENT scope — so after the swap generation 2's container was asked for
    /// generation 1's <c>Beat</c> type, which it does not hold. Measured on a portal as
    /// <c>Unable to resolve service for type 'MeshWeaver.AI.ProviderCatalogConvergence' while attempting to
    /// activate 'MeshWeaver.AI.ProviderCatalogHealthCheck'</c> out of every <c>/health</c>.
    ///
    /// <para>Negative control: the forwarder before this change (factory captured at options build, only
    /// the provider rebound) — this test then throws exactly that shape for <c>Beat</c>/<c>ProbeCheck</c>.</para>
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AfterALiveSwap_TheModuleHealthCheck_ActivatesFromTheNewGenerationsContainer()
    {
        var ct = TestContext.Current.CancellationToken;
        var before = await Probe(ct);
        before.Entries[CheckName].Status.Should().Be(HealthStatus.Healthy, before.Entries[CheckName].Description ?? "");
        before.Entries[CheckName].Description.Should().StartWith("v1:", "the root's options were built while generation 1 was current");

        var outcome = await Mesh.ServiceProvider.GetRequiredService<ModuleLiveUpdater>()
            .Swap(Write("g2", 2), "test: #6766 health check across a live swap").Timeout(Budget).Await(ct);
        outcome.Kind.Should().Be(ModuleSwapKind.Live, outcome.Reason);

        var after = await Probe(ct);
        after.Entries[CheckName].Status.Should().Be(HealthStatus.Healthy, after.Entries[CheckName].Description ?? "");
        after.Entries[CheckName].Description.Should().StartWith("v2:",
            "the check must be generation 2's, activated in generation 2's container with generation 2's Beat");
    }

    /// <summary>
    /// MeshWeaver#6766 — a check whose module has been RETIRED (a live uninstall) is a NAMED Degraded
    /// verdict on <c>/health</c>, never an exception out of the health middleware. Negative control: the
    /// forwarder before this change throws <c>Module … holds no service scope</c> out of
    /// <c>CheckHealthAsync</c>.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AfterTheModuleIsRetired_ItsHealthCheck_IsANamedDegradedVerdict()
    {
        var ct = TestContext.Current.CancellationToken;
        (await Probe(ct)).Entries[CheckName].Status.Should().Be(HealthStatus.Healthy);

        var generation = Contexts.Current(Module) ?? throw new Xunit.Sdk.XunitException($"{Module} is not held");
        await Contexts.Retire(generation, Budget).Timeout(Budget).Await(ct);

        var report = await Probe(ct);
        report.Entries[CheckName].Status.Should().Be(HealthStatus.Degraded);
        report.Entries[CheckName].Description.Should().Contain(Module).And.Contain(CheckName)
            .And.Contain("no longer held", "the verdict names the module, the check and why it cannot run");
    }

    private static string Source(int version) => $$"""
        using Microsoft.Extensions.DependencyInjection;
        using Microsoft.Extensions.Diagnostics.HealthChecks;
        [assembly: MeshWeaver.Test.LiveHealth.Module]
        namespace MeshWeaver.Test.LiveHealth;
        public sealed class Beat { public string Id { get; } = System.Guid.NewGuid().ToString("N"); }
        public sealed class ProbeCheck(Beat beat) : IHealthCheck
        {
            public string BeatId => beat.Id;
            public System.Threading.Tasks.Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, System.Threading.CancellationToken ct = default)
                => System.Threading.Tasks.Task.FromResult(HealthCheckResult.Healthy("v{{version}}:" + beat.Id));
        }
        public sealed class ModuleAttribute : MeshWeaver.Mesh.MeshNodeProviderAttribute
        {
            public override System.Collections.Generic.IEnumerable<MeshWeaver.Mesh.MeshNode> Nodes =>
                [new MeshWeaver.Mesh.MeshNode("LiveHealthProbe") { Name = "probe", NodeType = "Markdown" }
                    .WithGlobalServiceRegistry(services =>
                    {
                        services.AddSingleton<Beat>();
                        services.AddHealthChecks().AddCheck<ProbeCheck>("live_health_probe");
                        return services;
                    })];
        }
        """;

    private string Write(string generation, int version)
    {
        var references = PlatformReferences.Platform()
            .Add(MetadataReference.CreateFromFile(typeof(IHealthCheck).Assembly.Location))
            .Add(MetadataReference.CreateFromFile(typeof(HealthCheckServiceOptions).Assembly.Location));
        var compilation = CSharpCompilation.Create(Module, [CSharpSyntaxTree.ParseText(Source(version))], references,
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
