using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// Layout areas for AccessAssignment nodes.
/// Custom Thumbnail uses MeshNodeThumbnailControl with role chips.
/// Custom Overview shows user thumbnail + roles in LayoutGrid.
/// </summary>
public static class AccessAssignmentLayoutAreas
{
    public static MessageHubConfiguration AddAccessAssignmentViews(this MessageHubConfiguration configuration)
        => configuration.AddLayout(layout => layout
            .WithView(MeshNodeLayoutAreas.ThumbnailArea, Thumbnail)
            .WithView(MeshNodeLayoutAreas.OverviewArea, Overview)
            .WithView(MeshNodeLayoutAreas.DeleteArea, DeleteLayoutArea.Delete));

    /// <summary>   
    /// Custom thumbnail — rich card showing user icon + name, role names + icons, × buttons.
    /// Async: queries IMeshQuery for user and role node details.
    /// </summary>
    public static IObservable<UiControl?> Thumbnail(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? [])
            ?? Observable.Return<MeshNode[]>([]);

        return nodeStream.SelectMany(async nodes =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);
            if (node == null)
                return (UiControl?)MeshNodeThumbnailControl.FromNode(null, hubPath);

            var assignment = AccessControlLayoutArea.DeserializeAssignment(node);
            if (assignment == null)
                return (UiControl?)MeshNodeThumbnailControl.FromNode(node, hubPath);

            var meshQuery = host.Hub.ServiceProvider.GetService<IMeshQuery>();
            var permissions = await PermissionHelper.GetEffectivePermissionsAsync(host.Hub, hubPath);
            var canDelete = permissions.HasFlag(Permission.Delete);

