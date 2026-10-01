// <meshweaver>
// Id: EmployeeNodeLayoutAreas
// DisplayName: Employee Node Views
// </meshweaver>

using System.Globalization;
using MeshWeaver.Domain;
using MeshWeaver.Layout;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Instance-level views for individual Employee MeshNodes — the two TEMPLATE shapes side by side
/// (Doc/GUI/DataBinding → "Templates first, data later").
///
/// <para><b>EmployeeOverview</b> shows stored fields, so every value is a
/// <see cref="JsonPointerReference"/> into the node that the GUI resolves through the node
/// stream. <b>Employment</b> shows values the hub has to COMPUTE (dates in the viewer's format,
/// years of service), so it splits the TEMPLATE — a markdown control declared at once — from the
/// FEED (<see cref="EmploymentFeed"/>), which builds no control and writes each computed value into
/// the control's <c>/data</c> slot. Either way the page is on screen at the first render, shows a
/// skeleton where data has not arrived, and follows every later edit of the node.</para>
/// </summary>
public static class EmployeeNodeLayoutAreas
{
    /// <summary>The <c>/data</c> id the Employment feed writes to.</summary>
    public const string EmploymentDataId = "employment";

    /// <summary>Registers the employee views.</summary>
    /// <param name="layout">The layout definition.</param>
    /// <returns>The layout definition with the views added.</returns>
    public static LayoutDefinition AddEmployeeNodeLayoutAreas(this LayoutDefinition layout) =>
        layout
            .WithDefaultArea("EmployeeOverview")
            .WithView("EmployeeOverview", EmployeeOverview)
            .WithView("Employment", Employment);

    /// <summary>Employee overview with personal details.</summary>
    /// <param name="host">The area host; only its address is read.</param>
    /// <param name="ctx">The rendering context.</param>
    /// <returns>The overview template.</returns>
    [Display(GroupName = "Overview", Order = 0)]
    public static UiControl EmployeeOverview(LayoutAreaHost host, RenderingContext ctx)
        => OverviewTemplate(host.Hub.Address.ToString());

    /// <summary>Employment details and dates.</summary>
    /// <param name="host">The area host; the feed reads its node.</param>
    /// <param name="ctx">The rendering context.</param>
    /// <returns>The employment template, fed by <see cref="EmploymentFeed"/>.</returns>
    [Display(GroupName = "Employment", Order = 0)]
    public static UiControl Employment(LayoutAreaHost host, RenderingContext ctx)
        => EmploymentTemplate(EmploymentFeed(host));

    /// <summary>The overview of the employee at <paramref name="nodePath"/>, bound by path.</summary>
    /// <param name="nodePath">The employee node.</param>
    /// <returns>The complete control tree — it never waits on data.</returns>
    public static UiControl OverviewTemplate(string nodePath)
    {
        var content = LayoutAreaReference.GetMeshNodeDataContext(nodePath);
        // The heading is the node's own Name (EmployeeContent.FullName is projected onto it).
        var fields = LayoutAreaReference.GetMeshNodeDataContext(nodePath, bindContent: false);
        return Controls.Stack
            .WithView(Bound(Controls.H2(new JsonPointerReference("name")), fields))
            .WithView(Controls.LayoutGrid
                .WithView(Card("Personal Information", content,
                        ("Employee ID", nameof(EmployeeContent.EmployeeId)),
                        ("First Name", nameof(EmployeeContent.FirstName)),
                        ("Last Name", nameof(EmployeeContent.LastName)),
                        ("City", nameof(EmployeeContent.City)),
                        ("Country", nameof(EmployeeContent.Country))),
                    skin => skin.WithXs(12).WithMd(6))
                .WithView(Card("Position", content,
                        ("Title", nameof(EmployeeContent.Title)),
                        ("Title of Courtesy", nameof(EmployeeContent.TitleOfCourtesy))),
                    skin => skin.WithXs(12).WithMd(6)));
    }

    /// <summary>
    /// The Employment page: declared at once, its body bound to <c>/data/employment</c>, which
    /// <paramref name="employment"/> fills. It holds no data of its own.
    /// </summary>
    /// <param name="employment">The rendered details, as they change.</param>
    /// <returns>The complete control tree — it never waits on data.</returns>
    public static UiControl EmploymentTemplate(IObservable<string> employment) =>
        Controls.Stack
            .WithView(Controls.H2("Employment Details"))
            .WithView(employment.Bind(markdown => Controls.Markdown(markdown), EmploymentDataId));

    /// <summary>
    /// The FEED half of Employment: the node's employment details, computed for the viewer, on
    /// every emission of the node. Builds no control. A failure is logged and shown as a line of
    /// text — reported, never swallowed.
    /// </summary>
    /// <param name="host">The area host whose node is read.</param>
    /// <returns>The rendered details.</returns>
    public static IObservable<string> EmploymentFeed(LayoutAreaHost host)
    {
        var culture = CultureInfo.GetCultureInfo(host.ViewerLocale());
        return host.Workspace.GetMeshNodeStream()
            .Select(node => node.ContentAs<EmployeeContent>(host.Hub.JsonSerializerOptions))
            .Select(employee => employee is null
                ? "*Employee data not available*"
                : EmploymentMarkdown(employee, culture, DateTime.UtcNow))
            .DistinctUntilChanged()
            .Catch<string, Exception>(ex =>
            {
                host.Hub.ServiceProvider.GetRequiredService<ILoggerFactory>()
                    .CreateLogger(nameof(EmployeeNodeLayoutAreas))
                    .LogWarning(ex, "Employment details of {Path} could not be read", host.Hub.Address);
                return Observable.Return("*Employment details could not be read.*");
            });
    }

    /// <summary>The employment details as markdown — pure, so it is testable without a mesh.</summary>
    /// <param name="employee">The employee.</param>
    /// <param name="culture">The viewer's culture, for the dates.</param>
    /// <param name="today">Today, for the years of service.</param>
    /// <returns>A markdown list.</returns>
    public static string EmploymentMarkdown(EmployeeContent employee, CultureInfo culture, DateTime today)
    {
        string Date(DateTime d) => d == DateTime.MinValue ? "—" : d.ToString("D", culture);
        var years = employee.HireDate == DateTime.MinValue
            ? 0
            : (int)((today - employee.HireDate).TotalDays / 365.25);
        return $"""
            - **Hire Date:** {Date(employee.HireDate)}
            - **Years of Service:** {years}
            - **Birth Date:** {Date(employee.BirthDate)}
            - **Reports To:** {(employee.ReportsTo > 0 ? $"Employee #{employee.ReportsTo}" : "—")}
            """;
    }

    /// <summary>A titled card of caption / bound-value pairs.</summary>
    private static UiControl Card(string title, string content, params (string Caption, string Property)[] fields) =>
        fields.Aggregate(
            Controls.Stack
                .WithStyle("padding: 20px; border-radius: 8px; background: var(--neutral-layer-2); gap: 12px;")
                .WithView(Controls.H3(title)),
            (card, field) => card.WithView(Controls.Stack
                .WithView(Controls.Label(field.Caption).WithStyle("font-size: 12px; color: var(--neutral-foreground-hint);"))
                .WithView(Bound(Controls.Body(Pointer(field.Property)), content))));

    /// <summary>A pointer into the content — camelCase, as the hub serializes it.</summary>
    private static JsonPointerReference Pointer(string property) =>
        new(char.ToLowerInvariant(property[0]) + property[1..]);

    private static LabelControl Bound(LabelControl label, string context) => label with { DataContext = context };
}
