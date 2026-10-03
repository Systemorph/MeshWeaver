// <meshweaver>
// Id: BudgetTests
// DisplayName: Budget Tests — the rules and the view agree with the content
// </meshweaver>
#nullable enable
using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;

/// <summary>
/// The cases of <c>Doc/Architecture/ATypicalNodeType/Budget</c>. Pure cases are <c>public static void</c> methods that THROW on
/// failure — no test framework, no mocks: they run the NodeType's own code on the NodeType's own
/// data. Two cases take the Tests area's host and so need a mesh: the serializer round trip and
/// the live <see cref="SummaryArea_RendersTheHostsOwnContent"/>. Everything else is pure. <see cref="BudgetTestsArea"/> lists them; a case that is not
/// listed there runs nowhere.
///
/// <para>This file may reference its own <c>Test/</c> folder and the NodeType's declared sources,
/// never another type's <c>Test/</c> folder: the mesh compiles each NodeType alone.</para>
/// </summary>
public static class BudgetTests
{
    private static readonly Budget Launch = new()
    {
        Purpose = "Launch campaign",
        Currency = "CHF",
        Planned = 1000m,
        Spent = 750m,
    };

    /// <summary>Health follows spend against plan, with the at-risk boundary INCLUSIVE.</summary>
    public static void Health_FollowsSpendAgainstThePlan()
    {
        Expect(BudgetRules.Health(Launch) == BudgetHealth.OnTrack, "750 of 1000 is on track");
        Expect(BudgetRules.Health(Launch with { Spent = 900m }) == BudgetHealth.AtRisk,
            "exactly 90% is at risk — the boundary is inclusive");
        Expect(BudgetRules.Health(Launch with { Spent = 899.99m }) == BudgetHealth.OnTrack,
            "just below 90% is still on track");
        Expect(BudgetRules.Health(Launch with { Spent = 1000m }) == BudgetHealth.AtRisk,
            "fully spent is at risk, not overspent");
        Expect(BudgetRules.Health(Launch with { Spent = 1000.01m }) == BudgetHealth.Overspent,
            "one cent over the plan is overspent");
        Expect(BudgetRules.Health(Launch with { Planned = 0m }) == BudgetHealth.Unplanned,
            "no plan, no ratio");
    }

    /// <summary>Remaining is plan minus spend, and goes negative rather than stopping at zero.</summary>
    public static void Remaining_IsPlanMinusSpend_AndGoesNegative()
    {
        Expect(BudgetRules.Remaining(Launch) == 250m, $"expected 250, got {BudgetRules.Remaining(Launch)}");
        var over = Launch with { Spent = 1200m };
        Expect(BudgetRules.Remaining(over) == -200m, $"expected -200, got {BudgetRules.Remaining(over)}");
    }

    /// <summary>Recording a spend returns new content and leaves the old untouched; a negative spend is refused.</summary>
    public static void RecordSpend_AddsToSpent_AndRefusesANegativeAmount()
    {
        var after = BudgetRules.RecordSpend(Launch, 100m);
        Expect(after.Spent == 850m, $"expected 850 spent, got {after.Spent}");
        Expect(Launch.Spent == 750m, "the input content must not change — records are immutable");
        var refused = false;
        try { BudgetRules.RecordSpend(Launch, -1m); }
        catch (ArgumentOutOfRangeException) { refused = true; }
        Expect(refused, "a negative spend must be refused");
    }

    /// <summary>An unknown health value is shown as itself — the vocabulary is open.</summary>
    public static void UnknownHealth_IsShownAsItself()
    {
        Expect(BudgetTexts.English.HealthLabel("Frozen") == "Frozen",
            "an unknown value must never take a known label");
        Expect(BudgetTexts.German.HealthLabel(BudgetHealth.Overspent) == "Überschritten",
            "a known value takes the viewer's label");
    }

