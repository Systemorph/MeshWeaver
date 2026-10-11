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

/// <summary>
/// MeshWeaver#6192 - an unmet dependency floor (OpenAI requires AI@^1.23.0 beside AI 1.21.1) holds the DEPENDENT back
/// at the generation the mesh's last ACCEPTED (adopted) module set runs, and the rest of the wave advances. The cases
/// that hold first propose and adopt a coherent set: that set, not the immediately displaced landing, is what is known
/// to have run.
/// </summary>
public sealed class ModuleSetHoldBackTest : IDisposable
{
    private const string Ai = "MeshWeaver.AI";
    private const string OpenAi = "MeshWeaver.AI.OpenAI";
    private const string Mail = "MeshWeaver.HoldBack.Mail";

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
        catch { /* temp cleanup is the OS's problem, never a test failure */ }
    }

    /// <summary>THE repro: accepted OpenAI 1.5.7 + AI 1.21.1, then a wave landing OpenAI 1.5.8 and a wave landing
    /// OpenAI 1.5.9, both requiring AI@^1.23.0. Only the ACCEPTED 1.5.7 is held - never the unaccepted 1.5.8 the
    /// displaced landing names - while the unrelated module advances, and a repeat wave writes nothing. Fails on main,
    /// which refuses the whole wave.</summary>
    [Fact]
    public async Task Proposal_HoldsTheDependentAtTheAcceptedGeneration_AndAdvancesTheRest()
    {
        var accepted = await EstablishAcceptedSet();

        await Land(OpenAi, "OpenAI", "1.5.8");
        var unaccepted = Head(OpenAi);
        // Nothing else moved: the held set IS the accepted set, so no record is written.
        Assert.Null(await Propose(Installed("1.5.8", "AI@^1.23.0")));

        await Land(OpenAi, "OpenAI", "1.5.9");
        await Land(Mail, "Mail", "2.1.0");
        var proposed = await Propose(Installed("1.5.9", "AI@^1.23.0", mail: "2.1.0"));

        Assert.NotNull(proposed);
        Assert.Equal(accepted.Generations[OpenAi], proposed!.Generations[OpenAi]);
        Assert.NotEqual(unaccepted, proposed.Generations[OpenAi]);
        Assert.NotEqual(Head(OpenAi), proposed.Generations[OpenAi]);
        Assert.Equal(accepted.Generations[Ai], proposed.Generations[Ai]);
        Assert.Equal(Head(Mail), proposed.Generations[Mail]);
        Assert.NotEqual(accepted.Generations[Mail], proposed.Generations[Mail]);
        Assert.Equal(proposed.Id, ModuleSetStore.Read(root).Proposed!.Id);

        Assert.Null(await Propose(Installed("1.5.9", "AI@^1.23.0", mail: "2.1.0")));
        Assert.Equal(proposed.Id, ModuleSetStore.Read(root).Proposed!.Id);
    }

    /// <summary>A proposal no replica has booted onto is not accepted: the wave is refused, nothing new is proposed.</summary>
    [Fact]
    public async Task Proposal_RefusesTheWave_WhenNoProposalWasEverAdopted()
    {
        await Land(Ai, "AI", "1.21.1");
        await Land(OpenAi, "OpenAI", "1.5.7");
        await Land(Mail, "Mail", "2.0.0");
        var proposedOnly = await Propose(Installed("1.5.7", "AI@^1.21.0"));
        Assert.NotNull(proposedOnly);
        await Land(OpenAi, "OpenAI", "1.5.8");
        await Land(OpenAi, "OpenAI", "1.5.9");
        await Land(Mail, "Mail", "2.1.0");

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Propose(Installed("1.5.9", "AI@^1.23.0", mail: "2.1.0")));

        Assert.Contains("AI@^1.23.0", refusal.Message);
        Assert.Contains("no accepted module set", refusal.Message);
        Assert.Equal(proposedOnly!.Id, ModuleSetStore.Read(root).Proposed!.Id);
    }

    /// <summary>A deployment with no module set at all is refused as before #6192.</summary>
    [Fact]
    public async Task Proposal_RefusesTheWave_WhenTheDeploymentHasNoModuleSet()
    {
        await Land(Ai, "AI", "1.21.1");
        await Land(OpenAi, "OpenAI", "1.5.9");

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Propose(Installed("1.5.9", "AI@^1.23.0")));

        Assert.Contains("AI@^1.23.0", refusal.Message);
        Assert.Null(ModuleSetStore.Read(root).Proposed);
    }

    /// <summary>The accepted set never ran OpenAI, so there is no coherent generation of it to hold.</summary>
    [Fact]
    public async Task Proposal_RefusesTheWave_WhenTheAcceptedSetDoesNotNameTheDependent()
    {
        await Land(Ai, "AI", "1.21.1");
        await Land(Mail, "Mail", "2.0.0");
        var accepted = await Accept(Installed("1.5.7", "AI@^1.21.0"));
        await Land(OpenAi, "OpenAI", "1.5.9");

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Propose(Installed("1.5.9", "AI@^1.23.0")));

        Assert.Contains("does not name it", refusal.Message);
        Assert.Equal(accepted.Id, ModuleSetStore.Read(root).Proposed!.Id);
    }

    /// <summary>An inconsistent partial proposal is refused: the held OpenAI was accepted beside AI 1.21.0 and the wave
    /// moved AI to 1.21.1 - a pairing no set ever ran, so the held version's requirements cannot be established.</summary>
    [Fact]
    public async Task Proposal_RefusesTheWave_WhenADependencyOfTheHeldModuleMovedSinceTheAcceptedSet()
    {
        var accepted = await EstablishAcceptedSet(ai: "1.21.0");
        await Land(Ai, "AI", "1.21.1");
        await Land(OpenAi, "OpenAI", "1.5.9");

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Propose(Installed("1.5.9", "AI@^1.23.0")));

        Assert.Contains("cannot be established", refusal.Message);
        Assert.Equal(accepted.Id, ModuleSetStore.Read(root).Proposed!.Id);
    }

    /// <summary>Holding OpenAI back at the accepted 1.5.7 would break another package's OpenAI@^1.5.9.</summary>
    [Fact]
    public async Task Proposal_RefusesTheWave_WhenHoldingTheDependentBackBreaksAnotherRequirement()
    {
        var accepted = await EstablishAcceptedSet();
        await Land(OpenAi, "OpenAI", "1.5.9");
        var installed = Installed("1.5.9", "AI@^1.23.0").Add(
            new PackageManifest { Id = "Extra", ReleasedVersion = "0.1.0", Requires = ["OpenAI@^1.5.9"] });

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(async () => await Propose(installed));

        Assert.Contains("OpenAI@^1.5.9", refusal.Message);
        Assert.Equal(accepted.Id, ModuleSetStore.Read(root).Proposed!.Id);
    }

    /// <summary>The accepted generation's bytes are gone: there is nothing coherent to hold, so the wave is refused.</summary>
    [Fact]
    public async Task Proposal_RefusesTheWave_WhenTheAcceptedGenerationsBytesAreGone()
    {
        var accepted = await EstablishAcceptedSet();
        await Land(OpenAi, "OpenAI", "1.5.9");
        File.Delete(Path.Combine(root, "modules", accepted.Generations[OpenAi], OpenAi + ".dll"));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Propose(Installed("1.5.9", "AI@^1.23.0")));

        Assert.Equal(accepted.Id, ModuleSetStore.Read(root).Proposed!.Id);
    }

    /// <summary>The replica that adopted the accepted set fell back to another OpenAI generation, so the accepted one is
    /// not known to have run: the wave is refused.</summary>
    [Fact]
    public async Task Proposal_RefusesTheWave_WhenTheAdoptingReplicaRanAnotherGeneration()
    {
        await Land(Ai, "AI", "1.21.1");
        await Land(OpenAi, "OpenAI", "1.5.7");
        await Land(Mail, "Mail", "2.0.0");
        var proposed = await Propose(Installed("1.5.7", "AI@^1.21.0"));
        Assert.NotNull(proposed);
        ModuleSetStore.RecordAdoption(root, proposed!,
            runningGenerations: proposed!.Generations.SetItem(OpenAi, OpenAi + "@0000000000000000"));
        await Land(OpenAi, "OpenAI", "1.5.9");

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Propose(Installed("1.5.9", "AI@^1.23.0")));

        Assert.Contains("did not run", refusal.Message);
        Assert.Equal(proposed.Id, ModuleSetStore.Read(root).Proposed!.Id);
    }

    /// <summary>Lands AI, OpenAI 1.5.7 (requiring AI@^1.21.0) and Mail 2.0.0, and makes that set the accepted one.</summary>
    private async Task<ModuleSet> EstablishAcceptedSet(string ai = "1.21.1")
    {
        await Land(Ai, "AI", ai);
        await Land(OpenAi, "OpenAI", "1.5.7");
        await Land(Mail, "Mail", "2.0.0");
        return await Accept(Installed("1.5.7", "AI@^1.21.0", ai: ai));
    }

    /// <summary>Proposes the landed set through the checked proposal and records a replica's adoption of it.</summary>
    private async Task<ModuleSet> Accept(ImmutableList<PackageManifest> installed)
    {
        var proposed = await Propose(installed);
        Assert.NotNull(proposed);
        ModuleSetStore.RecordAdoption(root, proposed!);
        Assert.Equal(proposed!.Id, ModuleSetStore.Read(root).Current?.Id);
        return proposed;
    }

    private static ImmutableList<PackageManifest> Installed(
        string openAi, string openAiRequires, string ai = "1.21.1", string mail = "2.0.0") =>
    [
        new PackageManifest { Id = "AI", ReleasedVersion = ai, Module = Ai },
        new PackageManifest { Id = "OpenAI", ReleasedVersion = openAi, Module = OpenAi, Requires = [openAiRequires] },
        new PackageManifest { Id = "Mail", ReleasedVersion = mail, Module = Mail },
    ];

    private string? Head(string module) =>
        ModuleActivationSidecar.Read(root).Entries.Single(e => e.Name == module).Directory;

    private async Task<ModuleSet?> Propose(ImmutableList<PackageManifest> installed) =>
        await landing.ProposeCheckedModuleSet(installed)
            .Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);

    /// <summary>Lands one generation of a module; the version marker is a static asset, so two landings of one module
    /// carry different bytes and therefore different content-addressed generations.</summary>
    private async Task Land(string module, string package, string version) =>
        await landing.LandModule(
                module,
                [(module + ".dll", File.ReadAllBytes(typeof(BundleReader).Assembly.Location))],
                packagePath: "Plugins/" + package,
                version: version,
                staticAssets: [("version.txt", Encoding.UTF8.GetBytes(version))])
            .Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);
}
