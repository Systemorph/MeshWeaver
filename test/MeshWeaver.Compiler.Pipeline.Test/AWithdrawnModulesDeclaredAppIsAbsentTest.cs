using System.Reactive.Linq;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Apps;
using MeshWeaver.Mesh;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// #7927 review: <b>a module that declares a launcher tile and is withdrawn at boot takes its tile
/// with it — and nothing else.</b> A held module's platform-class registration is forwarded from the
/// root; once the module has no current generation that forwarder can only throw, and the app
/// directory read the declarations with <c>GetServices&lt;DeclaredApp&gt;()</c> — so one withdrawn
/// module failed EVERY viewer's whole directory (Settings and Inbox too), and no recomputation could
/// recover. <see cref="DeclaredApp"/> is now an absence-aware set
/// (<see cref="AbsentWhenModuleGoneAttribute"/>, <see cref="InstalledModules.CurrentSet{T}"/>): the
/// withdrawn module's member is absent, and the directory serves the rest.
/// </summary>
public sealed class AWithdrawnModulesDeclaredAppIsAbsentTest(ITestOutputHelper output)
    : ModuleSingletonsBuiltAtBootTestBase(output)
{
    /// <inheritdoc />
    protected override bool Broken => true;

    /// <inheritdoc />
    protected override bool DeclaresApp => true;

    [Fact(Timeout = 60000)]
    public async Task TheDirectory_ServesEveryViewer_WithoutTheWithdrawnModulesTile()
    {
        var ct = TestContext.Current.CancellationToken;
        Contexts.Current(Module).Should().BeNull("the precondition: the module was withdrawn at boot");
        WithdrawnModuleReads.CollectEverything();

        var set = Record.Exception(() => Mesh.ServiceProvider.GetRequiredService<InstalledModules>()
            .CurrentSet<DeclaredApp>(Mesh.ServiceProvider).ToArray());
        set.Should().BeNull("reading the declared set never faults on a withdrawn module's member");
        Mesh.ServiceProvider.GetRequiredService<InstalledModules>().CurrentSet<DeclaredApp>(Mesh.ServiceProvider)
            .Should().NotContain(d => d.Id == DeclaredAppId, "a withdrawn module's tile goes with it");

        var list = await Mesh.ServiceProvider.GetRequiredService<AppDirectory>()
            .Compute("withdrawn-viewer")
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        list.Select(e => e.Id).Should().Contain([AppDirectory.SettingsId, AppDirectory.InboxId],
            "one withdrawn module never takes the built-ins down");
        list.Select(e => e.Id).Should().NotContain(DeclaredAppId);
    }
}

/// <summary>The negative control: published, the module's declared tile IS on the launcher.</summary>
public sealed class APublishedModulesDeclaredAppIsATileTest(ITestOutputHelper output)
    : ModuleSingletonsBuiltAtBootTestBase(output)
{
    /// <inheritdoc />
    protected override bool Broken => false;

    /// <inheritdoc />
    protected override bool DeclaresApp => true;

    [Fact(Timeout = 60000)]
    public async Task TheDeclaredTile_IsServedFromTheModulesCurrentGeneration()
    {
        var ct = TestContext.Current.CancellationToken;
        Contexts.Current(Module).Should().NotBeNull();
        Mesh.ServiceProvider.GetRequiredService<InstalledModules>().CurrentSet<DeclaredApp>(Mesh.ServiceProvider)
            .Should().Contain(d => d.Id == DeclaredAppId);

        var list = await Mesh.ServiceProvider.GetRequiredService<AppDirectory>()
            .Compute("published-viewer")
            .Where(l => l.Any(e => e.Id == DeclaredAppId))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        list.Single(e => e.Id == DeclaredAppId).OpenPath.Should().Be("BootBuiltProbe");
    }
}
