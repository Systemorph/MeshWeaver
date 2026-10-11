using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>MeshWeaver#6192 - an unmet dependency floor (OpenAI 1.5.9 requires AI@^1.23.0 beside AI 1.21.1)
/// holds the DEPENDENT back at its previous generation instead of refusing the whole landing wave.</summary>
public sealed class ModuleSetHoldBackTest : IDisposable
{
    private const string Ai = "MeshWeaver.AI";
    private const string OpenAi = "MeshWeaver.AI.OpenAI";
    private const string Mail = "MeshWeaver.Mail.Test";

    private readonly string root = Path.Combine(Path.GetTempPath(), "mw-holdback-" + Guid.NewGuid().ToString("N"));
    private readonly ModuleLandingService landing;

    public ModuleSetHoldBackTest()
    {
        Directory.CreateDirectory(root);
        landing = new ModuleLandingService(baseDirectory: root);
    }

    public void Dispose()
    {
        landing.Dispose();
        try { Directory.Delete(root, recursive: true); }
        catch { /* temp cleanup is the OS's problem */ }
    }

    [Fact]
    public async Task Proposal_HoldsBackOnlyTheUnmetDependent_AndProposesTheRest()
    {
        await Land(Ai, "AI", "1.21.1");
        await Land(OpenAi, "OpenAI", "1.5.8");
        await Land(OpenAi, "OpenAI", "1.5.9");
        await Land(Mail, "Mail", "2.0.0");
        var entries = (await landing.GetActivation().Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken)).Entries;
        var openAi = entries.Single(e => e.Name == OpenAi);
        Assert.NotNull(openAi.PreviousDirectory);

        var proposed = await Propose(Installed());

        Assert.NotNull(proposed);
        Assert.Equal(openAi.PreviousDirectory, proposed!.Generations[OpenAi]);
        Assert.Equal(entries.Single(e => e.Name == Ai).Directory, proposed.Generations[Ai]);
        Assert.Equal(entries.Single(e => e.Name == Mail).Directory, proposed.Generations[Mail]);
        Assert.Equal(proposed.Id, ModuleSetStore.Read(root).Proposed!.Id);
    }

    [Fact]
    public async Task Proposal_IsWrittenOnce_WhenTheHeldSetIsAlreadyProposed()
    {
        await Land(Ai, "AI", "1.21.1");
        await Land(OpenAi, "OpenAI", "1.5.8");
        await Land(OpenAi, "OpenAI", "1.5.9");
        var first = await Propose(Installed());
        var second = await Propose(Installed());

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.Equal(first!.Sequence, ModuleSetStore.Read(root).Proposed!.Sequence);
    }

    [Fact]
    public async Task Proposal_StillRefusesTheWave_WhenTheUnmetDependentHasNoPreviousGeneration()
    {
        await Land(Ai, "AI", "1.21.1");
        await Land(OpenAi, "OpenAI", "1.5.9");

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(async () => await Propose(Installed()));

        Assert.Contains("AI@^1.23.0", refusal.Message);
        Assert.Contains("1.21.1", refusal.Message);
        Assert.Null(ModuleSetStore.Read(root).Proposed);
    }

    [Fact]
    public async Task Proposal_Refuses_WhenHoldingTheDependentBackWouldBreakAnotherRequirement()
    {
        await Land(Ai, "AI", "1.21.1");
        await Land(OpenAi, "OpenAI", "1.5.8");
        await Land(OpenAi, "OpenAI", "1.5.9");
        var installed = Installed().Add(
            new PackageManifest { Id = "Extra", ReleasedVersion = "0.1.0", Requires = ["OpenAI@^1.5.9"] });

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(async () => await Propose(installed));

        Assert.Contains("OpenAI@^1.5.9", refusal.Message);
        Assert.Contains("1.5.8", refusal.Message);
        Assert.Null(ModuleSetStore.Read(root).Proposed);
    }

    private static ImmutableList<PackageManifest> Installed() =>
    [
        new PackageManifest { Id = "AI", ReleasedVersion = "1.21.1", Module = Ai },
        new PackageManifest { Id = "OpenAI", ReleasedVersion = "1.5.9", Module = OpenAi, Requires = ["AI@^1.23.0"] },
        new PackageManifest { Id = "Mail", ReleasedVersion = "2.0.0", Module = Mail },
    ];

    private async Task<ModuleSet?> Propose(ImmutableList<PackageManifest> installed) =>
        await landing.ProposeCheckedModuleSet(installed)
            .Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);

    /// <summary>Lands one module generation; the version marker is a static asset so two landings of one
    /// module carry different bytes and therefore different content-addressed generations.</summary>
    private async Task Land(string module, string package, string version) =>
        await landing.LandModule(
                module,
                [(module + ".dll", File.ReadAllBytes(typeof(BundleReader).Assembly.Location))],
                packagePath: "Plugins/" + package,
                version: version,
                staticAssets: [("version.txt", Encoding.UTF8.GetBytes(version))])
            .Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);
}