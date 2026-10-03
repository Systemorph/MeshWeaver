// <meshweaver>
// Id: BudgetTestsArea
// DisplayName: Budget Tests Area
// </meshweaver>
#nullable enable
using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;

/// <summary>
/// The <c>Tests</c> area of <c>Doc/Architecture/ATypicalNodeType/Budget</c> — the one the build queue EXECUTES. Rendering it
/// runs every case listed below and emits ONE verdict frame: a title carrying the literal
/// <c>N/M passed</c> token and a grid with a ✅ or ❌ row per case. The gate
/// (<c>mw-plugin-test</c>'s <c>AreaProbe</c>) reads exactly those: any ❌ is red, a count short of
/// its total is red, no verdict within its budget is red.
///
/// <para>🚨 The verdict text is NOT translated, deliberately: <c>N/M passed</c> and the two glyphs
/// are the gate's wire format, not chrome. Five German suites rendering <c>N/M bestanden</c> once
/// passed with no count at all.</para>
/// </summary>
public static class BudgetTestsArea
{
    /// <summary>The bound of one live case; past it the case FAILS, it is never waited out.</summary>
    public static readonly TimeSpan LiveCaseBudget = TimeSpan.FromSeconds(20);

    /// <summary>The Tests view — registered in the NodeType's configuration as <c>WithView("Tests", …)</c>.</summary>
    /// <param name="host">The layout area host; its node is the fixture of the live case.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>One verdict frame.</returns>
    public static IObservable<UiControl?> Tests(LayoutAreaHost host, RenderingContext context)
    {
        var pure = new (string Name, Action Body)[]
        {
            ("Health follows spend against the plan", BudgetTests.Health_FollowsSpendAgainstThePlan),
            ("Remaining is plan minus spend, and goes negative", BudgetTests.Remaining_IsPlanMinusSpend_AndGoesNegative),
            ("A spend adds to Spent; a negative one is refused", BudgetTests.RecordSpend_AddsToSpent_AndRefusesANegativeAmount),
            ("An unknown health is shown as itself", BudgetTests.UnknownHealth_IsShownAsItself),
            ("The summary speaks the viewer's language", BudgetTests.Summary_ShowsTheFiguresInTheViewersLanguage),
            ("The view composes platform controls", BudgetTests.View_ComposesPlatformControls),
            ("Content arriving as JSON types back through this hub",
                () => BudgetTests.Content_RoundTripsThroughThisHubsSerializer(host)),
        };
        var live = new (string Name, Func<IObservable<Unit>> Body)[]
        {
            ("LIVE: the Summary area renders this node's own content",
                () => BudgetTests.SummaryArea_RendersTheHostsOwnContent(host, context)),
        };

        var pureRows = pure.Select(c => RunPure(c.Name, c.Body)).ToImmutableList();
        return live.Select(c => RunLive(c.Name, c.Body))
            .Concat()
            .ToList()
            .Select(liveRows => (UiControl?)Verdict(host, pureRows.AddRange(liveRows)));
    }

    private static BudgetTestRow RunPure(string name, Action body)
    {
        try
        {
            body();
            return BudgetTestRow.Pass(name);
        }
        catch (Exception ex)
        {
            return BudgetTestRow.Fail(name, ex.Message);
        }
    }

    // One live case: its own subscription, its own bound, and every outcome — including
    // "completed without emitting" — turned into a row, never into silence.
    private static IObservable<BudgetTestRow> RunLive(string name, Func<IObservable<Unit>> body) =>
        Observable.Defer(body)
            .Take(1)
            .Select(_ => BudgetTestRow.Pass(name))
            .DefaultIfEmpty(BudgetTestRow.Fail(name, "the case completed without an outcome"))
            .Timeout(LiveCaseBudget, Observable.Return(
                BudgetTestRow.Fail(name, $"no outcome within {LiveCaseBudget.TotalSeconds:F0}s")))
            .Catch((Exception ex) => Observable.Return(BudgetTestRow.Fail(name, ex.Message)));

    private static UiControl Verdict(LayoutAreaHost host, ImmutableList<BudgetTestRow> rows)
    {
        var passed = rows.Count(r => r.Passed);
        return Controls.Stack
            .WithView(Controls.Markdown($"### Budget tests — {passed}/{rows.Count} passed"))
            .WithView(host.ToDataGrid(rows));
    }
}

/// <summary>One executed case, as a grid row.</summary>
/// <param name="Case">What the case asserts.</param>
/// <param name="Result">✅, or ❌ with the failure message.</param>
public sealed record BudgetTestRow(string Case, string Result)
{
    /// <summary>Whether the case passed.</summary>
    public bool Passed => Result.StartsWith("✅", StringComparison.Ordinal);

    /// <summary>A passing row.</summary>
    /// <param name="name">The case.</param>
    /// <returns>The row.</returns>
    public static BudgetTestRow Pass(string name) => new(name, "✅ pass");

    /// <summary>A failing row; the message is flattened to one line.</summary>
    /// <param name="name">The case.</param>
    /// <param name="message">Why it failed.</param>
    /// <returns>The row.</returns>
    public static BudgetTestRow Fail(string name, string message) =>
        new(name, "❌ " + message.Replace('\n', ' '));
}
