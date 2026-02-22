using System.Reactive.Linq;
using System.Text;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// Layout area for managing access control on a mesh node.
/// Inherited assignments are loaded via IMeshQuery from ancestor nodes (read-only markdown table).
/// Local assignments are loaded via ObserveQuery from children (editable, reactive).
/// </summary>
public static class AccessControlLayoutArea
{
    /// <summary>
    /// Entry point for the Access Control layout area.
    /// </summary>
    public static IObservable<UiControl?> AccessControl(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var securityService = host.Hub.ServiceProvider.GetService<ISecurityService>();

        if (securityService == null)
        {
            return Observable.Return<UiControl?>(
                Controls.Stack.WithView(
                    Controls.Html(
                        "<p style=\"color: var(--warning-color);\">Row-Level Security is not enabled. " +
                        "Add .AddRowLevelSecurity() to your mesh configuration.</p>")
                )
            );
        }

        var meshQuery = host.Hub.ServiceProvider.GetService<IMeshQuery>();
        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? [])
            ?? Observable.Return<MeshNode[]>([]);

        // Use ObserveQuery for local assignments so the view reactively updates
        // when assignments are added or removed via IMeshCatalog.
        var localAssignmentsStream = meshQuery != null
            ? meshQuery.ObserveQuery<MeshNode>(
                MeshQueryRequest.FromQuery($"path:{hubPath} nodeType:AccessAssignment scope:children"))
                .Select(change => (IReadOnlyList<MeshNode>)change.Items)
            : Observable.Return<IReadOnlyList<MeshNode>>([]);

