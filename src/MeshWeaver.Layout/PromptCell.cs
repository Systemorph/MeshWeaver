using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Layout;

/// <summary>
/// One faked (or live) agent exchange: the code the "agent" produced and the
/// output control to render beneath it. Simulated responders author both
/// fields directly; the live adapter (MeshWeaver.AI's <c>TrainingSimResponder</c>)
/// projects a real thread round into this shape.
/// </summary>
/// <param name="Code">The code pane's content — rendered as a fenced code block
/// (language from <see cref="PromptCellConfig.CodeLanguage"/>).</param>
/// <param name="Output">The result control rendered in the output pane.</param>
public record PromptCellResponse(string Code, UiControl Output);

/// <summary>
/// Configuration for a <see cref="PromptCell"/> — the training control that
/// MIMICS an agent exchange on course pages and exercises.
/// <para>The MODE is the <see cref="Responder"/> you plug in:</para>
/// <list type="bullet">
///   <item><b>Simulated</b> — <see cref="PromptCell.Simulated"/> wraps a pure
///   <c>prompt → (code, output)</c> function; no AI infrastructure involved.</item>
///   <item><b>Live</b> — MeshWeaver.AI's <c>TrainingSimResponder.Live(hub, ns)</c>
///   returns a responder that submits the prompt to a real agent thread
///   (<c>hub.StartThread</c>, agent <c>TrainingSim</c>) and projects the reply.</item>
/// </list>
/// </summary>
public record PromptCellConfig
{
    /// <summary>Placeholder text shown in the empty prompt composer.</summary>
    public string? Placeholder { get; init; }

    /// <summary>
    /// The prompt the ✨ "Suggest a prompt" button fills into the composer —
    /// the escape hatch for learners who are stuck.
    /// </summary>
    public string? SuggestedPrompt { get; init; }

    /// <summary>
    /// Maps the submitted prompt to the exchange to render (code pane + output
    /// pane). The cell subscribes the first emission. See the class remarks for
    /// the Simulated / Live factory helpers.
    /// </summary>
    public Func<string, IObservable<PromptCellResponse>>? Responder { get; init; }

    /// <summary>
    /// Seed folded into the deterministic pseudo-stats (see
    /// <see cref="PromptCell.DeriveStats"/>) so two cells on one page show
    /// different — but stable — numbers. No randomness anywhere.
    /// </summary>
    public int FakeStatsSeed { get; init; }

    /// <summary>Language tag of the code pane's fence. Default <c>csharp</c>.</summary>
    public string CodeLanguage { get; init; } = "csharp";

    /// <summary>
    /// Data id backing the prompt composer (bound pointer under <c>/data/…</c>).
    /// Defaults to a fresh Guid per render; set explicitly when a page hosts a
    /// single well-known cell (e.g. for tests).
    /// </summary>
    public string? DataId { get; init; }
}

/// <summary>
/// The training "prompt in → output below" cell: a chat-composer-styled input
/// with Send and ✨ Suggest-a-prompt buttons, and a three-pane exchange beneath
/// it — the learner's prompt bubble, the code the (simulated or live) agent
/// produced, and the output cell with a faked usage-stats bar. All state lives
/// in the layout-area data stream (bound pointers / <c>host.UpdateData</c>);
/// click actions are synchronous lambdas — nothing async, nothing hand-rolled.
/// </summary>
public static class PromptCell
{
    /// <summary>Area id of the composer bar (input + magic + send).</summary>
    public const string ComposerArea = "Composer";
    /// <summary>Area id of the prompt composer (TextArea).</summary>
    public const string InputArea = "PromptInput";
    /// <summary>Area id of the ✨ "Suggest a prompt" button.</summary>
    public const string SuggestButtonArea = "SuggestPrompt";
    /// <summary>Area id of the Send button.</summary>
    public const string SendButtonArea = "SendPrompt";
    /// <summary>Area id of the exchange pane (user bubble + code + output + stats).</summary>
    public const string ExchangeArea = "Exchange";
    /// <summary>Area id of the user prompt bubble inside the exchange.</summary>
    public const string ExchangeUserArea = "UserBubble";
    /// <summary>Area id of the agent code pane inside the exchange.</summary>
    public const string ExchangeCodeArea = "AgentCode";
    /// <summary>Area id of the agent output pane inside the exchange.</summary>
    public const string ExchangeOutputArea = "AgentOutput";
    /// <summary>Area id of the faked usage-stats bar inside the exchange.</summary>
    public const string ExchangeStatsArea = "Stats";

