using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Data;
using Microsoft.Extensions.Logging;
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
    /// Custom thumbnail — rich card showing user icon + name (path-bound via
    /// MeshNodeThumbnailControl) and role chips. Backend declares structure only;
    /// the GUI subscribes to per-node streams. No await, no fetch, no resolution
    /// of user/role node values on the backend.
    /// </summary>
    public static IObservable<UiControl?> Thumbnail(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        // There is always exactly one MeshNode per path — GetMeshNodeStream returns it.
        var ownNode = host.Workspace.GetMeshNodeStream();
        var permsStream = PermissionHelper.ObservePermissions(host.Hub, hubPath);

        return ownNode.CombineLatest(permsStream, (node, perms) =>
            {
                var assignment = AccessControlLayoutArea.DeserializeAssignment(node);
                if (assignment == null)
                    return (UiControl?)new MeshNodeThumbnailControl(hubPath, node.Name ?? hubPath);
                return (UiControl?)BuildThumbnailCardSync(host, hubPath, assignment, perms.HasFlag(Permission.Delete));
            });
    }

    /// <summary>
    /// Builds the thumbnail card structure. Values for user / role nodes are not
    /// loaded here — the backend declares <see cref="MeshNodeThumbnailControl"/>
    /// with NodePath only and the GUI binds to the per-node MeshNodeReference stream.
    /// </summary>
    private static UiControl BuildThumbnailCardSync(
        LayoutAreaHost host, string hubPath,
        AccessAssignment assignment, bool canDelete)
    {
        var userPath = assignment.AccessObject;
        var userFallback = assignment.DisplayName ?? userPath;
        var card = Controls.Stack.WithStyle("gap: 6px; padding: 8px; width: 100%;");

        // Top row: bound user thumbnail + "+" button. The thumbnail control declares
        // its NodePath; MeshNodeThumbnailView subscribes to the per-node stream and
        // renders avatar + name live.
        var topRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("align-items: center; gap: 8px;")
            .WithView(new MeshNodeThumbnailControl(userPath, userFallback));

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

        // Role chips — display name derived from the path's last segment. (For live
        // role-name updates, callers can switch chips to MeshNodeThumbnailControl per
        // role; today the chip shows the segment name which rarely changes.)
        if (assignment.Roles.Count > 0)
        {
            var roleInfos = new List<(string Name, bool Denied, int Index)>();
            for (int i = 0; i < assignment.Roles.Count; i++)
            {
                var role = assignment.Roles[i];
                roleInfos.Add((GetRoleDisplayName(role.Role), role.Denied, i));
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

                    // Role chip: click to toggle denied — bind directly to workspace stream
                    chipsRow = chipsRow.WithView(Controls.Button(info.Name)
                        .WithAppearance(Appearance.Stealth)
                        .WithStyle(chipTextStyle +
                            "border-radius:12px 0 0 12px;background:var(--neutral-fill-secondary-rest);")
                        .WithClickAction(ctx =>
                        {
                            ToggleDenied(ctx.Host, hubPath, capturedIndex);
                            return Task.CompletedTask;
                        }));

                    // × button: remove role
                    chipsRow = chipsRow.WithView(Controls.Button("\u00d7")
                        .WithAppearance(Appearance.Stealth)
                        .WithStyle("font-size:12px;padding:0 4px;height:22px;min-width:auto;" +
                            "border-radius:0 12px 12px 0;background:var(--neutral-fill-secondary-rest);margin-left:-4px;")
                        .WithClickAction(ctx =>
                        {
                            RemoveRole(ctx.Host, hubPath, capturedIndex);
                            return Task.CompletedTask;
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
    /// Custom overview — user thumbnail + roles in LayoutGrid.
    /// Backend declares path-bound controls only; GUI subscribes to per-node streams.
    /// </summary>
    public static IObservable<UiControl?> Overview(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var ownNode = host.Workspace.GetMeshNodeStream();
        var permsStream = PermissionHelper.ObservePermissions(host.Hub, hubPath);

        return ownNode.CombineLatest(permsStream, (node, perms) =>
            {
                if (!perms.HasFlag(Permission.Read))
                    return (UiControl?)Controls.Html("<p>Access denied.</p>");
                var canEdit = perms.HasFlag(Permission.Update);
                return BuildOverviewContentSync(host, node, hubPath, canEdit);
            });
    }

    private static UiControl BuildOverviewContentSync(
        LayoutAreaHost host, MeshNode? node, string hubPath, bool canEdit)
    {
        var stack = Controls.Stack.WithWidth("100%").WithStyle(MeshNodeLayoutAreas.GetContainerStyle(host));
        stack = stack.WithView(MeshNodeLayoutAreas.BuildHeader(host, node, canEdit));

        var assignment = node?.Content == null ? null
            : (node.Content as AccessAssignment ?? AccessControlLayoutArea.DeserializeAssignment(node));

        if (assignment == null)
            return stack;

        // User card: declare NodePath-bound thumbnail; GUI subscribes to the per-node stream.
        if (!string.IsNullOrEmpty(assignment.AccessObject))
        {
            stack = stack.WithView(new MeshNodeThumbnailControl(
                assignment.AccessObject,
                assignment.DisplayName ?? assignment.AccessObject));
        }

        // Change Subject button (admin only)
        if (canEdit)
        {
            stack = stack.WithView(Controls.Button("Change Subject")
                .WithAppearance(Appearance.Neutral)
                .WithStyle("align-self: flex-start; margin-top: 8px;")
                .WithClickAction(ctx =>
                {
                    ShowChangeAccessObjectDialog(ctx, hubPath);
                    return Task.CompletedTask;
                }));
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
                var rolePath = role.Role;
                var fallback = string.IsNullOrEmpty(rolePath) ? "(no role)" : GetRoleDisplayName(rolePath);
                // Path-bound: the GUI binds to the per-role MeshNodeReference stream.
                var card = string.IsNullOrEmpty(rolePath)
                    ? new MeshNodeThumbnailControl(string.Empty, "(no role)")
                    : new MeshNodeThumbnailControl(rolePath, fallback);

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
                            .WithClickAction(ctx =>
                            {
                                RemoveRole(ctx.Host, hubPath, capturedIndex);
                                return Task.CompletedTask;
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
    /// Toggles the Denied flag on a role at the given index. Pure subscribe — no await.
    /// Reads current node from the workspace stream, mutates Roles, requests change.
    /// Errors propagate via OnError to the subscription instead of being swallowed.
    /// </summary>
    internal static void ToggleDenied(LayoutAreaHost host, string nodePath, int roleIndex)
    {
        SubscribeOnceToCurrentNode(host, nodePath, node =>
        {
            var assignment = AccessControlLayoutArea.DeserializeAssignment(node);
            if (assignment == null) return;
            var roles = assignment.Roles.ToList();
            if (roleIndex < 0 || roleIndex >= roles.Count) return;
            roles[roleIndex] = roles[roleIndex] with { Denied = !roles[roleIndex].Denied };
            var updated = node with { Content = assignment with { Roles = roles } };
            host.Workspace.RequestChange(DataChangeRequest.Update([updated]), null, null);
        });
    }

    /// <summary>
    /// Removes a role from the assignment. Pure subscribe — no await.
    /// </summary>
    internal static void RemoveRole(LayoutAreaHost host, string nodePath, int indexToRemove)
    {
        SubscribeOnceToCurrentNode(host, nodePath, node =>
        {
            var assignment = AccessControlLayoutArea.DeserializeAssignment(node);
            if (assignment == null) return;
            var roles = assignment.Roles.ToList();
            if (indexToRemove < 0 || indexToRemove >= roles.Count) return;
            roles.RemoveAt(indexToRemove);
            if (roles.Count == 0)
            {
                host.Workspace.RequestChange(
                    new DataChangeRequest().WithDeletions(node), null, null);
            }
            else
            {
                var updated = node with { Content = assignment with { Roles = roles } };
                host.Workspace.RequestChange(DataChangeRequest.Update([updated]), null, null);
            }
        });
    }

    /// <summary>
    /// Adds a role to the assignment. Pure subscribe — no await.
    /// </summary>
    internal static void AddRole(LayoutAreaHost host, string nodePath, string selectedRole)
    {
        SubscribeOnceToCurrentNode(host, nodePath, node =>
        {
            var assignment = AccessControlLayoutArea.DeserializeAssignment(node);
            if (assignment == null) return;
            var roles = assignment.Roles.ToList();
            roles.Add(new RoleAssignment { Role = selectedRole, Denied = false });
            var updated = node with { Content = assignment with { Roles = roles } };
            host.Workspace.RequestChange(DataChangeRequest.Update([updated]), null, null);
        });
    }

    /// <summary>
    /// Subscribes to the workspace stream, takes the current node at <paramref name="path"/>,
    /// invokes <paramref name="onNode"/> exactly once. Errors propagate to the layout host's
    /// log via OnError; no swallowed exceptions, no await.
    /// </summary>
    private static void SubscribeOnceToCurrentNode(
        LayoutAreaHost host, string path, Action<MeshNode> onNode)
    {
        var stream = host.Workspace.GetStream<MeshNode>();
        if (stream == null)
        {
            host.Hub.ServiceProvider.GetService<ILoggerFactory>()
                ?.CreateLogger(typeof(AccessAssignmentLayoutAreas))
                .LogWarning("No MeshNode workspace stream for {Path}", path);
            return;
        }
        stream.Select(n => n ?? [])
            .Select(nodes => nodes.FirstOrDefault(n => n.Path == path))
            .Where(n => n != null)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(5))
            .Subscribe(
                onNext: node => onNode(node!),
                onError: ex => host.Hub.ServiceProvider.GetService<ILoggerFactory>()
                    ?.CreateLogger(typeof(AccessAssignmentLayoutAreas))
                    .LogWarning(ex, "Failed reading node {Path} from workspace", path));
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

        // Query roles from the Role namespace (where built-in roles live)
        // and from ancestors of the current node (for custom partition-level roles)
        var roleQueries = new[]
        {
            "namespace:Role nodeType:Role",
            $"namespace:{nodePath.Split("/_Access")[0]} nodeType:Role scope:subtree"
        };

        var formContent = Controls.Stack.WithStyle("gap: 16px; padding: 16px;")
            .WithView(new MeshNodePickerControl(new JsonPointerReference("selectedRole"))
            {
                Queries = roleQueries,
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
                    AddRole(addCtx.Host, nodePath, selectedRole);
                }));

        ctx.Host.UpdateArea(DialogControl.DialogArea,
            Controls.Dialog(formContent, "Add Role").WithSize("M").WithActions(actions));
    }

    private static void ShowChangeAccessObjectDialog(UiActionContext ctx, string nodePath)
    {
        var formId = $"change_subject_{Guid.NewGuid().AsString()}";
        ctx.Host.UpdateData(formId, new Dictionary<string, object?> { ["accessObject"] = "" });

        // Subject picker — only Users and Groups, both partition-scoped (no fan-out).
        var subjectQueries = new[]
        {
            "namespace:User nodeType:User",
            "namespace:Group nodeType:Group"
        };

        var formContent = Controls.Stack.WithStyle("gap: 16px; padding: 16px;")
            .WithView(new MeshNodePickerControl(new JsonPointerReference("accessObject"))
            {
                Queries = subjectQueries,
                Label = "Subject (User or Group)",
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
            .WithView(Controls.Button("Save")
                .WithAppearance(Appearance.Accent)
                .WithClickAction(async saveCtx =>
                {
                    var formValues = await saveCtx.Host.Stream
                        .GetDataStream<Dictionary<string, object?>>(formId).FirstAsync();

                    var selectedSubject = formValues.GetValueOrDefault("accessObject")?.ToString()?.Trim();
                    if (string.IsNullOrEmpty(selectedSubject))
                    {
                        var errorDialog = Controls.Dialog(
                            Controls.Markdown("Please select a **Subject**."),
                            "Validation Error"
                        ).WithSize("S").WithClosable(true);
                        saveCtx.Host.UpdateArea(DialogControl.DialogArea, errorDialog);
                        return;
                    }

                    saveCtx.Host.UpdateArea(DialogControl.DialogArea, null!);
                    UpdateAccessObject(saveCtx.Host, nodePath, selectedSubject);
                }));

        ctx.Host.UpdateArea(DialogControl.DialogArea,
            Controls.Dialog(formContent, "Change Subject").WithSize("M").WithActions(actions));
    }

    /// <summary>
    /// Updates the AccessObject on the assignment node. Pure subscribe — no await.
    /// </summary>
    internal static void UpdateAccessObject(LayoutAreaHost host, string nodePath, string newAccessObject)
    {
        if (string.IsNullOrEmpty(newAccessObject)) return;
        SubscribeOnceToCurrentNode(host, nodePath, node =>
        {
            var assignment = AccessControlLayoutArea.DeserializeAssignment(node);
            if (assignment == null) return;
            var updated = node with { Content = assignment with { AccessObject = newAccessObject } };
            host.Workspace.RequestChange(DataChangeRequest.Update([updated]), null, null);
        });
    }
}
