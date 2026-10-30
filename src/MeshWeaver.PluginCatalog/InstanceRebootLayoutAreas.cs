using System.Reactive.Linq;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// 🚨 The page of an <c>Admin/_Reboot/{id}</c> request (MeshWeaver#7322). The request stores its
/// outcome — <see cref="InstanceRebootRequest.Failure"/>, each step's and each check's detail, the
/// log — in English, the wire value every reader, log line and the watchdog's <c>stalled: </c>
/// marker use, and a keyed form beside each (<see cref="RebootText"/>). The generic content view
/// rendered the English, so a German viewer read English; this page renders every keyed sentence
/// in the VIEWER's language (<see cref="LayoutAreaLocalizationExtensions.ViewerLocale"/>, never the
/// ambient culture), and a sentence with no faithful key — a module's own smoke-check detail, a
/// request written before the keys — as written.
///
/// <para>Composed from the platform's controls (labels, Markdown headings, data grids), bound to the
/// request's own node stream: it re-renders as the executor writes each step.</para>
/// </summary>
public static class InstanceRebootLayoutAreas
{
    /// <summary>Registers the request's page as the node's Overview.</summary>
    /// <param name="configuration">The request node's hub configuration.</param>
    /// <returns>The configuration with the page registered.</returns>
    public static MessageHubConfiguration AddInstanceRebootViews(this MessageHubConfiguration configuration)
        => configuration.AddLayout(layout => layout.WithNodePage(MeshNodeLayoutAreas.OverviewArea, Overview));

    /// <summary>The request's page, live on its own node stream.</summary>
    /// <param name="host">The layout area host.</param>
    /// <param name="_">The rendering context.</param>
    /// <returns>The page, once per change of the request.</returns>
    public static IObservable<UiControl?> Overview(LayoutAreaHost host, RenderingContext _)
    {
        // Captured at entry: the stream below emits on whichever turn the node changes on.
        var locale = host.ViewerLocale();
        var options = host.Hub.JsonSerializerOptions;
        return host.Workspace.GetMeshNodeStream()
            .Select(node => node?.ContentAs<InstanceRebootRequest>(options))
            .Where(request => request is not null)
            .Select(request => (UiControl?)Render(request!, locale));
    }

