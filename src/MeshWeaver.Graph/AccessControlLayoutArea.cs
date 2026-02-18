using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// Layout area for managing access control on a mesh node.
/// Uses data-bound ItemTemplateControl for reactive updates,
/// SwitchControl for Allow/Deny toggles, and permission gating.
/// </summary>
public static class AccessControlLayoutArea
{
    private const string InheritedStreamId = "acl_inherited";
    private const string LocalStreamId = "acl_local";

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

        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? [])
            ?? Observable.Return<MeshNode[]>([]);

        return nodeStream.SelectMany(async nodes =>
        {
            var node = nodes.FirstOrDefault(n => n.Namespace == hubPath || n.Path == hubPath);
            var isAdmin = await CheckAdminPermission(host.Hub, hubPath);
            return BuildAccessControlPage(host, securityService, node, hubPath, isAdmin);
        });
    }

    private static async Task<bool> CheckAdminPermission(IMessageHub hub, string nodePath)
    {
        var permissions = await PermissionHelper.GetEffectivePermissionsAsync(hub, nodePath);
        return permissions.HasFlag(Permission.Delete); // Admin = has Delete permission
    }

    private static UiControl? BuildAccessControlPage(
        LayoutAreaHost host,
        ISecurityService securityService,
        MeshNode? node,
        string nodePath,
        bool isAdmin)
    {
        var stack = Controls.Stack.WithStyle("padding: 24px; gap: 24px;");

        // Header
        var headerText = node?.Name ?? nodePath.Split('/').LastOrDefault() ?? nodePath;
        stack = stack.WithView(Controls.H2($"Access Control - {headerText}"));

        // Create reactive subjects that we can push updates into
        var inheritedSubject = new BehaviorSubject<IEnumerable<AccessAssignment>>([]);
        var localSubject = new BehaviorSubject<IEnumerable<AccessAssignment>>([]);

        // Load initial data and push to subjects
        LoadAssignmentsAsync(securityService, nodePath, inheritedSubject, localSubject);

        // Inherited Permissions section
        stack = stack.WithView(Controls.H3("Inherited Permissions").WithStyle("margin: 0;"));
        stack = stack.WithView(BuildAssignmentList(
            inheritedSubject, InheritedStreamId, host, securityService, nodePath, isAdmin, showSource: true));

        // Local Assignments section
        stack = stack.WithView(Controls.H3("Local Assignments").WithStyle("margin: 0;"));
        stack = stack.WithView(BuildAssignmentList(
            localSubject, LocalStreamId, host, securityService, nodePath, isAdmin, showSource: false));

        // Add Assignment button (only for admins)
        if (isAdmin)
        {
            stack = stack.WithView(BuildAddButton(host, securityService, nodePath,
                inheritedSubject, localSubject));
        }

        return stack;
    }

    private static async void LoadAssignmentsAsync(
        ISecurityService securityService,
        string nodePath,
        BehaviorSubject<IEnumerable<AccessAssignment>> inheritedSubject,
        BehaviorSubject<IEnumerable<AccessAssignment>> localSubject)
    {
        var inherited = new List<AccessAssignment>();
        var local = new List<AccessAssignment>();

        await foreach (var assignment in securityService.GetAccessAssignmentsAsync(nodePath))
        {
            if (assignment.IsLocal)
                local.Add(assignment);
            else
                inherited.Add(assignment);
        }

        inheritedSubject.OnNext(inherited.OrderBy(a => a.UserId).ThenBy(a => a.RoleId));
        localSubject.OnNext(local.OrderBy(a => a.UserId).ThenBy(a => a.RoleId));
    }

    private static UiControl BuildAssignmentList(
        IObservable<IEnumerable<AccessAssignment>> stream,
        string streamId,
        LayoutAreaHost host,
        ISecurityService securityService,
        string nodePath,
        bool isAdmin,
        bool showSource)
    {
        return stream.BindMany(streamId, a =>
            BuildAssignmentRow(a, nodePath, showSource, isAdmin, host, securityService, stream, streamId)
        );
    }

    private static StackControl BuildAssignmentRow(
        AccessAssignment a,
        string nodePath,
        bool showSource,
        bool isAdmin,
        LayoutAreaHost host,
        ISecurityService securityService,
        IObservable<IEnumerable<AccessAssignment>> parentStream,
        string streamId)
    {
        var row = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("padding: 8px 16px; border-bottom: 1px solid var(--neutral-stroke-rest); align-items: center;")
            .WithView(Controls.Label(a.DisplayName ?? a.UserId).WithStyle("flex: 1; min-width: 120px;"))
            .WithView(Controls.Label(a.RoleId).WithStyle("flex: 1; min-width: 120px;"));

        if (showSource)
        {
            var sourceDisplay = string.IsNullOrEmpty(a.SourcePath) ? "Global" : a.SourcePath;
            row = row.WithView(Controls.Label(sourceDisplay).WithStyle("flex: 1; min-width: 100px;"));
        }

        // Toggle switch: Allow (on) / Deny (off)
        var toggleId = $"toggle_{Guid.NewGuid().AsString()}";
        var isActive = !a.Denied;
        host.UpdateData(toggleId, isActive);

        var toggle = Controls.Switch(new JsonPointerReference(LayoutAreaReference.GetDataPointer(toggleId)))
            .WithCheckedMessage("Allow")
            .WithUncheckedMessage("Deny");

        if (!isAdmin)
            toggle = toggle with { Disabled = true };

        // Handle toggle via click action on a wrapper button
        var toggleButton = Controls.Button("")
            .WithStyle("background: transparent; border: none; padding: 0; min-width: auto; width: 80px;")
            .WithClickAction(async ctx =>
            {
                if (!isAdmin) return;

                var svc = ctx.Hub.ServiceProvider.GetRequiredService<ISecurityService>();
                var newDenied = !a.Denied;
                await svc.ToggleRoleAssignmentAsync(nodePath, a.UserId, a.RoleId, newDenied);

                // Reload and push new data to the streams
                await RefreshAssignmentsAsync(svc, nodePath, ctx.Host);
            });

        row = row.WithView(Controls.Stack
            .WithStyle("width: 100px; display: flex; justify-content: center;")
            .WithView(toggle));

        return row;
    }

    private static async Task RefreshAssignmentsAsync(
        ISecurityService securityService,
        string nodePath,
        LayoutAreaHost host)
    {
        var inherited = new List<AccessAssignment>();
        var local = new List<AccessAssignment>();

        await foreach (var assignment in securityService.GetAccessAssignmentsAsync(nodePath))
        {
            if (assignment.IsLocal)
                local.Add(assignment);
            else
                inherited.Add(assignment);
        }

        host.Stream.SetData(InheritedStreamId,
            inherited.OrderBy(a => a.UserId).ThenBy(a => a.RoleId).ToArray(),
            host.Stream.StreamId);
        host.Stream.SetData(LocalStreamId,
            local.OrderBy(a => a.UserId).ThenBy(a => a.RoleId).ToArray(),
            host.Stream.StreamId);
    }

    private static UiControl BuildAddButton(
        LayoutAreaHost host,
        ISecurityService securityService,
        string nodePath,
        BehaviorSubject<IEnumerable<AccessAssignment>> inheritedSubject,
        BehaviorSubject<IEnumerable<AccessAssignment>> localSubject)
    {
        return Controls.Button("Add Assignment")
            .WithAppearance(Appearance.Accent)
            .WithIconStart(FluentIcons.PersonAdd())
            .WithClickAction(async ctx =>
            {
                await ShowAddAssignmentDialog(ctx, securityService, nodePath);
            });
    }

    private static async Task ShowAddAssignmentDialog(
        UiActionContext ctx,
        ISecurityService securityService,
        string nodePath)
    {
        // Prepare form data
        var formId = $"add_assignment_{Guid.NewGuid().AsString()}";
        ctx.Host.UpdateData(formId, new Dictionary<string, object?>
        {
            ["userId"] = "",
            ["roleId"] = ""
        });

        // Load roles for the select control
        var rolesOptionsId = $"roles_options_{Guid.NewGuid().AsString()}";
        var roles = new List<Option>();
        await foreach (var role in securityService.GetRolesAsync())
        {
            roles.Add(new Option<string>(role.Id, $"{role.DisplayName ?? role.Id}"));
        }
        ctx.Host.UpdateData(rolesOptionsId, roles.ToArray());

        // Build dialog content
        var formContent = Controls.Stack.WithStyle("gap: 16px; padding: 16px;")
            .WithView(new TextFieldControl(new JsonPointerReference("userId"))
            {
                Placeholder = "Enter user ID...",
                Label = "User ID",
                Required = true,
                Immediate = true,
                DataContext = LayoutAreaReference.GetDataPointer(formId)
            })
            .WithView(new SelectControl(
                new JsonPointerReference("roleId"),
                new JsonPointerReference(LayoutAreaReference.GetDataPointer(rolesOptionsId)))
            {
                Label = "Role",
                Required = true,
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

                        var userId = formValues.GetValueOrDefault("userId")?.ToString()?.Trim();
                        var roleId = formValues.GetValueOrDefault("roleId")?.ToString()?.Trim();

                        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(roleId))
                        {
                            var errorDialog = Controls.Dialog(
                                Controls.Markdown("Please fill in both **User ID** and **Role**."),
                                "Validation Error"
                            ).WithSize("S").WithClosable(true);
                            saveCtx.Host.UpdateArea(DialogControl.DialogArea, errorDialog);
                            return;
                        }

                        var svc = saveCtx.Hub.ServiceProvider.GetRequiredService<ISecurityService>();
                        await svc.AddUserRoleAsync(userId, roleId, nodePath);

                        // Close dialog and refresh data reactively
                        saveCtx.Host.UpdateArea(DialogControl.DialogArea, null!);
                        await RefreshAssignmentsAsync(svc, nodePath, saveCtx.Host);
                    })));

        var dialog = Controls.Dialog(formContent, "Add Role Assignment")
            .WithSize("M")
            .WithClosable(true);

        ctx.Host.UpdateArea(DialogControl.DialogArea, dialog);
    }
}
