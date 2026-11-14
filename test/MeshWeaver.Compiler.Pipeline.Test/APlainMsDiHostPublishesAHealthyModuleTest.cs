using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>A plain MS DI host publishes a HEALTHY module at boot</b> (Doc/Architecture/Platform4Net11).
/// On a host built with plain <c>BuildServiceProvider()</c>, the registry's boot publication runs inside
/// the registry's own singleton factory (<see cref="ModuleContexts.PublishOnce"/>, MeshWeaver#6557). If
/// building a module's container resolves the registry from the root there, it asks for a singleton
/// that is still under construction: Microsoft.Extensions.DependencyInjection 11 refuses that as a
/// circular dependency (10 answered it), the build fails, and every healthy module is withdrawn at
/// boot. The root-container hosts never reach that path (they claim the publication earlier, outside
/// the factory), so only this host shape can see the regression.
///
/// <para><b>Negative control, measured:</b> with <c>ModuleServices.Scope</c> resolving
/// <c>root.GetService&lt;ModuleContexts&gt;()</c> again instead of using the registry it is handed, this
/// test fails (the healthy module is withdrawn, its probe refused). The other tests of this assembly that
/// go through <c>BuildBeforePublish</c> all pass under that same regression, which is why this test exists.</para>
/// </summary>
public sealed class APlainMsDiHostPublishesAHealthyModuleTest(ITestOutputHelper output)
    : ModuleSingletonsBuiltAtBootTestBase(output)
{
    /// <inheritdoc />
    protected override bool Broken => false;

    [Fact]
    public void OnAPlainMsDiHost_AHealthyModuleIsPublishedAndServes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "healthy-msdi-" + Guid.NewGuid().ToString("N"));
        var services = new ServiceCollection();
        var builder = new MeshBuilder(configure => configure(services), new Address("mesh", "healthy-msdi"));
        builder.InstallAssemblies(WriteModule(directory, broken: false, circuitBound: false));
        using var provider = services.BuildServiceProvider();

        // The registry's first resolve runs the boot publication from inside its own factory — the
        // re-entrant path MS DI 11 refuses when a module's build asks the root for the registry.
        var contexts = provider.GetRequiredService<ModuleContexts>();

        provider.GetServices<IncompatibleModule>().Where(m => m.Name == Module)
            .Should().BeEmpty("a healthy module is never reported as failing to install");
        contexts.Current(Module).Should().NotBeNull("a module whose shared instances build is published at boot");
        provider.GetRequiredService<IBootBuiltProbe>().Answer()
            .Should().Be("boot-built", "the root's forwarder reaches the published generation");
    }
}
