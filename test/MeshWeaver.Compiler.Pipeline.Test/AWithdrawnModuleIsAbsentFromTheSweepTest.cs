using System.Reactive.Linq;
using MeshWeaver.Compiler;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.ServiceProvider;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// MeshWeaver#6557 — <b>a module withdrawn at boot is ABSENT from the installed-module set, and never
/// faults whoever enumerates it.</b> A held module whose shared instances cannot be built is withdrawn
/// once the root container exists (built before published, MeshWeaver#6542). Its registration used to
/// stay behind as a transient that could only throw "no longer held by this mesh's module registry"
/// (once the boot generation's weak reference was collected) or answer the withdrawn generation (before
/// that); the boot pre-warm sweep enumerates the set, faulted, and the pod refused readiness for one
/// broken module. The set is now one registry the mesh owns (<see cref="InstalledModules"/>), served
/// to every container kind through an ordinary factory — so this holds on the Autofac host AND on a
/// host built with plain MS DI <c>BuildServiceProvider()</c>.
/// </summary>
public sealed class AWithdrawnModuleIsAbsentFromTheSweepTest(ITestOutputHelper output)
    : ModuleSingletonsBuiltAtBootTestBase(output)
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

    /// <inheritdoc />
    protected override bool Broken => true;

    [Fact]
    public async Task OnTheAutofacHost_TheWithdrawnModuleIsAbsent_AndTheSweepCompletes()
    {
        var ct = TestContext.Current.CancellationToken;
        Contexts.Current(Module).Should().BeNull("the precondition: the module was withdrawn at boot");
        Incompatible.Should().ContainSingle("the withdrawal is reported where every failed module is");
        CollectEverything();

        AssertAbsentFromEveryRead(Mesh.ServiceProvider);

        // The sweep itself: the probe and the warm-up both run to completion — neither faults.
        var report = await DynamicTypePreWarmer.ProbeDynamicTypes(Mesh)
            .FirstAsync().Timeout(Budget).Await(ct);
        report.Should().NotBeNull();
        var warmed = await DynamicTypePreWarmer.WarmDynamicTypes(Mesh, betweenTypes: TimeSpan.Zero)
            .ToList().Timeout(Budget).Await(ct);
        warmed.Should().NotContain(o => o.Status == PreWarmStatus.Faulted,
            "a withdrawn module is absent from the sweep, never a fault in it");
    }

    [Fact]
    public void OnAPlainMsDiHost_TheWithdrawnModuleIsAbsent()
    {
        var directory = Path.Combine(Path.GetTempPath(), "withdrawn-msdi-" + Guid.NewGuid().ToString("N"));
        var services = new ServiceCollection();
        var builder = new MeshBuilder(configure => configure(services), new Address("mesh", "msdi"));
        builder.InstallAssemblies(WriteModule(directory, broken: true, circuitBound: false));
        using var provider = services.BuildServiceProvider();

        // Held and listed before the withdrawal — the negative control on this host's own reading.
        provider.GetServices<InstalledModuleAssembly>()
            .Should().Contain(m => m.Assembly.GetName().Name == Module, "a held module is installed until it is withdrawn");

        // What a host does once its root container is built (built before published) — withdraws it.
        foreach (var hook in provider.GetServices<IRootContainerBuilt>())
            hook.Built(provider);
        provider.GetRequiredService<ModuleContexts>().Current(Module)
            .Should().BeNull("the precondition: the module was withdrawn");
        CollectEverything();

        AssertAbsentFromEveryRead(provider);
    }

    private static void AssertAbsentFromEveryRead(IServiceProvider provider)
    {
        var enumerated = Record.Exception(() => provider.GetServices<InstalledModuleAssembly>().ToArray());
        enumerated.Should().BeNull("enumerating the installed modules never faults on a withdrawn one");
        provider.GetServices<InstalledModuleAssembly>()
            .Should().NotContain(m => m.Assembly.GetName().Name == Module, "a withdrawn module is not installed");
        provider.GetRequiredService<InstalledModules>().Current(provider)
            .Should().NotContain(m => m.Assembly.GetName().Name == Module);
        provider.GetRequiredService<InstalledModules>().HeldModuleNames
            .Should().Contain(Module, "it WAS installed at boot — the registry answers it absent, by name");
        var fingerprint = Record.Exception(() => _ = InstalledModulesFingerprint.Live(provider).Hash);
        fingerprint.Should().BeNull("the bake fingerprint the sweep stamps reads the same set");
    }

    // The boot generation was referenced weakly; production collected it before the sweep ran.
    private static void CollectEverything()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}
