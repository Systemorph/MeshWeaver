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
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown.Export.Configuration;
using MeshWeaver.Markdown.Export.Messaging;
using MeshWeaver.Markdown.Export.Pixel;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using UglyToad.PdfPig;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// The end-to-end contract for the pixel export's asset inlining (issue #990): a slide that
/// references an image stored in a REAL content collection prints with that image <b>embedded as a
/// <c>data:</c> URI</b>.
///
/// <para><b>Why this is not a nice-to-have.</b> The print document declares
/// <c>default-src 'none'; img-src data:</c> and the browser runs with no DNS, so a reference that
/// fails to inline cannot fall back to fetching itself: it is a <b>blank image in the user's PDF</b>.
/// Inlining is therefore load-bearing for correctness, and "the parse is unit-tested" does not cover
/// it — the parse never meets a collection. This test does, on a mesh that mounts content the way
/// the portal does: a default <c>content</c> collection per node over a shared store, plus named
/// collections beside it, so both of the content route's shapes
/// (<c>{node}/{collection}/{file}</c> and <c>{node}/{file…}</c>) are exercised.</para>
///
/// <para>Composing and inlining need no browser, so the whole contract runs on every machine. The
/// last test drives the real export through the mesh and asserts whichever contract the machine
/// supports, exactly as <see cref="DeckPixelExportScriptRelayTest"/> does.</para>
/// </summary>
public class DeckAssetInliningTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private readonly string storageRoot =
        Path.Combine(Path.GetTempPath(), $"mw-deck-assets-{Guid.NewGuid():N}");

    /// <summary>A qualified collection name — its '/' travels through a URL as '~'.</summary>
    private const string QualifiedCollection = "Media/2026";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddMarkdownExport(cfg => cfg.PixelRendering = cfg.PixelRendering with
            {
                // Chromium's sandbox needs unprivileged user namespaces, which Ubuntu 24.04 (and
                // therefore the CI runner) restricts by default.
                NoSandbox = OperatingSystem.IsLinux(),
            })
            .ConfigureDefaultNodeHub(config =>
            {
                // Per-node collections over a shared store — the shape the content route serves and
                // the shape `MapContentCollection("attachments", "storage", $"attachments/{node}")`
                // gives every node in the portal. A default `content` collection plus two named
                // ones, so the test exercises both of the route's shapes.
                var nodePath = config.Address.ToString();
                if (string.IsNullOrEmpty(nodePath))
                    return config;

                return config
                    .AddContentCollection(_ => Collection(
                        ContentCollectionsExtensions.DefaultCollectionName, "content", nodePath))
                    .AddContentCollection(_ => Collection("attachments", "attachments", nodePath))
                    .AddContentCollection(_ => Collection(QualifiedCollection, "media", nodePath));
            });

    private ContentCollectionConfig Collection(string name, string folder, string nodePath)
    {
        var dir = Path.Combine(storageRoot, folder, nodePath);
        Directory.CreateDirectory(dir);
        return new ContentCollectionConfig
        {
            Name = name,
            SourceType = "FileSystem",
            BasePath = dir,
            IsEditable = true,
            ExposeInChildren = true,
            IsStatic = true,
            Settings = new Dictionary<string, string> { ["BasePath"] = dir },
        };
    }

    [Fact(Timeout = 120000)]
    public async Task A_stored_slide_image_is_embedded_as_a_data_uri()
    {
        var (space, _, slidePath) = await CreateDeck("![logo](logo.png)");
        var logo = Payload(1);
        // Where the portal puts a node's image: the node's default `content` collection. Written
        // through the SAME resolution the reader uses, so the test cannot quietly agree with itself
        // about a layout the product does not use.
        await StoreAsset($"{slidePath}/logo.png", logo);

        var inlining = await Inline(space, slidePath, "![logo](logo.png)");

        inlining.Unresolved.Should().BeEmpty(
            "a stored image must resolve; under the print CSP an un-inlined reference is a blank image");
        inlining.Inlined.Should().HaveCount(1);
        inlining.Html.Should().Contain($"data:image/png;base64,{Convert.ToBase64String(logo)}",
            "the document must carry the image's own bytes, not a link to them");
        inlining.Html.Should().NotContain("api/content/",
            "every reference was resolved, so none may survive as a link the CSP will block");

        await NodeFactory.DeleteNode(space).Should().Emit();
    }

    [Fact(Timeout = 120000)]
    public async Task An_unresolvable_reference_is_reported_rather_than_silently_blank()
    {
        var (space, _, slidePath) = await CreateDeck("![missing](nowhere.png)");

        var inlining = await Inline(space, slidePath, "![missing](nowhere.png)");

        inlining.Inlined.Should().BeEmpty();
        inlining.Unresolved.Should().HaveCount(1,
            "the export must SAY an asset did not resolve — the CSP turns a quiet miss into a blank "
            + "image, which looks like a rendering bug rather than a missing file");
        var unresolved = inlining.Unresolved.Single();
        unresolved.Reference.Should().EndWith("nowhere.png");
        unresolved.Reason.Should().NotBeNullOrWhiteSpace(
            "the report has to name what went wrong, or it is just a different kind of silence");
        Output.WriteLine($"unresolved: {unresolved.Reference} — {unresolved.Reason}");

        inlining.Html.Should().Contain(unresolved.Reference,
            "an unresolved reference is left alone rather than dropped, so the document still shows "
            + "where the picture belonged");

        await NodeFactory.DeleteNode(space).Should().Emit();
    }

    [Fact(Timeout = 120000)]
    public async Task Every_reference_shape_resolves_against_a_real_collection()
    {
        // One slide carrying every shape the product renders or a user pastes back in.
        var nodeRelative = Payload(2);      // api/content/{node}/{file}         → default collection
        var explicitDefault = Payload(3);   // api/content/{node}/content/{file} → default, named
        var namedCollection = Payload(4);   // api/content/{node}/attachments/…  → named collection
        var tildeCollection = Payload(5);   // api/content/{node}/Media~2026/…   → '~' decoded to '/'
        var percentEscaped = Payload(6);    // api/content/{node}/a%20folder/…   → per-segment decode

        var (space, _, slidePath) = await CreateDeck("");

        await StoreAsset($"{slidePath}/logo.png", nodeRelative);
        await StoreAsset($"{slidePath}/content/banner.png", explicitDefault);
        await StoreAsset($"{slidePath}/attachments/chart.png", namedCollection);
        await StoreAsset($"{slidePath}/Media~2026/cover.png", tildeCollection);
        await StoreAsset($"{slidePath}/a folder/logo two.png", percentEscaped);

        var markdown =
            "![node-relative](logo.png)\n\n"
            + "![percent-escaped](a%20folder/logo%20two.png)\n\n"
            + $"<img src=\"/api/content/{slidePath}/content/banner.png\" alt=\"explicit default\" />\n\n"
            + $"<img src=\"/api/content/{slidePath}/attachments/chart.png\" alt=\"named\" />\n\n"
            + $"<img src=\"/api/content/{slidePath}/Media~2026/cover.png\" alt=\"qualified\" />";

        var inlining = await Inline(space, slidePath, markdown);

        inlining.Unresolved.Should().BeEmpty(
            "every one of these shapes is a shape the content route serves, so every one must inline");
        inlining.Inlined.Should().HaveCount(5);

        foreach (var (label, payload) in new (string, byte[])[]
                 {
                     ("node-relative", nodeRelative),
                     ("explicit default collection", explicitDefault),
                     ("named collection", namedCollection),
                     ("'~'-encoded qualified collection name", tildeCollection),
                     ("percent-escaped segments", percentEscaped),
                 })
            inlining.Html.Should().Contain($"data:image/png;base64,{Convert.ToBase64String(payload)}",
                because: $"the {label} shape must reach the document as its own bytes");

        await NodeFactory.DeleteNode(space).Should().Emit();
    }

    /// <summary>
    /// The whole path, through the mesh: a real <c>ExportDocumentRequest</c> with
    /// <see cref="ExportFidelity.Pixel"/> against a deck whose slide references a stored image.
    ///
    /// <para>Never skipped. The pixel branch needs a headless browser and whether one exists is a
    /// property of the machine, so this asks the renderer's own probe and asserts the contract that
    /// applies: with a browser, a PDF that actually carries an embedded image; without one, the
    /// actionable refusal. The inlining assertion — that the activity reported no unresolved asset
    /// — is checked whenever the export ran at all.</para>
    /// </summary>
    [Fact(Timeout = 180000)]
    public async Task The_export_embeds_the_stored_image_or_refuses_clearly()
    {
        var (space, deck, slidePath) = await CreateDeck("# Cover\n\n![logo](logo.png)");
        await StoreAsset($"{slidePath}/logo.png", Payload(7));

        var request = new ExportDocumentRequest(deck, new DocumentExportOptions
        {
            Format = ExportFormat.Pdf,
            Fidelity = ExportFidelity.Pixel,
            CoverPage = false,
            TableOfContents = false
        });

        var dispatch = await Mesh
            .Observe<ExportDocumentResponse>(request, o => o.WithTarget(new Address(deck)))
            .Should().Within(30.Seconds()).Emit();
        dispatch.Message.Error.Should().BeNullOrEmpty("the export should start successfully");

        var workspace = GetClient(c => c.AddData()).GetWorkspace();
        var terminal = await workspace
            .GetMeshNodeStream(dispatch.Message.ActivityPath)
            .Select(node => node?.Content as ActivityLog)
            .Should().Within(2.Minutes())
            .Match(log => log is not null && log.Status != ActivityStatus.Running);

        // Ask the SAME probe the export asked, so the expectation can never disagree with what the
        // script actually found.
        var renderer = Mesh.ServiceProvider.GetRequiredService<IPixelPdfRenderer>();
        var executable = await renderer.Probe().FirstAsync().ToTask(TestContext.Current.CancellationToken);
        Output.WriteLine($"Pixel renderer executable: {executable ?? "<none>"}");

        var messages = string.Join("\n  ", terminal!.Messages.Select(m => $"[{m.LogLevel}] {m.Message}"));

        if (executable is null)
        {
            terminal.Status.Should().Be(ActivityStatus.Failed,
                because: "pixel fidelity without a browser must refuse, never quietly downgrade. "
                         + $"Messages:\n  {messages}");
            messages.Should().Contain("headless Chromium");
            await NodeFactory.DeleteNode(space).Should().Emit();
            return;
        }

        terminal.Status.Should().Be(ActivityStatus.Succeeded, because: $"Messages:\n  {messages}");
        messages.Should().Contain("Inlined 1/1 slide assets",
            because: $"the deck's one stored image had to be inlined. Messages:\n  {messages}");
        messages.Should().NotContain("blank image",
            because: $"nothing may have been left unresolved. Messages:\n  {messages}");

        var rendered = terminal.ReturnValue!.Value.Deserialize<RenderedDocument>(Mesh.JsonSerializerOptions);
        rendered!.Content.Should().NotBeNull().And.NotBeEmpty();

        using var pdf = PdfDocument.Open(rendered.Content);
        var images = pdf.GetPages().SelectMany(p => p.GetImages()).ToList();
        Output.WriteLine($"pages={pdf.NumberOfPages} images={images.Count}");
        images.Should().NotBeEmpty(
            "the stored PNG must reach the printed page — the CSP denies every other way it could, "
            + "so an empty page here IS the blank-image defect this test exists to catch");

        await NodeFactory.DeleteNode(space).Should().Emit();
    }

    /// <summary>
    /// Creates a Space, a Deck and one Slide, and returns their paths. The slide node has to exist:
    /// the reference resolves through <see cref="MeshWeaver.Mesh.Services.IPathResolver"/>, which is
    /// what decides where the node path stops and the file path starts.
    /// </summary>
    private async Task<(string Space, string Deck, string Slide)> CreateDeck(string slideBody)
    {
        var space = $"Space{Guid.NewGuid():N}"[..16];
        var deck = $"{space}/pitch";
        var slide = $"{deck}/intro";

        await NodeFactory.CreateNode(MeshNode.FromPath(space) with
        {
            Name = "Asset Space",
            NodeType = SpaceNodeType.NodeType,
            Content = new Space()
        }).Should().Emit();

        await NodeFactory.CreateNode(MeshNode.FromPath(deck) with
        {
            Name = "Pitch",
            NodeType = DeckNodeType.NodeType,
            Content = new DeckContent { Title = "Pitch", Slides = [slide] }
        }).Should().Emit();

        await NodeFactory.CreateNode(MeshNode.FromPath(slide) with
        {
            Name = "Intro",
            NodeType = SlideNodeType.NodeType,
            Order = 1,
            Content = new SlideContent { Content = slideBody }
        }).Should().Emit();

        return (space, deck, slide);
    }

    /// <summary>
    /// Composes the print document for one slide exactly as the export template does — the slide's
    /// own path is both the markdown pipeline's node path and its collection — and inlines it.
    /// </summary>
    private async Task<SlideAssetInlining> Inline(string space, string slidePath, string markdown)
    {
        var html = SlidePrintComposer.Compose(
            "Pitch", [new PrintSlide(new SlideContent { Content = markdown }, slidePath, slidePath)]);
        foreach (var reference in SlidePrintComposer.CollectAssetReferences(html))
            Output.WriteLine($"reference: {reference}");

        var hub = Mesh.GetHostedHub(new Address(space), HostedHubCreation.Always)!;
        return await SlideAssetInliner.Inline(html, hub)
            .FirstAsync().ToTask(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Stores bytes at the location an <c>/api/content/…</c> reference names, using the product's
    /// own resolution — the same one <c>MeshOperations.Upload</c> and the content route use. The
    /// test therefore never encodes its own opinion about where a node's file lives on disk.
    /// </summary>
    private async Task StoreAsset(string reference, byte[] bytes)
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await ContentFileResolver.Resolve(Mesh, reference)
            .FirstAsync().ToTask(ct);
        result.Resolution.Should().NotBeNull(
            because: $"the test cannot store '{reference}' if the product cannot say where it goes "
                     + $"({result.Reason})");

        var resolution = result.Resolution!;
        var contentService = Mesh.ServiceProvider.GetRequiredService<IContentService>();
        contentService.AddConfiguration(resolution.QualifiedConfig);
        var collection = await contentService.GetCollection(resolution.QualifiedName)
            .FirstAsync().ToTask(ct);
        collection.Should().NotBeNull();

        var directory = Path.GetDirectoryName(resolution.FilePath)?.Replace('\\', '/') ?? "";
        await collection!
            .SaveFile(directory, Path.GetFileName(resolution.FilePath), () => new MemoryStream(bytes))
            .ToTask(ct);
        Output.WriteLine(
            $"stored '{reference}' → collection '{resolution.QualifiedName}' at '{resolution.FilePath}'");
    }

    /// <summary>
    /// A real 1×1 PNG whose single pixel byte varies, so a test can tell one asset's bytes from
    /// another's in the composed document.
    /// </summary>
    private static byte[] Payload(byte seed) =>
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
        0x08, 0x02, 0x00, 0x00, 0x00, 0x90, 0x77, 0x53, 0xDE,
        0x00, 0x00, 0x00, 0x0C, 0x49, 0x44, 0x41, 0x54,
        0x08, 0xD7, 0x63, seed, 0xCF, 0xC0, 0x00, 0x00, 0x03, 0x01, 0x01, 0x00,
        0x18, 0xDD, 0x8D, 0xB0,
        0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82
    ];
}
