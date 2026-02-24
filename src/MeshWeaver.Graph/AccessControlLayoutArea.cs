using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Domain;
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
/// Inherited assignments are loaded via IMeshQuery from ancestor nodes (merged per person).
/// Local assignments are rendered via MeshSearchControl with Thumbnail areas.
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

        return nodeStream
            .SelectMany(async nodes =>
            {
                var node = nodes.FirstOrDefault(n => n.Namespace == hubPath || n.Path == hubPath);
                var isAdmin = await CheckAdminPermission(host.Hub, hubPath);

                // Load inherited assignments from ancestor nodes via IMeshQuery (one-shot, rarely changes)
                var inherited = new List<(AccessAssignment Assignment, string SourcePath, MeshNode Node)>();
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
                                inherited.Add((assignment, assignmentNode.Namespace ?? "", assignmentNode));
                        }
                    }
                    catch
                    {
                        // Query may fail if index not ready
                    }
                }

                // Pre-fetch user nodes for icons (assignment nodes may lack avatars)
                var userNodeLookup = new Dictionary<string, MeshNode>();
                if (meshQuery != null)
                {
                    var userPaths = inherited.Select(x => x.Assignment.AccessObject).Distinct();
                    foreach (var userPath in userPaths)
                    {
                        try
                        {
                            var userNode = await meshQuery.QueryAsync<MeshNode>(
                                $"path:{userPath} scope:exact").FirstOrDefaultAsync();
                            if (userNode != null)
                                userNodeLookup[userPath] = userNode;
                        }
                        catch { }
                    }
                }

                return BuildAccessControlPage(host, node, hubPath, isAdmin, inherited, userNodeLookup);
            });
    }

    internal static AccessAssignment? DeserializeAssignment(MeshNode node)
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
        LayoutAreaHost host,
        MeshNode? node,
        string nodePath,
        bool isAdmin,
        IReadOnlyList<(AccessAssignment Assignment, string SourcePath, MeshNode Node)> inherited,
        Dictionary<string, MeshNode> userNodeLookup)
    {
        var stack = Controls.Stack.WithStyle("padding: 24px; gap: 24px; width: 100%;");

        // Header
        var headerText = node?.Name ?? nodePath.Split('/').LastOrDefault() ?? nodePath;
        stack = stack.WithView(Controls.H2($"Access Control - {headerText}"));

        // Section 1: Inherited Permissions (merged per person, using builder)
        stack = stack.WithView(Controls.H3("Inherited Permissions").WithStyle("margin: 0;"));

        if (inherited.Count == 0)
        {
            stack = stack.WithView(Controls.Html("<p style=\"color: var(--neutral-foreground-hint);\">No inherited permissions.</p>"));
        }
        else
        {
            stack = stack.WithView(BuildInheritedSection(inherited, userNodeLookup));
        }

        // Section 2: Local Assignments (reactive via MeshSearchControl with Thumbnail areas)
        stack = stack.WithView(Controls.H3("Local Assignments").WithStyle("margin: 0;"));

        stack = stack.WithView(Controls.MeshSearch
            .WithHiddenQuery($"namespace:{nodePath} nodeType:AccessAssignment")
            .WithPlaceholder("Search assignments...")
            .WithItemArea(MeshNodeLayoutAreas.ThumbnailArea)
            .WithGridBreakpoints(xs: 12, sm: 6, md: 4, lg: 3)
            .WithReactiveMode(true)
            .WithDisableNavigation()
            .WithStyle("width:100%;"));

        // + button if admin
        if (isAdmin)
        {
            stack = stack.WithView(Controls.Button("+ Add Assignment")
                .WithAppearance(Appearance.Accent)
                .WithStyle("align-self: flex-start; margin-top: 8px;")
                .WithClickAction(async ctx => await ShowAddAssignmentDialog(ctx, nodePath)));
        }

        return stack;
    }

    /// <summary>
    /// Builds the inherited section by merging assignments per person.
    /// Uses the MeshNode from the ancestor query to get user icons.
    /// </summary>
    private static UiControl BuildInheritedSection(
        IReadOnlyList<(AccessAssignment Assignment, string SourcePath, MeshNode Node)> inherited,
        Dictionary<string, MeshNode> userNodeLookup)
    {
        var merged = inherited
            .GroupBy(x => x.Assignment.AccessObject)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var first = g.First();
                var mergedRoles = g
                    .SelectMany(x =>
                    {
                        var source = string.IsNullOrEmpty(x.SourcePath) ? "Global" : x.SourcePath.Split('/').LastOrDefault() ?? x.SourcePath;
                        return x.Assignment.Roles.Select(r => new RoleAssignment
                        {
                            Role = string.IsNullOrEmpty(r.Role) ? $"(no role) [{source}]" : $"{r.Role} [{source}]",
                            Denied = r.Denied
                        });
                    })
                    .ToList();

                return (Assignment: first.Assignment with { Roles = mergedRoles }, first.Node);
            })
            .ToList();

        var container = Controls.Stack.WithStyle("gap: 6px;");
        foreach (var item in merged)
        {
            // Prefer user node (has avatar) over assignment node (may lack icon)
            var userNode = userNodeLookup.GetValueOrDefault(item.Assignment.AccessObject);
            var displayNode = userNode ?? item.Node;
            container = container.WithView(AccessAssignmentControlBuilder.Build(
                item.Assignment, node: displayNode, isEditable: false));
        }
        return container;
    }

    /// <summary>
    /// Deletes an AccessAssignment node.
    /// </summary>
    internal static async Task DeleteAssignment(UiActionContext ctx, LayoutAreaHost host, string nodePath)
    {
        var meshCatalog = host.Hub.ServiceProvider.GetService<IMeshCatalog>();
        if (meshCatalog != null)
        {
            try
            {
                await meshCatalog.DeleteNodeAsync(nodePath);
            }
            catch (Exception ex)
            {
                var dialog = Controls.Dialog(
                    Controls.Markdown($"Failed to delete: {ex.Message}"),
                    "Error"
                ).WithSize("S").WithClosable(true);
                ctx.Host.UpdateArea(DialogControl.DialogArea, dialog);
            }
        }
    }

    /// <summary>
    /// Shows a dialog to add a new access assignment.
    /// Captures both Subject (user/group) AND Role in one dialog.
    /// </summary>
    private static Task ShowAddAssignmentDialog(UiActionContext ctx, string nodePath)
    {
        var formId = $"add_assignment_{Guid.NewGuid().AsString()}";
        ctx.Host.UpdateData(formId, new Dictionary<string, object?>
        {
            ["accessObject"] = "",
            ["role"] = ""
        });

        // Resolve queries for AccessObject from [MeshNode] attribute
        var meshNodeAttr = typeof(AccessAssignment).GetProperty(nameof(AccessAssignment.AccessObject))!
            .GetCustomAttributes(typeof(MeshNodeAttribute), false).OfType<MeshNodeAttribute>().First();
        var subjectQueries = MeshNodeAttribute.ResolveQueries(meshNodeAttr.Queries, nodePath, nodePath);

        // Resolve queries for Role from [MeshNodeCollection] attribute
        var rolesAttr = typeof(AccessAssignment).GetProperty(nameof(AccessAssignment.Roles))!
            .GetCustomAttributes(typeof(MeshNodeCollectionAttribute), false)
            .OfType<MeshNodeCollectionAttribute>().First();
        var roleQueries = MeshNodeCollectionAttribute.ResolveQueries(rolesAttr.Queries, nodePath, nodePath);

        var formContent = Controls.Stack.WithStyle("gap: 16px; padding: 16px;")
            .WithView(new MeshNodePickerControl(new JsonPointerReference("accessObject"))
            {
                Queries = subjectQueries,
                Label = "Subject (User or Group)",
                Required = true,
                DataContext = LayoutAreaReference.GetDataPointer(formId)
            })
            .WithView(new MeshNodePickerControl(new JsonPointerReference("role"))
            {
                Queries = roleQueries,
                Label = "Role",
                Required = true,
                DataContext = LayoutAreaReference.GetDataPointer(formId)
            });

        var actions = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 8px;")
            .WithView(Controls.Button("Create")
                .WithAppearance(Appearance.Accent)
                .WithClickAction(async saveCtx =>
                {
                    var formValues = await saveCtx.Host.Stream
                        .GetDataStream<Dictionary<string, object?>>(formId).FirstAsync();

                    var selectedSubject = formValues.GetValueOrDefault("accessObject")?.ToString()?.Trim();
                    var selectedRole = formValues.GetValueOrDefault("role")?.ToString()?.Trim();

                    if (string.IsNullOrEmpty(selectedSubject))
                    {
                        ShowValidationError(saveCtx, "Please select a **Subject**.");
                        return;
                    }
                    if (string.IsNullOrEmpty(selectedRole))
                    {
                        ShowValidationError(saveCtx, "Please select a **Role**.");
                        return;
                    }

                    var subjectName = selectedSubject.Split('/').Last();
                    var nodeId = $"{subjectName}_Access";
                    var path = $"{nodePath}/{nodeId}";

                    // Check if an assignment already exists for this subject
                    MeshNode? existing = null;
                    var query = saveCtx.Hub.ServiceProvider.GetService<IMeshQuery>();
                    if (query != null)
                    {
                        try
                        {
                            existing = await query.QueryAsync<MeshNode>($"path:{path} scope:exact")
                                .FirstOrDefaultAsync();
                        }
                        catch { }
                    }

                    // Close dialog
                    saveCtx.Host.UpdateArea(DialogControl.DialogArea, null!);

                    if (existing != null)
                    {
                        // Assignment already exists for this subject — dialog closes, page stays
                    }
                    else
                    {
                        // Create the node directly with the role already set
                        var catalog = saveCtx.Hub.ServiceProvider.GetService<IMeshCatalog>();
                        if (catalog != null)
                        {
                            // Look up the subject node to copy their icon
                            string? subjectIcon = null;
                            if (query != null)
                            {
                                try
                                {
                                    var subjectNode = await query.QueryAsync<MeshNode>($"path:{selectedSubject} scope:exact")
                                        .FirstOrDefaultAsync();
                                    subjectIcon = subjectNode?.Icon;
                                }
                                catch { }
                            }

                            var newNode = new MeshNode(nodeId, nodePath)
                            {
                                NodeType = Configuration.AccessAssignmentNodeType.NodeType,
                                Name = $"{subjectName} Access",
                                Icon = subjectIcon,
                                Content = new AccessAssignment
                                {
                                    AccessObject = selectedSubject,
                                    DisplayName = subjectName,
                                    Roles = [new RoleAssignment { Role = selectedRole, Denied = false }]
                                }
                            };

                            // Save directly — no transient/Create view needed
                            saveCtx.Hub.Post(
                                new DataChangeRequest { ChangedBy = saveCtx.Host.Stream.ClientId }.WithUpdates(newNode),
                                o => o.WithTarget(saveCtx.Hub.Address));
                        }
                    }
                }))
            .WithView(Controls.Button("Cancel")
                .WithAppearance(Appearance.Neutral)
                .WithClickAction(cancelCtx =>
                {
                    cancelCtx.Host.UpdateArea(DialogControl.DialogArea, null!);
                    return Task.CompletedTask;
                }));

        var dialog = Controls.Dialog(formContent, "Add Assignment")
            .WithSize("M")
            .WithActions(actions);

        ctx.Host.UpdateArea(DialogControl.DialogArea, dialog);
        return Task.CompletedTask;
    }

    private static void ShowValidationError(UiActionContext ctx, string message)
    {
        var errorDialog = Controls.Dialog(
            Controls.Markdown(message),
            "Validation Error"
        ).WithSize("S").WithClosable(true);
        ctx.Host.UpdateArea(DialogControl.DialogArea, errorDialog);
    }
}
