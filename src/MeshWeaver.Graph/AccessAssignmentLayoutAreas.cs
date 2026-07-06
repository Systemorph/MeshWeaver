using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
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
/// Layout areas for AccessAssignment nodes. Each assignment renders as ONE clean row —
/// a path-bound person/group thumbnail plus a node-bound role editor per role (role dropdown +
/// Deny). All editing binds DIRECTLY to the assignment's node stream (no hand-built HTML, no
/// layout-area data replica).
/// </summary>
public static class AccessAssignmentLayoutAreas
{
    /// <summary>
    /// Registers the AccessAssignment layout-area views (Thumbnail, Overview, Delete) on the hub configuration.
    /// </summary>
    /// <param name="configuration">The message hub configuration to register the views on.</param>
    /// <returns>The same configuration with the AccessAssignment views added, for chaining.</returns>
    public static MessageHubConfiguration AddAccessAssignmentViews(this MessageHubConfiguration configuration)
        => configuration.AddLayout(layout => layout
            .WithView(MeshNodeLayoutAreas.ThumbnailArea, Thumbnail)
            .WithView(MeshNodeLayoutAreas.OverviewArea, Overview)
            .WithView(MeshNodeLayoutAreas.DeleteArea, DeleteLayoutArea.Delete));

    /// <summary>
    /// Thumbnail — one row for the assignment: <c>[person/group] [role ▾ + Deny] [dismiss role]…
    /// [+ Add role] [remove user]</c>. The person and each role are path-/node-bound controls; the
    /// backend only declares structure. Edit controls render only when the caller has the
    /// <see cref="Permission.Delete"/> (manage-access) permission on the node.
    /// </summary>
    public static IObservable<UiControl?> Thumbnail(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var ownNode = host.Workspace.GetMeshNodeStream();
        var permsStream = host.Hub.GetEffectivePermissions(hubPath);

        return ownNode.CombineLatest(permsStream, (node, perms) =>
            {
                var assignment = AccessControlLayoutArea.DeserializeAssignment(node);
                if (assignment == null)
                    return (UiControl?)new MeshNodeThumbnailControl(hubPath, node.Name ?? hubPath);
                return (UiControl?)BuildAssignmentRow(hubPath, assignment, perms.HasFlag(Permission.Delete));
            });
    }

    /// <summary>
    /// Builds the assignment row. The subject (User/Group) and every role bind to their own node
    /// streams GUI-side, so the row stays live with no backend value resolution.
    /// </summary>
    private static UiControl BuildAssignmentRow(string nodePath, AccessAssignment assignment, bool canEdit)
    {
        var subjectPath = assignment.AccessObject;
        var subjectFallback = assignment.DisplayName
            ?? subjectPath.Split('/').LastOrDefault()
            ?? subjectPath;

        // Person / group — path-bound avatar + name.
        var subject = new MeshNodeThumbnailControl(subjectPath, subjectFallback)
            .WithStyle("flex: 1; min-width: 0;");

        // Roles — one node-bound editor per role, with a per-role remove and an "Add role" button.
        // flex-shrink: 0 + nowrap keeps the role dropdown + "Deny" checkbox at their natural size so
        // the "Deny" label can't clip to "Der…" (issue #236) — the subject (min-width: 0) absorbs the
        // squeeze and its name truncates instead.
        var rolesBlock = Controls.Stack.WithStyle("gap: 6px; flex-shrink: 0;");
        for (var i = 0; i < assignment.Roles.Count; i++)
        {
            var index = i;
            var roleRow = Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithStyle("align-items: center; gap: 6px; white-space: nowrap;")
                .WithView(new MeshNodeRoleEditorControl(nodePath, index) { CanEdit = canEdit });

            // Per-role remove — a subtle dismiss (×) with a tooltip so it's distinct from the
            // remove-user action at the end of the row (issue #236: the two were indistinguishable).
            if (canEdit)
                roleRow = roleRow.WithView(Controls.Button("")
                    .WithAppearance(Appearance.Stealth)
                    .WithIconStart(FluentIcons.Dismiss(IconSize.Size16))
                    .WithLabel("Remove role")
                    .WithStyle("min-width: 24px; padding: 0 4px; height: 24px;")
                    .WithClickAction(ctx =>
                    {
                        RemoveRole(ctx.Host, nodePath, index);
                        return Task.CompletedTask;
                    }));

            rolesBlock = rolesBlock.WithView(roleRow);
        }

        if (canEdit)
            rolesBlock = rolesBlock.WithView(Controls.Button("+ Add role")
                .WithAppearance(Appearance.Stealth)
                .WithStyle("align-self: flex-start; font-size: 12px; padding: 0 6px; height: 24px;")
                .WithClickAction(ctx =>
                {
                    AddRole(ctx.Host, nodePath, Role.Editor.Id);
                    return Task.CompletedTask;
                }));

        var row = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("align-items: flex-start; gap: 16px; padding: 8px 0; flex-wrap: wrap; " +
                       "border-bottom: 1px solid var(--neutral-stroke-divider-rest); width: 100%;")
            .WithView(subject)
            .WithView(rolesBlock);

        // Remove the whole assignment (the user/group) from this scope. A person-remove icon (not a
        // bare ×) with a name-bearing tooltip + a left margin, so it reads as "remove this person from
        // the scope" and is clearly separated from the per-role × (issue #236).
        if (canEdit)
            row = row.WithView(Controls.Button("")
                .WithAppearance(Appearance.Stealth)
                .WithIconStart(FluentIcons.PersonDelete(IconSize.Size16))
                .WithLabel($"Remove {subjectFallback} from this scope")
                .WithStyle("min-width: 28px; height: 28px; margin-left: 8px; color: var(--error-foreground);")
                .WithClickAction(ctx =>
                {
                    RemoveUser(ctx.Host, nodePath);
                    return Task.CompletedTask;
                }));

        return row;
    }

