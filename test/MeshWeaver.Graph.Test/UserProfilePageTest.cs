using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reflection;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The owner's profile page (<c>/{user}/EditProfile</c>): the picture control, the node-bound
/// basics (display name on <see cref="MeshNode.Name"/>, read-only sign-in email, language and time
/// zone), localized chrome, and the module extension point
/// (<see cref="ProfileSectionsExtensions"/>) — ordering, de-duplication and the permission gate.
/// Pure control-tree assertions; the picture's store round trip is
/// <see cref="ProfilePictureRoundTripTest"/>. Design: <c>Doc/GUI/ProfilePage</c>.
/// </summary>
public class UserProfilePageTest
{
    private const string NodePath = "rbuergi";
    private static readonly JsonSerializerOptions Options = new();

    private static MeshNode UserNode(User content, string? name = "Roland Bürgi") =>
        MeshNode.FromPath(NodePath) with { Name = name, NodeType = "User", Content = content };

    private static UiControl Editor(
        string? locale = null,
        IReadOnlyList<(ProfileSectionDefinition, UiControl)>? contributed = null)
        => UserActivityLayoutAreas.BuildProfileEditor(
            UserNode(new User { Email = "roland@example.com" }), NodePath, Options, locale, contributed);

    [Fact]
    public void Picture_IsTheNodeBoundUploadControl_ForTheUserNode()
    {
        var picture = Descendants(Editor()).OfType<NodeImageUploadControl>().Should().ContainSingle().Subject;

        picture.NodePath.Should().Be(NodePath, "the picture is the user node's own Icon");
        picture.CanEdit.Should().BeTrue();
        picture.DisplayName.Should().Be("Roland Bürgi", "initials fall back to the display name");
        picture.UploadLabel.Should().Be("Upload picture");
        picture.RemoveLabel.Should().Be("Remove picture");
    }

    [Fact]
    public void DisplayName_IsBoundToTheNodesOwnName_NotACopy()
    {
        var nameField = Descendants(Editor()).OfType<TextFieldControl>()
            .Should().ContainSingle(f => f.Data is JsonPointerReference { Pointer: nameof(MeshNode.Name) })
            .Subject;

        nameField.DataContext.Should().Be(
            LayoutAreaReference.GetMeshNodeDataContext(NodePath, bindContent: false),
            "the name is written straight onto the user node — never a /data replica plus a save");
        nameField.Label.Should().Be("Display name");
    }

    [Fact]
    public void SignInEmail_IsShown_ReadOnly()
    {
        var editors = Descendants(Editor()).OfType<MeshNodeContentEditorControl>().ToList();

        var email = editors.Should().ContainSingle(e => e.Fields.Any(f => f.Key == "email")).Subject;
        email.CanEdit.Should().BeFalse("the sign-in email comes from the identity provider");
        email.NodePath.Should().Be(NodePath);
    }

    [Fact]
    public void LanguageAndTimeZone_AreTheSameFieldsAsThePreferencesTab()
    {
        var prefs = Descendants(Editor()).OfType<MeshNodeContentEditorControl>()
            .Should().ContainSingle(e => e.CanEdit).Subject;

        prefs.Fields.Select(f => f.Key).Should().Equal("timeZoneId", "locale");
    }

    [Fact]
    public void Chrome_FollowsTheViewer_German()
    {
        var editor = Editor(locale: "de");

        Descendants(editor).OfType<NodeImageUploadControl>().Single().UploadLabel.Should().Be("Bild hochladen");
        Descendants(editor).OfType<TextFieldControl>()
            .Single(f => f.Data is JsonPointerReference { Pointer: nameof(MeshNode.Name) })
            .Label.Should().Be("Anzeigename");
    }

    [Fact]
    public void BuiltInSections_CarryStableIds_InOrder()
    {
        var ids = Descendants(Editor())
            .Select(c => c.Id as string)
            .Where(id => id is not null && id.StartsWith(UserActivityLayoutAreas.ProfileSectionIdPrefix))
            .ToList();

        ids.Should().Equal(
            "profile-section-picture", "profile-section-basics", "profile-section-bio",
            "profile-section-links", "profile-section-showcase");
    }