            return (UiControl?)await BuildThumbnailCardAsync(host, hubPath, assignment, node, meshQuery, canDelete);
        });
    }

    private static async Task<UiControl> BuildThumbnailCardAsync(
        LayoutAreaHost host, string hubPath,
        AccessAssignment assignment, MeshNode node,
        IMeshQuery? meshQuery, bool canDelete)
    {
        // Load user node for name + icon
        MeshNode? userNode = null;
        if (meshQuery != null && !string.IsNullOrEmpty(assignment.AccessObject))
        {
            try
            {
                userNode = await meshQuery.QueryAsync<MeshNode>(
                    $"path:{assignment.AccessObject} scope:exact").FirstOrDefaultAsync();
            }
            catch { }
        }

        var userName = userNode?.Name ?? assignment.DisplayName ?? assignment.AccessObject;
        var userImageUrl = MeshNodeThumbnailControl.GetImageUrlForNode(userNode)
            ?? MeshNodeThumbnailControl.GetImageUrlForNode(node);

        var card = Controls.Stack.WithStyle("gap: 6px; padding: 8px; width: 100%; cursor: pointer;");

        // Top row: user icon + name + × button
        var topRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("align-items: center; gap: 12px;");

        // User icon
        if (!string.IsNullOrEmpty(userImageUrl))
        {
            topRow = topRow.WithView(Controls.Html(
                $"<img src=\"{EscapeHtml(userImageUrl)}\" alt=\"{EscapeHtml(userName)}\" " +
                "style=\"width:48px;height:48px;min-width:48px;border-radius:8px;object-fit:cover;\" />"));
        }
        else
        {
            var initial = !string.IsNullOrEmpty(userName) ? userName[0].ToString().ToUpper() : "?";
            topRow = topRow.WithView(Controls.Html(
                "<div style=\"width:48px;height:48px;min-width:48px;border-radius:8px;" +
                "background:var(--accent-fill-rest,#0078d4);color:var(--foreground-on-accent-rest,white);" +
                $"display:flex;align-items:center;justify-content:center;font-weight:bold;font-size:18px;\">{initial}</div>"));
        }

        // User name
        topRow = topRow.WithView(Controls.Html(
            $"<span style=\"font-weight:600;flex:1;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;\">{EscapeHtml(userName)}</span>"));

        card = card.WithView(topRow);

        // Role rows
        for (int i = 0; i < assignment.Roles.Count; i++)
        {
            var role = assignment.Roles[i];
            MeshNode? roleNode = null;
            if (meshQuery != null && !string.IsNullOrEmpty(role.Role))
            {
                try
                {
                    roleNode = await meshQuery.QueryAsync<MeshNode>(
                        $"path:{role.Role} scope:exact").FirstOrDefaultAsync();
                }
                catch { }
            }

            var roleName = roleNode?.Name ?? GetRoleDisplayName(role.Role);
            var roleImageUrl = MeshNodeThumbnailControl.GetImageUrlForNode(roleNode);

            var roleRow = Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithStyle("align-items: center; gap: 8px; padding-left: 60px;");

            // Role icon (small)
            if (!string.IsNullOrEmpty(roleImageUrl))
            {
                roleRow = roleRow.WithView(Controls.Html(
                    $"<img src=\"{EscapeHtml(roleImageUrl)}\" alt=\"{EscapeHtml(roleName)}\" " +
                    "style=\"width:20px;height:20px;border-radius:4px;object-fit:cover;\" />"));
            }
            else
            {
                roleRow = roleRow.WithView(Controls.Html(
                    "<div style=\"width:20px;height:20px;border-radius:4px;" +
                    "background:var(--neutral-stroke-rest);display:flex;align-items:center;" +
                    "justify-content:center;font-size:10px;\">\u25cf</div>"));
            }

            // Role name
            var roleStyle = role.Denied
                ? "font-size:13px;color:var(--error-foreground);text-decoration:line-through;flex:1;"
                : "font-size:13px;flex:1;";
            roleRow = roleRow.WithView(Controls.Html(
                $"<span style=\"{roleStyle}\">{EscapeHtml(roleName)}</span>"));

            // Remove role × button (admin only)
            if (canDelete)
            {
                var capturedIndex = i;
                roleRow = roleRow.WithView(Controls.Button("\u00d7")
                    .WithAppearance(Appearance.Stealth)
                    .WithStyle("min-width:24px;padding:0 2px;height:24px;font-size:14px;")
                    .WithClickAction(async ctx =>
                    {
                        await RemoveRoleAsync(ctx.Host, hubPath, capturedIndex);
                    }));
            }

            card = card.WithView(roleRow);
        }

        // + Add role button (admin only)
        if (canDelete)
        {
            card = card.WithView(Controls.Button("+")
                .WithAppearance(Appearance.Stealth)
                .WithStyle("min-width:28px;padding:0 4px;height:24px;font-size:16px;align-self:flex-start;margin-left:56px;")
                .WithClickAction(ctx =>
                {
                    ShowAddRoleDialog(ctx, hubPath);
                    return Task.CompletedTask;
                }));
        }

        return card;
    }

    private static string EscapeHtml(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
            .Replace("\"", "&quot;").Replace("'", "&#39;");
    }

    /// <summary>
    /// Custom overview — user thumbnail + roles in LayoutGrid loaded from IMeshQuery.
    /// </summary>
    public static IObservable<UiControl?> Overview(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();

        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? [])
            ?? Observable.Return<MeshNode[]>([]);

        return nodeStream.SelectMany(async nodes =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);
            var permissions = await PermissionHelper.GetEffectivePermissionsAsync(host.Hub, hubPath);

            if (!permissions.HasFlag(Permission.Read))
                return (UiControl?)Controls.Html("<p>Access denied.</p>");

            var canEdit = permissions.HasFlag(Permission.Update);
            return (UiControl?)await BuildOverviewContentAsync(host, node, hubPath, canEdit);
        });
    }

    private static async Task<UiControl> BuildOverviewContentAsync(
        LayoutAreaHost host, MeshNode? node, string hubPath, bool canEdit)
    {
        var stack = Controls.Stack.WithWidth("100%").WithStyle(MeshNodeLayoutAreas.GetContainerStyle(host));

        // Header
        stack = stack.WithView(MeshNodeLayoutAreas.BuildHeader(host, node, canEdit));

        if (node?.Content == null)
            return stack;

        var assignment = node.Content as AccessAssignment
            ?? AccessControlLayoutArea.DeserializeAssignment(node);
        if (assignment == null)
            return stack;

        var meshQuery = host.Hub.ServiceProvider.GetService<IMeshQuery>();

        // User card — load from IMeshQuery
        if (meshQuery != null && !string.IsNullOrEmpty(assignment.AccessObject))
        {
            try
            {
                var userNode = await meshQuery.QueryAsync<MeshNode>(
                    $"path:{assignment.AccessObject} scope:exact").FirstOrDefaultAsync();
                stack = stack.WithView(MeshNodeThumbnailControl.FromNode(
                    userNode, assignment.AccessObject));
            }
            catch
            {
                stack = stack.WithView(MeshNodeThumbnailControl.FromNode(null, assignment.AccessObject));
            }
        }

        // Roles section — LayoutGrid, 3 per row
        stack = stack.WithView(Controls.H3("Roles").WithStyle("margin: 32px 0 16px 0;"));

        if (assignment.Roles.Count == 0)
        {
            stack = stack.WithView(Controls.Html(
                "<p style=\"color:var(--neutral-foreground-hint);\">No roles assigned.</p>"));
        }
        else
        {
            var grid = Controls.LayoutGrid
                .WithSkin(s => s.WithSpacing(2))
                .WithStyle(s => s.WithWidth("100%"));

            for (int i = 0; i < assignment.Roles.Count; i++)
            {
                var role = assignment.Roles[i];
                MeshNode? roleNode = null;
                if (meshQuery != null && !string.IsNullOrEmpty(role.Role))
                {
                    try
                    {
                        roleNode = await meshQuery.QueryAsync<MeshNode>(
                            $"path:{role.Role} scope:exact").FirstOrDefaultAsync();
                    }
                    catch { }
                }

                var card = MeshNodeThumbnailControl.FromNode(
                    roleNode, string.IsNullOrEmpty(role.Role) ? "(no role)" : role.Role);

                if (canEdit)
                {
                    var capturedIndex = i;
                    var row = Controls.Stack
                        .WithOrientation(Orientation.Horizontal)
                        .WithStyle("align-items: center; gap: 4px;")
                        .WithView(card.WithStyle("flex: 1;"))
                        .WithView(Controls.Button("×")
                            .WithAppearance(Appearance.Stealth)
                            .WithStyle("min-width:28px;padding:0 4px;height:28px;font-size:16px;")
                            .WithClickAction(async ctx =>
                            {
                                await RemoveRoleAsync(ctx.Host, hubPath, capturedIndex);
                            }));
                    grid = grid.WithView(row, s => s.WithXs(12).WithSm(6).WithMd(4));
                }
                else
                {
                    grid = grid.WithView(card, s => s.WithXs(12).WithSm(6).WithMd(4));
                }
            }
            stack = stack.WithView(grid);
        }

        // + Add button
        if (canEdit)
        {
            stack = stack.WithView(Controls.Button("+ Add")
                .WithAppearance(Appearance.Accent)
                .WithStyle("align-self: flex-start; margin-top: 20px;")
                .WithClickAction(ctx =>
                {
                    ShowAddRoleDialog(ctx, hubPath);
                    return Task.CompletedTask;
                }));
        }

        return stack;
    }

    /// <summary>
    /// Removes a role from the assignment. Reads current node from workspace stream,
    /// then posts a DataChangeRequest (no IMeshCatalog dependency).
    /// </summary>
    private static async Task RemoveRoleAsync(LayoutAreaHost host, string nodePath, int indexToRemove)
    {
        var node = await GetCurrentNodeAsync(host, nodePath);
        if (node == null) return;

        var assignment = AccessControlLayoutArea.DeserializeAssignment(node);
        if (assignment == null) return;

        var roles = assignment.Roles.ToList();
        if (indexToRemove < 0 || indexToRemove >= roles.Count) return;
        roles.RemoveAt(indexToRemove);

        if (roles.Count == 0)
        {
            // No roles left — delete entire node
            host.Hub.Post(
                new DataChangeRequest { ChangedBy = host.Stream.ClientId }
                    .WithDeletions(node),
                o => o.WithTarget(host.Hub.Address));
        }
        else
        {
            var updated = node with { Content = assignment with { Roles = roles } };
            host.Hub.Post(
                new DataChangeRequest { ChangedBy = host.Stream.ClientId }.WithUpdates(updated),
                o => o.WithTarget(host.Hub.Address));
        }
    }

    /// <summary>
    /// Reads the current node from the workspace stream (no IMeshCatalog/IMeshQuery dependency).
    /// </summary>
    private static async Task<MeshNode?> GetCurrentNodeAsync(LayoutAreaHost host, string path)
    {
        var stream = host.Workspace.GetStream<MeshNode>();
        if (stream == null) return null;
        var nodes = await stream.Select(n => n ?? []).FirstAsync();
        return nodes.FirstOrDefault(n => n.Path == path);
    }

    /// <summary>
    /// Properly async add — reads node from IMeshQuery, adds role, saves via DataChangeRequest.
    /// </summary>
    private static async Task AddRoleAsync(LayoutAreaHost host, string nodePath, string selectedRole)
    {
        var meshQuery = host.Hub.ServiceProvider.GetService<IMeshQuery>();
        if (meshQuery == null) return;

        MeshNode? node;
        try
        {
            node = await meshQuery.QueryAsync<MeshNode>(
                $"path:{nodePath} scope:exact").FirstOrDefaultAsync();
        }
        catch { return; }
        if (node == null) return;

        var assignment = AccessControlLayoutArea.DeserializeAssignment(node);
        if (assignment == null) return;

        var roles = assignment.Roles.ToList();
        roles.Add(new RoleAssignment { Role = selectedRole, Denied = false });

        var updated = node with { Content = assignment with { Roles = roles } };
        host.Hub.Post(
            new DataChangeRequest { ChangedBy = host.Stream.ClientId }.WithUpdates(updated),
            o => o.WithTarget(host.Hub.Address));
    }

    internal static string GetRoleDisplayName(string rolePath)
    {
        if (string.IsNullOrEmpty(rolePath))
            return "(no role)";
        var lastSlash = rolePath.LastIndexOf('/');
        return lastSlash >= 0 ? rolePath[(lastSlash + 1)..] : rolePath;
    }

    private static void ShowAddRoleDialog(UiActionContext ctx, string nodePath)
    {
        var formId = $"add_role_{Guid.NewGuid().AsString()}";
        ctx.Host.UpdateData(formId, new Dictionary<string, object?> { ["selectedRole"] = "" });

        var rolesAttr = typeof(AccessAssignment).GetProperty(nameof(AccessAssignment.Roles))!
            .GetCustomAttributes(typeof(MeshNodeCollectionAttribute), false)
            .OfType<MeshNodeCollectionAttribute>().First();
        var queries = MeshNodeCollectionAttribute.ResolveQueries(rolesAttr.Queries, nodePath, nodePath);

        var formContent = Controls.Stack.WithStyle("gap: 16px; padding: 16px;")
            .WithView(new MeshNodePickerControl(new JsonPointerReference("selectedRole"))
            {
                Queries = queries,
                Label = "Role",
                Required = true,
                DataContext = LayoutAreaReference.GetDataPointer(formId)
            });

        var actions = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("justify-content: flex-end; gap: 8px;")
            .WithView(Controls.Button("Cancel")
                .WithAppearance(Appearance.Neutral)
                .WithClickAction(cancelCtx =>
                {
                    cancelCtx.Host.UpdateArea(DialogControl.DialogArea, null!);
                    return Task.CompletedTask;
                }))
            .WithView(Controls.Button("Add")
                .WithAppearance(Appearance.Accent)
                .WithClickAction(async addCtx =>
                {
                    var formValues = await addCtx.Host.Stream
                        .GetDataStream<Dictionary<string, object?>>(formId).FirstAsync();

                    var selectedRole = formValues.GetValueOrDefault("selectedRole")?.ToString()?.Trim();
                    if (string.IsNullOrEmpty(selectedRole))
                    {
                        var errorDialog = Controls.Dialog(
                            Controls.Markdown("Please select a **Role**."),
                            "Validation Error"
                        ).WithSize("S").WithClosable(true);
                        addCtx.Host.UpdateArea(DialogControl.DialogArea, errorDialog);
                        return;
                    }

                    addCtx.Host.UpdateArea(DialogControl.DialogArea, null!);
                    await AddRoleAsync(addCtx.Host, nodePath, selectedRole);
                }));

        ctx.Host.UpdateArea(DialogControl.DialogArea,
            Controls.Dialog(formContent, "Add").WithSize("M").WithActions(actions));
    }
}
