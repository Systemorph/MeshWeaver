// <meshweaver>
// Id: EmployeeNodeViews
// DisplayName: Employee Node Views
// </meshweaver>

using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Domain;
using MeshWeaver.Layout;

/// <summary>
/// Instance-level views for individual Employee MeshNodes.
/// Displays employee information and contact details.
/// </summary>
public static class EmployeeNodeViews
{
    public static LayoutDefinition AddEmployeeNodeViews(this LayoutDefinition layout) =>
        layout
            .WithDefaultArea("EmployeeOverview")
            .WithView("EmployeeOverview", EmployeeOverview)
            .WithView("Employment", Employment);

    private static EmployeeContent? ExtractEmployeeContent(MeshNode? node)
    {
        if (node?.Content == null)
            return null;

        if (node.Content is EmployeeContent ec)
            return ec;

        if (node.Content is JsonElement json)
        {
            return new EmployeeContent
            {
                EmployeeId = json.TryGetProperty("employeeId", out var eid) ? eid.GetInt32() : 0,
                LastName = json.TryGetProperty("lastName", out var ln) ? ln.GetString() ?? "" : "",
                FirstName = json.TryGetProperty("firstName", out var fn) ? fn.GetString() ?? "" : "",
                Title = json.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "",
                TitleOfCourtesy = json.TryGetProperty("titleOfCourtesy", out var toc) ? toc.GetString() ?? "" : "",
                BirthDate = json.TryGetProperty("birthDate", out var bd) ? bd.GetDateTime() : DateTime.MinValue,
                HireDate = json.TryGetProperty("hireDate", out var hd) ? hd.GetDateTime() : DateTime.MinValue,
                City = json.TryGetProperty("city", out var city) ? city.GetString() ?? "" : "",
                Region = json.TryGetProperty("region", out var region) ? region.GetString() ?? "" : "",
                Country = json.TryGetProperty("country", out var country) ? country.GetString() ?? "" : "",
                ReportsTo = json.TryGetProperty("reportsTo", out var rt) ? rt.GetInt32() : 0
            };
        }
        return null;
    }

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
            var employee = ExtractEmployeeContent(node);

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
            var employee = ExtractEmployeeContent(node);

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
