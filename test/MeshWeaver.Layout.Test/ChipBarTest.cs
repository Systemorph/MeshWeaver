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
/// <see cref="Controls.ChipBar(System.Collections.Generic.IEnumerable{Chip}, Breadcrumb)"/>,
/// <see cref="Controls.Breadcrumb"/> and <see cref="Controls.FieldStrip"/> — the platform's chip bar,
/// breadcrumb trail and card field strip. Pins: a chip is a NAVIGATING BADGE (never a button, never
/// markup) whose whole look is its style; the bar interleaves → between chips and leads with a
/// breadcrumb only when it has something to show; tones and emphases resolve to theme-aware mixes
/// (no colour names one theme); the strip skips blank values; and, end to end, all three render
/// through a real layout host as Badge and Label controls only.
/// </summary>
public class ChipBarTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string Area = "ChipBarProbe";

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddLayout(layout => layout.WithView(Area, (_, _) => Controls.Stack
                .WithView(Controls.Breadcrumb(("Crm", "/Crm"), ("Portfolio", "/Crm/Portfolio"), ("Fabrikam", null)), "Trail")
                .WithView(Controls.ChipBar(
                    [
                        new Chip("Meeting", "/X/meeting", ChipTone.Warning, Glyph: ChipGlyph.Done),
                        new Chip("Q&A <v2>", "/X/qa", ChipTone.Success, "the full note", ChipGlyph.Current)
                            .WithEmphasis(ChipEmphasis.Strong),
                    ],
                    Breadcrumb.Of(("Journey", "/Crm/Portfolio"))), "Bar")
                .WithView(Controls.FieldStrip(("Premium", "CHF 1.2m"), ("Broker", null), ("Inception", "1 Jan")), "Strip")));

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    private static string[] Ids(StackControl stack) => [.. stack.Areas.Select(a => a.Id?.ToString() ?? "")];

    [Fact]
    public void Chip_IsANavigatingBadge_SizedByItsStyle_WithTheTextAsData()
    {
        var chip = new Chip("Q&A <v2> \"sent\"", "/X/i?a=1&b=2", ChipTone.Warning, "the whole long note", ChipGlyph.Done);
        var badge = chip.ToControl();

        badge.Should().BeOfType<BadgeControl>();
        badge.Data.Should().Be("✓ Q&A <v2> \"sent\"");
        badge.Href.Should().Be("/X/i?a=1&b=2");
        badge.Tooltip.Should().Be("the whole long note");
        badge.MaxWidth.Should().Be($"{ChipStyle.MaxWidth}px");
        badge.BackgroundColor.Should().Be("transparent");
        badge.Color.Should().Be("inherit");
        badge.Style.Should().Be(chip.Style.Css);
        chip.Style.Css.Should().Contain("text-decoration:none");
        chip.Style.Css.Should().Contain("border-radius:999px");
    }

    [Fact]
    public void Chip_WithoutHrefOrTooltip_IsAPlainPill()
    {
        var badge = new Chip("Draft").ToControl();
        badge.Href.Should().BeNull();
        badge.Tooltip.Should().BeNull();
        badge.Data.Should().Be("Draft");
        new Chip("x", Tooltip: "  ").ToControl().Tooltip.Should().BeNull();
    }

    [Fact]
    public void ATone_IsTheHueItself_SoAModulesOwnColourNeedsNoResolution()
    {
        ChipTone.Info.Should().Be(ThemePalette.Blue);
        ChipTone.Success.Should().Be(ThemePalette.Green);
        ChipTone.Warning.Should().Be(ThemePalette.Amber);
        ChipTone.Danger.Should().Be(ThemePalette.Red);
        ChipTone.Neutral.Should().Be(ThemePalette.Slate);

        new Chip("x", Tone: "#7c3aed").Style.Background
            .Should().Be(ThemePalette.Fill("#7c3aed"), "a module's own hue is mixed into the theme like the platform's");
        new Chip("x", Tone: "var(--accent-fill-rest)").Style.Text
            .Should().Be(ThemePalette.Text("var(--accent-fill-rest)"));
    }

    [Fact]
    public void Emphasis_SetsBorderWeightAndDash()
    {
        var normal = new Chip("x", Tone: ChipTone.Warning).Style;
        normal.Should().Be(new ChipStyle(ThemePalette.Fill(ThemePalette.Amber), ThemePalette.Stroke(ThemePalette.Amber),
            ThemePalette.Text(ThemePalette.Amber), 1, Dashed: false, Bold: false));
        normal.Background.Should().Be("color-mix(in srgb, #f59e0b 16%, var(--neutral-layer-1,#ffffff))");

        var strong = new Chip("x", Tone: ChipTone.Warning).WithEmphasis(ChipEmphasis.Strong).Style;
        strong.BorderWidth.Should().Be(2);
        strong.Bold.Should().BeTrue();
        strong.Css.Should().Contain("font-weight:700");

        var faint = new Chip("x", Tone: ChipTone.Success).WithEmphasis(ChipEmphasis.Faint).Style;
        faint.Dashed.Should().BeTrue();
        faint.Text.Should().Be(ThemePalette.Hint, "a faint chip claims nothing, whatever its tone");
        faint.Css.Should().Contain("dashed");

        var neutral = new Chip("x").Style;
        neutral.Background.Should().Be(ThemePalette.Fill(ThemePalette.Slate, 20), "grey is mixed a little stronger");
        new Chip("x").Style.Should().Be(ChipStyle.For(ChipTone.Neutral, ChipEmphasis.Normal), "Neutral and Normal are the defaults");
    }

    [Fact]
    public void EveryColourIsThemeAware()
    {
        string[] tones = [ChipTone.Neutral, ChipTone.Info, ChipTone.Success, ChipTone.Warning, ChipTone.Danger];
        var emphases = Enum.GetValues<ChipEmphasis>();
        foreach (var tone in tones)
            foreach (var emphasis in emphases)
            {
                var style = new Chip("x", Tone: tone).WithEmphasis(emphasis).Style;
                foreach (var colour in new[] { style.Background, style.Border, style.Text })
                    ThemePalette.IsThemeAware(colour).Should().BeTrue($"{tone}/{emphasis}: {colour}");
            }
        foreach (var style in ChipBarLayout.ColouredStyles)
            ThemePalette.IsThemeAware(style).Should().BeTrue(style);
        ThemePalette.IsThemeAware("#475569").Should().BeFalse("a literal hex names one theme");
    }

    [Fact]
    public void ChipBar_InterleavesArrows_AndLeadsWithAVisiblePrefixOnly()
    {
        Chip[] three = [new("a", "/a"), new("b", "/b"), new("c", "/c")];

        Ids(Controls.ChipBar(three)).Should().Equal("Chip0", "Arrow1", "Chip1", "Arrow2", "Chip2");
        Ids(Controls.ChipBar(three, Breadcrumb.Of(("Journey", "/Crm/Portfolio"))))
            .Should().Equal("Prefix", "Chip0", "Arrow1", "Chip1", "Arrow2", "Chip2");
        Ids(Controls.ChipBar(three, Breadcrumb.Of(("  ", "/x")))).Should().HaveCount(5, "a blank breadcrumb adds no prefix");
        Ids(Controls.ChipBar(null)).Should().BeEmpty();
        Ids(Controls.ChipBar([])).Should().BeEmpty();
        Ids(Controls.ChipBar("Deal:", three)).Should().Equal("Caption", "Chip0", "Arrow1", "Chip1", "Arrow2", "Chip2");
    }

    [Fact]
    public void ChipBar_IsAWrappingHorizontalRow_WithNoStyleOfItsOwn()
    {
        var bar = Controls.ChipBar([new Chip("a")]);
        var skin = bar.Skin;
        skin.Orientation.Should().Be(Orientation.Horizontal);
        skin.Wrap.Should().Be(true);
        bar.Style.Should().BeNull("a caller may add a margin without losing the layout");
    }

    [Fact]
    public void Breadcrumb_SeparatesSegments_AndSkipsBlankOnes()
    {
        var trail = Controls.Breadcrumb(("Crm", "/Crm"), ("", "/nowhere"), ("Portfolio", "/Crm/Portfolio"), ("Fabrikam", null));
        Ids(trail).Should().Equal("Crumb0", "Sep1", "Crumb1", "Sep2", "Crumb2");
        Breadcrumb.Of(("Crm", "/Crm"), (" ", null)).Visible.Should().HaveCount(1);
        trail.Skin.Width.Should().Be("auto", "a trail nested in a bar sizes to its content");
    }

    [Fact]
    public void FieldStrip_SkipsBlankValues_AndKeepsEachLabelWithItsValue()
    {
        var strip = Controls.FieldStrip(("Premium", "CHF 1.2m"), ("Broker", null), ("Line", "  "), ("", "Motor"), ("Inception", "1 Jan"));
        Ids(strip).Should().Equal("Field0", "Sep1", "Field1", "Sep2", "Field2");
        Ids(Controls.FieldStrip()).Should().BeEmpty();
        Ids(Controls.FieldStrip(("a", null))).Should().BeEmpty();

        strip.Skin.Wrap.Should().Be(true, "the strip wraps BETWEEN fields");
        var field = ChipBarLayout.Field("Premium", "CHF 1.2m");
        field.Skin.Wrap.Should().Be(false, "a field never wraps, so its label stays with its value");
        Ids(field).Should().Equal("Label", "Value");
        Ids(ChipBarLayout.Field("", "Motor")).Should().Equal("Value");
    }

    private static string? Prop(JsonElement control, string name)
        => control.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static (string Id, JsonElement Control)[] Areas(JsonElement frame)
        => frame.TryGetProperty("areas", out var areas) && areas.ValueKind == JsonValueKind.Object
            ? [.. areas.EnumerateObject().Where(a => a.Value.ValueKind == JsonValueKind.Object)
                .Select(a => (JsonSerializer.Deserialize<string>(a.Name) ?? a.Name, a.Value))]
            : [];

    private static bool Has(JsonElement frame, string type, string data, string? href = null)
        => Areas(frame).Any(a => Prop(a.Control, "$type") == type && Prop(a.Control, "data") == data
            && (href is null || Prop(a.Control, "href") == href));

    [HubFact]
    public async Task TheThreeRender_AsBadgesAndLabelsOnly()
    {
        var stream = GetClient().GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(CreateHostAddress(), new LayoutAreaReference(Area));

        var frame = await stream.Select(c => c.Value).Should().Within(10.Seconds())
            .Match(f => Has(f, nameof(BadgeControl), "✓ Meeting", "/X/meeting")
                    && Has(f, nameof(LabelControl), "Inception"),
                "the probe renders its chips and its strip");

        Has(frame, nameof(BadgeControl), "● Q&A <v2>", "/X/qa").Should().BeTrue("free text travels verbatim, as data");
        Has(frame, nameof(BadgeControl), "Journey", "/Crm/Portfolio").Should().BeTrue("the prefix is a navigating badge");
        Has(frame, nameof(LabelControl), ChipBarLayout.ChipSeparator).Should().BeTrue();
        Has(frame, nameof(BadgeControl), "Crm", "/Crm").Should().BeTrue("a linked crumb is a navigating badge");
        Has(frame, nameof(LabelControl), "Fabrikam").Should().BeTrue("the unlinked crumb is a label");
        Has(frame, nameof(LabelControl), ChipBarLayout.CrumbSeparator).Should().BeTrue();
        Has(frame, nameof(LabelControl), "CHF 1.2m").Should().BeTrue();
        Has(frame, nameof(LabelControl), "Broker").Should().BeFalse("a blank value drops its label too");

        Areas(frame).Should().NotContain(a => Prop(a.Control, "$type") == nameof(HtmlControl));
        Areas(frame).Should().NotContain(a => Prop(a.Control, "$type") == nameof(MarkdownControl));
        Areas(frame).Should().NotContain(a => Prop(a.Control, "$type") == nameof(ButtonControl));
    }
}
