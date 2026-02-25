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

        var card = Controls.Stack.WithStyle("gap: 6px; padding: 8px; width: 100%;");

        // Top row: user icon + name + "+" button
        var topRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("align-items: center; gap: 8px;");

        // User icon
        if (!string.IsNullOrEmpty(userImageUrl))
        {
            topRow = topRow.WithView(Controls.Html(
                $"<img src=\"{EscapeHtml(userImageUrl)}\" alt=\"{EscapeHtml(userName)}\" " +
                "style=\"width:36px;height:36px;min-width:36px;border-radius:6px;object-fit:cover;\" />"));
        }
        else
        {
            var initial = !string.IsNullOrEmpty(userName) ? userName[0].ToString().ToUpper() : "?";
            topRow = topRow.WithView(Controls.Html(
                "<div style=\"width:36px;height:36px;min-width:36px;border-radius:6px;" +
                "background:var(--accent-fill-rest,#0078d4);color:var(--foreground-on-accent-rest,white);" +
                $"display:flex;align-items:center;justify-content:center;font-weight:bold;font-size:14px;\">{initial}</div>"));
        }

        // User name
        topRow = topRow.WithView(Controls.Html(
            $"<span style=\"font-weight:600;flex:1;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;font-size:14px;\">{EscapeHtml(userName)}</span>"));

        // + button next to name (admin only)
        if (canDelete)
        {
            topRow = topRow.WithView(Controls.Button("+")
                .WithAppearance(Appearance.Stealth)
                .WithStyle("min-width:24px;padding:0 2px;height:24px;font-size:16px;")
                .WithClickAction(ctx =>
                {
                    ShowAddRoleDialog(ctx, hubPath);
                    return Task.CompletedTask;
                }));
        }

        card = card.WithView(topRow);

        // Role chips — compact inline wrapping, max ~2 rows
        if (assignment.Roles.Count > 0)
        {
            // Resolve role display names
            var roleInfos = new List<(string Name, bool Denied, int Index)>();
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
                roleInfos.Add((roleName, role.Denied, i));
            }

            // Build chip container as HTML for compactness
            var chipsHtml = "<div style=\"display:flex;flex-wrap:wrap;gap:4px;max-height:52px;overflow:hidden;\">";
            foreach (var info in roleInfos)
            {
                var chipStyle = info.Denied
                    ? "text-decoration:line-through;color:var(--error-foreground);background:var(--neutral-fill-secondary-rest);"
                    : "background:var(--neutral-fill-secondary-rest);";
                chipsHtml += $"<span style=\"{chipStyle}display:inline-block;padding:2px 8px;border-radius:12px;font-size:12px;line-height:18px;white-space:nowrap;\">" +
                    $"{EscapeHtml(info.Name)}</span>";
            }
            chipsHtml += "</div>";

            // If admin, use buttons for toggling + × for removal; otherwise static HTML
            if (canDelete)
            {
                var chipsRow = Controls.Stack
                    .WithOrientation(Orientation.Horizontal)
                    .WithStyle("flex-wrap:wrap;gap:4px;max-height:52px;overflow:hidden;align-items:center;");

                foreach (var info in roleInfos)
                {
                    var capturedIndex = info.Index;
                    var chipTextStyle = info.Denied
                        ? "text-decoration:line-through;color:var(--error-foreground);font-size:12px;padding:0 6px;height:22px;min-width:auto;"
                        : "font-size:12px;padding:0 6px;height:22px;min-width:auto;";

                    // Role chip: click to toggle denied
                    chipsRow = chipsRow.WithView(Controls.Button(info.Name)
                        .WithAppearance(Appearance.Stealth)
                        .WithStyle(chipTextStyle +
                            "border-radius:12px 0 0 12px;background:var(--neutral-fill-secondary-rest);")
                        .WithClickAction(async ctx =>
                        {
                            await ToggleDeniedAsync(ctx.Host, hubPath, capturedIndex);
                        }));

                    // × button: remove role
                    chipsRow = chipsRow.WithView(Controls.Button("\u00d7")
                        .WithAppearance(Appearance.Stealth)
                        .WithStyle("font-size:12px;padding:0 4px;height:22px;min-width:auto;" +
                            "border-radius:0 12px 12px 0;background:var(--neutral-fill-secondary-rest);margin-left:-4px;")
                        .WithClickAction(async ctx =>
                        {
                            await RemoveRoleAsync(ctx.Host, hubPath, capturedIndex);
                        }));
                }

                card = card.WithView(chipsRow);
            }
            else
            {
                card = card.WithView(Controls.Html(chipsHtml));
            }
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
    /// Toggles the Denied flag on a role at the given index.
    /// Uses workspace.RequestChange directly for reliable reactive updates.
    /// </summary>
    internal static async Task ToggleDeniedAsync(LayoutAreaHost host, string nodePath, int roleIndex)
    {
        var node = await GetCurrentNodeAsync(host, nodePath);
        if (node == null) return;

        var assignment = AccessControlLayoutArea.DeserializeAssignment(node);
        if (assignment == null) return;

        var roles = assignment.Roles.ToList();
        if (roleIndex < 0 || roleIndex >= roles.Count) return;

        roles[roleIndex] = roles[roleIndex] with { Denied = !roles[roleIndex].Denied };

        var updated = node with { Content = assignment with { Roles = roles } };
        host.Workspace.RequestChange(DataChangeRequest.Update([updated]), null, null);
    }

    /// <summary>
    /// Removes a role from the assignment. Reads current node from workspace stream,
    /// then uses workspace.RequestChange directly for reliable reactive updates.
    /// </summary>
    internal static async Task RemoveRoleAsync(LayoutAreaHost host, string nodePath, int indexToRemove)
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
            host.Workspace.RequestChange(
                new DataChangeRequest().WithDeletions(node), null, null);
        }
        else
        {
            var updated = node with { Content = assignment with { Roles = roles } };
            host.Workspace.RequestChange(DataChangeRequest.Update([updated]), null, null);
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
    /// Adds a role to the assignment. Reads current node from workspace stream,
    /// then uses workspace.RequestChange directly for reliable reactive updates.
    /// </summary>
    internal static async Task AddRoleAsync(LayoutAreaHost host, string nodePath, string selectedRole)
    {
        var node = await GetCurrentNodeAsync(host, nodePath);
        if (node == null) return;

        var assignment = AccessControlLayoutArea.DeserializeAssignment(node);
        if (assignment == null) return;

        var roles = assignment.Roles.ToList();
        roles.Add(new RoleAssignment { Role = selectedRole, Denied = false });

        var updated = node with { Content = assignment with { Roles = roles } };
        host.Workspace.RequestChange(DataChangeRequest.Update([updated]), null, null);
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
            .WithStyle("gap: 8px;")
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
            Controls.Dialog(formContent, "Add Role").WithSize("M").WithActions(actions));
    }
}
