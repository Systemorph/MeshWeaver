using System.Reactive.Linq;
using MeshWeaver.Compiler;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
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

        // No host hook is called here: the registry's first resolve runs the boot publication on a
        // host that never ran it (MeshWeaver#6557), and every read of the set resolves it first.
        CollectEverything();
        AssertAbsentFromEveryRead(provider);
        provider.GetRequiredService<ModuleContexts>().Current(Module)
            .Should().BeNull("the precondition: the module was withdrawn");
    }

    /// <summary>
    /// The set is ORDERED as it was declared: a held module keeps the slot it was installed at, between
    /// the members declared before and after it — never "every image-bound member, then every held one"
    /// (first-mount-wins static assets and the list-ordered compile reference surface read this order).
    /// </summary>
    [Fact]
    public void OnAPlainMsDiHost_AHeldModuleKeepsItsInstallPosition()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ordered-msdi-" + Guid.NewGuid().ToString("N"));
        var services = new ServiceCollection();
        var before = typeof(FactAttribute).Assembly;
        var after = typeof(ServiceCollection).Assembly;
        services.AddSingleton(new InstalledModuleAssembly(before));
        var builder = new MeshBuilder(configure => configure(services), new Address("mesh", "ordered"));
        builder.InstallAssemblies(WriteModule(directory, broken: false, circuitBound: false));
        services.AddSingleton(new InstalledModuleAssembly(after));
        using var provider = services.BuildServiceProvider();

        var names = provider.GetServices<InstalledModuleAssembly>()
            .Select(m => m.Assembly)
            .Where(a => a == before || a == after || a.GetName().Name == Module)
            .Select(a => a.GetName().Name)
            .ToArray();
        names.Should().Equal([before.GetName().Name, Module, after.GetName().Name],
            "the held module is answered in the slot it was installed at");
    }

    private static void AssertAbsentFromEveryRead(IServiceProvider provider)
    {
        var enumerated = Record.Exception(() => provider.GetServices<InstalledModuleAssembly>().ToArray());
        enumerated.Should().BeNull("enumerating the installed modules never faults on a withdrawn one — "
            + "neither on its held slot nor on the installed module it registered through its own services");
        provider.GetServices<InstalledModuleAssembly>()
            .Should().NotContain(m => m.Assembly.GetName().Name == Module,
                "a withdrawn module is not installed, and neither is what it contributed");
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
