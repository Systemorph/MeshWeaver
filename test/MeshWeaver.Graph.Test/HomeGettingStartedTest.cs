using System;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The <b>Getting started</b> guide on the default home (policy <c>home-getting-started-guide</c>):
/// it renders the five steps in the viewer's language, it is data-bound to the owner's node, and
/// <b>Hide</b> persists PER USER on <see cref="User.HideGettingStarted"/> — hiding it for one person
/// leaves it on for another.
/// </summary>
public class HomeGettingStartedTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Alice = "guide-alice";
    private const string Bob = "guide-bob";
    private static TimeSpan Bound => TestTimeouts.Convergence;

    // ── Pure: the text and the shape ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("en", "Getting started", "Ask the assistant")]
    [InlineData("de", "Erste Schritte", "Fragen Sie den Assistenten")]
    public void TheGuide_HasATitleAndFiveSteps_InTheViewersLanguage(string locale, string title, string firstStep)
    {
        var steps = HomeGettingStarted.StepsMarkdown(locale);

        for (var i = 1; i <= HomeGettingStarted.StepCount; i++)
            steps.Should().Contain($"\n{i}. ", $"step {i} is a numbered markdown item");
        steps.Should().Contain(firstStep);
        steps.Should().Contain("(/Store)", "the Store step links the Store");
        steps.Should().Contain("(/Doc)", "the docs step links the documentation");
        steps.Should().NotContain("home.gettingStarted.", "every step key resolves in " + locale);
        LocalizationCatalog.Get("home.gettingStarted.title", locale).Should().Be(title);
    }

    [Fact]
    public void TheGuide_IsTitleStepsAndHide_WithItsStyleBound_AndHiddenOnceTheFlagIsSet()
    {
        var guide = (StackControl)HomeGettingStarted.Template("en", () => Observable.Return(Unit.Default));
        guide.Areas.Select(a => a.Id?.ToString()).Should().Contain(["Header", "Steps"]);
        guide.Style.Should().BeOfType<JsonPointerReference>("showing and hiding is a data change, never a rebuild")
            .Which.Pointer.Should().Be(LayoutAreaReference.GetDataPointer(HomeGettingStarted.StyleId));

        HomeGettingStarted.StyleFor(new User()).Should().BeEmpty();
        HomeGettingStarted.StyleFor(null).Should().BeEmpty("a profile with no content yet shows the guide");
        HomeGettingStarted.StyleFor(new User { HideGettingStarted = true }).Should().Be(HomeGettingStarted.HiddenStyle);
    }

    [Fact]
    public void TheGuideIsNotOnAnAuthoredHome()
    {
        var authored = UserActivityLayoutAreas.BuildOwnerHome(Alice, "Alice", MeshNode.FromPath(Alice) with
        {
            NodeType = UserNodeType.NodeType,
            Content = new User { Body = "# My own page" },
        }, Mesh.JsonSerializerOptions);

        var markdown = authored.Should().BeOfType<MarkdownControl>().Which.Markdown?.ToString();
        Assert.NotNull(markdown);
        markdown.Should().NotContain("area/GettingStarted",
            "a home whose owner wrote their own Body shows only what that page embeds");
    }

    // ── Mesh: renders, binds, and Hide persists per user ────────────────────────────────────────

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder).AddMeshNodes(
            Person(Alice), AssignmentNodeFactory.UserRole(Alice, "Admin", Alice),
            Person(Bob), AssignmentNodeFactory.UserRole(Bob, "Admin", Bob),
            // Bob may READ Alice's home, so the owner-only guard — not an access denial — is what
            // keeps the guide from him.
            AssignmentNodeFactory.UserRole(Bob, "Viewer", Alice));

    private static MeshNode Person(string id) => new(id)
    {
        NodeType = UserNodeType.NodeType,
        Name = id,
        State = MeshNodeState.Active,
        Content = new User { FullName = id, Email = $"{id}@meshweaver.io" },
    };

    private static AccessContext Login(string id) => new() { ObjectId = id, Name = id, Email = $"{id}@meshweaver.io" };

    /// <summary>
    /// The guide area on <paramref name="owner"/>'s home, opened as <paramref name="viewer"/> (the
    /// owner by default): emits whether the TEMPLATE is there, and the bound style's live value.
    /// </summary>
    private (IObservable<bool> Template, IObservable<string> Style) GuideFor(string owner, string? viewer = null)
    {
        var user = Login(viewer ?? owner);
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var client = GetClient(c => base.ConfigureClient(c)
            .AddLayoutClient()
            .AddPostPipeline(pipeline => pipeline.AddPipeline((delivery, next) =>
                next(delivery.AccessContext is null ? delivery.SetAccessContext(user) : delivery))));
        var reference = new LayoutAreaReference(HomeGettingStarted.AreaName);
        ISynchronizationStream<JsonElement> stream;
        using (access.SwitchAccessContext(user))
            stream = client.GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(new Address(owner), reference);
        var template = stream.GetControlStream(HomeGettingStarted.AreaName)
            .OfType<StackControl>()
            .Select(stack => stack.Areas.Count > 0);
        var style = stream.GetDataStream<string>(
                new JsonPointerReference(LayoutAreaReference.GetDataPointer(HomeGettingStarted.StyleId)))
            .Select(s => s ?? string.Empty);
        return (template, style);
    }

    [Fact(Timeout = 180_000)]
    public async Task TheGuideRenders_AndHidePersistsForThatUserOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        TestUsers.DevLogin(Mesh, Login(Alice));

        var (aliceTemplate, aliceStyleSource) = GuideFor(Alice);
        var aliceStyle = aliceStyleSource.Replay();
        using var aliceConnection = aliceStyle.Connect();
        await aliceTemplate.FirstAsync(present => present).Timeout(Bound).Await(ct);
        await aliceStyle.FirstAsync(style => style.Length == 0).Timeout(Bound).Await(ct);

        // The click's write: Alice's own node, through the one mutation API.
        await HomeGettingStarted.Hide(Mesh, Alice).FirstAsync().Timeout(Bound).Await(ct);

        // The open area hides the guide — its style is bound to the node, not baked at render time.
        await aliceStyle.FirstAsync(style => style == HomeGettingStarted.HiddenStyle).Timeout(Bound).Await(ct);
        var stored = await Mesh.GetWorkspace().GetMeshNodeStream(Alice)
            .Select(node => node?.ContentAs<User>(Mesh.JsonSerializerOptions))
            .FirstAsync(user => user?.HideGettingStarted == true)
            .Timeout(Bound).Await(ct);
        Assert.NotNull(stored);
        stored.Email.Should().Be($"{Alice}@meshweaver.io", "hiding the guide touches no other field of the profile");

        // Bob is untouched: the preference is Alice's, not the platform's.
        TestUsers.DevLogin(Mesh, Login(Bob));
        var (_, bobStyle) = GuideFor(Bob);
        await bobStyle.FirstAsync(style => style.Length == 0).Timeout(Bound).Await(ct);
    }

    [Fact(Timeout = 180_000)]
    public async Task AVisitorOfSomeoneElsesHome_GetsNoGuide()
    {
        var ct = TestContext.Current.CancellationToken;
        TestUsers.DevLogin(Mesh, Login(Bob));

        // Bob can read Alice's home, but the start page is personal: the area answers the empty control.
        var (template, _) = GuideFor(Alice, viewer: Bob);
        var present = await template.FirstAsync().Timeout(Bound).Await(ct);

        present.Should().BeFalse("only the home's owner gets the guide — a visitor sees no steps and no Hide button");
    }
}