    /// <summary>
    /// Overview — the assignment's detail page: subject thumbnail, Change Subject, and the same
    /// node-bound role editors as the row.
    /// </summary>
    public static IObservable<UiControl?> Overview(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var ownNode = host.Workspace.GetMeshNodeStream();
        var permsStream = host.Hub.GetEffectivePermissions(hubPath);

        return ownNode.CombineLatest(permsStream, (node, perms) =>
            {
                if (!perms.HasFlag(Permission.Read))
                    return (UiControl?)Controls.Markdown("Access denied.");
                return BuildOverviewContent(host, node, hubPath, perms.HasFlag(Permission.Delete));
            });
    }

    private static UiControl BuildOverviewContent(
        LayoutAreaHost host, MeshNode? node, string hubPath, bool canEdit)
    {
        var stack = Controls.Stack.WithWidth("100%").WithStyle(MeshNodeLayoutAreas.GetContainerStyle(host));
        stack = stack.WithView(MeshNodeLayoutAreas.BuildHeader(host, node, canEdit));

        var assignment = node?.Content == null ? null
            : (node.ContentAs<AccessAssignment>(host.Hub.JsonSerializerOptions) ?? AccessControlLayoutArea.DeserializeAssignment(node));
        if (assignment == null)
            return stack;

        // Subject — path-bound thumbnail.
        if (!string.IsNullOrEmpty(assignment.AccessObject))
            stack = stack.WithView(new MeshNodeThumbnailControl(
                assignment.AccessObject,
                assignment.DisplayName ?? assignment.AccessObject));

        if (canEdit)
            stack = stack.WithView(Controls.Button("Change Subject")
                .WithAppearance(Appearance.Neutral)
                .WithStyle("align-self: flex-start; margin-top: 8px;")
                .WithClickAction(ctx =>
                {
                    ShowChangeAccessObjectDialog(ctx, hubPath);
                    return Task.CompletedTask;
                }));

        stack = stack.WithView(Controls.H3("Roles").WithStyle("margin: 24px 0 12px 0;"));

        if (assignment.Roles.Count == 0)
        {
            stack = stack.WithView(Controls.Markdown("_No roles assigned._"));
        }
        else
        {
            var rolesBlock = Controls.Stack.WithStyle("gap: 8px;");
            for (var i = 0; i < assignment.Roles.Count; i++)
            {
                var index = i;
                var roleRow = Controls.Stack
                    .WithOrientation(Orientation.Horizontal)
                    .WithStyle("align-items: center; gap: 8px;")
                    .WithView(new MeshNodeRoleEditorControl(hubPath, index) { CanEdit = canEdit });

                if (canEdit)
                    roleRow = roleRow.WithView(Controls.Button("×")
                        .WithAppearance(Appearance.Stealth)
                        .WithStyle("min-width: 28px; height: 28px;")
                        .WithClickAction(ctx =>
                        {
                            RemoveRole(ctx.Host, hubPath, index);
                            return Task.CompletedTask;
                        }));

                rolesBlock = rolesBlock.WithView(roleRow);
            }
            stack = stack.WithView(rolesBlock);
        }

        if (canEdit)
            stack = stack.WithView(Controls.Button("+ Add role")
                .WithAppearance(Appearance.Accent)
                .WithStyle("align-self: flex-start; margin-top: 16px;")
                .WithClickAction(ctx =>
                {
                    AddRole(ctx.Host, hubPath, Role.Editor.Id);
                    return Task.CompletedTask;
                }));

        return stack;
    }

