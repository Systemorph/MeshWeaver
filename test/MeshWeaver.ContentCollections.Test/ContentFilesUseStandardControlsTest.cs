using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Fixture;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.ContentCollections.Test;

/// <summary>
/// A content file renders through platform controls, never hand-built markup: a picture — SVG
/// included — is an <see cref="ImageControl"/> carrying a <c>data:image/…</c> URI (so markup in an
/// uploaded SVG runs no script and the file is never put on the icon backplate), and a PDF is an
/// <see cref="EmbedControl"/> of kind <see cref="EmbedKind.Document"/> on the access-controlled content
/// route with a download <see cref="LinkControl"/> beside it. Each case boots a hub, renders the real
/// <c>$Content</c> area over a remote stream, and walks its child areas.
/// </summary>
public class ContentFilesUseStandardControlsTest : HubTestBase
{
    private readonly string _basePath = Path.Combine(AppContext.BaseDirectory, "Files", "StandardControls");

    public ContentFilesUseStandardControlsTest(ITestOutputHelper output) : base(output)
    {
        Directory.CreateDirectory(_basePath);
        File.WriteAllBytes(Path.Combine(_basePath, "photo.png"), [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        File.WriteAllText(Path.Combine(_basePath, "diagram.svg"),
            "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 10 10'><script>alert(1)</script><rect width='10' height='10'/></svg>");
        File.WriteAllBytes(Path.Combine(_basePath, "annual report.pdf"), "%PDF-1.4"u8.ToArray());
    }

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration).AddContentCollections();

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .AddContentCollections()
            .AddContentCollection(_ => new ContentCollectionConfig
            {
                Name = "content",
                SourceType = "FileSystem",
                BasePath = _basePath,
                Settings = new Dictionary<string, string> { ["BasePath"] = _basePath }
            });

    private async Task<List<UiControl>> Render(string id)
    {
        // A hub cannot GetRemoteStream its own address — subscribe from the HOST hub.
        var stream = GetHost().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            GetClient().Address, new LayoutAreaReference(ContentCollectionsExtensions.ContentAreaName) { Id = id });
        var ct = TestContext.Current.CancellationToken;
        var controls = new List<UiControl>();
        var queue = new Queue<string>([ContentCollectionsExtensions.ContentAreaName]);
        while (queue.Count > 0)
        {
            var area = queue.Dequeue();
            var control = await stream.GetControlStream(area).Where(c => c is not null).Take(1)
                .Timeout(TimeSpan.FromSeconds(30)).Await(ct);
            controls.Add(control!);
            if (control is IContainerControl container)
                foreach (var child in container.Areas.Select(a => a.Area?.ToString()).OfType<string>())
                    queue.Enqueue(child);
        }
        Output.WriteLine(string.Join(", ", controls.Select(c => c.GetType().Name)));
        return controls;
    }

    [Fact]
    public async Task APicture_IsAnImageControl_WithADataUri()
    {
        var controls = await Render("content/photo.png");
        var image = controls.OfType<ImageControl>().Should().ContainSingle().Subject;
        var source = image.Source.Should().BeOfType<Icon>().Subject;
        source.Id.Should().StartWith("data:image/png;base64,");
        ImageSources.Resolve(source).Kind.Should().Be(ImageSourceKind.Url, "a renderer draws it as a picture");
        (image.MaxWidth?.ToString()).Should().Be("100%");
        controls.OfType<HtmlControl>().Should().BeEmpty();
    }

    /// <summary>The SVG file is a data-URI PICTURE: never inline markup (its script would run) and never the icon plate.</summary>
    [Fact]
    public async Task AnSvgFile_IsAnImageDataUri_NeverInlineMarkup()
    {
        var controls = await Render("content/diagram.svg");
        var image = controls.OfType<ImageControl>().Should().ContainSingle().Subject;
        var source = image.Source.Should().BeOfType<Icon>().Subject;
        source.Id.Should().StartWith("data:image/svg+xml;base64,");
        ImageSources.Resolve(source).Kind.Should().Be(ImageSourceKind.Url,
            "an SVG content file is a picture — the inline-svg ICON form (and its backplate) is for icons");
        controls.OfType<HtmlControl>().Should().BeEmpty("no uploaded markup reaches the page as markup");
    }

    [Fact]
    public async Task APdf_IsADocumentEmbed_WithADownloadLink()
    {
        var controls = await Render("content/annual%20report.pdf");
        var embed = controls.OfType<EmbedControl>().Should().ContainSingle().Subject;
        var src = embed.Src.ToString()!;
        (embed.Kind?.ToString()).Should().Be(EmbedKind.Document);
        embed.Title.ToString().Should().Be("annual report.pdf");
        src.Should().Contain("annual%20report.pdf", "the user-supplied name is URL-encoded");
        EmbedSources.SafeSrc(src, EmbedKind.Document).Should().Be(src,
            "the content route is a relative portal address, which a renderer frames");
        var link = controls.OfType<LinkControl>().Should().ContainSingle().Subject;
        link.Href.ToString().Should().Be(src + "?download");
        (link.Download?.ToString()).Should().Be("annual report.pdf");
        link.Text.ToString().Should().Be("Download PDF");
        controls.OfType<HtmlControl>().Should().BeEmpty();
    }
}
