using System.Reflection;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Unit tests for the polished, owner-editable public profile (<see cref="UserActivityLayoutAreas"/>):
/// the read-only showcase every visitor sees, the node-bound owner editor, the curated-pins Showcase
/// with a recent-public-content fallback, the getting-started card on an empty profile, and the two
/// hard constraints — email is hidden from visitors (#471 PII) and everything is built from
/// layout-area controls, never hand-rolled HTML.
/// </summary>
public class UserProfileShowcaseTest
{
    private const string NodePath = "rbuergi";
    private static readonly JsonSerializerOptions Options = new();

    private static MeshNode UserNode(User content) =>
        MeshNode.FromPath(NodePath) with { Name = "Roland", NodeType = "User", Content = content };

    // ── (a) Owner sees editable bio / links / showcase ──────────────────────────────────────────────

    [Fact]
    public void EditProfile_Owner_HasNodeBoundBioAndLinksEditors()
    {
        var editor = UserActivityLayoutAreas.BuildProfileEditor(
            UserNode(new User { PinnedPaths = ["rbuergi/SpaceA"] }), NodePath, Options);

        // Both fields are node-bound markdown editors — Value is a JsonPointer into the User content,
        // so each edit is a per-field read-modify-write straight to the node (no /data replica).
        var boundPointers = Descendants(editor)
            .OfType<MarkdownEditorControl>()
            .Select(e => (e.Value as JsonPointerReference)?.Pointer)
            .ToList();

        boundPointers.Should().Contain("bio");
        boundPointers.Should().Contain("links");
    }

    [Fact]
    public void EditProfile_Owner_ShowcaseIsCuratableInPlace()
    {
        var editor = UserActivityLayoutAreas.BuildProfileEditor(
            UserNode(new User { PinnedPaths = ["rbuergi/SpaceA", "rbuergi/DocB"] }), NodePath, Options);

        // The owner's pinned cards carry the inline unpin overlay (PinnedThumbnail) so pins can be
        // curated right on the editor — the framework Pin/Unpin surface, not a hand-rolled list editor.
        Descendants(editor).OfType<MeshSearchControl>()
            .Should().Contain(s => Equals(s.ItemArea, PinLayoutArea.PinnedThumbnailArea));
    }

    [Fact]
    public void Profile_Owner_OffersEditEntryPoint()
    {
        var profile = UserActivityLayoutAreas.BuildProfile(
            NodePath, NodePath, "Roland", UserNode(new User { Bio = "hi" }),
            isOwner: true, canSeeEmail: true, Options);

        Descendants(profile).OfType<ButtonControl>()
            .Should().Contain(b => (b.Data as string) == "Edit profile");
    }

    // ── (b) Non-owner sees the profile read-only, WITHOUT email ─────────────────────────────────────

    [Fact]
    public void Profile_Visitor_IsReadOnly_AndHidesEmail_ButShowsOptInBio()
    {
        var node = UserNode(new User { Email = "roland@systemorph.com", Bio = "Builder of things" });
        var profile = UserActivityLayoutAreas.BuildProfile(
            NodePath, NodePath, "Roland", node, isOwner: false, canSeeEmail: false, Options);

        // A visitor never edits.
        Descendants(profile).OfType<MarkdownEditorControl>().Should().BeEmpty();
        // Email is never surfaced to a visitor — the header control carries no email (#471 PII).
        Descendants(profile).OfType<UserProfileControl>().Should().OnlyContain(c => c.Email == null);
        // …but the opt-in bio the owner chose to publish IS shown, read-only, as markdown.
        Descendants(profile).OfType<MarkdownControl>()
            .Should().Contain(m => m.Markdown != null && m.Markdown.ToString() == "Builder of things");
    }

    [Fact]
    public void Profile_OwnerOrAdmin_SeesEmail()
    {
        var node = UserNode(new User { Email = "roland@systemorph.com", Bio = "x" });

        var asOwner = UserActivityLayoutAreas.BuildProfile(
            NodePath, NodePath, "Roland", node, isOwner: true, canSeeEmail: true, Options);
        Descendants(asOwner).OfType<UserProfileControl>()
            .Should().Contain(c => c.Email == "roland@systemorph.com");

        // Admin viewing another user (isOwner false, canSeeEmail true) also sees the email.
        var asAdmin = UserActivityLayoutAreas.BuildProfile(
            NodePath, NodePath, "Roland", node, isOwner: false, canSeeEmail: true, Options);
        Descendants(asAdmin).OfType<UserProfileControl>()
            .Should().Contain(c => c.Email == "roland@systemorph.com");
    }

    // ── (c) Empty profile renders the getting-started card ──────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Profile_Empty_RendersGettingStartedCard(bool isOwner)
    {
        var profile = UserActivityLayoutAreas.BuildProfile(
            NodePath, NodePath, "Roland", UserNode(new User()), isOwner, isOwner, Options);

        Descendants(profile).Should().Contain(
            c => Equals(c.Id, UserActivityLayoutAreas.GettingStartedId),
            "an empty profile (no bio, links, or pins) must render the getting-started card for every user");
    }

