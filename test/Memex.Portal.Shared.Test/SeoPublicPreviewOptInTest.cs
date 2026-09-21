using System.Threading.Tasks;
using Memex.Portal.Shared.Api;
using Memex.Portal.Shared.Seo;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 THE OPT-IN: <see cref="PartitionAccessPolicy.PublicPreview"/>, which lets a page whose content
/// stays GATED describe ITSELF in a link preview — its own name, its own authored summary, its own
/// mark — to whoever holds the URL.
///
/// <para><b>Why it is an opt-in and not a behaviour.</b> A blanket version of this would be a
/// disclosure surface wearing a feature's colours — the same objection that made <c>/health</c> print
/// the PARTITION rather than the node's own name (#4258/#3890). The difference here is consent: the
/// owner of the data states it, per scope, in the same <c>_Policy</c> that governs everything else
/// about that subtree. So the control that matters most in this file is the NEGATIVE one — with the
/// flag unset, which is every partition until somebody sets it, nothing changes at all.</para>
///
/// <para><b>What it is measured against.</b> A link into a partition that is gated all the way up:
/// on a live control instance, <c>/PG3/LocalHardwareOffer</c> AND <c>/PG3</c> both unfurled as the
/// bare site card, so there was no public ancestor anywhere on that chain and the ancestor fallback
/// (<see cref="SeoAncestorCard"/>) correctly had nothing to offer. The fixture below is that shape:
/// <c>Offers</c> has no grant of any kind, and only the flag makes its pages say anything.</para>
/// </summary>
public class SeoPublicPreviewOptInTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string OfferName = "Local Hardware Offer";
    private const string OfferSummary = "Hardware, sourced locally, quoted per quarter.";
    private const string OfferBody = "BODY-THAT-MUST-NEVER-BE-DISCLOSED";
    private const string SealedName = "SEALED-NAME";
    private const string Mark =
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 48 48'>"
        + "<rect width='48' height='48' rx='10' fill='#0b7'/></svg>";

    // The usual test configuration grants Public Admin, which would make every path here readable
    // and every control in this file vacuous.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder).AddMeshNodes(
            // OPTED IN: gated all the way up — no grant anywhere — and the owner has said its page
            // names and summaries may travel.
            new MeshNode("Offers") { NodeType = "Markdown", Name = "Offers" },
            AssignmentNodeFactory.Policy("Offers", new PartitionAccessPolicy
            {
                PublicPreview = true,
                RedirectOnDenied = "Offers/Subscribe",
            }),
            new MeshNode("LocalHardware", "Offers")
            {
                NodeType = "Markdown", Name = OfferName, Description = OfferSummary, Icon = Mark,
                Content = System.Text.Json.JsonSerializer.SerializeToElement(
                    new { content = OfferBody }),
            },
            // 🚨 The shapes a review found this feature broken for, and it is the COMMON pair: a store
            // plugin authors its og image as /api/content/… and its icon as a content: reference.
            // Both routes still require Read, so declaring either for a gated node promises a picture
            // that 404s — worse than none, because unfurlers drop a preview whose image fails.
            new MeshNode("Authored", "Offers")
            {
                NodeType = "Markdown", Name = "Authored Offer", Description = "Authored art.",
                Icon = "content:brand/mark.svg",
                Content = System.Text.Json.JsonSerializer.SerializeToElement(
                    new { ogImage = "/api/content/Offers/content/og.png" }),
            },
            // The other side: an image on SOMEBODY ELSE's host is fetchable on its own terms.
            new MeshNode("Offsite", "Offers")
            {
                NodeType = "Markdown", Name = "Offsite Offer",
                Content = System.Text.Json.JsonSerializer.SerializeToElement(
                    new { ogImage = "https://cdn.example.org/offer.png" }),
            },

            // A deeper scope opting back OUT of the inherited flag.
            new MeshNode("Sealed", "Offers") { NodeType = "Markdown", Name = SealedName },
            AssignmentNodeFactory.Policy("Offers/Sealed",
                new PartitionAccessPolicy { PublicPreview = false }),
            new MeshNode("Bid", "Offers/Sealed") { NodeType = "Markdown", Name = "SEALED-BID-NAME" },

            // NOT opted in — the control. Same shape, no flag anywhere.
            new MeshNode("Vault") { NodeType = "Markdown", Name = "VAULT-NAME" },
            new MeshNode("Ledger", "Vault")
            {
                NodeType = "Markdown", Name = "LEDGER-NAME", Description = "LEDGER-SUMMARY",
                Icon = Mark,
            },

            // Public, for the no-regression side: a readable page must behave exactly as before.
            new MeshNode("Open") { NodeType = "Markdown", Name = "Open", Description = "Open page." },
            AssignmentNodeFactory.Policy("Open", new PartitionAccessPolicy { PublicRead = true }));

    private Task<SeoPreviewCard?> Preview(string path, string? locale = null) =>
        SeoResolver.ResolvePreview(Mesh, path, locale).Should().Emit();

    private Task<ShareableNode?> Shareable(string path) =>
        SeoResolver.ResolveShareableNode(Mesh, path).Should().Emit();

    private Task<SeoPageData?> Page(string path) => SeoResolver.Resolve(Mesh, path).Should().Emit();

    private Task<bool> Flag(string path) => Mesh.GetPublicPreview(path).Should().Emit();

    /// <summary>
    /// 🚨 THE FEATURE, in the user's own words: couldn't it say the name of the node? And the
    /// description? And the icon? On an opted-in scope it does — and the page is STILL gated.
    /// </summary>
    [Fact]
    public async Task AnOptedInGatedPage_DescribesItself()
    {
        var card = await Preview("Offers/LocalHardware");

        Assert.NotNull(card);
        Assert.Equal(OfferName, card.Title);
        Assert.Equal("Offers/LocalHardware", card.NodePath);
        Assert.StartsWith(OfferSummary, card.Description);
        // Its OWN card, not an ancestor's and not the site's.
        Assert.Equal("/api/og/Offers/LocalHardware.png", card.Image);
        // Its OWN mark: the svg data URI plus the two raster channels Safari needs.
        Assert.NotEmpty(card.Icons);
        Assert.Contains(card.Icons, i => i.Href.StartsWith("data:image/svg+xml", System.StringComparison.Ordinal));
        Assert.Contains(card.Icons, i => i.Href == "/api/icon/Offers/LocalHardware.png?size=32");
    }

    /// <summary>
    /// 🚨 THE CONTROL THAT MATTERS MOST: with no flag anywhere, every answer is what it was before
    /// this feature existed — no preview card, no shareable node for the image routes, and the page
    /// still withheld. A node with a name, a summary AND a mark is used, so the test would fail if
    /// any of the three leaked.
    /// </summary>
    [Fact]
    public async Task WithoutTheFlag_NothingIsDisclosed()
    {
        Assert.False(await Flag("Vault/Ledger"));
        Assert.Null(await Preview("Vault/Ledger"));
        Assert.Null(await Shareable("Vault/Ledger"));
        Assert.Null(await Page("Vault/Ledger"));
    }

    /// <summary>
    /// The page stays GATED on an opted-in scope — the flag is a disclosure decision, never a read
    /// grant. <see cref="SeoResolver.Resolve"/> is what every content-serving consumer asks, and it
    /// still refuses, which is also what keeps the node out of the published surface and the sitemap
    /// (<c>PublicSite</c> reads that same call).
    /// </summary>
    [Fact]
    public async Task OptingIn_GrantsNoRead()
    {
        Assert.True(await Flag("Offers/LocalHardware"));
        Assert.Null(await Page("Offers/LocalHardware"));
    }

    /// <summary>
    /// 🚨 THE BODY IS NEVER DISCLOSED, and the card is structurally incapable of carrying it: a
    /// <see cref="SeoPreviewCard"/> holds four strings and the icon links, no
    /// <see cref="MeshNode"/> and no <see cref="SeoPageData"/> — so the crawler-facing body
    /// component, which renders a <c>SeoPageData</c>, has nothing to render from. The summary that
    /// IS disclosed is an authored summary: <see cref="SeoResolver.ExtractDescription"/> reads six
    /// summary members and never <c>content</c>.
    /// </summary>
    [Fact]
    public async Task TheBodyIsNeverDisclosed_EvenWhenOptedIn()
    {
        var card = await Preview("Offers/LocalHardware");

        Assert.NotNull(card);
        Assert.DoesNotContain(OfferBody, card.Description ?? "");
        Assert.DoesNotContain(OfferBody, card.Title);
        // And the one type that CAN carry a body is still refused for this path.
        Assert.Null(await Page("Offers/LocalHardware"));
    }

    /// <summary>
    /// The flag inherits down a partition, nearest scope first — so a root opts its whole subtree in
    /// with one line, and a deeper scope opts back OUT with an explicit <c>false</c>, which then
    /// governs everything under IT too.
    /// </summary>
    [Fact]
    public async Task TheFlagInheritsDown_AndADeeperScopeCanOptBackOut()
    {
        Assert.True(await Flag("Offers"));
        Assert.True(await Flag("Offers/LocalHardware"));
        Assert.False(await Flag("Offers/Sealed"));
        Assert.False(await Flag("Offers/Sealed/Bid"));
    }

    /// <summary>
    /// The opted-out subtree discloses nothing — the names under it never reach a card.
    /// </summary>
    [Fact]
    public async Task AnOptedOutSubtree_DisclosesNothing()
    {
        Assert.Null(await Preview("Offers/Sealed"));
        Assert.Null(await Preview("Offers/Sealed/Bid"));
        Assert.Null(await Shareable("Offers/Sealed/Bid"));
    }

    /// <summary>
    /// 🚨 THE PICTURE HAS TO BE FETCHABLE. Both image routes ask
    /// <see cref="SeoResolver.ResolveShareableNode"/> — the same one predicate the head's card block
    /// asks — so a previewed page's declared <c>og:image</c> and icon actually resolve. Without this
    /// the head would promise a card that 404s, and several unfurlers drop the whole preview when the
    /// image they were promised does not fetch.
    /// </summary>
    [Fact]
    public async Task TheImageRoutesServeAPreviewedNode_SoTheDeclaredCardFetches()
    {
        var cleared = await Shareable("Offers/LocalHardware");

        Assert.NotNull(cleared);
        Assert.Equal("Offers/LocalHardware", cleared.Node.Path);
        // Cleared by the POLICY, not by the gate — which is what decides its cache directive.
        Assert.False(cleared.AnonymousReadable);
        // The endpoint's own mapping, from the node the predicate cleared: name and authored summary
        // reach the drawn card, and nothing else can.
        var card = SeoEndpoints.CardContent(cleared.Node);
        Assert.Equal(OfferName, card.Title);
        Assert.Equal(OfferSummary, card.Description);
        Assert.DoesNotContain(OfferBody, card.Description ?? "");
        // And the icon route answers with a real PNG rather than the 404 it gave before.
        var icon = SeoEndpoints.IconResult(
            new DefaultHttpContext(), cleared.Node, 32, null, cleared.AnonymousReadable);
        Assert.IsType<FileContentHttpResult>(icon);
    }

    /// <summary>
    /// A readable page is untouched by any of this: same card, same description, same drawn image,
    /// and it never consults the flag (it cannot tell whether one is set).
    /// </summary>
    [Fact]
    public async Task APublicPage_IsUnaffected()
    {
        var page = await Page("Open");

        Assert.NotNull(page);
        Assert.Equal("Open", page.Node.Name);
        Assert.Equal("Open page.", page.Description);
        Assert.Equal("/api/og/Open.png", page.Image);
        // The preview surface answers null for it — a readable page has no use for a preview card,
        // and the caller already has Resolve.
        Assert.Null(await Preview("Open"));
        // The image routes still serve it, as they always did.
        Assert.NotNull(await Shareable("Open"));
    }

    /// <summary>
    /// The partition's call to action rides along when its policy declares a
    /// <see cref="PartitionAccessPolicy.RedirectOnDenied"/> — the same localized sentence and the
    /// same key the ancestor card uses, so a previewed page tells the reader there is a way in. The
    /// redirect TARGET is not named: it is not in the URL the sharer pasted.
    /// </summary>
    [Fact]
    public async Task TheCallToAction_RidesAlong_AndFollowsTheViewersLanguage()
    {
        var english = await Preview("Offers/LocalHardware");
        var german = await Preview("Offers/LocalHardware", "de-CH");

        Assert.NotNull(english);
        Assert.NotNull(german);
        Assert.Contains(LocalizationCatalog.Get(SeoResolver.CallToActionKey, null), english.Description ?? "");
        Assert.Contains(LocalizationCatalog.Get(SeoResolver.CallToActionKey, "de-CH"), german.Description ?? "");
        Assert.DoesNotContain("Subscribe", english.Description ?? "");
    }

    /// <summary>
    /// A path that matches no node discloses nothing and costs no policy read — there is no node to
    /// describe, and the flag is never consulted for one.
    /// </summary>
    [Fact]
    public async Task APathThatMatchesNoNode_DisclosesNothing()
    {
        Assert.Null(await Preview("NoSuchPartition/NoSuchPage"));
        Assert.Null(await Shareable("NoSuchPartition/NoSuchPage"));
    }

    /// <summary>
    /// The composition is PURE and takes the node, so what a previewed page can say is decided in
    /// one place: no summary means the card carries the call to action alone rather than an empty
    /// string, and a node with no mark yields no icon links rather than a synthesised one.
    /// </summary>
    [Fact]
    public void ComposePreviewCard_InventsNothing()
    {
        var bare = new MeshNode("Offers/Bare") { NodeType = "Markdown", Name = "Bare" };

        var alone = SeoResolver.ComposePreviewCard(bare, null);
        Assert.Equal("Bare", alone.Title);
        Assert.Null(alone.Description);
        Assert.Empty(alone.Icons);

        Assert.Equal("go on", SeoResolver.ComposePreviewCard(bare, "go on").Description);
    }

    /// <summary>
    /// 🚨 A REVIEW FINDING, KEPT AS A TEST: an authored image on the portal's own
    /// <c>/api/content/…</c> route is <c>Read</c>-gated, which this opt-in deliberately does not open
    /// (that route serves file BYTES, not the four strings the flag consents to). Declaring it for a
    /// gated node would promise an <c>og:image</c> anonymous unfurlers get a 404 for — worse than no
    /// card, since several then drop the preview entirely. The preview therefore falls back to the
    /// DRAWN card, which the same flag does serve.
    /// </summary>
    [Fact]
    public async Task AnAuthoredImageOnTheContentRoute_FallsBackToTheDrawnCard()
    {
        var card = await Preview("Offers/Authored");

        Assert.NotNull(card);
        Assert.Equal("/api/og/Offers/Authored.png", card.Image);
        Assert.DoesNotContain("/api/content/", card.Image);
        // The card still says what it is — only the picture changed.
        Assert.Equal("Authored Offer", card.Title);
        Assert.StartsWith("Authored art.", card.Description);
    }

    /// <summary>
    /// The other side of that control: an ABSOLUTE authored image is another host's business, fetchable
    /// or not on its own terms, and nothing here can make it worse — so it is kept, exactly as a public
    /// page keeps it.
    /// </summary>
    [Fact]
    public async Task AnAbsoluteAuthoredImage_IsKept()
    {
        var card = await Preview("Offers/Offsite");

        Assert.NotNull(card);
        Assert.Equal("https://cdn.example.org/offer.png", card.Image);
    }

    /// <summary>
    /// 🚨 THE SECOND REVIEW FINDING: a <c>content:</c> icon resolves to <c>/api/content/…</c>, still
    /// <c>Read</c>-gated, and <c>ResolveIconLinks</c> yields that ONE link and no raster channels for
    /// it — so a previewed page would publish exactly one icon link and it would be broken. Such a
    /// node gets NO icon link, which is the honest fallback the icon route already documents: the
    /// portal favicon stays rather than a link that 404s.
    /// </summary>
    [Fact]
    public async Task AContentBackedIcon_YieldsNoIconLinkAtAll()
    {
        var card = await Preview("Offers/Authored");

        Assert.NotNull(card);
        Assert.Empty(card.Icons);
        Assert.DoesNotContain(card.Icons, i => i.Href.Contains("/api/content/", System.StringComparison.Ordinal));
    }

    /// <summary>
    /// 🚨 THE CACHE DIRECTIVE FOLLOWS THE DECISION, because a preview is REVOCABLE and a shared cache
    /// never re-asks the origin: a day of <c>public, max-age</c> would leave a withdrawn disclosure
    /// publicly retrievable after its owner withdrew it. A gate-admitted node keeps the shared
    /// directive it has always had.
    /// </summary>
    [Fact]
    public async Task APreviewedPictureIsNotSharedCacheable_AndAPublicOneStillIs()
    {
        var previewed = await Shareable("Offers/LocalHardware");
        var open = await Shareable("Open");

        Assert.NotNull(previewed);
        Assert.NotNull(open);
        Assert.False(previewed.AnonymousReadable);
        Assert.True(open.AnonymousReadable);

        // The endpoint's OWN header decision, reached from the node it cleared — not a copy of the
        // rule beside it.
        var previewHttp = new DefaultHttpContext();
        SeoEndpoints.IconResult(previewHttp, previewed.Node, 32, null, previewed.AnonymousReadable);
        Assert.Equal("private, no-store", previewHttp.Response.Headers.CacheControl.ToString());

        var publicHttp = new DefaultHttpContext();
        SeoEndpoints.IconResult(publicHttp, WithMark(open.Node), 32, null, open.AnonymousReadable);
        Assert.Equal("public, max-age=86400", publicHttp.Response.Headers.CacheControl.ToString());
    }

    /// <summary>The public fixture node carries no mark, so give it one for the header assertion —
    /// <c>IconResult</c> 404s (and sets no cache header) for a node it cannot draw.</summary>
    private static MeshNode WithMark(MeshNode node) => node with { Icon = Mark };
}
