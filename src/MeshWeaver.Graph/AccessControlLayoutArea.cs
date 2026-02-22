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
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// Layout area for managing access control on a mesh node.
/// Inherited assignments are loaded via IMeshQuery from ancestor nodes (read-only markdown table).
/// Local assignments are loaded via IMeshQuery from children (editable).
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

        return nodeStream.SelectMany(async nodes =>
        {
            var node = nodes.FirstOrDefault(n => n.Namespace == hubPath || n.Path == hubPath);
            var isAdmin = await CheckAdminPermission(host.Hub, hubPath);

            // Load inherited assignments from ancestor nodes via IMeshQuery
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

            // Load local assignments from children via IMeshQuery
            var local = new List<MeshNode>();
            if (meshQuery != null)
            {
                try
                {
                    local = await meshQuery
                        .QueryAsync<MeshNode>($"path:{hubPath} nodeType:AccessAssignment scope:children")
                        .ToListAsync();
                }
                catch
                {
                    // Query may fail if index not ready
                }
            }

            return BuildAccessControlPage(host, node, hubPath, isAdmin, inherited, local);
        });
    }

    private static AccessAssignment? DeserializeAssignment(MeshNode node, System.Text.Json.JsonSerializerOptions? options = null)
    {
        if (node.Content is AccessAssignment aa)
            return aa;
        if (node.Content is System.Text.Json.JsonElement je)
        {
            if (options != null)
                return System.Text.Json.JsonSerializer.Deserialize<AccessAssignment>(je.GetRawText(), options);
            return System.Text.Json.JsonSerializer.Deserialize<AccessAssignment>(je.GetRawText());
        }
        return null;
    }

    private static async Task<bool> CheckAdminPermission(IMessageHub hub, string nodePath)
    {
        var permissions = await PermissionHelper.GetEffectivePermissionsAsync(hub, nodePath);
        return permissions.HasFlag(Permission.Delete); // Admin = has Delete permission
    }

    private static UiControl? BuildAccessControlPage(
        LayoutAreaHost host,
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

                var subjectDisplay = assignment.DisplayName ?? assignment.SubjectId;

                foreach (var role in assignment.Roles)
                {
                    var isActive = !role.Denied;

                    var row = Controls.Stack
                        .WithOrientation(Orientation.Horizontal)
                        .WithStyle("padding: 8px 16px; border-bottom: 1px solid var(--neutral-stroke-rest); align-items: center; gap: 12px;")
                        .WithView(Controls.Label(subjectDisplay).WithStyle("flex: 1; min-width: 120px;"))
                        .WithView(Controls.Label(role.RoleId).WithStyle("flex: 1; min-width: 120px;"))
                        .WithView(Controls.Switch(isActive)
                            .WithCheckedMessage("Allow")
                            .WithUncheckedMessage("Deny"));

                    if (isAdmin)
                    {
                        row = row.WithView(Controls.Button("")
                            .WithIconStart(FluentIcons.Delete())
                            .WithAppearance(Appearance.Stealth)
                            .WithClickAction(async ctx =>
                            {
                                var catalog = ctx.Hub.ServiceProvider.GetService<IMeshCatalog>();
                                if (catalog != null)
                                    await catalog.DeleteNodeAsync(assignmentNode.Path);
                            }));
                    }

                    stack = stack.WithView(row);
                }
            }
        }

        // Add Assignment button (only for admins)
        if (isAdmin)
        {
            stack = stack.WithView(BuildAddButton(host, nodePath));
        }

        return stack;
    }

    private static string BuildInheritedMarkdownTable(
        IReadOnlyList<(AccessAssignment Assignment, string SourcePath)> inherited)
    {
        var sb = new StringBuilder();
        sb.AppendLine("| Subject | Role | Source | Access |");
        sb.AppendLine("|---------|------|--------|--------|");

        foreach (var (assignment, sourcePath) in inherited.OrderBy(x => x.Assignment.SubjectId))
        {
            var subject = assignment.DisplayName ?? assignment.SubjectId;
            var source = string.IsNullOrEmpty(sourcePath) ? "Global" : sourcePath;

            foreach (var role in assignment.Roles.OrderBy(r => r.RoleId))
            {
                var access = role.Denied ? "Deny" : "Allow";
                sb.AppendLine($"| {subject} | {role.RoleId} | {source} | {access} |");
            }
        }

        return sb.ToString();
    }

    private static UiControl BuildAddButton(LayoutAreaHost host, string nodePath)
    {
        return Controls.Button("Add Assignment")
            .WithAppearance(Appearance.Accent)
            .WithIconStart(FluentIcons.PersonAdd())
            .WithClickAction(async ctx =>
            {
                await ShowAddAssignmentDialog(ctx, nodePath);
            });
    }

    private static async Task ShowAddAssignmentDialog(UiActionContext ctx, string nodePath)
    {
        var formId = $"add_assignment_{Guid.NewGuid().AsString()}";
        ctx.Host.UpdateData(formId, new Dictionary<string, object?>
        {
            ["subjectId"] = "",
            ["roleId"] = ""
        });

        // Load subject options (User and Group nodes) via IMeshQuery
        var subjectOptionsId = $"subject_options_{Guid.NewGuid().AsString()}";
        var subjectOptions = new List<Option>();
        var meshQuery = ctx.Hub.ServiceProvider.GetService<IMeshQuery>();
        if (meshQuery != null)
        {
            try
            {
                await foreach (var suggestion in meshQuery.AutocompleteAsync(nodePath, "", limit: 50))
                {
                    if (suggestion.NodeType is "User" or "Group")
                        subjectOptions.Add(new Option<string>(suggestion.Path, $"{suggestion.Name} ({suggestion.Path})"));
                }
            }
            catch { }
        }
        ctx.Host.UpdateData(subjectOptionsId, subjectOptions.ToArray());

        // Load role options via IMeshQuery
        var rolesOptionsId = $"roles_options_{Guid.NewGuid().AsString()}";
        var roleOptions = new List<Option>();
        // Add built-in roles
        roleOptions.Add(new Option<string>("Admin", "Administrator (All permissions)"));
        roleOptions.Add(new Option<string>("Editor", "Editor (Read, Create, Update, Comment)"));
        roleOptions.Add(new Option<string>("Viewer", "Viewer (Read only)"));
        roleOptions.Add(new Option<string>("Commenter", "Commenter (Read, Comment)"));
        // Add custom Role nodes
        if (meshQuery != null)
        {
            try
            {
                var roleNodes = await meshQuery
                    .QueryAsync<MeshNode>($"nodeType:Role scope:subtree")
                    .ToListAsync();
                foreach (var rn in roleNodes)
                {
                    var role = rn.Content as Role
                        ?? (rn.Content is System.Text.Json.JsonElement rje
                            ? System.Text.Json.JsonSerializer.Deserialize<Role>(rje.GetRawText())
                            : null);
                    if (role != null && !new[] { "Admin", "Editor", "Viewer", "Commenter" }.Contains(role.Id))
                        roleOptions.Add(new Option<string>(role.Id, $"{role.DisplayName ?? role.Id} ({role.Permissions})"));
                }
            }
            catch { }
        }
        ctx.Host.UpdateData(rolesOptionsId, roleOptions.ToArray());

        var formContent = Controls.Stack.WithStyle("gap: 16px; padding: 16px;")
            .WithView(new ComboboxControl(
                new JsonPointerReference("subjectId"),
                new JsonPointerReference(LayoutAreaReference.GetDataPointer(subjectOptionsId)))
            {
                Label = "Subject (User or Group)",
                Required = true,
                Autocomplete = ComboboxAutocomplete.Both,
                Placeholder = "Search users or groups...",
                DataContext = LayoutAreaReference.GetDataPointer(formId)
            })
            .WithView(new ComboboxControl(
                new JsonPointerReference("roleId"),
                new JsonPointerReference(LayoutAreaReference.GetDataPointer(rolesOptionsId)))
            {
                Label = "Role",
                Required = true,
                Autocomplete = ComboboxAutocomplete.List,
                DataContext = LayoutAreaReference.GetDataPointer(formId)
            })
            .WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithStyle("justify-content: flex-end; gap: 8px; margin-top: 16px;")
                .WithView(Controls.Button("Save")
                    .WithAppearance(Appearance.Accent)
                    .WithClickAction(async saveCtx =>
                    {
                        var formValues = await saveCtx.Host.Stream
                            .GetDataStream<Dictionary<string, object?>>(formId).FirstAsync();

                        var subjectId = formValues.GetValueOrDefault("subjectId")?.ToString()?.Trim();
                        var roleId = formValues.GetValueOrDefault("roleId")?.ToString()?.Trim();

                        if (string.IsNullOrEmpty(subjectId) || string.IsNullOrEmpty(roleId))
                        {
                            var errorDialog = Controls.Dialog(
                                Controls.Markdown("Please fill in both **Subject** and **Role**."),
                                "Validation Error"
                            ).WithSize("S").WithClosable(true);
                            saveCtx.Host.UpdateArea(DialogControl.DialogArea, errorDialog);
                            return;
                        }

                        // Create AccessAssignment MeshNode
                        var catalog = saveCtx.Hub.ServiceProvider.GetService<IMeshCatalog>();
                        if (catalog != null)
                        {
                            var assignmentNode = new MeshNode($"{subjectId}_Access", nodePath)
                            {
                                NodeType = Configuration.AccessAssignmentNodeType.NodeType,
                                Name = $"{subjectId} Access",
                                Content = new AccessAssignment
                                {
                                    SubjectId = subjectId,
                                    Roles = [new RoleAssignment { RoleId = roleId }],
                                    DisplayName = subjectId
                                }
                            };
                            await catalog.CreateNodeAsync(assignmentNode);
                        }

                        saveCtx.Host.UpdateArea(DialogControl.DialogArea, null!);
                    })));

        var dialog = Controls.Dialog(formContent, "Add Role Assignment")
            .WithSize("M")
            .WithClosable(true);

        ctx.Host.UpdateArea(DialogControl.DialogArea, dialog);
    }
}
