using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// <see cref="MessageBarControl"/> and <see cref="LinkControl"/> — the platform's banner and inline
/// link, which replace hand-built <c>Controls.Html("&lt;div style=…&gt;")</c> banners and
/// <c>Controls.Html("&lt;a href=…&gt;")</c> links. Pins: both round-trip through the hub's
/// polymorphic JSON with their SHORT <c>$type</c> (the reflective <c>AddLayoutTypes</c> sweep registers
/// them), literal values and binding pointers alike; a bar's action renders as its own child area
/// whose click reaches its own handler; and the href rules (<see cref="LinkHrefs"/>) every renderer
/// applies refuse a <c>javascript:</c> address however it is spelled.
/// </summary>
public class MessageBarAndLinkControlTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string Area = "Banner";
    private const string ActionArea = Area + "/" + MessageBarControl.ActionArea;

    private readonly AsyncSubject<Unit> actionClicked = new();

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddLayout(layout => layout.WithView(
                Area,
                Controls.MessageBar("The sync failed.")
                    .WithIntent(MessageBarIntent.Error)
                    .WithTitle("GitHub")
                    .WithAction(Controls.Button("Retry").WithClickAction(_ =>
                    {
                        actionClicked.OnNext(Unit.Default);
                        actionClicked.OnCompleted();
                    }))));

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient().AddLayoutTypes();

    private T RoundTrip<T>(UiControl control) where T : UiControl
    {
        var options = GetClient().JsonSerializerOptions;
        var serialized = JsonSerializer.Serialize(control, options);
        using (var doc = JsonDocument.Parse(serialized))
            doc.RootElement.GetProperty("$type").GetString().Should().Be(typeof(T).Name);
        return JsonSerializer.Deserialize<UiControl>(serialized, options).Should().BeOfType<T>().Subject;
    }

    [Fact]
    public void MessageBar_RoundTrips_LiteralValues()
    {
        var bar = RoundTrip<MessageBarControl>(Controls.MessageBar("Read-only")
            .WithIntent(MessageBarIntent.Warning)
            .WithTitle("Settings"));
        bar.Body.ToString().Should().Be("Read-only");
        bar.Intent!.ToString().Should().Be(MessageBarIntent.Warning);
        bar.Title!.ToString().Should().Be("Settings");
        bar.HasAction.Should().BeFalse();
    }

    [Fact]
    public void MessageBar_DefaultsToInfo_AndRoundTripsBindings()
    {
        Controls.MessageBar("x").Intent.Should().Be(MessageBarIntent.Info);
        var bar = RoundTrip<MessageBarControl>(Controls.MessageBar(new JsonPointerReference("message"))
            .WithIntent(new JsonPointerReference("intent"))
            .WithTitle(new JsonPointerReference("title")));
        bar.Body.Should().BeOfType<JsonPointerReference>().Which.Pointer.Should().Be("message");
        bar.Intent.Should().BeOfType<JsonPointerReference>().Which.Pointer.Should().Be("intent");
        bar.Title.Should().BeOfType<JsonPointerReference>().Which.Pointer.Should().Be("title");
    }

    /// <summary>The action is a CHILD AREA, not a serialized property: the wire carries only <c>hasAction</c>.</summary>
    [Fact]
    public void MessageBar_Action_IsNotSerialized_OnlyHasAction()
    {
        var options = GetClient().JsonSerializerOptions;
        var serialized = JsonSerializer.Serialize<UiControl>(
            Controls.MessageBar("x").WithAction(Controls.Button("Retry")), options);
        using var doc = JsonDocument.Parse(serialized);
        doc.RootElement.GetProperty("hasAction").GetBoolean().Should().BeTrue();
        doc.RootElement.TryGetProperty("action", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(MessageBarIntent.Info, "status")]
    [InlineData(MessageBarIntent.Success, "status")]
    [InlineData(MessageBarIntent.Warning, "alert")]
    [InlineData(MessageBarIntent.Error, "alert")]
    [InlineData("error", "alert")]
    [InlineData("Maintenance", "status")]
    [InlineData(null, "status")]
    public void MessageBar_AriaRole_FollowsTheIntent(string? intent, string role) =>
        MessageBarIntent.AriaRoleOf(intent).Should().Be(role);

    /// <summary>
    /// The bar's action is rendered into <c>{area}/Action</c> and is a live control there: a click on
    /// it reaches the action's own handler, exactly as the same button would anywhere else.
    /// </summary>
    [HubFact]
    public async Task MessageBar_Action_RendersAsAChildArea_AndItsClickReachesItsHandler()
    {
        var client = GetClient();
        var stream = client.GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), new LayoutAreaReference(Area));

        await stream.GetControlStream(Area).Should().Within(10.Seconds()).Match(
            c => c is MessageBarControl { HasAction: true },
            "the bar renders at its own area and says it carries an action",
            TestContext.Current.CancellationToken);
        await stream.GetControlStream(ActionArea).Should().Within(10.Seconds()).Match(
            c => c is ButtonControl,
            "the action renders as the bar's child area {area}/Action",
            TestContext.Current.CancellationToken);

        stream.SubmitUserAction(
            new ClickedEvent(ActionArea, stream.StreamId),
            actingUser: null, onRefused: null, onAccepted: null);

        await actionClicked.Should().Within(10.Seconds()).Emit(
            "a click on the bar's action runs the action's own click handler",
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Link_RoundTrips()
    {
        var link = RoundTrip<LinkControl>(Controls.Link("Open the pull request", "https://github.com/acme/repo/pull/1")
            .WithOpenInNewTab()
            .WithTooltip("GitHub"));
        link.Text.ToString().Should().Be("Open the pull request");
        link.Href.ToString().Should().Be("https://github.com/acme/repo/pull/1");
        link.Target!.ToString().Should().Be("_blank");
        link.Tooltip!.ToString().Should().Be("GitHub");
    }

    [Fact]
    public void Link_RoundTrips_BindingsAndDownload()
    {
        var link = RoundTrip<LinkControl>(Controls.Link(new JsonPointerReference("name"), new JsonPointerReference("url"))
            .WithDownload("report.pdf"));
        link.Text.Should().BeOfType<JsonPointerReference>().Which.Pointer.Should().Be("name");
        link.Href.Should().BeOfType<JsonPointerReference>().Which.Pointer.Should().Be("url");
        link.Download!.ToString().Should().Be("report.pdf");
    }

    [Theory]
    [InlineData("https://example.com/x", true)]
    [InlineData("http://example.com", true)]
    [InlineData("mailto:someone@example.com", true)]
    [InlineData("tel:+41000000000", true)]
    [InlineData("/Doc/GUI/MessageBarAndLink", true)]
    [InlineData("../Sibling", true)]
    [InlineData("#section", true)]
    [InlineData("?q=a:b", true)]
    [InlineData("/path/with:colon", true)]
    [InlineData("//cdn.example.com/file.js", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("JavaScript:alert(1)", false)]
    [InlineData("  javascript:alert(1)", false)]
    [InlineData("java\tscript:alert(1)", false)]
    [InlineData("java\nscript:alert(1)", false)]
    [InlineData("vbscript:msgbox", false)]
    [InlineData("data:text/html,<script>alert(1)</script>", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void LinkHrefs_IsSafe_RefusesEveryScriptScheme(string? href, bool safe) =>
        LinkHrefs.IsSafe(href).Should().Be(safe);

    [Theory]
    [InlineData("https://example.com", null, LinkHrefs.ExternalRel)]
    [InlineData("//example.com/x", null, LinkHrefs.ExternalRel)]
    [InlineData("/Doc/GUI", "_blank", LinkHrefs.ExternalRel)]
    [InlineData("/Doc/GUI", "_self", null)]
    [InlineData("/Doc/GUI", null, null)]
    [InlineData("mailto:someone@example.com", null, null)]
    public void LinkHrefs_RelFor_MarksExternalAndNewTabLinks(string href, string? target, string? rel) =>
        LinkHrefs.RelFor(href, target).Should().Be(rel);

    /// <summary>
    /// 🚨 Spellings a browser resolves to ANOTHER SITE although they do not start with a literal
    /// <c>//</c>: the WHATWG URL parser strips ASCII tab and newline anywhere, trims leading C0
    /// controls and spaces, and reads <c>\</c> as <c>/</c> in a scheme-relative or special-scheme
    /// address. Each is safe to link to, and each must be external, so it carries
    /// <c>noreferrer</c> whatever its target — otherwise the portal's URL leaks as the referrer.
    /// </summary>
    [Theory]
    [InlineData("/\t/example.com/x")]
    [InlineData("/\n/example.com/x")]
    [InlineData("\t//example.com/x")]
    [InlineData("\\\\example.com/x")]
    [InlineData("/\\example.com/x")]
    [InlineData("\\/example.com/x")]
    [InlineData("\u0001//example.com/x")]
    [InlineData(" //example.com/x")]
    [InlineData("https:\\\\example.com/x")]
    [InlineData("ht\ttps://example.com/x")]
    public void LinkHrefs_BrowserNormalizedSchemeRelativeForms_AreExternal(string href)
    {
        LinkHrefs.IsSafe(href).Should().BeTrue();
        LinkHrefs.IsExternal(href).Should().BeTrue();
        LinkHrefs.RelFor(href, null).Should().Be(LinkHrefs.ExternalRel);
        LinkHrefs.RelFor(href, "_self").Should().Be(LinkHrefs.ExternalRel);
    }

    /// <summary>A single leading slash or backslash, after the same normalization, is still a path on THIS site.</summary>
    [Theory]
    [InlineData("/Doc/GUI")]
    [InlineData("\\Doc\\GUI")]
    [InlineData("/Doc\\GUI")]
    [InlineData("/Doc/\t/GUI")]
    public void LinkHrefs_SingleSlashPaths_StayInternal(string href)
    {
        LinkHrefs.IsSafe(href).Should().BeTrue();
        LinkHrefs.IsExternal(href).Should().BeFalse();
    }
}