    /// <summary>The page for <paramref name="request"/> in <paramref name="locale"/>. Pure.</summary>
    /// <param name="request">The request.</param>
    /// <param name="locale">The viewer's language tag.</param>
    /// <returns>The page.</returns>
    public static UiControl Render(InstanceRebootRequest request, string? locale)
    {
        string T(string key) => LocalizationCatalog.GetNamed(key, locale, null);
        string Word(string prefix, string value) => LocalizationCatalog.GetNamed($"{prefix}.{value}", locale, null, fallback: value);
        string At(DateTimeOffset? at) => at is { } v ? InstanceReboot.Stamp(v) : "";

        var page = Controls.Stack.WithWidth("100%").WithVerticalGap(12)
            .WithView(Controls.H2($"{T("reboot.view.title")} — {Word("reboot.status", request.Status)}"))
            .WithView(Grid(
                [
                    new RebootFactRow(T("reboot.view.reason"), request.Reason),
                    new RebootFactRow(T("reboot.view.requestedBy"), request.RequestedBy ?? T("reboot.unattributed")),
                    new RebootFactRow(T("reboot.view.requestedAt"), At(request.RequestedAt)),
                    new RebootFactRow(T("reboot.view.trigger"), request.Trigger),
                    new RebootFactRow(T("reboot.view.runningImage"), request.RunningImage ?? ""),
                    new RebootFactRow(T("reboot.view.targetImage"), request.TargetImage ?? ""),
                ],
                (nameof(RebootFactRow.Property), T("reboot.view.property")),
                (nameof(RebootFactRow.Value), T("reboot.view.value"))));

        if (RebootText.Localize(request.Failure, request.FailureText, locale) is { } failure)
            page = page
                .WithView(Controls.H3(T("reboot.view.failure")))
                .WithView(Controls.Body(failure));

        page = page
            .WithView(Controls.H3(T("reboot.view.steps")))
            .WithView(Grid(
                request.Steps.Select(s => new RebootStepRow(
                    Word("reboot.step", s.Name), Word("reboot.outcome", s.Outcome),
                    RebootText.Localize(s.Detail, s.DetailText, locale) ?? "", At(s.At))).ToArray(),
                (nameof(RebootStepRow.Step), T("reboot.view.step")),
                (nameof(RebootStepRow.Outcome), T("reboot.view.outcome")),
                (nameof(RebootStepRow.Detail), T("reboot.view.detail")),
                (nameof(RebootStepRow.At), T("reboot.view.at"))));

        if (!request.Modules.IsEmpty)
            page = page
                .WithView(Controls.H3(T("reboot.view.modules")))
                .WithView(Grid(
                    request.Modules.Select(m => new RebootModuleRow(
                        m.Module, m.TargetVersion ?? m.RunningVersion ?? "",
                        m.Landed ? "✓" : "—", m.Failure ?? m.Decision ?? "")).ToArray(),
                    (nameof(RebootModuleRow.Module), T("reboot.view.module")),
                    (nameof(RebootModuleRow.Version), T("reboot.view.version")),
                    (nameof(RebootModuleRow.Landed), T("reboot.view.landed")),
                    (nameof(RebootModuleRow.Detail), T("reboot.view.detail"))));

        page = page.WithView(Controls.H3(T("reboot.view.verification")));
        if (request.Replicas.IsEmpty)
            page = page.WithView(Controls.Hint(T("reboot.view.noReplica")));
        foreach (var replica in request.Replicas.Values.OrderBy(r => r.Process, StringComparer.Ordinal))
            page = page
                .WithView(Controls.Body(LocalizationCatalog.GetNamed("reboot.view.replica", locale, new Dictionary<string, object>
                {
                    ["process"] = replica.Process,
                    ["image"] = replica.Image ?? "?",
                    ["at"] = InstanceReboot.Stamp(replica.ReportedAt),
                })))
                .WithView(Grid(
                    replica.Checks.Select(c => new RebootCheckRow(
                        c.Name, Word("reboot.outcome", c.Outcome),
                        RebootText.Localize(c.Detail, c.DetailText, locale) ?? "")).ToArray(),
                    (nameof(RebootCheckRow.Check), T("reboot.view.check")),
                    (nameof(RebootCheckRow.Outcome), T("reboot.view.outcome")),
                    (nameof(RebootCheckRow.Detail), T("reboot.view.detail"))));

        return page
            .WithView(Controls.H3(T("reboot.view.log")))
            .WithView(Grid(
                InstanceReboot.LocalizedLog(request, locale).Select(line => new RebootLogRow(line)).ToArray(),
                (nameof(RebootLogRow.Line), T("reboot.view.log"))));
    }

    private static DataGridControl Grid<TRow>(TRow[] rows, params (string Property, string Title)[] columns)
        => columns.Aggregate(Controls.DataGrid(rows), (grid, column) => grid.WithColumn(
            new PropertyColumnControl<string> { Property = char.ToLowerInvariant(column.Property[0]) + column.Property[1..] }.WithTitle(column.Title)));

    /// <summary>One fact about the request.</summary>
    public sealed record RebootFactRow(string Property, string Value);

    /// <summary>One step as the viewer reads it.</summary>
    public sealed record RebootStepRow(string Step, string Outcome, string Detail, string At);

    /// <summary>One module the landing step reported.</summary>
    public sealed record RebootModuleRow(string Module, string Version, string Landed, string Detail);

    /// <summary>One verification check one process reported.</summary>
    public sealed record RebootCheckRow(string Check, string Outcome, string Detail);

    /// <summary>One log line as the viewer reads it.</summary>
    public sealed record RebootLogRow(string Line);
}
