// <meshweaver>
// Id: EmployeeNodeLayoutAreas
// DisplayName: Employee Node Views
// </meshweaver>

using System.Reactive.Linq;
using MeshWeaver.Domain;
using MeshWeaver.Layout;

/// <summary>
/// Instance-level views for individual Employee MeshNodes.
/// Displays employee information and contact details.
/// </summary>
public static class EmployeeNodeLayoutAreas
{
    public static LayoutDefinition AddEmployeeNodeLayoutAreas(this LayoutDefinition layout) =>
        layout
            .WithDefaultArea("EmployeeOverview")
            .WithView("EmployeeOverview", EmployeeOverview)
            .WithView("Employment", Employment);

    /// <summary>
    /// The node's employee content. <c>ContentAs</c> — never <c>is</c> + a hand-rolled JSON branch:
    /// the accessor covers the already-typed value AND the degraded JsonElement/JsonNode AND a
    /// same-short-named <c>EmployeeContent</c> from another build (every recompile of a dynamic
    /// NodeType mints a new collectible assembly, so "the same" record has a different CLR identity
    /// per build — the case the hand-rolled version had no round-trip to recover, leaving the view
    /// blank after a recompile with nothing to grep).
    /// </summary>
    private static EmployeeContent? ExtractEmployeeContent(LayoutAreaHost host, MeshNode? node) =>
        node.ContentAs<EmployeeContent>(host.Hub.JsonSerializerOptions);

    /// <summary>Employee overview with personal details.</summary>
    [Display(GroupName = "Overview", Order = 0)]
    public static IObservable<UiControl?> EmployeeOverview(LayoutAreaHost host, RenderingContext ctx)
    {
        var hubPath = host.Hub.Address.ToString();

        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return(Array.Empty<MeshNode>());

        return nodeStream.Select(nodes =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);
            var employee = ExtractEmployeeContent(host, node);

            if (employee == null)
                return (UiControl?)Controls.Markdown("*Employee data not available*");

            var location = string.Join(", ", new[] { employee.City, employee.Region, employee.Country }
                .Where(s => !string.IsNullOrWhiteSpace(s)));

            return (UiControl?)Controls.Stack
                .WithView(Controls.Markdown($"## {employee.TitleOfCourtesy} {employee.FullName}"))
                .WithView(Controls.Html($@"
                    <div style='display: grid; grid-template-columns: repeat(2, 1fr); gap: 24px; margin: 16px 0;'>
                        <div style='padding: 20px; border-radius: 8px; background: var(--mud-palette-surface);'>
                            <h3 style='margin: 0 0 16px 0; color: var(--mud-palette-primary);'>Personal Information</h3>
                            <div style='margin-bottom: 12px;'>
                                <div style='font-size: 12px; color: var(--mud-palette-text-secondary);'>Employee ID</div>
                                <div style='font-size: 16px; font-weight: 500;'>{employee.EmployeeId}</div>
                            </div>
                            <div style='margin-bottom: 12px;'>
                                <div style='font-size: 12px; color: var(--mud-palette-text-secondary);'>Full Name</div>
                                <div style='font-size: 16px; font-weight: 500;'>{employee.FullName}</div>
                            </div>
                            <div>
                                <div style='font-size: 12px; color: var(--mud-palette-text-secondary);'>Location</div>
                                <div style='font-size: 16px;'>{location}</div>
                            </div>
                        </div>
                        <div style='padding: 20px; border-radius: 8px; background: var(--mud-palette-surface);'>
                            <h3 style='margin: 0 0 16px 0; color: var(--mud-palette-primary);'>Position</h3>
                            <div style='margin-bottom: 12px;'>
                                <div style='font-size: 12px; color: var(--mud-palette-text-secondary);'>Title</div>
                                <div style='font-size: 16px; font-weight: 500;'>{employee.Title}</div>
                            </div>
                            <div>
                                <div style='font-size: 12px; color: var(--mud-palette-text-secondary);'>Title of Courtesy</div>
                                <div style='font-size: 16px;'>{employee.TitleOfCourtesy}</div>
                            </div>
                        </div>
                    </div>
                "));
        });
    }

    /// <summary>Employment details and dates.</summary>
    [Display(GroupName = "Employment", Order = 0)]
    public static IObservable<UiControl?> Employment(LayoutAreaHost host, RenderingContext ctx)
    {
        var hubPath = host.Hub.Address.ToString();

        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return(Array.Empty<MeshNode>());

        return nodeStream.Select(nodes =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);
            var employee = ExtractEmployeeContent(host, node);

            if (employee == null)
                return (UiControl?)Controls.Markdown("*Employee data not available*");

            var yearsOfService = employee.HireDate != DateTime.MinValue
                ? (int)((DateTime.Now - employee.HireDate).TotalDays / 365.25)
                : 0;

            return (UiControl?)Controls.Stack
                .WithView(Controls.Markdown("## Employment Details"))
                .WithView(Controls.Html($@"
                    <div style='padding: 20px; border-radius: 8px; background: var(--mud-palette-surface); max-width: 600px;'>
                        <div style='display: grid; grid-template-columns: 150px 1fr; gap: 12px;'>
                            <div style='color: var(--mud-palette-text-secondary);'>Hire Date:</div>
                            <div>{(employee.HireDate != DateTime.MinValue ? employee.HireDate.ToString("MMMM d, yyyy") : "—")}</div>
                            <div style='color: var(--mud-palette-text-secondary);'>Years of Service:</div>
                            <div>{yearsOfService} years</div>
                            <div style='color: var(--mud-palette-text-secondary);'>Birth Date:</div>
                            <div>{(employee.BirthDate != DateTime.MinValue ? employee.BirthDate.ToString("MMMM d, yyyy") : "—")}</div>
                            <div style='color: var(--mud-palette-text-secondary);'>Reports To:</div>
                            <div>{(employee.ReportsTo > 0 ? $"Employee #{employee.ReportsTo}" : "—")}</div>
                        </div>
                    </div>
                "));
        });
    }
}