    [Fact]
    public void ContributedSections_RenderAfterTheBuiltIns_UnderTheirTitle()
    {
        var body = Controls.Markdown("plan: personal");
        var section = new ProfileSectionDefinition("subscription", "Subscription", (_, _) => body);

        var editor = Editor(contributed: [(section, body)]);

        var ids = Descendants(editor)
            .Select(c => c.Id as string)
            .Where(id => id is not null && id.StartsWith(UserActivityLayoutAreas.ProfileSectionIdPrefix))
            .ToList();
        ids.Last().Should().Be("profile-section-subscription");
        Descendants(editor).Should().Contain(body);
        Descendants(editor).OfType<LabelControl>()
            .Should().Contain(l => Equals(l.Data, "Subscription"), "the page supplies the section heading");
    }

    [Fact]
    public void Merge_SortsByOrder_AndTheFirstRegistrationOfAnIdWins()
    {
        UiControl Body() => Controls.Markdown("x");
        var first = new ProfileSectionDefinition("billing", "Billing", (_, _) => Body(), Order: 20);
        var duplicate = new ProfileSectionDefinition("billing", "Billing (again)", (_, _) => Body(), Order: 0);
        var early = new ProfileSectionDefinition("integrations", "Integrations", (_, _) => Body(), Order: 10);

        var merged = ProfileSectionsExtensions.Merge([[first], [duplicate, early]]);

        merged.Select(s => s.Title).Should().Equal("Integrations", "Billing");
    }

    [Fact]
    public void FilterByPermission_HidesOwnerSectionsFromAVisitor()
    {
        UiControl Body() => Controls.Markdown("x");
        var ownerOnly = new ProfileSectionDefinition("subscription", "Subscription", (_, _) => Body());
        var everyone = new ProfileSectionDefinition("badge", "Badge", (_, _) => Body(),
            RequiredPermission: Permission.None);

        ProfileSectionsExtensions.FilterByPermission([ownerOnly, everyone], Permission.Read)
            .Select(s => s.Id).Should().Equal("badge");
        ProfileSectionsExtensions.FilterByPermission([ownerOnly, everyone], Permission.Read | Permission.Update)
            .Select(s => s.Id).Should().Equal("subscription", "badge");
    }

    [Fact]
    public void AddProfileSections_AccumulatesOnTheHubConfiguration()
    {
        var a = new ProfileSectionDefinition("a", "A", (_, _) => Controls.Markdown("a"));
        var b = new ProfileSectionDefinition("b", "B", (_, _) => Controls.Markdown("b"));
        var config = new Messaging.MessageHubConfiguration(null!, new Messaging.Address("test", "1"))
            .AddProfileSections(a)
            .AddProfileSections(b);

        config.Get<ProfileSectionProviderCollection>()!.Providers.Should().HaveCount(2,
            "two modules contributing must both land — a record replaced, never a slot overwritten");
    }

    [Fact]
    public void PictureUrl_ResolvesContentReferences_AndIgnoresNonPictures()
    {
        MeshNodeImageHelper.ResolvePictureUrl("content:picture/a1.png", "rbuergi")
            .Should().Be("/api/content/rbuergi/picture/a1.png");
        MeshNodeImageHelper.ResolvePictureUrl("https://example.com/me.jpg", "rbuergi")
            .Should().Be("https://example.com/me.jpg");
        MeshNodeImageHelper.ResolvePictureUrl("🦊", "rbuergi").Should().BeNull("an emoji is not a picture");
        MeshNodeImageHelper.ResolvePictureUrl("<svg></svg>", "rbuergi").Should().BeNull();
        MeshNodeImageHelper.ResolvePictureUrl(null, "rbuergi").Should().BeNull();
    }

    [Theory]
    [InlineData("content:picture/hand-set.svg")]
    [InlineData("https://example.com/me.SVG?v=2")]
    [InlineData("data:image/svg+xml;base64,PHN2Zz48L3N2Zz4=")]
    public void PictureUrl_IsNeverAnSvg_InAnySpelling(string icon)
        => MeshNodeImageHelper.ResolvePictureUrl(icon, "rbuergi").Should().BeNull(
            "an SVG can carry script and the upload refuses it — the avatar falls back to initials");

    [Fact]
    public void ManagedFilePath_OnlyForServerGeneratedNames()
    {
        const string generated = "0123456789abcdef0123456789abcdef.png";
        ContentCollections.NodeImageUpload.ManagedFilePath($"content:picture/{generated}")
            .Should().Be($"picture/{generated}");
        ContentCollections.NodeImageUpload.ManagedFilePath("content:picture/me.png")
            .Should().BeNull("a file the owner put in picture/ by hand is theirs, never deleted");
        ContentCollections.NodeImageUpload.ManagedFilePath("content:picture/../secrets.png").Should().BeNull();
        ContentCollections.NodeImageUpload.ManagedFilePath("content:logo.png").Should().BeNull();
    }

