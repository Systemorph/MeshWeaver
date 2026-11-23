using System;
using System.IO;
using System.Reactive.Linq;
using System.Text;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 A dependent whose declared floor the landed set does not meet HOLDS ITSELF BACK; it does not
/// refuse the whole wave's module set.
///
/// <para><b>The incident (control, 2026-11-23).</b> OpenAI's install record required
/// <c>AI@^4.1.0</c>, the landed AI was 4.0.13 (4.1.0 was published after it). Every landing wave's
/// proposal was refused (<c>Not proposing a module set: 1 declared dependency floor(s) are not
/// met</c>) for two hours, so the providers that landed in the same waves — unrelated to OpenAI —
/// stayed off the mesh too, until AI 4.1.0 itself landed.</para>
///
/// <para><b>The rule pinned here.</b> The proposal names the dependent at the generation the mesh
/// already runs (or leaves it out when none), proposes everything else that landed, and the wave that
/// lands the dependency proposes both. A floor with no module to hold back (a content-only dependent)
/// still refuses, as before.</para>
///
/// <para>Real seams: the real <see cref="ModuleLandingService"/> lands real assembly bytes into a
/// throwaway module root and proposes through <see cref="ModuleLandingService.ProposeCheckedModuleSet"/>.</para>
/// </summary>
public class ADependentWhoseFloorIsUnmetHoldsItselfBackNotTheWaveTest : IDisposable
{
    private const string AiModule = "MeshWeaver.AI";
    private const string OpenAiModule = "MeshWeaver.AI.OpenAI";
    private const string AnthropicModule = "MeshWeaver.AI.Anthropic";

    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-dependent-hold-" + Guid.NewGuid().ToString("N")[..8]);

    public ADependentWhoseFloorIsUnmetHoldsItselfBackNotTheWaveTest() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { /* a temp directory that outlives the test is harmless */ }
        catch (UnauthorizedAccessException) { /* likewise */ }
        GC.SuppressFinalize(this);
    }

    private static PackageManifest Record(string id, string module, params string[] requires) => new()
    {
        Id = id,
        Name = id,
        Kind = PackageKind.NodeRepo,
        TargetPartition = id,
        SourceFolder = id,
        Version = "4.0.0",
        ReleasedVersion = "4.0.0",
        Module = module,
        Requires = [.. requires],
    };

    private Task Land(ModuleLandingService landing, string module, string package, string version) =>
        landing.LandModule(
                module,
                [(module + ".dll", File.ReadAllBytes(typeof(BundleReader).Assembly.Location))],
                frameworkMvid: null,
                packagePath: "Plugins/" + package,
                version: version,
                minMeshVersion: null,
                staticAssets: [("wwwroot/version.txt", Encoding.UTF8.GetBytes(module + version))])
            .Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);

    private Task<ModuleSet?> Propose(ModuleLandingService landing, params PackageManifest[] records) =>
        landing.ProposeCheckedModuleSet(records)
            .Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);

    [Fact]
    public async Task AnUnmetDependent_IsHeldAtItsRunningGeneration_AndTheRestOfTheWaveStillProposes()
    {
        using var landing = new ModuleLandingService(baseDirectory: root);
        var openAi = Record("OpenAI", OpenAiModule, "AI@^4.1.0");
        var ai = Record("AI", AiModule);
        var anthropic = Record("Anthropic", AnthropicModule, "AI@^4.0.0");

        // The set the mesh runs: AI 4.0.13, OpenAI 4.0.13, Anthropic 4.0.13 (unchecked — nothing
        // declared a floor yet).
        await Land(landing, AiModule, "AI", "4.0.13");
        await Land(landing, OpenAiModule, "OpenAI", "4.0.13");
        await Land(landing, AnthropicModule, "Anthropic", "4.0.13");
        var running = await Propose(landing);
        running.Should().NotBeNull();
        var runningOpenAi = running!.Generations[OpenAiModule];
        var runningAnthropic = running.Generations[AnthropicModule];

        // The next wave: OpenAI 4.0.14 and Anthropic 4.0.14 land, AI does NOT (4.1.0 is not out).
        await Land(landing, OpenAiModule, "OpenAI", "4.0.14");
        await Land(landing, AnthropicModule, "Anthropic", "4.0.14");

        var proposed = await Propose(landing, ai, openAi, anthropic);

        proposed.Should().NotBeNull(
            "the unrelated provider that landed in the wave must reach the mesh — the unmet floor is OpenAI's alone");
        proposed!.Generations[AnthropicModule].Should().NotBe(runningAnthropic,
            "Anthropic's new generation is proposed");
        proposed.Generations[OpenAiModule].Should().Be(runningOpenAi,
            "OpenAI keeps the generation the mesh runs until AI@^4.1.0 is landed");
        proposed.Generations[AiModule].Should().Be(running.Generations[AiModule]);

        // Idempotent: the same wave proposes nothing new.
        (await Propose(landing, ai, openAi, anthropic)).Should().BeNull();

        // The dependency lands: the next proposal carries OpenAI's new generation too.
        await Land(landing, AiModule, "AI", "4.1.0");
        var converged = await Propose(landing, ai, openAi, anthropic);
        converged.Should().NotBeNull();
        converged!.Generations[OpenAiModule].Should().NotBe(runningOpenAi,
            "the floor is met now — the held dependent joins the set");
        converged.Generations[AiModule].Should().NotBe(running.Generations[AiModule]);
    }

    [Fact]
    public async Task AnUnmetDependent_WithNoRunningGeneration_IsLeftOutUntilItsDependencyLands()
    {
        using var landing = new ModuleLandingService(baseDirectory: root);
        await Land(landing, AiModule, "AI", "4.0.13");
        await Land(landing, AnthropicModule, "Anthropic", "4.0.13");
        await Land(landing, OpenAiModule, "OpenAI", "4.0.14");

        var proposed = await Propose(landing,
            Record("AI", AiModule), Record("Anthropic", AnthropicModule), Record("OpenAI", OpenAiModule, "AI@^4.1.0"));

        proposed.Should().NotBeNull();
        proposed!.Generations.Keys.Should().Contain([AiModule, AnthropicModule]);
        proposed.Generations.Keys.Should().NotContain(OpenAiModule,
            "no generation of it is proposed yet, and the new one must not reach a boot below its floor");
    }

    [Fact]
    public async Task AnUnmetFloor_WithNoModuleToHoldBack_StillRefusesTheSet()
    {
        using var landing = new ModuleLandingService(baseDirectory: root);
        await Land(landing, AiModule, "AI", "4.0.13");
        var contentOnly = new PackageManifest
        {
            Id = "Governance",
            Name = "Governance",
            Kind = PackageKind.NodeRepo,
            TargetPartition = "Governance",
            SourceFolder = "Governance",
            Version = "1.0.0",
            ReleasedVersion = "1.0.0",
            Requires = ["AI@^4.1.0"],
        };

        Func<Task> act = async () => await Propose(landing, Record("AI", AiModule), contentOnly);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Not proposing a module set*",
            "a content-only dependent has no module to hold back — the original refusal stands");
    }
}
