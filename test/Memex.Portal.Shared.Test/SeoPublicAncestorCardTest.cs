using System.Threading.Tasks;
using Memex.Portal.Shared.Seo;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 WHAT A LINK INTO A GATED SUBTREE UNFURLS AS — the whole feature, gate included, on a live
/// mesh.
///
/// <para><b>The measurement that motivates it.</b> On <c>www.meshweaver.cloud</c>, 2026-09-20:
/// <c>/PG3Reporting</c> unfurled completely (<c>og:title "Fund Reporting"</c>, a description,
/// <c>og:image /api/og/PG3Reporting.png</c>) while every descendant — <c>…/Funds</c>,
/// <c>…/Funds/InsuranceCore</c>, <c>…/Funds/InsuranceCore/2026-06-30</c> — fell back to
/// <c>og:title "MeshWeaver"</c> and <c>/api/og.png</c>. It was never depth: deep public pages
/// (<c>/Doc/Architecture/AccessControl</c>) unfurled fully on the same host at the same minute. It
/// was ACCESS — a public root over gated content — so the fixture below is that shape and not a
/// deep one: <c>Reporting</c> carries <c>PublicRead</c>, <c>Reporting/Funds</c> caps Read off, and
/// the nodes under it are the ones a share link names.</para>
///
/// <para><b>Controls on BOTH sides.</b> A public page still resolves exactly as it did
/// (<see cref="APublicPage_StillResolvesItsOwnCard"/>) — this feature must be invisible to every
/// page that already unfurled. A gated page under a public root gets the ancestor's card; a gated
/// page with NO public ancestor still gets nothing, which is what keeps the caller on the site
/// card. And the security control names the withheld nodes' own words and asserts they are in NO
/// part of the card — the one assertion that would catch this feature turning into a disclosure.</para>
/// </summary>
public class SeoPublicAncestorCardTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // The withheld nodes' own words. They are deliberately unmistakable: the security control
    // searches the WHOLE card for them, so a substring of a real sentence would not do.
    private const string WithheldFundName = "WITHHELD-FUND-NAME";
    private const string WithheldFundDescription = "WITHHELD-FUND-DESCRIPTION";
    private const string WithheldQuarterName = "WITHHELD-QUARTER-NAME";
    private const string WithheldQuarterDescription = "WITHHELD-QUARTER-DESCRIPTION";

    private const string PublicRootDescription = "Quarterly fund reporting, one record per quarter.";

    // The usual test configuration grants Public Admin, which would make every path below readable
    // and every control here vacuous.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder).AddMeshNodes(
            // A PUBLIC listing over GATED content — the measured shape.
            new MeshNode("Reporting")
            {
                NodeType = "Markdown", Name = "Fund Reporting", Description = PublicRootDescription,
            },
            AssignmentNodeFactory.Policy("Reporting", new PartitionAccessPolicy
            {
                PublicRead = true,
                // The owner SAYING there is a way in. This is what earns the call to action.
                RedirectOnDenied = "Reporting/Subscribe",
            }),
            new MeshNode("Funds", "Reporting")
            {
                NodeType = "Markdown", Name = WithheldFundName, Description = WithheldFundDescription,
            },
            AssignmentNodeFactory.Policy("Reporting/Funds", new PartitionAccessPolicy { Read = false }),
            new MeshNode("InsuranceCore", "Reporting/Funds")
            {
                NodeType = "Markdown", Name = WithheldQuarterName,
                Description = WithheldQuarterDescription,
            },

            // A public root with NO redirect: a card, but no advice that leads nowhere.
            new MeshNode("Library") { NodeType = "Markdown", Name = "Library", Description = "Open shelves." },
            AssignmentNodeFactory.Policy("Library", new PartitionAccessPolicy { PublicRead = true }),
            new MeshNode("Closed", "Library") { NodeType = "Markdown", Name = "WITHHELD-CLOSED-NAME" },
            AssignmentNodeFactory.Policy("Library/Closed", new PartitionAccessPolicy { Read = false }),

            // Private all the way up: no grant anywhere, so anonymous is denied by default.
            new MeshNode("Vault") { NodeType = "Markdown", Name = "WITHHELD-VAULT-NAME" },
            new MeshNode("Ledger", "Vault") { NodeType = "Markdown", Name = "WITHHELD-LEDGER-NAME" });

    private Task<SeoPageData?> Page(string path) =>
        SeoResolver.Resolve(Mesh, path).Should().Emit();

    private Task<SeoAncestorCard?> Card(string path, string? locale = null) =>
        SeoResolver.ResolvePublicAncestor(Mesh, path, locale).Should().Emit();

    /// <summary>
    /// THE REGRESSION CONTROL, and the other side of the change: a page the gate admits resolves to
    /// its OWN card exactly as before — same title, same description, same drawn card — and never
    /// reaches the ancestor walk at all.
    /// </summary>
    [Fact]
    public async Task APublicPage_StillResolvesItsOwnCard()
    {
        var page = await Page("Reporting");

        Assert.NotNull(page);
        Assert.Equal("Fund Reporting", page.Node.Name);
        Assert.Equal(PublicRootDescription, page.Description);
        Assert.Equal("/api/og/Reporting.png", page.Image);
        Assert.Null(page.Remainder);
    }

    /// <summary>
    /// The gated page is still withheld — this feature changes NOTHING about who may read what. It
    /// changes only what is said about a page nobody may read.
    /// </summary>
    [Fact]
    public async Task AGatedPage_IsStillWithheld()
    {
        Assert.Null(await Page("Reporting/Funds"));
        Assert.Null(await Page("Reporting/Funds/InsuranceCore"));
    }

    /// <summary>
    /// 🚨 THE FEATURE: the nearest public ancestor's card, captioned with the path segments the
    /// sharer pasted, and the partition's call to action because its policy declares a redirect.
    /// </summary>
    [Fact]
    public async Task AGatedPageUnderAPublicRoot_GetsTheAncestorsCard()
    {
        var card = await Card("Reporting/Funds/InsuranceCore");

        Assert.NotNull(card);
        Assert.Equal("Reporting", card.AncestorPath);
        Assert.Equal("Fund Reporting · Funds / InsuranceCore", card.Title);
        Assert.Equal("/api/og/Reporting.png", card.Image);
        Assert.Equal(
            PublicRootDescription + " This page is not public. Follow the link to sign in or get access.",
            card.Description);
    }

    /// <summary>
    /// 🚨 THE SECURITY CONTROL. The card is built from a node the gate ADMITTED plus the URL's own
    /// segments; the withheld nodes' names and descriptions must appear in NO field of it. A tail
    /// that reads "Funds / InsuranceCore" is the URL, which the sharer pasted and the reader is
    /// already looking at — the node BEHIND those segments is called something else, and that is
    /// what must not be here.
    /// </summary>
    [Fact]
    public async Task TheWithheldNodesOwnWords_AppearNowhereOnTheCard()
    {
        var card = await Card("Reporting/Funds/InsuranceCore");

        Assert.NotNull(card);
        var everything = string.Join(
            "\n", card.Title, card.Description ?? "", card.Image, card.AncestorPath);
        foreach (var withheld in new[]
                 {
                     WithheldFundName, WithheldFundDescription,
                     WithheldQuarterName, WithheldQuarterDescription,
                 })
        {
            Assert.DoesNotContain(withheld, everything, System.StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The redirect target is NOT on the card. It is the one piece of this that is not already in
    /// the URL the sharer pasted, so printing it would disclose something new — and the link on the
    /// card already goes there for whoever clicks it.
    /// </summary>
    [Fact]
    public async Task TheRedirectTarget_IsNotNamedOnTheCard()
    {
        var card = await Card("Reporting/Funds/InsuranceCore");

        Assert.NotNull(card);
        Assert.DoesNotContain("Subscribe", card.Description ?? "");
        Assert.DoesNotContain("Subscribe", card.Title);
    }

    /// <summary>
    /// A public root that offers no way in gets the card and NO call to action: without a
    /// <see cref="PartitionAccessPolicy.RedirectOnDenied"/> nobody has published a route, and
    /// "sign in" would be advice that leads nowhere.
    /// </summary>
    [Fact]
    public async Task WithoutARedirectOnDenied_TheCardCarriesNoCallToAction()
    {
        var card = await Card("Library/Closed");

        Assert.NotNull(card);
        Assert.Equal("Library · Closed", card.Title);
        Assert.Equal("Open shelves.", card.Description);
    }

    /// <summary>
    /// 🚨 THE HONEST FLOOR: nothing above it is public either, so there is no card — and the caller
    /// keeps the site card it already had. A private page under a private root says only what its
    /// URL already said.
    /// </summary>
    [Fact]
    public async Task AGatedPageWithNoPublicAncestor_GetsNoCard()
    {
        Assert.Null(await Card("Vault/Ledger"));
    }

    /// <summary>
    /// A URL that matches no node at any prefix costs ONE resolution and stops. <c>ResolvePath</c>
    /// already falls back to the nearest EXISTING ancestor, so "no resolution" means there is
    /// nothing above it to find — not that the walk should start guessing at URL segments.
    /// </summary>
    [Fact]
    public async Task APathThatMatchesNoNode_GetsNoCard()
    {
        Assert.Null(await Card("NoSuchPartition/NoSuchPage"));
    }

    /// <summary>
    /// The call to action is platform-owned text, so it follows the VIEWER's language — resolved
    /// from the locale the caller reads off the AccessContext, never from an ambient culture. The
    /// ancestor's own description is AUTHORED content and stays exactly as authored.
    /// </summary>
    [Fact]
    public async Task TheCallToAction_FollowsTheViewersLanguage()
    {
        var card = await Card("Reporting/Funds/InsuranceCore", "de-CH");

        Assert.NotNull(card);
        Assert.Contains(PublicRootDescription, card.Description ?? "");
        Assert.Contains(
            LocalizationCatalog.Get(SeoResolver.CallToActionKey, "de-CH"), card.Description ?? "");
        Assert.DoesNotContain("not public", card.Description ?? "");
    }

    /// <summary>
    /// A layout-area route under a gated node — <c>/{node}/{area}/{id}</c> — shares the same card as
    /// the node: the resolution falls back to the deepest node that EXISTS, and the walk starts
    /// above THAT, so an area name in the URL cannot smuggle the walk past a gate.
    /// </summary>
    [Fact]
    public async Task ALayoutAreaRouteUnderAGatedNode_GetsTheSameAncestorCard()
    {
        var card = await Card("Reporting/Funds/InsuranceCore/Holdings/main");

        Assert.NotNull(card);
        Assert.Equal("Reporting", card.AncestorPath);
        Assert.Equal("Fund Reporting · Funds / InsuranceCore / Holdings / main", card.Title);
    }

    /// <summary>
    /// The composition is PURE and is given only a page the gate admitted: no ancestor description
    /// means the card carries the call to action alone rather than an empty one, and a request for
    /// the ancestor ITSELF carries no caption to add.
    /// </summary>
    [Fact]
    public void ComposeAncestorCard_HandlesTheEdgesWithoutInventingText()
    {
        var bare = new SeoPageData(
            new MeshNode("Root") { Name = "Root", NodeType = "Markdown" }, null, "/api/og/Root.png");

        Assert.Equal("Root · Deep / Page", SeoResolver.ComposeAncestorCard(bare, "Root/Deep/Page", null).Title);
        Assert.Null(SeoResolver.ComposeAncestorCard(bare, "Root/Deep", null).Description);
        Assert.Equal("go on", SeoResolver.ComposeAncestorCard(bare, "Root/Deep", "go on").Description);
        Assert.Equal("Root", SeoResolver.ComposeAncestorCard(bare, "Root", null).Title);
    }
}
