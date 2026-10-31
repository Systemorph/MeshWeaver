using System;
using System.Collections.Immutable;
using System.IO;
using System.Reactive.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 Systemorph/MeshWeaver#6192, reopened 2026-10-31 — the installer's dependency floor and the
/// module-set proposal must answer ONE question the same way.
///
/// <para><b>The measured case.</b> On memex.systemorph.com a freshly started replica installed
/// Chat 1.8.0 (<c>requires AI@^1.43.0</c>) at 03:16:12Z while the activation record landed
/// <c>MeshWeaver.AI</c> at 1.42.3. The installer's gate let it through; the proposal, which judges
/// the LANDED activation entry (<see cref="ModuleDependencyFloor.Unmet"/>), then refused every
/// landing wave on every replica: <c>[ModuleSet] NOT proposing the landed module set: 'Chat' 1.8.0
/// requires AI@^1.43.0, but this set loads 'MeshWeaver.AI' (package 'AI') at 1.42.3</c>. The gate
/// read only what the process had LOADED (an image copy can read newer than the landed entry), and
/// a reading that answered nothing or faulted judged nothing at all — so an update the proposal
/// would refuse was installed, and module adoption stalled until the dependency landed.</para>
///
/// <para><b>The rule pinned here.</b> <see cref="PackageDependencyFloorGate.Evaluate"/> judges the
/// candidate against the landed activation entry the proposal judges, as well as against the
/// loaded reading; a reading that answers nothing or faults HOLDS the update (it is never read as
/// "met"). The negative controls: a satisfied requirement still installs, and a dependency with
/// no module anywhere on this deployment is still not judged.</para>
///
/// <para>Real seams: the real <see cref="ModuleLandingService"/> lands real assembly bytes into a
/// throwaway module root, and the proposal's own <see cref="ModuleDependencyFloor.Unmet"/> reads
/// the same activation record. One seam is substituted, as in
/// <c>ADependencyFloorHoldsTheImportTest</c>: the loaded-module reading, because a test process
/// cannot load two builds of one module.</para>
/// </summary>
public class ADependencyUpdateIsJudgedAgainstTheLandedSetTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string Ai = "AI";
    private const string AiModule = "MeshWeaver.AI";
    private const string Requirement = "AI@^1.43.0";

    private readonly string moduleRoot =
        Path.Combine(Path.GetTempPath(), "mw-6192-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly SwitchableLoadedModules loaded = new();
    private ModuleLandingService? landing;

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddPluginCatalog()
            .ConfigureServices(services => services
                .AddSingleton(new PluginCatalogOptions { InstallPreInstalledPackages = false })
                // Registered AFTER the catalog's own, so these are the instances resolved.
                .AddSingleton(_ => landing ??= new ModuleLandingService(baseDirectory: moduleRoot))
                .AddSingleton<ILoadedPackageModules>(loaded));

    private ModuleLandingService Landing => Mesh.ServiceProvider.GetRequiredService<ModuleLandingService>();

    /// <summary>The candidate the registry served: Chat 1.8.0 requiring AI 1.43.</summary>
    private static PackageManifest Chat180 => new()
    {
        Id = "Chat",
        Name = "Chat",
        Kind = PackageKind.NodeRepo,
        TargetPartition = "Chat",
        SourceFolder = "Chat",
        Version = "1.8.0",
        ReleasedVersion = "1.8.0",
        ModuleVersion = "8761a43515c0bac1",
        Module = "MeshWeaver.Blazor.Chat",
        Requires = ["Store@^1.0.0", Requirement],
    };

    /// <summary>A real, loadable managed assembly's bytes — the landing measures its link
    /// requirements, so a stand-in byte array would be refused.</summary>
    private static byte[] RealAssemblyBytes =>
        File.ReadAllBytes(typeof(BundleReader).Assembly.Location);

    /// <summary>Lands one build of AI at <paramref name="version"/> as the registry lane would
    /// (package path <c>Plugins/AI</c>). The version rides a static asset so two versions are two
    /// different contents — the generation leaf is a content address.</summary>
    private async Task LandAi(string version, CancellationToken ct) =>
        await Landing.LandModule(
                AiModule,
                [(AiModule + ".dll", RealAssemblyBytes)],
                packagePath: $"{PackageInstaller.InstalledPartition}/{Ai}",
                version: version,
                staticAssets: [("wwwroot/version.txt", Encoding.UTF8.GetBytes(version))])
            .Timeout(TestTimeouts.Convergence).Await(ct);

    private Task<UnmetPackageDependency?> Judge(PackageManifest candidate, CancellationToken ct) =>
        PackageDependencyFloorGate.Evaluate(Mesh, candidate, logger: null)
            .Should().Within(TestTimeouts.Convergence).Emit("the gate answers exactly once", ct);

    /// <summary>
    /// THE reproduction: the process's loaded reading says AI 1.43.4 (an image copy), the landed
    /// activation entry says 1.42.3. The gate must hold — exactly what the proposal will refuse —
    /// and once a satisfying AI is landed, the same candidate passes (the negative control).
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task ALandedDependencyBelowTheFloor_HoldsTheUpdate_EvenWhenTheLoadedReadingSaysMet()
    {
        var ct = TestContext.Current.CancellationToken;
        await LandAi("1.42.3", ct);
        loaded.Answer(Ai, AiModule, "1.43.4");

        // The proposal's own verdict over the same record — the judge the gate must agree with.
        var activation = await Landing.GetActivation().Should().Within(TestTimeouts.Convergence)
            .Emit("the activation record reads", ct);
        ModuleDependencyFloor.Unmet([Chat180], activation).Should().ContainSingle(
            "precondition: the proposal refuses Chat 1.8.0 over AI 1.42.3 — the incident's log line");

        var unmet = await Judge(Chat180, ct);
        unmet.Should().NotBeNull(
            "an update the module-set proposal would refuse must never be installed: the record would "
            + "take a floor the landed set does not meet, and every landing wave would be refused");
        unmet!.Requirement.Should().Be(Requirement);
        unmet.Module.Should().Be(AiModule);
        unmet.LoadedVersion.Should().Be("1.42.3");
        PackageDependencyFloorGate.IsDependencyHold(PackageDependencyFloorGate.HeldSentence(Chat180, unmet))
            .Should().BeTrue("the hold is recorded as a dependency hold, so a later met pass clears it");

        // ── negative control: a satisfying AI lands, and the same candidate is admitted ─────────
        await LandAi("1.43.4", ct);
        (await Judge(Chat180, ct)).Should().BeNull("a satisfied requirement still installs");
    }

    /// <summary>A loaded reading that ANSWERS NOTHING holds the update — absence of the reading is
    /// not evidence that the requirement is met.</summary>
    [Fact(Timeout = 300_000)]
    public async Task AReadingThatAnswersNothing_HoldsTheUpdate()
    {
        var ct = TestContext.Current.CancellationToken;
        loaded.AnswerNothing();

        var unmet = await Judge(Chat180, ct);
        unmet.Should().NotBeNull("no reading is never 'met'");
        PackageDependencyFloorGate.IsDependencyHold(PackageDependencyFloorGate.HeldSentence(Chat180, unmet!))
            .Should().BeTrue();
        PackageDependencyFloorGate.HeldSentence(Chat180, unmet!).Should().Contain("could not be judged");
    }

    /// <summary>A loaded reading that FAULTS holds the update too, naming the fault.</summary>
    [Fact(Timeout = 300_000)]
    public async Task AReadingThatFaults_HoldsTheUpdate()
    {
        var ct = TestContext.Current.CancellationToken;
        loaded.Fault("the module volume did not answer");

        var unmet = await Judge(Chat180, ct);
        unmet.Should().NotBeNull("a faulted reading is never 'met'");
        PackageDependencyFloorGate.HeldSentence(Chat180, unmet!).Should()
            .Contain("could not be judged").And.Contain("the module volume did not answer");
    }

    /// <summary>
    /// Negative controls of the hold: a met requirement passes, and a dependency with no module
    /// loaded or landed anywhere here (a content-only package) is still NOT judged. A gate that
    /// held everything fails here.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task AMetRequirement_AndADependencyWithNoModuleHere_AreAdmitted()
    {
        var ct = TestContext.Current.CancellationToken;
        loaded.Answer(Ai, AiModule, "1.43.4");
        (await Judge(Chat180, ct)).Should().BeNull("the loaded AI satisfies the floor and nothing is landed");

        loaded.Answer("SomeOtherPackage", "MeshWeaver.Other", "0.0.1", replace: true);
        (await Judge(Chat180, ct)).Should().BeNull(
            "a dependency with no module loaded or landed here is not judged — absent is not 'unmet'");
    }

    /// <summary>
    /// The registry MODULE lane asks the same question before it lands a dependent's module: on
    /// memex.systemorph.com Chat's module 1.8.2 landed at 08:22Z beside AI 1.42.3 while Chat's
    /// content update was held. A served listing whose requirement the landed set does not meet is
    /// declined; an unread listing is declined; a met one is not.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task TheRegistryModuleLane_DeclinesADependentWhoseFloorIsUnmet()
    {
        var ct = TestContext.Current.CancellationToken;
        await LandAi("1.42.3", ct);
        loaded.Answer(Ai, AiModule, "1.42.3");

        var declined = await RegistryUpdateReconciler.DependencyDecline(Mesh, Chat180, logger: null)
            .Should().Within(TestTimeouts.Convergence).Emit("the decision answers once", ct);
        declined.Should().NotBeNull().And.Contain(Requirement).And.Contain("1.42.3");

        var unread = await RegistryUpdateReconciler.DependencyDecline(Mesh, served: null, logger: null)
            .Should().Within(TestTimeouts.Convergence).Emit("the decision answers once", ct);
        unread.Should().NotBeNull("a listing that could not be read is never a licence to land");

        await LandAi("1.43.4", ct);
        loaded.Answer(Ai, AiModule, "1.43.4");
        (await RegistryUpdateReconciler.DependencyDecline(Mesh, Chat180, logger: null)
                .Should().Within(TestTimeouts.Convergence).Emit("the decision answers once", ct))
            .Should().BeNull("the negative control: a met floor lands the module");
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        try
        {
            if (Directory.Exists(moduleRoot))
                Directory.Delete(moduleRoot, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory no later run reads; a locked file only leaves litter.
        }
    }

    /// <summary>The loaded-module reading, set by the test: an answer, no answer, or a fault.</summary>
    private sealed class SwitchableLoadedModules : ILoadedPackageModules
    {
        private ImmutableDictionary<string, LoadedPackageModule> answer =
            ImmutableDictionary<string, LoadedPackageModule>.Empty;

        private IObservable<ImmutableDictionary<string, LoadedPackageModule>>? reading;

        public void Answer(string package, string module, string version, bool replace = false)
        {
            answer = (replace ? ImmutableDictionary<string, LoadedPackageModule>.Empty : answer)
                .SetItem(package, new LoadedPackageModule(module, version));
            reading = null;
        }

        public void AnswerNothing() =>
            reading = Observable.Empty<ImmutableDictionary<string, LoadedPackageModule>>();

        public void Fault(string message) =>
            reading = Observable.Throw<ImmutableDictionary<string, LoadedPackageModule>>(new IOException(message));

        public IObservable<ImmutableDictionary<string, LoadedPackageModule>> Read() =>
            Observable.Defer(() => reading ?? Observable.Return(answer));
    }
}