        return nodeStream.CombineLatest(localAssignmentsStream, (nodes, localAssignments) => (nodes, localAssignments))
            .SelectMany(async tuple =>
            {
                var (nodes, localAssignments) = tuple;
                var node = nodes.FirstOrDefault(n => n.Namespace == hubPath || n.Path == hubPath);
                var isAdmin = await CheckAdminPermission(host.Hub, hubPath);

                // Load inherited assignments from ancestor nodes via IMeshQuery (one-shot, rarely changes)
                var inherited = new List<(AccessAssignment Assignment, string SourcePath)>();
                if (meshQuery != null)
                {
                    try
                    {
                        var ancestorAssignments = await meshQuery
                            .QueryAsync<MeshNode>($"path:{hubPath} nodeType:AccessAssignment scope:ancestors")
                            .ToListAsync();

                        foreach (var assignmentNode in ancestorAssignments)
                        {
                            var assignment = DeserializeAssignment(assignmentNode);
                            if (assignment != null)
                                inherited.Add((assignment, assignmentNode.Namespace ?? ""));
                        }
                    }
                    catch
                    {
                        // Query may fail if index not ready
                    }
                }

                return BuildAccessControlPage(node, hubPath, isAdmin, inherited, localAssignments);
            });
    }

    private static AccessAssignment? DeserializeAssignment(MeshNode node)
    {
        if (node.Content is AccessAssignment aa)
            return aa;
        if (node.Content is System.Text.Json.JsonElement je)
            return System.Text.Json.JsonSerializer.Deserialize<AccessAssignment>(je.GetRawText());
        return null;
    }

    private static async Task<bool> CheckAdminPermission(IMessageHub hub, string nodePath)
    {
        var permissions = await PermissionHelper.GetEffectivePermissionsAsync(hub, nodePath);
        return permissions.HasFlag(Permission.Delete); // Admin = has Delete permission
    }

    private static UiControl? BuildAccessControlPage(
        MeshNode? node,
        string nodePath,
        bool isAdmin,
        IReadOnlyList<(AccessAssignment Assignment, string SourcePath)> inherited,
        IReadOnlyList<MeshNode> localAssignmentNodes)
    {
        var stack = Controls.Stack.WithStyle("padding: 24px; gap: 24px;");

        // Header
        var headerText = node?.Name ?? nodePath.Split('/').LastOrDefault() ?? nodePath;
        stack = stack.WithView(Controls.H2($"Access Control - {headerText}"));

        // Section 1: Inherited Permissions (read-only Markdown Table)
        stack = stack.WithView(Controls.H3("Inherited Permissions").WithStyle("margin: 0;"));

        if (inherited.Count == 0)
        {
            stack = stack.WithView(Controls.Html("<p style=\"color: var(--neutral-foreground-hint);\">No inherited permissions.</p>"));
        }
        else
        {
            var markdown = BuildInheritedMarkdownTable(inherited);
            stack = stack.WithView(Controls.Markdown(markdown));
        }

        // Section 2: Local Assignments (editable)
        stack = stack.WithView(Controls.H3("Local Assignments").WithStyle("margin: 0;"));

        if (localAssignmentNodes.Count == 0)
        {
            stack = stack.WithView(Controls.Html("<p style=\"color: var(--neutral-foreground-hint);\">No local assignments.</p>"));
        }
        else
        {
            foreach (var assignmentNode in localAssignmentNodes)
            {
                var assignment = DeserializeAssignment(assignmentNode);
                if (assignment == null) continue;

                var subjectDisplay = assignment.DisplayName ?? assignment.AccessObject;
                var capturedNode = assignmentNode;

                foreach (var role in assignment.Roles)
                {
                    var isActive = !role.Denied;

                    var row = Controls.Stack
                        .WithOrientation(Orientation.Horizontal)
                        .WithStyle("padding: 4px 16px; border-bottom: 1px solid var(--neutral-stroke-rest); align-items: center; gap: 12px;")
                        .WithView(Controls.Label(subjectDisplay).WithStyle("font-weight: 600; flex: 1; min-width: 120px;"))
                        .WithView(Controls.Label(role.Role).WithStyle("flex: 1; min-width: 120px;"));

                    if (isAdmin)
                    {
                        row = row
                            .WithView(Controls.Label(isActive ? "Allow" : "Deny")
                                .WithStyle("color: var(--neutral-foreground-hint);"))
                            .WithView(Controls.Button("")
                                .WithIconStart(FluentIcons.Delete())
                                .WithAppearance(Appearance.Stealth)
                                .WithClickAction(async ctx =>
                                {
                                    var catalog = ctx.Hub.ServiceProvider.GetService<IMeshCatalog>();
                                    if (catalog != null)
                                        await catalog.DeleteNodeAsync(capturedNode.Path);
                                }));
                    }
                    else
                    {
                        row = row.WithView(Controls.Label(isActive ? "Allow" : "Deny")
                            .WithStyle("color: var(--neutral-foreground-hint);"));
                    }

                    stack = stack.WithView(row);
                }
            }
        }

        // Add Assignment button (only for admins) — navigates to standard Create flow
        if (isAdmin)
        {
            var createUrl = MeshNodeLayoutAreas.BuildContentUrl(nodePath, MeshNodeLayoutAreas.CreateNodeArea, "type=AccessAssignment");
            stack = stack.WithView(Controls.Button("Add Assignment")
                .WithAppearance(Appearance.Accent)
                .WithIconStart(FluentIcons.PersonAdd())
                .WithNavigateToHref(createUrl));
        }

        return stack;
    }

    private static string BuildInheritedMarkdownTable(
        IReadOnlyList<(AccessAssignment Assignment, string SourcePath)> inherited)
    {
        var sb = new StringBuilder();
        sb.AppendLine("| Subject | Role | Source | Access |");
        sb.AppendLine("|---------|------|--------|--------|");

        foreach (var (assignment, sourcePath) in inherited.OrderBy(x => x.Assignment.AccessObject))
        {
            var subject = assignment.DisplayName ?? assignment.AccessObject;
            var source = string.IsNullOrEmpty(sourcePath) ? "Global" : sourcePath;

            foreach (var role in assignment.Roles)
            {
                var access = role.Denied ? "Deny" : "Allow";
                sb.AppendLine($"| {subject} | {role.Role} | {source} | {access} |");
            }
        }

        return sb.ToString();
    }
}
