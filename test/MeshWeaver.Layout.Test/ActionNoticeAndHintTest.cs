using System;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Client;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// <see cref="Controls.Hint"/> and <see cref="ActionNotice"/> — the two pieces that replace the
/// hand-styled <c>&lt;p style="color: var(--neutral-foreground-hint)"&gt;</c> lines and the
/// <c>&lt;p style="color:#4ade80"&gt;</c> outcome strings pages stored in a data slot. Pins: a hint is
/// a Body label in the theme's hint colour; a notice is data (intent + text) that round-trips through
/// the hub's JSON; and, end to end, a click that writes a notice makes the slot render ONE
/// <see cref="MessageBarControl"/> with that intent and text, replaced by the next click's notice.
/// </summary>
public class ActionNoticeAndHintTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string Area = "NoticeProbe";
    private const string ResultId = "noticeProbeResult";

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddLayout(layout => layout.WithView(Area, (host, _) => Controls.Stack
                .WithView(Controls.Hint("Press a button."))
                .WithView(Controls.Button("Succeed").WithClickAction(ctx =>
                    ctx.Host.ShowNotice(ResultId, ActionNotice.Success("It worked."))))
                .WithView(Controls.Button("Fail").WithClickAction(ctx =>
                    ctx.Host.ShowNotice(ResultId, ActionNotice.Error("It broke: <b>no markup</b>."))))
                .WithView((h, _) => h.NoticeView(ResultId))));

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    [Fact]
    public void Hint_IsABodyLabel_InTheHintColour()
    {
        var hint = Controls.Hint("No sources yet.");
        hint.Data.Should().Be("No sources yet.");
        hint.Typo.Should().Be(Typography.Body);
        hint.Color.Should().Be(Controls.HintColor);
        Controls.HintColor.Should().Be("var(--neutral-foreground-hint)");
    }

    [Fact]
    public void ActionNotice_Factories_CarryTheirIntent_AndRenderAsAMessageBar()
    {
        ActionNotice.Info("a").Intent.Should().Be(MessageBarIntent.Info);
        ActionNotice.Success("a").Intent.Should().Be(MessageBarIntent.Success);
        ActionNotice.Warning("a").Intent.Should().Be(MessageBarIntent.Warning);
        ActionNotice.Error("a").Intent.Should().Be(MessageBarIntent.Error);

        var bar = ActionNotice.Error("It broke.").ToControl();
        bar.Body.Should().Be("It broke.");
        bar.Intent.Should().Be(MessageBarIntent.Error);
    }

    [Fact]
    public void ActionNotice_RoundTripsThroughTheHubsJson()
    {
        var options = GetHost().JsonSerializerOptions;
        var json = JsonSerializer.Serialize<object>(ActionNotice.Warning("Careful."), options);
        JsonSerializer.Deserialize<object>(json, options).Should().Be(ActionNotice.Warning("Careful."));
    }

    private static string? Prop(JsonElement control, string name)
        => control.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static (string Id, JsonElement Control)[] Areas(JsonElement frame)
        => frame.TryGetProperty("areas", out var areas) && areas.ValueKind == JsonValueKind.Object
            ? [.. areas.EnumerateObject().Where(a => a.Value.ValueKind == JsonValueKind.Object)
                .Select(a => (JsonSerializer.Deserialize<string>(a.Name) ?? a.Name, a.Value))]
            : [];

    private static (string? Intent, string? Body)[] Bars(JsonElement frame)
        => [.. Areas(frame).Where(a => Prop(a.Control, "$type") == nameof(MessageBarControl))
            .Select(a => (Prop(a.Control, "intent"), Prop(a.Control, "body")))];

    private static string Button(JsonElement frame, string title)
        => Areas(frame).First(a => Prop(a.Control, "$type") == nameof(ButtonControl) && Prop(a.Control, "data") == title).Id;

    [HubFact]
    public async Task AClickWritesANotice_AndTheSlotRendersItAsOneMessageBar()
    {
        var stream = GetClient().GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(CreateHostAddress(), new LayoutAreaReference(Area));
        var frames = stream.Select(c => c.Value);

        var initial = await frames.Should().Within(10.Seconds())
            .Match(f => Areas(f).Any(a => Prop(a.Control, "$type") == nameof(LabelControl)
                    && Prop(a.Control, "data") == "Press a button." && Prop(a.Control, "color") == Controls.HintColor)
                && Areas(f).Count(a => Prop(a.Control, "$type") == nameof(ButtonControl)) == 2,
                "the probe renders its hint and buttons");
        Bars(initial).Should().BeEmpty("the slot is empty until a click writes a notice");

        stream.SubmitUserAction(new ClickedEvent(Button(initial, "Succeed"), stream.StreamId), actingUser: null, onRefused: null, onAccepted: null);
        var succeeded = await frames.Should().Within(10.Seconds())
            .Match(f => Bars(f).Contains((MessageBarIntent.Success, "It worked.")), "the success notice renders");
        Bars(succeeded).Should().HaveCount(1);

        stream.SubmitUserAction(new ClickedEvent(Button(initial, "Fail"), stream.StreamId), actingUser: null, onRefused: null, onAccepted: null);
        var failed = await frames.Should().Within(10.Seconds())
            .Match(f => Bars(f).Contains((MessageBarIntent.Error, "It broke: <b>no markup</b>.")),
                "the error notice replaces the success notice, its text verbatim — never parsed as markup");
        Bars(failed).Should().HaveCount(1, "one slot shows the latest outcome only");
        Areas(failed).Should().NotContain(a => Prop(a.Control, "$type") == nameof(HtmlControl));
    }
}