    [Fact]
    public void ContributedSection_Icon_IsRenderedBesideItsHeading()
    {
        var body = Controls.Markdown("x");
        var icon = MeshWeaver.Application.Styles.FluentIcons.Payment();
        var section = new ProfileSectionDefinition("subscription", "Subscription", (_, _) => body, Icon: icon);

        Descendants(Editor(contributed: [(section, body)])).OfType<IconControl>()
            .Should().Contain(i => Equals(i.Data, icon));
    }

    [Fact]
    public void Localized_WithoutAnAccessService_KeepsTheTitleResolvable()
    {
        var section = new ProfileSectionDefinition("s", "Subscription", (_, _) => Controls.Markdown("x"))
        { TitleKey = "profile.picture" };

        section.Localized(null).Title.Should().Be("Picture",
            "a null AccessService resolves the key in English rather than throwing");
    }

    // ── content-driven sections (UiContribution, Context = Profile) ─────────────────────────────

    private static (MeshNode, UiContribution) Contribution(string id, UiContribution content)
        => (MeshNode.FromPath($"Store/ProfileSections/{id}") with { NodeType = UiContributionNodeType.NodeType, Content = content }, content);

    [Fact]
    public void ContentSection_EmbedsTheDeclaredAreaOfTheDeclaredAddress()
    {
        var sections = UiContributionProjection.ProjectProfileSections(
            [Contribution("subscription", new UiContribution
            {
                Context = UiContribution.ProfileContext,
                Address = "Store",
                Area = "MyPlan",
                Label = "Subscription",
                LabelKey = "profile.picture",
                Order = 7,
                RequiredPermission = Permission.Update,
            })],
            NodePath, UserNode(new User()), isAdmin: false, viewerId: NodePath);

        var section = sections.Should().ContainSingle().Subject;
        section.Id.Should().Be("subscription", "the contribution node's id is the stable section id");
        section.Title.Should().Be("Subscription");
        section.TitleKey.Should().Be("profile.picture");
        section.Order.Should().Be(7);
        section.RequiredPermission.Should().Be(Permission.Update);

        var embed = section.ContentBuilder(null!, null).Should().BeOfType<LayoutAreaControl>().Subject;
        embed.Address.ToString().Should().Be("Store");
        embed.Reference.Area.Should().Be("MyPlan");
    }

    [Fact]
    public void ContentSection_OnlyTheProfileContext_WithAnArea_PassingItsGates()
    {
        var sections = UiContributionProjection.ProjectProfileSections(
            [
                Contribution("menu", new UiContribution { Context = UiContribution.NodeContext, Area = "X" }),
                Contribution("noarea", new UiContribution { Context = UiContribution.ProfileContext }),
                Contribution("admin", new UiContribution
                {
                    Context = UiContribution.ProfileContext, Area = "Y",
                    Gates = new UiContributionGates { AdminOnly = true },
                }),
                Contribution("ok", new UiContribution { Context = UiContribution.ProfileContext, Area = "Z" }),
            ],
            NodePath, UserNode(new User()), isAdmin: false, viewerId: NodePath);

        sections.Select(s => s.Id).Should().Equal("ok");
        sections[0].RequiredPermission.Should().Be(Permission.Read, "a contribution never demands less than Read");
    }

    [Fact]
    public void SeedValidation_KnowsTheProfileContext()
        => UiContributionSeedValidation.PlatformContexts.Should().Contain(UiContribution.ProfileContext,
            "a Profile contribution must not be reported as rendering nowhere");

    private static IEnumerable<UiControl> Descendants(UiControl root)
    {
        yield return root;
        if (ViewsProperty(root.GetType())?.GetValue(root) is System.Collections.IEnumerable views)
            foreach (var v in views)
                if (v is UiControl child)
                    foreach (var d in Descendants(child))
                        yield return d;
    }

    private static PropertyInfo? ViewsProperty(System.Type? t)
    {
        for (; t is not null; t = t.BaseType)
        {
            var p = t.GetProperty("Views",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (p is not null)
                return p;
        }
        return null;
    }
}