    /// <summary>
    /// Removes a role from the assignment. When it was the last role, the whole assignment node is
    /// deleted. Pure subscribe — no await.
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

    /// <summary>Adds a role to the assignment. Pure subscribe — no await.</summary>
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

    /// <summary>Removes the whole assignment (the user/group) from this scope. Pure subscribe.</summary>
    internal static void RemoveUser(LayoutAreaHost host, string nodePath)
    {
        var meshService = host.Hub.ServiceProvider.GetRequiredService<IMeshService>();
        meshService.DeleteNode(nodePath).Subscribe(
            _ => { },
            ex => host.Hub.ServiceProvider.GetService<ILoggerFactory>()
                ?.CreateLogger(typeof(AccessAssignmentLayoutAreas))
                .LogWarning(ex, "Failed to remove access assignment {Path}", nodePath));
    }

    /// <summary>
    /// Subscribes to the OWN node's stream, takes the current value, invokes <paramref name="onNode"/>
    /// exactly once. Used by click handlers that need the latest content before mutating it.
    /// </summary>
    private static void SubscribeOnceToCurrentNode(
        LayoutAreaHost host, string path, Action<MeshNode> onNode)
    {
        host.Workspace.GetMeshNodeStream()
            .Where(n => n != null)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(5))
            .Subscribe(
                onNext: node => onNode(node!),
                onError: ex => host.Hub.ServiceProvider.GetService<ILoggerFactory>()
                    ?.CreateLogger(typeof(AccessAssignmentLayoutAreas))
                    .LogWarning(ex, "Failed reading node {Path} from workspace", path));
    }

    private static void ShowChangeAccessObjectDialog(UiActionContext ctx, string nodePath)
    {
        var formId = $"change_subject_{Guid.NewGuid().AsString()}";
        ctx.Host.UpdateData(formId, new Dictionary<string, object?> { ["accessObject"] = "" });

        // Canonical subject queries for the assignment's SCOPE (the path prefix before
        // /_Access). The previous hand-rolled pair searched the legacy "Group" partition,
        // whose schema no longer exists — the group half always came back empty (issue #213).
        var subjectQueries = AccessSubjectQueries.ForScope(
            AccessSubjectQueries.ScopeOfAssignment(nodePath));

        var formContent = Controls.Stack.WithStyle("gap: 16px; padding: 16px;")
            .WithView(new MeshNodePickerControl(new JsonPointerReference("accessObject"))
            {
                Queries = subjectQueries,
                FilterInMemory = true,
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
                .WithClickAction((Action<UiActionContext>)(saveCtx =>
                {
                    saveCtx.Host.Stream.GetDataStream<Dictionary<string, object?>>(formId)
                        .Take(1)
                        .Subscribe(formValues =>
                        {
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
                        });
                })));

        ctx.Host.UpdateArea(DialogControl.DialogArea,
            Controls.Dialog(formContent, "Change Subject").WithSize("M").WithActions(actions));
    }

    /// <summary>Updates the AccessObject on the assignment node. Pure subscribe — no await.</summary>
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
