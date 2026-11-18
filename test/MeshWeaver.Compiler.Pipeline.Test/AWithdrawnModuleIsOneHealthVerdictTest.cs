using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 A module WITHDRAWN at boot is ONE named Degraded health verdict — never a duplicate registration that
/// stops the portal.
///
/// <para><b>The incident.</b> A v4 build portal crash-looped (exit 139, 18 restarts) on every boot: its dump
/// showed <c>ArgumentException: Duplicate health checks were registered with the name(s):
/// MeshWeaver.SelfUpdate.Aks, MeshWeaver.SelfUpdate.Aks</c> thrown out of <c>MapHealthChecks</c>. The module's
/// shared instances could not be built, so boot withdrew it (<c>ModuleContexts.BuildAllBeforePublish</c>); the
/// module makes several health-check setups, the root holds one <c>ModuleHealthCheckOptionsForwarder</c> per
/// setup, and every one of them added the "module is not held" verdict under the module's name.</para>
///
/// <para>The module here has the same shape: a singleton whose constructor throws (so boot withdraws it) and
/// two health-check setups (two <c>AddHealthChecks().AddCheck</c> calls). Negative control: without the
/// once-per-module guard the health-check service throws exactly the duplicate-name exception.</para>
/// </summary>
public sealed class AWithdrawnModuleIsOneHealthVerdictTest : MonolithMeshTestBase
{
    private const string Module = "MeshWeaver.Test.WithdrawnHealth";
    private readonly string root = Path.Combine(Path.GetTempPath(), "withdrawn-health-" + Guid.NewGuid().ToString("N"));

    public AWithdrawnModuleIsOneHealthVerdictTest(ITestOutputHelper output) : base(output)
    {
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { /* a temp directory that outlives the test is harmless */ }
        catch (UnauthorizedAccessException) { /* likewise */ }
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder)
            .ConfigureServices(s => { s.AddHealthChecks(); return s; })
            .InstallAssemblies(Write());

    [Fact(Timeout = 180_000)]
    public async Task AModuleWithdrawnAtBoot_WithSeveralHealthSetups_IsOneNamedDegradedVerdict()
    {
        var ct = TestContext.Current.CancellationToken;
        Mesh.ServiceProvider.GetRequiredService<ModuleContexts>().Current(Module).Should()
            .BeNull("precondition: boot withdrew the module, because its shared instances could not be built");

        var options = Mesh.ServiceProvider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;
        options.Registrations.Count(r => r.Name == Module).Should().Be(1,
            "one verdict per withdrawn module, whatever number of health-check setups it made");

        var report = await Mesh.ServiceProvider.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(r => r.Name == Module, ct);
        report.Entries[Module].Status.Should().Be(HealthStatus.Degraded);
        report.Entries[Module].Description.Should().Contain("withdrawn");
    }

    private const string Source = """
        using Microsoft.Extensions.DependencyInjection;
        using Microsoft.Extensions.Diagnostics.HealthChecks;
        [assembly: MeshWeaver.Test.WithdrawnHealth.Module]
        namespace MeshWeaver.Test.WithdrawnHealth;
        public sealed class Boom { public Boom() => throw new System.InvalidOperationException("cannot be built"); }
        public sealed class ProbeCheck : IHealthCheck
        {
            public System.Threading.Tasks.Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, System.Threading.CancellationToken ct = default)
                => System.Threading.Tasks.Task.FromResult(HealthCheckResult.Healthy());
        }
        public sealed class ModuleAttribute : MeshWeaver.Mesh.MeshNodeProviderAttribute
        {
            public override System.Collections.Generic.IEnumerable<MeshWeaver.Mesh.MeshNode> Nodes =>
                [new MeshWeaver.Mesh.MeshNode("WithdrawnHealthProbe") { Name = "probe", NodeType = "Markdown" }
                    .WithGlobalServiceRegistry(services =>
                    {
                        services.AddSingleton<Boom>();
                        services.AddHealthChecks().AddCheck<ProbeCheck>("withdrawn_probe_a");
                        services.AddHealthChecks().AddCheck<ProbeCheck>("withdrawn_probe_b");
                        return services;
                    })];
        }
        """;

    private string Write()
    {
        var references = PlatformReferences.Platform()
            .Add(MetadataReference.CreateFromFile(typeof(IHealthCheck).Assembly.Location))
            .Add(MetadataReference.CreateFromFile(typeof(HealthCheckServiceOptions).Assembly.Location));
        var compilation = CSharpCompilation.Create(Module, [CSharpSyntaxTree.ParseText(Source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
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
