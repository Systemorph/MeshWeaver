#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// The STORE shape, in the harness the framework's own suites use: a SELF-TYPED root (root
/// <c>Shop</c> is nodeType <c>Shop/Front</c>, defined by a child of the same package) whose
/// NodeType node ships the compile stamp a node repo COMMITS — <c>compilationStatus: Ok</c> with
/// assembly coordinates and a <c>compiledFrameworkVersion</c> from a long-gone framework build.
/// That build cannot be loaded here, so the type is rebuilt on install; the root's hub must end
/// up bound to the REBUILT configuration and serve the type's own areas.
///
/// <para>A hub resolves which configuration serves it exactly ONCE, at activation. So every step
/// of the install that TOUCHES the root is a chance to pin the pre-rebuild answer for the hub's
/// lifetime, after which the root serves only the generic areas — "No renderer is registered for
/// area <c>Tests</c>", the plugin gate's Store/Catalog RED. <see cref="PackageInstaller"/> has two
/// such touches: the warm (guarded) and the CONTENT publish, which posts a
/// <c>SyncContentFilesRequest</c> to the root's address and therefore activates its hub exactly
/// as the warm does. The second fact below is that door.</para>
/// </summary>
public class StaleStampRootBindingTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly TimeSpan StepTimeout = 120.Seconds();

    // Two real Roslyn compiles' worth of budget (the install's rebuild, plus the render).
    protected override TimeSpan TestSoftDeadline => TimeSpan.FromSeconds(120);
    protected override TimeSpan TestHardDeadline => TimeSpan.FromSeconds(280);

    private readonly string _contentRoot = Path.Combine(
        Path.GetTempPath(), "StaleStampRootBindingTest", Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Mirrors the portal: the writable <c>content</c> collection is mounted once per partition,
    /// on the ROOT (a single-segment node path). That is the only hub where the collection
    /// resolves — and the reason the installer posts its content sync at the root.
    /// </summary>
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGraph()
            .AddPluginCatalog()
            .ConfigureDefaultNodeHub(config =>
            {
                var nodePath = config.Address.ToString();
                if (nodePath.Contains('/'))
                    return config;
                return config.AddContentCollection(_ => new ContentCollectionConfig
                {
                    Name = ContentCollectionsExtensions.DefaultCollectionName,
                    SourceType = "FileSystem",
                    BasePath = Path.Combine(_contentRoot, nodePath),
                    IsEditable = true,
                    IsStatic = true,
                    ExposeInChildren = true,
                });
            });

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddData();

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(_contentRoot))
        {
            try { Directory.Delete(_contentRoot, recursive: true); }
            catch { /* best effort */ }
        }
    }

    // ── the fixture, byte for byte the gate's (PluginGateRunnerTest's Shop/Front), so a
    //    divergence between the two harnesses is a difference in the HOST, never in the input ──

    // `@ID@` stands in for the package id so the JSON can stay a verbatim copy of the gate's
    // fixture (raw-string interpolation cannot carry the trailing `}}` of a JSON object).
    private static string IndexJson(string id) =>
        """{"$type":"MeshNode","id":"@ID@","namespace":"","path":"@ID@","mainNode":"@ID@","name":"@ID@","nodeType":"@ID@/Front","state":"Active","content":{"$type":"FrontContent","intro":"hello"}}"""
            .Replace("@ID@", id, StringComparison.Ordinal);

    /// <summary>
    /// The committed STALE stamp: <c>Ok</c> plus assembly coordinates and a framework hash this
    /// process has never produced. <c>HasUsableBuild</c> is false for it, so the per-NodeType hub
    /// flips it to Pending and rebuilds — and anything that activates the root inside that window
    /// binds against a type with no loadable build.
    /// </summary>
    private static string FrontNodeTypeJson(string id) =>
        """{"$type":"MeshNode","id":"Front","namespace":"@ID@","path":"@ID@/Front","mainNode":"@ID@/Front","name":"Front","nodeType":"NodeType","state":"Active","content":{"$type":"NodeTypeDefinition","description":"The shop front.","configuration":"config => config.WithContentType<FrontContent>().AddDefaultLayoutAreas().AddLayout(layout => layout.WithView(\"Tests\", FrontTestsArea.Tests))","includeGlobalTypes":true,"compilationStatus":"Ok","lastCompiledVersion":201,"latestAssemblyCollection":"local","latestAssemblyPath":"@ID@_Front/v201-0123456789abcdef0123456789abcdef-aaaabbbbcccc.dll","compiledFrameworkVersion":"0123456789abcdef0123456789abcdef"}}"""
            .Replace("@ID@", id, StringComparison.Ordinal);

    private const string FrontContentSource =
        """
        public record FrontContent
        {
            public string? Intro { get; init; }

            public int Answer() => 42;
        }
        """;

    private const string FrontTestsArea =
        """
        using System;
        using System.Reactive.Linq;
        using MeshWeaver.Layout;
        using MeshWeaver.Layout.Composition;

        public static class FrontTestsArea
        {
            public static IObservable<UiControl?> Tests(LayoutAreaHost host, RenderingContext _)
            {
                var sb = new System.Text.StringBuilder("### Front tests\n\n| Test | Result |\n|---|---|\n");
                var passed = 0;
                try
                {
                    if (new FrontContent().Answer() != 42)
                        throw new Exception("expected the answer to be 42");
                    sb.Append("| Answer is 42 | PASS |\n");
                    passed++;
                }
                catch (Exception ex) { sb.Append($"| Answer is 42 | FAIL {ex.Message} |\n"); }
                sb.Append($"\n**{passed}/1 passed.**");
                return Observable.Return<UiControl?>(Controls.Markdown(sb.ToString()));
            }
        }
        """;

    // Deliberately NOT valid UTF-8 — the shape RepoFileCodec gives a committed binary.
    private static readonly byte[] PosterBytes =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0xFF, 0xC0, 0xDE];

    private static IReadOnlyList<RepoFile> Files(string id, bool withContentAssets)
    {
        var files = new List<RepoFile>
        {
            new($"{id}/index.json", IndexJson(id)),
            new($"{id}/Front.json", FrontNodeTypeJson(id)),
            new($"{id}/Front/Source/FrontContent.cs", FrontContentSource),
            new($"{id}/Front/Test/FrontTestsArea.cs", FrontTestsArea),
        };
        if (withContentAssets)
            files.Add(new($"{id}/content/images/cover.png", "", PosterBytes));
        return files;
    }

    /// <summary>
    /// The shape #1101 fixed, pinned in the framework harness: no content assets, so the only
    /// root-activating touch is the (now guarded) warm. The rebuild must SETTLE and the root must
    /// serve its type's <c>Tests</c> area.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task StaleStampSelfTypedRoot_RootServesItsTypesArea()
        => await InstallAndAssertRootServesItsTypesArea("ShopA", withContentAssets: false);

    /// <summary>
    /// 🚨 The residual door #1101 named but did not close: a package that ships CONTENT ASSETS.
    /// <c>SyncPackageContent</c> runs immediately after the warm and posts a
    /// <c>SyncContentFilesRequest</c> to the ROOT's address — and routing a message to a node
    /// address that has no hub yet ACTIVATES one
    /// (<c>MonolithRoutingService.RouteImpl</c> → <c>CreateHub</c> →
    /// <c>IMeshNodeHubFactory.ResolveHubConfiguration</c>). So the guard the warm gained is
    /// bypassed the moment the package carries a single byte of content, and the root binds
    /// against the still-stale type exactly as before.
    ///
    /// <para>Both halves are asserted: the root serves its type's area AND the committed asset is
    /// published (the fix must not buy the binding by dropping the content — that is issue #848
    /// in reverse).</para>
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task StaleStampSelfTypedRootShippingContent_RootServesItsTypesArea()
    {
        const string id = "ShopB";
        await InstallAndAssertRootServesItsTypesArea(id, withContentAssets: true);

        var cover = Path.Combine(_contentRoot, id, "images", "cover.png");
        File.Exists(cover).Should().BeTrue(
            $"the committed asset must still be published into the root's content collection ({cover})");
        File.ReadAllBytes(cover).Should().Equal(PosterBytes);
    }

    /// <summary>
    /// The same door in the shape that needs no race to expose it, and the whole reason the gate
    /// is a WAIT with a verdict rather than a blind post: a package whose in-package NodeType can
    /// never produce a loadable build. It ships the committed stale stamp AND source that no
    /// longer compiles, so the rebuild ends at <c>Error</c> while the stale assembly coordinates
    /// stay on the node — <c>HasLoadableBuild</c> is false for the rest of the run, deterministically.
    ///
    /// <para>Measured before the fix: the install ran <b>60.9 s</b> — the mesh hub's whole
    /// RequestTimeout — and then reported "its binaries are not being served". The content sync
    /// had activated the root; that activation's framework-stale heal waited for a usable build
    /// that was never coming, with no bound at all (the root's hub was still absent 180 s later),
    /// so the request was abandoned at the sender. Whether the bytes landed came down to which
    /// side of that dead heat the machine fell on.</para>
    ///
    /// <para>What must hold now: the install does NOT spend a request timeout on a root it cannot
    /// activate. It recognises that the type's rebuild has run and produced nothing loadable, and
    /// skips the publish — measured at 393 ms against 60.9 s, and the assets are published by the
    /// next install once the type compiles (for a package under auto-update, the next poll). The
    /// healthy counterpart is <see cref="StaleStampSelfTypedRootShippingContent_RootServesItsTypesArea"/>,
    /// which pins that a package whose type DOES rebuild publishes its assets and serves its own
    /// areas — so the skip here is scoped to the broken case and cannot quietly become the norm.</para>
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task SelfTypedRootWhoseTypeNeverCompiles_SkipsThePublishPromptly()
    {
        const string id = "ShopC";
        // The healthy fixture with ONE deliberate compile error in FrontContent: MissingHelper
        // exists nowhere. Everything else — including the Tests area the configuration names — is
        // intact, so the ONLY reason this type cannot produce a build is that error.
        var broken = Files(id, withContentAssets: true)
            .Select(f => f.Path.EndsWith("FrontContent.cs", StringComparison.Ordinal)
                ? new RepoFile(f.Path,
                    "public record FrontContent { public string? Intro => MissingHelper.Frobnicate(); "
                    + "public int Answer() => 42; }")
                : f)
            .ToList();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Install(id, broken);
        var installMs = sw.ElapsedMilliseconds;
        Output.WriteLine($"install of the broken package returned at +{installMs}ms");

        var type = await Mesh.GetWorkspace().GetMeshNodeStream($"{id}/Front")
            .Where(n => n?.Content is NodeTypeDefinition).FirstAsync().Timeout(StepTimeout).ToTask();
        type.HasLoadableBuild().Should().BeFalse(
            "the fixture is only meaningful while the type genuinely cannot produce a loadable "
            + "build — if this flips, the test has stopped covering the deterministic window");

        installMs.Should().BeLessThan(30_000,
            "the install must not post into a root it cannot activate and then wait out the hub's "
            + $"whole request timeout — it did exactly that before the fix (60.9 s); took {installMs}ms");

        // 🅿️ The same install AGAIN — the RECORDED-fact case in its purest form (#1277). Nothing
        // changed, so no release is requested and NOTHING will ever emit on the type's stream
        // again. A publish gate that insists on WITNESSING a rebuild has nothing left to witness
        // and can only end on its 90 s cap; one that reads NodeTypeCompileParkRegistry answers
        // from state the process already holds. This is the half the first install cannot pin: it
        // still sees a live compile on its way past, which is exactly why the regression was
        // bimodal rather than constant.
        var again = System.Diagnostics.Stopwatch.StartNew();
        await Install(id, broken);
        var againMs = again.ElapsedMilliseconds;
        Output.WriteLine($"re-install of the broken package returned at +{againMs}ms");
        againMs.Should().BeLessThan(15_000,
            "a re-install of a package whose NodeType is PARKED compiles nothing at all, so the "
            + "publish gate must answer from the recorded park rather than wait out its settle "
            + $"budget; took {againMs}ms");
    }

    /// <summary>Installs the fixture package through the standard installer.</summary>
    private async Task Install(string id, IReadOnlyList<RepoFile> files)
    {
        var source = new NodeRepoPackageSource(
            (_, _, _, _) => Observable.Return(new RepoSnapshot($"commit-{id}", files)),
            repoUrl: "local");
        var manifest = new PackageManifest
        {
            Id = id,
            Name = id,
            Kind = PackageKind.NodeRepo,
            TargetPartition = id,
            SourceFolder = id,
            Version = $"commit-{id}",
        };
        var packageFiles = await source.FetchPackageFiles(manifest, "HEAD")
            .FirstAsync().Timeout(StepTimeout).ToTask();
        await PackageInstaller.Install(Mesh, manifest, packageFiles, "HEAD")
            .FirstAsync().Timeout(StepTimeout).ToTask();
    }

    private async Task InstallAndAssertRootServesItsTypesArea(string id, bool withContentAssets)
    {
        var typePath = $"{id}/Front";
        var sw = System.Diagnostics.Stopwatch.StartNew();

        await Install(id, Files(id, withContentAssets));
        Output.WriteLine($"install returned at +{sw.ElapsedMilliseconds}ms");

        // 🚨 Only NOW subscribe to the type's stream. Subscribing to a path that does not exist
        // yet resolves to a NotFound OnError, and the shared per-path handle replays that
        // terminal to every later subscriber — a "diagnostic" that would fail the assertions it
        // is meant to explain. The install has written every node by the time it returns.
        using var diag = Mesh.GetWorkspace().GetMeshNodeStream(typePath)
            .Subscribe(n => Output.WriteLine(
                n?.Content is NodeTypeDefinition d
                    ? $"[type +{sw.ElapsedMilliseconds}ms] v{n.Version} Status={d.CompilationStatus} "
                      + $"fv='{d.CompiledFrameworkVersion}' asm='{d.LatestAssemblyPath}'"
                      + (d.CompilationError is { Length: > 0 } e ? $" ERROR={e.Replace("\n", " | ")}" : "")
                    : $"[type +{sw.ElapsedMilliseconds}ms] content={n?.Content?.GetType().Name ?? "null"}"));

        // The committed stale stamp must be REBUILT, not trusted: a terminal status carrying a
        // build this framework can actually load (HasLoadableBuild is exactly that predicate).
        await Mesh.GetWorkspace().GetMeshNodeStream(typePath)
            .Should().Within(StepTimeout)
            .Match(n => n.Content is NodeTypeDefinition d
                && d.CompilationStatus == CompilationStatus.Ok
                && !string.IsNullOrEmpty(d.LatestAssemblyPath)
                && n.HasLoadableBuild());
        Output.WriteLine($"rebuild settled at +{sw.ElapsedMilliseconds}ms");

        // …and the ROOT's hub must be bound to that rebuilt configuration. Its `Tests` area only
        // exists there, so a root left on the defaults-only fallback renders "Area not found".
        var client = GetClient();
        var reference = new LayoutAreaReference("Tests") { Id = "" };
        var stream = client.GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(new Address(id), reference);
        try
        {
            var frame = await stream.Select(ci => ci.Value)
                .Where(f => Strings(f).Any(s =>
                    s.Contains("passed", StringComparison.Ordinal)
                    || s.Contains("Area not found", StringComparison.Ordinal)
                    || s.Contains("This area failed to render", StringComparison.Ordinal)))
                .FirstAsync().Timeout(StepTimeout).ToTask();
            var text = string.Join("\n---\n", Strings(frame));
            Output.WriteLine($"Tests frame at +{sw.ElapsedMilliseconds}ms:\n{text}");
            text.Should().NotContain("Area not found",
                "the root's hub must bind to its type's REBUILT configuration, never to the "
                + "defaults-only fallback pinned while the committed stamp was still stale");
            text.Should().Contain("1/1 passed",
                "the root must execute its type's own Tests area");
        }
        finally
        {
            stream.Dispose();
        }
    }

    private static IReadOnlyList<string> Strings(JsonElement element)
    {
        var strings = new List<string>();
        Walk(element, strings);
        return strings;

        static void Walk(JsonElement node, List<string> into)
        {
            switch (node.ValueKind)
            {
                case JsonValueKind.String:
                    if (node.GetString() is { Length: > 0 } s) into.Add(s);
                    break;
                case JsonValueKind.Object:
                    foreach (var property in node.EnumerateObject()) Walk(property.Value, into);
                    break;
                case JsonValueKind.Array:
                    foreach (var item in node.EnumerateArray()) Walk(item, into);
                    break;
            }
        }
    }
}
