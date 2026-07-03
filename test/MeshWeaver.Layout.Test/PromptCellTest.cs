using System;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// Structural coverage of the training <see cref="PromptCell"/> (Simulated mode):
/// <list type="number">
///   <item>The cell renders a composer (bound TextArea + ✨ Suggest + Send) and
///   an (initially empty) exchange pane.</item>
///   <item>The ✨ magic button fills the bound prompt pointer with the
///   configured suggestion.</item>
///   <item>Send renders the three-pane exchange — user bubble, agent code cell,
///   output pane — closed by the deterministic faked stats bar.</item>
/// </list>
/// </summary>
public class PromptCellTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string CellView = nameof(CellView);
    private const string PromptDataId = "training_prompt";
    private const string Suggestion = "Show total sales by region as a column chart";

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddLayout(layout => layout
                .WithView(CellView, (host, _) => PromptCell.Area(host, new PromptCellConfig
                {
                    DataId = PromptDataId,
                    Placeholder = "Ask the training agent…",
                    SuggestedPrompt = Suggestion,
                    FakeStatsSeed = 7,
                    Responder = PromptCell.Simulated(prompt => new PromptCellResponse(
                        $"// answer for: {prompt}\nvar rows = new[] {{ 1, 2, 3 }};",
                        Controls.Markdown($"echo: {prompt}")))
                })));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient(d => d);

    private static string FindArea(IContainerControl container, string id)
    {
        var match = container.Areas
            .Select(a => a.Area?.ToString())
            .FirstOrDefault(a => a is not null && (a == id || a.EndsWith("/" + id, StringComparison.Ordinal)));
        match.Should().NotBeNull(
            $"container should contain an area '{id}' — found: " +
            $"[{string.Join(", ", container.Areas.Select(a => a.Area))}]");
        return match!;
    }

    private (ISynchronizationStream<JsonElement> Stream, LayoutAreaReference Reference) OpenCell()
    {
        var reference = new LayoutAreaReference(CellView);
        var stream = GetClient().GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(CreateHostAddress(), reference);
        return (stream, reference);
    }

    [HubFact]
    public async Task Cell_Renders_Composer_With_Input_Magic_And_Send()
    {
        var (stream, reference) = OpenCell();

        var root = (StackControl)(await stream.GetControlStream(reference.Area!)
            .Should().Within(10.Seconds()).Match(c => c is StackControl s
                && s.Areas.Count >= 2))!;

        var composer = (StackControl)(await stream
            .GetControlStream(FindArea(root, PromptCell.ComposerArea))
            .Should().Within(10.Seconds()).Match(c => c is StackControl))!;

        var input = await stream
            .GetControlStream(FindArea(composer, PromptCell.InputArea))
            .Should().Within(10.Seconds()).Match(c => c is not null);
        var textArea = input.Should().BeOfType<TextAreaControl>().Subject;
        textArea.DataContext.Should().Be(LayoutAreaReference.GetDataPointer(PromptDataId),
            "the composer is data-bound to the prompt pointer — no hand-rolled state");
        textArea.Placeholder.Should().Be("Ask the training agent…");

        (await stream.GetControlStream(FindArea(composer, PromptCell.SuggestButtonArea))
                .Should().Within(10.Seconds()).Match(c => c is not null))
            .Should().BeOfType<ButtonControl>();
        (await stream.GetControlStream(FindArea(composer, PromptCell.SendButtonArea))
                .Should().Within(10.Seconds()).Match(c => c is not null))
            .Should().BeOfType<ButtonControl>();
    }

    [HubFact]
    public async Task Magic_Button_Fills_The_Bound_Prompt_Pointer()
    {
        var (stream, reference) = OpenCell();
        var root = (StackControl)(await stream.GetControlStream(reference.Area!)
            .Should().Within(10.Seconds()).Match(c => c is StackControl))!;
        var composer = (StackControl)(await stream
            .GetControlStream(FindArea(root, PromptCell.ComposerArea))
            .Should().Within(10.Seconds()).Match(c => c is StackControl))!;
        var suggestArea = FindArea(composer, PromptCell.SuggestButtonArea);
        await stream.GetControlStream(suggestArea)
            .Should().Within(10.Seconds()).Match(c => c is ButtonControl);

        GetClient().Post(new ClickedEvent(suggestArea, stream.StreamId),
            o => o.WithTarget(CreateHostAddress()));

        // The suggestion lands in the DATA stream the composer is bound to.
        await stream.GetDataStream<string>(new JsonPointerReference(
                LayoutAreaReference.GetDataPointer(PromptDataId)))
            .Should().Within(10.Seconds()).Match(v => v == Suggestion,
                "✨ writes the suggested prompt into the bound pointer");
    }

    [HubFact]
    public async Task Send_Renders_ThreePane_Exchange_With_Stats_Bar()
    {
        var (stream, reference) = OpenCell();
        var root = (StackControl)(await stream.GetControlStream(reference.Area!)
            .Should().Within(10.Seconds()).Match(c => c is StackControl))!;
        var composer = (StackControl)(await stream
            .GetControlStream(FindArea(root, PromptCell.ComposerArea))
            .Should().Within(10.Seconds()).Match(c => c is StackControl))!;
        var suggestArea = FindArea(composer, PromptCell.SuggestButtonArea);
        var sendArea = FindArea(composer, PromptCell.SendButtonArea);
        var exchangeArea = FindArea(root, PromptCell.ExchangeArea);
        await stream.GetControlStream(sendArea)
            .Should().Within(10.Seconds()).Match(c => c is ButtonControl);

        // Fill via ✨, confirm the data landed, then Send.
        GetClient().Post(new ClickedEvent(suggestArea, stream.StreamId),
            o => o.WithTarget(CreateHostAddress()));
        await stream.GetDataStream<string>(new JsonPointerReference(
                LayoutAreaReference.GetDataPointer(PromptDataId)))
            .Should().Within(10.Seconds()).Match(v => v == Suggestion);
        GetClient().Post(new ClickedEvent(sendArea, stream.StreamId),
            o => o.WithTarget(CreateHostAddress()));

        // The exchange renders the three panes + stats.
        var exchange = (StackControl)(await stream.GetControlStream(exchangeArea)
            .Should().Within(10.Seconds()).Match(c => c is StackControl s
                && s.Areas.Any(a => a.Area?.ToString() is { } p
                    && p.EndsWith("/" + PromptCell.ExchangeOutputArea, StringComparison.Ordinal))))!;

        // 1 — the user bubble carries the prompt.
        var bubble = await stream
            .GetControlStream(FindArea(exchange, PromptCell.ExchangeUserArea))
            .Should().Within(10.Seconds()).Match(c => c is not null);
        bubble.Should().BeOfType<MarkdownControl>()
            .Which.Markdown!.ToString().Should().Contain(Suggestion);

        // 2 — the agent code pane renders the responder's code as a fence.
        var codePane = await stream
            .GetControlStream(FindArea(exchange, PromptCell.ExchangeCodeArea))
            .Should().Within(10.Seconds()).Match(c => c is not null);
        codePane.Should().BeOfType<MarkdownControl>()
            .Which.Markdown!.ToString().Should().Contain("```csharp")
            .And.Contain("// answer for:");

        // 3 — the output pane frames the responder's output control.
        var outputPane = (StackControl)(await stream
            .GetControlStream(FindArea(exchange, PromptCell.ExchangeOutputArea))
            .Should().Within(10.Seconds()).Match(c => c is StackControl))!;
        var outputInner = await stream
            .GetControlStream(outputPane.Areas.First().Area!.ToString()!)
            .Should().Within(10.Seconds()).Match(c => c is not null);
        outputInner.Should().BeOfType<MarkdownControl>()
            .Which.Markdown!.ToString().Should().Contain($"echo: {Suggestion}");

        // 4 — the FAKED stats bar: deterministic pseudo-stats, no randomness.
        var (tokens, seconds) = PromptCell.DeriveStats(Suggestion, 7);
        var stats = await stream
            .GetControlStream(FindArea(exchange, PromptCell.ExchangeStatsArea))
            .Should().Within(10.Seconds()).Match(c => c is not null);
        stats.Should().BeOfType<LabelControl>();

        // And the derivation itself is stable + prompt-sensitive.
        PromptCell.DeriveStats(Suggestion, 7).Should().Be((tokens, seconds));
        PromptCell.DeriveStats(Suggestion + "!", 7).Should().NotBe((tokens, seconds));
        $"≈ {tokens} tokens · {seconds:0.0}s · model: training-sim"
            .Should().Contain("training-sim");
    }
}
