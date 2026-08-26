#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.GitSync;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// Issue #1979 — a landed module needs a restart, the mesh knows it, and until this test nobody was
/// told.
///
/// <para><b>The finding was the absence of a caller.</b> <c>PendingModuleActivations</c> and
/// <c>IsPendingForPackage</c> were built and unit-tested, and had ZERO production callers: the
/// operator health check reported the fleet-wide report, while the person who actually installed the
/// package saw a card reading "✓ Installed" for a module that was not running and would not run
/// until some unrelated restart. So a unit test of the predicate cannot close this — it is what
/// already existed. This test renders the REAL catalog view through the same
/// <c>GetRemoteStream</c>/<c>GetControlStream</c> binding the portal uses, and asserts the note
/// reaches the card.</para>
///
/// <para><b>And it pins the timing, which is the part a static render would miss.</b> On the install
/// path the module lands strictly AFTER the install record node is written, so the node-driven
/// re-render that flips a card to "installed" happens BEFORE the restart is pending. The test
/// therefore renders first, waits for the card WITHOUT the note, and only then lands a module —
/// touching no mesh node at all. The note can only appear if
/// <see cref="ModuleLandingService.ActivationChanged"/> drove the re-render, which is exactly the
/// moment the buyer is looking at the card. (It also means the emission cannot be observed early:
/// the signal is hot, so the area must already be subscribed — which the first assertion proves.)</para>
/// </summary>
public class RestartRequiredCardTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>The module name is deliberately not an assembly this process loads, so the derived
    /// report can honestly call it "landed but not loaded here".</summary>
    private const string ModuleName = "Fake.Restart.Probe";

    private const string PackageId = "pack-a";
    private const string CatalogPath = "rbuergi/restart-catalog";

    /// <summary>The language-neutral half of the note — asserted instead of the English text so the
    /// test does not encode one locale's wording (the sentence beside it is a catalog key with a
    /// German translation).</summary>
    private const string RestartGlyph = "🔄";

    private readonly string landingRoot =
        Path.Combine(Path.GetTempPath(), "mw-restart-card-" + Guid.NewGuid().ToString("N"));

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        Directory.CreateDirectory(Path.Combine(landingRoot, "modules"));
        return base.ConfigureMesh(builder).AddPluginCatalog()
            // Both halves rooted at the SAME per-test temp tree, never the test host's own bin
            // folder: the activation record is a persistent file, and a reader looking at a
            // different directory than the writer is how "installed, and nothing happened" becomes
            // unexplainable — the exact failure mode the DI registration warns about.
            .ConfigureServices(services => services
                .AddSingleton(new ModuleLandingService(baseDirectory: landingRoot))
                .AddSingleton(new PendingModuleActivations(landingRoot)));
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.AddPluginCatalogTypes();
        return base.ConfigureClient(configuration).AddLayoutClient();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        try
        {
            if (Directory.Exists(landingRoot))
                Directory.Delete(landingRoot, recursive: true);
        }
        catch
        {
            // Best-effort: a straggling handle on a just-written file must never turn a green test
            // red — leaked temp dirs are the OS's to reap.
        }
    }

    [Fact(Timeout = 120000)]
    public async Task ALandedModule_PutsTheRestartNoteOnItsPackageCard()
    {
        var repo = CreateTempRepo();
        try
        {
            var git = new GitCli(Mesh.ServiceProvider.GetRequiredService<IoPoolRegistry>());
            WriteFile(repo, $"catalog/{PackageId}/package.json",
                $$"""{"id":"{{PackageId}}","name":"Package A","kind":"content","targetPartition":"PartA","version":"1.0.0"}""");
            WriteFile(repo, $"catalog/{PackageId}/A.md", "# A");
            await git.Run(repo, ["init"]).FirstAsync().ToTask();
            await git.Run(repo, ["add", "-A"]).FirstAsync().ToTask();
            await git.Run(repo, ["-c", "user.email=t@t", "-c", "user.name=t", "commit", "-m", "init"])
                .FirstAsync().ToTask();

            await NodeFactory.CreateNode(MeshNode.FromPath(CatalogPath) with
            {
                Name = "Catalog",
                NodeType = "PluginCatalog",
                Content = new PluginCatalogContent
                {
                    SourceRepoPath = repo,
                    SourceRef = "HEAD",
                    SourceSubdir = "catalog",
                },
            }).Should().Emit();

            var workspace = GetClient(c => c.AddData()).GetWorkspace();
            var reference = new LayoutAreaReference(CatalogLayoutAreas.CatalogArea);
            var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
                new Address(CatalogPath), reference);

            // 1. The card renders, and says nothing about a restart — nothing has landed yet. This
            //    assertion is load-bearing twice over: it is the control for step 3, and it is what
            //    proves the area is SUBSCRIBED before the hot ActivationChanged signal fires.
            var cardArea = await CardArea(stream, reference);
            (await CardTexts(stream, cardArea)).Should()
                .NotContain(t => t.Contains(RestartGlyph, StringComparison.Ordinal),
                    "nothing has landed yet, so a restart note here would be a prompt no restart can clear");

            // 2. Land a module recorded against this package's install record — the ONLY thing that
            //    changes. No mesh node is written, so nothing else can drive a re-render. (A real
            //    activation entry with this PackagePath exists only because the package was
            //    installed; the install itself is covered by ModuleFunnelTest and is not what this
            //    test is about.)
            var landing = Mesh.ServiceProvider.GetRequiredService<ModuleLandingService>();
            await landing.LandModule(
                    ModuleName,
                    [($"{ModuleName}.dll", [1, 2, 3, 4])],
                    packagePath: $"{PackageInstaller.InstalledPartition}/{PackageId}",
                    version: "1.0.0")
                .FirstAsync().ToTask();

            // 3. …and the card now says so, on the surface the person who installed it is looking at.
            await CardTextStream(stream, cardArea)
                .Should().Within(30.Seconds())
                .Match(texts => texts.Any(t => t.Contains(RestartGlyph, StringComparison.Ordinal)));

            await NodeFactory.DeleteNode(CatalogPath).Should().Emit();
        }
        finally
        {
            TryDelete(repo);
        }
    }

    /// <summary>The single package card's area name, once the catalog has listed the repo.</summary>
    private static async Task<string> CardArea(
        ISynchronizationStream<JsonElement> stream, LayoutAreaReference reference)
    {
        var stack = (StackControl)(await stream.GetControlStream(reference.Area!)
            .Should().Within(60.Seconds()).Match(c =>
                c is StackControl s
                && s.Areas.Count(a => a.Area?.ToString()?.Contains("/pkg-", StringComparison.Ordinal) == true) == 1))!;
        return stack.Areas
            .Select(a => a.Area?.ToString())
            .First(p => p is not null && p.Contains("/pkg-", StringComparison.Ordinal))!;
    }

    /// <summary>
    /// Every label the card currently renders. A card is a stack of named sub-areas, so its text is
    /// only readable by resolving each one — the same walk the Blazor renderer does.
    /// </summary>
    private static IObservable<IReadOnlyList<string>> CardTextStream(
        ISynchronizationStream<JsonElement> stream, string cardArea) =>
        stream.GetControlStream(cardArea)
            .Select(c => c as StackControl)
            .Where(s => s is not null)
            .SelectMany(s => s!.Areas
                .Select(a => a.Area?.ToString())
                .Where(a => a is not null)
                .Select(a => stream.GetControlStream(a!).Select(c => (c as LabelControl)?.Data?.ToString() ?? string.Empty))
                .CombineLatest()
                .Select(texts => (IReadOnlyList<string>)texts));

    private static async Task<IReadOnlyList<string>> CardTexts(
        ISynchronizationStream<JsonElement> stream, string cardArea) =>
        await CardTextStream(stream, cardArea).FirstAsync().ToTask();

    private static string CreateTempRepo()
    {
        var p = Path.Combine(Path.GetTempPath(), "restartcard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(p);
        return p;
    }

    private static void WriteFile(string root, string relative, string content)
    {
        var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch { /* best effort */ }
    }
}
