using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// MeshWeaver#6557, the two residual gaps of <see cref="AWithdrawnModuleIsAbsentFromTheSweepTest"/>'s class,
/// each pinned on BOTH host kinds (the Autofac root every portal builds, and a plain MS DI
/// <c>BuildServiceProvider()</c> host):
/// <list type="number">
/// <item><b>A withdrawal is reported on every host.</b> The <see cref="IncompatibleModule"/> record of a
/// module withdrawn at boot reached consumers only through an Autofac registration source, and a plain MS
/// DI host never ran the publication that withdraws a failing module at all.</item>
/// <item><b>No forwarder of a withdrawn module is enumerated.</b> A held module that registers further
/// <see cref="InstalledModuleAssembly"/> instances itself (MeshWeaver.Testing's xunit closure) was served
/// through a root forwarder that throws once the module is withdrawn — faulting every enumeration of the
/// installed-module set, the boot pre-warm sweep's included.</item>
/// </list>
/// The published twins are the negative controls: the same module, built cleanly, lists its shipped
/// reference and no incompatible record.
/// </summary>
internal static class WithdrawnModuleReads
{
    /// <summary>Installed modules named <paramref name="name"/>, read without catching anything.</summary>
    public static InstalledModuleAssembly[] Installed(IServiceProvider provider, string name) =>
        provider.GetServices<InstalledModuleAssembly>().Where(m => m.Assembly.GetName().Name == name).ToArray();

    /// <summary>Incompatible records of <paramref name="module"/>.</summary>
    public static IncompatibleModule[] Incompatible(IServiceProvider provider, string module) =>
        provider.GetServices<IncompatibleModule>().Where(m => m.Name == module).ToArray();

    /// <summary>A plain MS DI host over the test module, which ships a reference of its own.</summary>
    public static Microsoft.Extensions.DependencyInjection.ServiceProvider PlainMsDiHost(bool broken)
    {
        var directory = Path.Combine(Path.GetTempPath(), "withdrawn-forwarder-msdi-" + Guid.NewGuid().ToString("N"));
        var services = new ServiceCollection();
        var builder = new MeshBuilder(configure => configure(services), new Address("mesh", "msdi"));
        builder.InstallAssemblies(ModuleSingletonsBuiltAtBootTestBase.WriteModule(
            directory, broken, circuitBound: false, shipsReferences: true));
        return services.BuildServiceProvider();
    }

    /// <summary>Collects the withdrawn module's boot generation, as production has by the time the
    /// sweep runs.</summary>
    public static void CollectEverything()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}

/// <summary>The Autofac host: a withdrawn module that ships a reference is reported, and its reference is
/// absent rather than a forwarder that throws.</summary>
public sealed class AWithdrawnModuleLeavesNoForwarderBehindTest(ITestOutputHelper output)
    : ModuleSingletonsBuiltAtBootTestBase(output)
{
    /// <inheritdoc />
    protected override bool Broken => true;

    /// <inheritdoc />
    protected override bool ShipsReferences => true;

    [Fact]
    public void OnTheAutofacHost_TheShippedReferenceIsAbsent_AndTheWithdrawalIsReported()
    {
        Contexts.Current(Module).Should().BeNull("the precondition: the module was withdrawn at boot");
        WithdrawnModuleReads.CollectEverything();

        var enumerated = Record.Exception(() => Mesh.ServiceProvider.GetServices<InstalledModuleAssembly>().ToArray());
        enumerated.Should().BeNull("no forwarder of a withdrawn module is ever enumerated");
        WithdrawnModuleReads.Installed(Mesh.ServiceProvider, ShippedReferenceName)
            .Should().BeEmpty("the reference a withdrawn module ships goes with it");
        WithdrawnModuleReads.Installed(Mesh.ServiceProvider, Module).Should().BeEmpty();
        WithdrawnModuleReads.Incompatible(Mesh.ServiceProvider, Module)
            .Should().ContainSingle("the withdrawal is reported where every module that failed to install is");
    }
}

/// <summary>The negative control on the Autofac host: published, the module's shipped reference IS installed.</summary>
public sealed class APublishedModuleListsTheReferenceItShipsTest(ITestOutputHelper output)
    : ModuleSingletonsBuiltAtBootTestBase(output)
{
    /// <inheritdoc />
    protected override bool Broken => false;

    /// <inheritdoc />
    protected override bool ShipsReferences => true;

    [Fact]
    public void OnTheAutofacHost_TheShippedReferenceIsInstalled()
    {
        Contexts.Current(Module).Should().NotBeNull();
        WithdrawnModuleReads.Installed(Mesh.ServiceProvider, ShippedReferenceName)
            .Should().ContainSingle("a held module's own InstalledModuleAssembly is served while the registry holds it");
        WithdrawnModuleReads.Installed(Mesh.ServiceProvider, Module).Should().ContainSingle();
        WithdrawnModuleReads.Incompatible(Mesh.ServiceProvider, Module).Should().BeEmpty();
    }
}

/// <summary>The plain MS DI host — the same readings, with no host hook called by the test.</summary>
public sealed class AWithdrawnModuleOnAPlainMsDiHostTest
{
    private const string Module = ModuleSingletonsBuiltAtBootTestBase.ModuleName;
    private const string ShippedReference = ModuleSingletonsBuiltAtBootTestBase.ShippedReferenceName;

    [Fact]
    public void OnAPlainMsDiHost_TheModuleIsWithdrawnAndReported_AndItsShippedReferenceIsAbsent()
    {
        using var provider = WithdrawnModuleReads.PlainMsDiHost(broken: true);

        // The report is read FIRST, before anything else resolves the module registry: the host must
        // withdraw and report on its own, with nobody calling a hook for it.
        WithdrawnModuleReads.Incompatible(provider, Module)
            .Should().ContainSingle("a plain MS DI host reports a withdrawal exactly as the Autofac host does")
            .Which.Error.Should().Contain("could not be built before it was published");
        provider.GetRequiredService<ModuleContexts>().Current(Module)
            .Should().BeNull("the host withdrew the module whose shared instances could not be built");
        WithdrawnModuleReads.CollectEverything();

        var enumerated = Record.Exception(() => provider.GetServices<InstalledModuleAssembly>().ToArray());
        enumerated.Should().BeNull("no forwarder of a withdrawn module is ever enumerated");
        WithdrawnModuleReads.Installed(provider, ShippedReference).Should().BeEmpty();
        WithdrawnModuleReads.Installed(provider, Module).Should().BeEmpty();
    }

    [Fact]
    public void OnAPlainMsDiHost_APublishedModuleListsItsShippedReference_AndNoRecord()
    {
        using var provider = WithdrawnModuleReads.PlainMsDiHost(broken: false);

        WithdrawnModuleReads.Incompatible(provider, Module).Should().BeEmpty();
        provider.GetRequiredService<ModuleContexts>().Current(Module).Should().NotBeNull();
        WithdrawnModuleReads.Installed(provider, ShippedReference).Should().ContainSingle();
        WithdrawnModuleReads.Installed(provider, Module).Should().ContainSingle();
    }
}
