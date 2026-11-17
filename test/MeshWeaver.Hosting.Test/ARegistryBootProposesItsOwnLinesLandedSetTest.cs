using System;
using System.IO;
using System.Reactive.Linq;
using System.Text;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 A registry's replica of a new platform line must propose that line's module set from the modules
/// its tree already holds (policy <c>major-lines-run-separately</c>).
///
/// <para><b>The incident.</b> A registry rolled from line 3 to line 4. Its line-4 tree held 40 landed
/// modules — shelved there by publishes — while the mesh stayed on line-4 module set 1, proposed empty
/// before any module landed. Every line-4 replica reported them "landed but in NO proposed module set",
/// its readiness gates (required modules, view packs) refused it, and the roll never completed. Nothing
/// closed the wave: a registry reconciles against no registry, so the reconcile that ends every
/// consumer's boot with a proposal never ran; the default install proposes only after it lands a fresh
/// carrier, and every package was already installed; and the publishes that filled the tree were
/// served by line-3 replicas, which derive a set against their own platform.</para>
///
/// <para><b>The rule pinned here.</b> With no registry configured, the boot of
/// <see cref="RegistryUpdateReconciler"/> proposes this process's own line's set from what its tree
/// holds (checked, idempotent). The negative control is the precondition: landing alone proposes
/// nothing, so before the boot the set does not name the module.</para>
///
/// <para>Real seams: a real mesh with the plugin catalog, the real <see cref="ModuleLandingService"/>
/// landing real assembly bytes into a throwaway module root, and the real reconciler's boot.</para>
/// </summary>
public class ARegistryBootProposesItsOwnLinesLandedSetTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        try { Directory.Delete(moduleRoot, recursive: true); }
        catch (IOException) { /* a temp directory that outlives the test is harmless */ }
        catch (UnauthorizedAccessException) { /* likewise */ }
    }

    private const string AiModule = "MeshWeaver.AI";

    private readonly string moduleRoot =
        Path.Combine(Path.GetTempPath(), "mw-regboot-" + Guid.NewGuid().ToString("N")[..8]);

    private ModuleLandingService? landing;

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddPluginCatalog()
            .ConfigureServices(services => services
                // No registry: this is the registry instance itself.
                .AddSingleton(new PluginCatalogOptions { InstallPreInstalledPackages = false })
                .AddSingleton(_ => landing ??= new ModuleLandingService(baseDirectory: moduleRoot)));

    private ModuleLandingService Landing => Mesh.ServiceProvider.GetRequiredService<ModuleLandingService>();

    [Fact(Timeout = 300_000)]
    public async Task TheBootOfARegistry_ProposesTheModulesItsTreeAlreadyHolds()
    {
        var ct = TestContext.Current.CancellationToken;
        // The modules a publish shelved into this line's tree, before this process booted.
        await Landing.LandModule(
                AiModule,
                [(AiModule + ".dll", File.ReadAllBytes(typeof(BundleReader).Assembly.Location))],
                packagePath: "Plugins/AI",
                version: "4.0.1",
                staticAssets: [("wwwroot/version.txt", Encoding.UTF8.GetBytes("4.0.1"))])
            .Timeout(TestTimeouts.Convergence).Await(ct);
        (ModuleSetStore.Read(Landing.BaseDirectory).Proposed?.Generations.ContainsKey(AiModule) ?? false)
            .Should().BeFalse("precondition: landing alone proposes nothing — without the boot the module stays in no set");

        using var reconciler = new RegistryUpdateReconciler(Mesh, NullLogger<RegistryUpdateReconciler>.Instance);
        await reconciler.StartAsync(ct);
        await reconciler.BootReconciled.Should().Within(TestTimeouts.Convergence)
            .Emit("the boot of a process with no registry ends", ct);

        if (ModuleSetStore.Read(Landing.BaseDirectory).Proposed is not { } proposed)
            throw new Xunit.Sdk.XunitException("the boot closes the wave the tree already holds — no set was proposed");
        proposed.Generations.Keys.Should().Contain(AiModule,
            "a landed module this line's tree holds is in the set every replica of the line adopts");

        // Idempotent: a second boot over the same tree writes no new set.
        using var second = new RegistryUpdateReconciler(Mesh, NullLogger<RegistryUpdateReconciler>.Instance);
        await second.StartAsync(ct);
        await second.BootReconciled.Should().Within(TestTimeouts.Convergence).Emit("the second boot ends", ct);
        (ModuleSetStore.Read(Landing.BaseDirectory).Proposed?.Sequence).Should().Be(proposed.Sequence,
            "a tree already proposed writes nothing");
    }
}