    [Fact]
    public void Profile_Populated_DoesNotRenderGettingStartedCard()
    {
        var profile = UserActivityLayoutAreas.BuildProfile(
            NodePath, NodePath, "Roland", UserNode(new User { Bio = "hi" }),
            isOwner: false, canSeeEmail: false, Options);

        Descendants(profile).Should().NotContain(c => Equals(c.Id, UserActivityLayoutAreas.GettingStartedId));
    }

    [Fact]
    public void GettingStarted_OwnerVariant_ExplainsSetupAndLinksToEditor()
    {
        var card = UserActivityLayoutAreas.BuildGettingStarted(NodePath, NodePath, "Roland", isOwner: true);

        card.Id.Should().Be(UserActivityLayoutAreas.GettingStartedId);
        Descendants(card).OfType<MarkdownControl>()
            .Select(m => m.Markdown?.ToString() ?? "")
            .Should().Contain(md => md.Contains("set up your profile"));
        // Owner gets a button straight into the editor; a visitor variant does not.
        Descendants(card).OfType<ButtonControl>().Should().NotBeEmpty();
    }

    [Fact]
    public void GettingStarted_VisitorVariant_IsGentleAndHasNoEditButton()
    {
        var card = UserActivityLayoutAreas.BuildGettingStarted(NodePath, NodePath, "Roland", isOwner: false);

        Descendants(card).OfType<MarkdownControl>()
            .Select(m => m.Markdown?.ToString() ?? "")
            .Should().Contain(md => md.Contains("hasn't set up their profile"));
        Descendants(card).OfType<ButtonControl>().Should().BeEmpty();
    }

    // ── (d) Curated pins render in Showcase; no pins → recent-public fallback ────────────────────────

    [Fact]
    public void Showcase_WithPins_QueriesTheCuratedPaths()
    {
        var showcase = UserActivityLayoutAreas.BuildShowcase(
            "rbuergi", ["rbuergi/SpaceA", "acme/DocB"], ownerView: false);

        showcase.Should().BeOfType<MeshSearchControl>();
        ((MeshSearchControl)showcase).HiddenQuery?.ToString()
            .Should().Contain("path:(rbuergi/SpaceA OR acme/DocB)");
        // A visitor's showcase has no inline unpin overlay.
        ((MeshSearchControl)showcase).ItemArea.Should().BeNull();
    }

    [Fact]
    public void Showcase_WithPins_OwnerView_AddsUnpinOverlay()
    {
        var showcase = (MeshSearchControl)UserActivityLayoutAreas.BuildShowcase(
            "rbuergi", ["rbuergi/SpaceA"], ownerView: true);

        showcase.ItemArea.Should().Be(PinLayoutArea.PinnedThumbnailArea);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Showcase_NoPins_FallsBackToRecentPublicContent(bool nullVsEmpty)
    {
        IReadOnlyList<string>? pins = nullVsEmpty ? null : [];

        var showcase = UserActivityLayoutAreas.BuildShowcase("rbuergi", pins, ownerView: false);

        showcase.Should().BeOfType<MeshSearchControl>();
        // The fallback is the owner's recent public content (visibility-filtered), never empty.
        ((MeshSearchControl)showcase).HiddenQuery?.ToString().Should().Contain("namespace:rbuergi");
        ((MeshSearchControl)showcase).HiddenQuery?.ToString().Should().NotContain("path:(");
    }

    // ── (e) Rendered output is layout-area controls, never raw HTML ─────────────────────────────────

    [Fact]
    public void Profile_UsesLayoutControls_NeverRawHtml()
    {
        var node = UserNode(new User
        {
            Bio = "A bio",
            Links = "[GitHub](https://github.com/x)",
            PinnedPaths = ["rbuergi/SpaceA"]
        });
        var profile = UserActivityLayoutAreas.BuildProfile(
            NodePath, NodePath, "Roland", node, isOwner: false, canSeeEmail: false, Options);

        var all = Descendants(profile).ToList();
        all.OfType<HtmlControl>().Should().BeEmpty(
            "the profile must be composed from controls (Stack/Markdown/MeshSearch/…), never hand-rolled HTML");
        all.Should().Contain(c => c is StackControl);
        all.Should().Contain(c => c is MeshSearchControl);   // Showcase
        all.Should().Contain(c => c is MarkdownControl);     // bio + links, rendered as markdown
    }

    [Fact]
    public void ProfileEditor_UsesLayoutControls_NeverRawHtml()
    {
        var editor = UserActivityLayoutAreas.BuildProfileEditor(UserNode(new User()), NodePath, Options);

        Descendants(editor).OfType<HtmlControl>().Should().BeEmpty();
    }

    // ── tree walker ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Yields <paramref name="root"/> and every descendant <see cref="UiControl"/> nested in a
    /// container's (protected) <c>Views</c> list — lets a pure unit test assert on the whole control
    /// tree (types, bindings, the absence of <see cref="HtmlControl"/>) without standing up a hub.</summary>
    private static IEnumerable<UiControl> Descendants(UiControl root)
    {
        yield return root;
        if (ViewsProperty(root.GetType())?.GetValue(root) is System.Collections.IEnumerable views)
            foreach (var v in views)
                if (v is UiControl child)
                    foreach (var d in Descendants(child))
                        yield return d;
    }

    private static PropertyInfo? ViewsProperty(Type? t)
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
