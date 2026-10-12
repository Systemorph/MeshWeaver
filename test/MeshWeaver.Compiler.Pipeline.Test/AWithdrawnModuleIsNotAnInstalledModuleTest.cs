using MeshWeaver.Mesh;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// MeshWeaver#6557 - <b>a module withdrawn at boot is not an installed module.</b> The boot pre-warm
/// sweep resolves <c>InstalledModuleAssembly[]</c> (the compile reference set and the installed-module
/// fingerprint). <c>MeshBuilder</c> registers one TRANSIENT <see cref="InstalledModuleAssembly"/> per module
/// held in its own load context when the module INSTALLS; the module's shared instances are built later, once
/// the root container exists (<c>ModuleContexts.BuildAllBeforePublish</c>), and one that cannot be built is
/// WITHDRAWN: no longer current in the registry, reported as an <see cref="IncompatibleModule"/>.
///
/// <para>The registration made at install time is not tied to that withdrawal. It answers the withdrawn
/// generation for as long as a weak reference to it is alive, and throws
/// <c>Module {name} is no longer held by this mesh's module registry</c> once it is not - the fault that
/// refused readiness on pod memex-portal-deployment-65d6cb7648-97nnd at 2026-10-12T10:46:59Z, 26 seconds
/// after the same pod logged <c>MeshWeaver.Blazor.Radzen ... could not be built before it was published, so it
/// was withdrawn</c> (#6554). Either way the list is wrong: a module nobody serves is listed as installed.</para>
///
/// <para>The shape is the one <see cref="AModuleWhoseSingletonCannotBeBuiltAtBootTest"/> already pins for the
/// withdrawal itself - a real module whose singleton throws, a real mesh, nothing mocked - and the question
/// asked here is the one the pod's sweep asked: <c>GetServices&lt;InstalledModuleAssembly&gt;()</c>.</para>
/// </summary>
public sealed class AWithdrawnModuleIsNotAnInstalledModuleTest(ITestOutputHelper output)
    : ModuleSingletonsBuiltAtBootTestBase(output)
{
    /// <inheritdoc />
    protected override bool Broken => true;

    private string[] InstalledModuleNames() =>
        Mesh.ServiceProvider.GetServices<InstalledModuleAssembly>()
            .Select(m => m.Assembly.GetName().Name ?? string.Empty)
            .ToArray();

    [Fact]
    public void TheWithdrawnModule_IsNotListedAsInstalled_ButStaysReportedAsIncompatible()
    {
        Contexts.Current(Module).Should().BeNull(
            "the module's singleton cannot be built, so it was withdrawn at boot - the precondition");

        var resolved = Record.Exception(() => InstalledModuleNames());

        resolved.Should().BeNull(
            "resolving the installed modules is what the boot pre-warm sweep does, and a withdrawn module must not fault it");
        InstalledModuleNames().Should().NotContain(Module,
            "a module that is not published runs nothing here - listing it hands every dynamic NodeType compile, "
            + "and the installed-module fingerprint, a generation nobody serves");
        Incompatible.Should().ContainSingle(
            "the withdrawal stays reported where every module that failed to install is - nothing is made quieter");
    }
}

/// <summary>
/// The control: the same module with a singleton that builds is published, and IS an installed module that
/// answers its current generation - so a fix cannot pass by emptying the list.
/// </summary>
public sealed class AModuleThatBuildsIsStillAnInstalledModuleTest(ITestOutputHelper output)
    : ModuleSingletonsBuiltAtBootTestBase(output)
{
    /// <inheritdoc />
    protected override bool Broken => false;

    [Fact]
    public void ThePublishedModule_IsListedAsInstalled_WithItsCurrentGeneration()
    {
        var current = Contexts.Current(Module);
        current.Should().NotBeNull("a module whose singletons build is published");

        Mesh.ServiceProvider.GetServices<InstalledModuleAssembly>()
            .Should().Contain(m => ReferenceEquals(m.Assembly, current!.Assembly),
                "an installed module answers the generation the registry holds as current");
        Incompatible.Should().BeEmpty();
    }
}