    /// <summary>
    /// Wraps a pure <c>prompt → response</c> function as a Simulated-mode
    /// responder (the common shape for course pages).
    /// </summary>
    public static Func<string, IObservable<PromptCellResponse>> Simulated(
        Func<string, PromptCellResponse> respond)
    {
        ArgumentNullException.ThrowIfNull(respond);
        return prompt => Observable.Defer(() => Observable.Return(respond(prompt)));
    }

    /// <summary>
    /// Builds the prompt cell. The composer's draft is data-bound to
    /// <c>/data/{dataId}</c>; ✨ writes <see cref="PromptCellConfig.SuggestedPrompt"/>
    /// into that pointer; Send snapshots the draft, renders the user bubble,
    /// invokes the responder, and renders code + output + stats reactively.
    /// </summary>
    public static UiControl Area(LayoutAreaHost host, PromptCellConfig config)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(config);

        var dataId = string.IsNullOrEmpty(config.DataId)
            ? $"promptcell_{Guid.NewGuid():N}"
            : config.DataId!;
        host.UpdateData(dataId, "");

        var composer = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("display: flex; align-items: flex-end; gap: 8px; width: 100%; " +
                       "border: 1px solid var(--neutral-stroke-rest); border-radius: 8px; " +
                       "padding: 8px 10px; background: var(--neutral-layer-1);")
            .WithView(new TextAreaControl(new JsonPointerReference(""))
                    .WithPlaceholder(config.Placeholder ?? "Ask the training agent…")
                    .WithRows(2)
                    .WithStyle("flex: 1; width: 100%;") with
                { DataContext = LayoutAreaReference.GetDataPointer(dataId) },
                InputArea)
            .WithView(Controls.Button("✨")
                    .WithLabel("Suggest a prompt")
                    .WithAppearance(Appearance.Neutral)
                    .WithClickAction(ctx =>
                    {
                        // The magic fill: write the suggested prompt straight into
                        // the bound pointer — the composer updates by data binding.
                        ctx.Host.UpdateData(dataId, config.SuggestedPrompt ?? "");
                        return Task.CompletedTask;
                    }),
                SuggestButtonArea)
            .WithView(Controls.Button("Send")
                    .WithAppearance(Appearance.Accent)
                    .WithClickAction(ctx =>
                    {
                        Send(ctx, config, dataId);
                        return Task.CompletedTask;
                    }),
                SendButtonArea);