    /// <summary>The summary shows the figures in the VIEWER's language and number format.</summary>
    public static void Summary_ShowsTheFiguresInTheViewersLanguage()
    {
        var english = BudgetLayoutAreas.Figures(Launch, BudgetTexts.English);
        Expect(english.Contains("**Remaining:** 250.00 CHF", StringComparison.Ordinal),
            $"English figures: {english}");
        Expect(english.Contains("**Health:** On track", StringComparison.Ordinal), $"English health: {english}");

        var german = BudgetLayoutAreas.Figures(Launch, BudgetTexts.German);
        Expect(german.Contains("**Verbleibend:** 250,00 CHF", StringComparison.Ordinal),
            $"German figures: {german}");
        Expect(german.Contains("**Zustand:** Im Plan", StringComparison.Ordinal), $"German health: {german}");
    }

    /// <summary>The view composes platform controls: figures plus a progress bar, or a notice without content.</summary>
    public static void View_ComposesPlatformControls()
    {
        var view = BudgetLayoutAreas.View(Launch, BudgetTexts.English);
        Expect(view is StackControl { Areas.Count: 2 }, $"a budget renders figures and a bar, got {view.GetType().Name}");

        var empty = BudgetLayoutAreas.View(null, BudgetTexts.English);
        Expect(empty is MarkdownControl { Markdown: string text } && text == BudgetTexts.English.NoBudget,
            "a node without content renders the notice");

        // Controls compare BY VALUE, children included — which is what lets the live case below
        // compare a rendered tree with the one the content implies. Pinned in both directions, so
        // that comparison can never be vacuous.
        Expect(view.Equals(BudgetLayoutAreas.View(Launch, BudgetTexts.English)),
            "the same content must compose an equal view");
        Expect(!view.Equals(BudgetLayoutAreas.View(Launch with { Spent = 751m }, BudgetTexts.English)),
            "different figures must compose an unequal view");
    }

    /// <summary>
    /// HOSTED: content that arrives as JSON — the way every node crosses a hub boundary — types
    /// back into <see cref="Budget"/> through THIS hub's serializer. A type the hub never
    /// registered degrades to an untyped <c>JsonElement</c>, and <c>ContentAs</c> is the one read
    /// that survives it; a cast would read a silent null and the summary would render empty.
    /// </summary>
    /// <param name="host">The Tests area's host, whose hub serializer is the subject.</param>
    public static void Content_RoundTripsThroughThisHubsSerializer(LayoutAreaHost host)
    {
        var options = host.Hub.JsonSerializerOptions;
        var json = JsonSerializer.SerializeToElement<object>(Launch, options);
        var arrived = new MeshNode("RoundTrip", "Doc/Architecture/ATypicalNodeType") { Content = json };
        var typed = arrived.ContentAs<Budget>(options);
        Expect(typed == Launch, $"the content came back as {typed?.ToString() ?? "null"}");
    }

    /// <summary>
    /// LIVE: the <c>Summary</c> area, rendered on the node hosting this Tests area, shows exactly
    /// what that node's content says. Emits once when it holds; an error fails the case.
    /// </summary>
    /// <param name="host">The Tests area's host — its node is the subject.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>One emission when the area agrees with the node.</returns>
    public static IObservable<Unit> SummaryArea_RendersTheHostsOwnContent(
        LayoutAreaHost host, RenderingContext context) =>
        host.Workspace.GetMeshNodeStream().Take(1)
            .Zip(BudgetLayoutAreas.Summary(host, context).Take(1), (node, rendered) =>
            {
                var content = node.ContentAs<Budget>(host.Hub.JsonSerializerOptions);
                var expected = BudgetLayoutAreas.View(content, BudgetTexts.For(host.ViewerLocale()));
                Expect(rendered is not null && rendered.GetType() == expected.GetType(),
                    $"the area rendered {rendered?.GetType().Name ?? "nothing"}, the content says {expected.GetType().Name}");
                // By value, children included: the figures and the bar on a budget, the notice
                // without one. View_ComposesPlatformControls proves this equality sees a figure change.
                Expect(expected.Equals(rendered),
                    content is null
                        ? "the area's notice differs from the content's"
                        : "the area's figures differ from the content's");
                return Unit.Default;
            });

    private static void Expect(bool condition, string because)
    {
        if (!condition)
            throw new InvalidOperationException(because);
    }
}