        return Controls.Stack
            .WithWidth("100%")
            .WithStyle("display: flex; flex-direction: column; gap: 12px;")
            .WithView(composer, ComposerArea)
            .WithView(Controls.Body("")
                    .WithStyle("display: none;"),
                ExchangeArea);
    }

    /// <summary>
    /// Send click: snapshot the bound draft (seeded, so <c>Take(1)</c> emits the
    /// current value synchronously), render the exchange, invoke the responder,
    /// swap in code + output + stats when it emits. Errors land in the exchange
    /// pane — never a silent hang.
    /// </summary>
    private static void Send(UiActionContext ctx, PromptCellConfig config, string dataId)
    {
        // The exchange pane sits at the CELL root, one level above the Send
        // button ({root}/Composer/SendPrompt → {root}/Exchange). Rendered
        // areas carry absolute paths, so strip the two trailing segments.
        var prefix = ctx.Area.EndsWith(SendButtonArea, StringComparison.Ordinal)
            ? ctx.Area[..^SendButtonArea.Length]
            : "";
        if (prefix.EndsWith(ComposerArea + "/", StringComparison.Ordinal))
            prefix = prefix[..^(ComposerArea.Length + 1)];
        var exchangeArea = prefix + ExchangeArea;

        ctx.Host.Stream.GetDataStream<string>(dataId)
            .Take(1)
            .Subscribe(draft =>
            {
                var prompt = draft?.Trim() ?? "";
                if (prompt.Length == 0)
                    return;

                // Composer clears like a chat input; the prompt lives on as the
                // user bubble in the exchange.
                ctx.Host.UpdateData(dataId, "");
                ctx.Host.UpdateArea(exchangeArea,
                    BuildExchange(prompt, response: null, config));

                if (config.Responder is null)
                {
                    ctx.Host.UpdateArea(exchangeArea, BuildExchange(prompt,
                        new PromptCellResponse("", Controls.Markdown(
                            "_No responder configured for this cell._")),
                        config));
                    return;
                }

                config.Responder(prompt)
                    .Take(1)
                    .Subscribe(
                        response => ctx.Host.UpdateArea(exchangeArea,
                            BuildExchange(prompt, response, config)),
                        ex => ctx.Host.UpdateArea(exchangeArea, BuildExchange(prompt,
                            new PromptCellResponse("", Controls.Markdown(
                                $"**The training agent could not answer:** {ex.Message}")),
                            config)));
            });
    }

    /// <summary>
    /// The three-pane exchange: user prompt bubble → agent code cell (fenced
    /// markdown, same treatment as the notebook cell's code segment) → output
    /// pane, closed by the faked stats bar. <paramref name="response"/> null
    /// renders the waiting state (bubble + "thinking" hint).
    /// </summary>
    private static UiControl BuildExchange(string prompt, PromptCellResponse? response, PromptCellConfig config)
    {
        var exchange = Controls.Stack
            .WithWidth("100%")
            .WithStyle("display: flex; flex-direction: column; gap: 8px;");

        // 1 — the learner's prompt as a right-aligned chat bubble.
        exchange = exchange.WithView(Controls.Markdown(prompt)
                .WithStyle("align-self: flex-end; max-width: 85%; padding: 8px 14px; " +
                           "border-radius: 14px 14px 2px 14px; background: var(--accent-fill-rest); " +
                           "color: var(--foreground-on-accent-rest);"),
            ExchangeUserArea);

        if (response is null)
        {
            return exchange.WithView(Controls.Body("training-sim is thinking…")
                    .WithStyle("display: block; font-style: italic; " +
                               "color: var(--neutral-foreground-hint);"),
                ExchangeStatsArea);
        }

        // 2 — the code the "agent" produced, in the notebook-cell code style.
        if (!string.IsNullOrEmpty(response.Code))
        {
            exchange = exchange.WithView(
                Controls.Markdown($"```{config.CodeLanguage}\n{response.Code}\n```")
                    .WithStyle("width: 100%; overflow: auto; " +
                               "border: 1px solid var(--neutral-stroke-rest); border-radius: 6px; " +
                               "background: var(--neutral-layer-1); padding: 0 12px;"),
                ExchangeCodeArea);
        }

        // 3 — the result cell, marked with the same left accent as a run output.
        // (Wrapped in a Stack because the styling applies to the FRAME, not the
        // caller's output control — whose concrete type we don't know here.)
        exchange = exchange.WithView(Controls.Stack
                .WithWidth("100%")
                .WithStyle("border-left: 3px solid var(--accent-fill-rest); " +
                           "background: var(--neutral-layer-2); padding: 10px 12px;")
                .WithView(response.Output),
            ExchangeOutputArea);

        // 4 — the FAKED usage stats: deterministic from the prompt, no Random.
        var (tokens, seconds) = DeriveStats(prompt, config.FakeStatsSeed);
        return exchange.WithView(Controls.Body(
                    $"≈ {tokens} tokens · {seconds:0.0}s · model: training-sim")
                .WithStyle("display: block; font-size: 0.8rem; " +
                           "color: var(--neutral-foreground-hint);"),
            ExchangeStatsArea);
    }

    /// <summary>
    /// Deterministic pseudo-stats for the stats bar: a stable polynomial hash of
    /// the prompt (NOT <c>string.GetHashCode</c>, which is randomized per
    /// process) folded with the config seed. Same prompt + seed ⇒ same numbers,
    /// every render, every process.
    /// </summary>
    public static (int Tokens, double Seconds) DeriveStats(string prompt, int seed)
    {
        unchecked
        {
            var h = 17 + seed * 31;
            foreach (var ch in prompt ?? "")
                h = h * 31 + ch;
            var words = string.IsNullOrWhiteSpace(prompt)
                ? 0
                : prompt!.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
            var tokens = words * 4 + Math.Abs(h % 23) + 3;
            var seconds = 0.8 + Math.Abs(h % 37) / 10.0;
            return (tokens, seconds);
        }
    }
}
