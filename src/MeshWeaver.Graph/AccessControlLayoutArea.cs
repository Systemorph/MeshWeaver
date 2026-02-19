using System.Reactive.Linq;
using System.Reactive.Subjects;
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
using Microsoft.Extensions.Logging;

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
        LoadAssignmentsAsync(securityService, nodePath, inheritedSubject, localSubject, host.Hub.ServiceProvider);

        // Inherited Permissions section with data-bound template
        stack = stack.WithView(Controls.H3("Inherited Permissions").WithStyle("margin: 0;"));
        stack = stack.WithView(
            inheritedSubject.BindMany(InheritedStreamId, a =>
                Controls.Stack
                    .WithOrientation(Orientation.Horizontal)
                    .WithStyle("padding: 8px 16px; border-bottom: 1px solid var(--neutral-stroke-rest); align-items: center;")
                    .WithView(Controls.Label(a.DisplayLabel).WithStyle("flex: 1; min-width: 120px;"))
                    .WithView(Controls.Label(a.RoleId).WithStyle("flex: 1; min-width: 120px;"))
                    .WithView(Controls.Label(a.SourceDisplay).WithStyle("flex: 1; min-width: 100px;"))
                    .WithView(Controls.Switch(a.IsActive)
                        .WithCheckedMessage("Allow")
                        .WithUncheckedMessage("Deny"))
            )
        );

        // Local Assignments section with data-bound template
        stack = stack.WithView(Controls.H3("Local Assignments").WithStyle("margin: 0;"));
        stack = stack.WithView(
            localSubject.BindMany(LocalStreamId, a =>
                Controls.Stack
                    .WithOrientation(Orientation.Horizontal)
                    .WithStyle("padding: 8px 16px; border-bottom: 1px solid var(--neutral-stroke-rest); align-items: center;")
                    .WithView(Controls.Label(a.DisplayLabel).WithStyle("flex: 1; min-width: 120px;"))
                    .WithView(Controls.Label(a.RoleId).WithStyle("flex: 1; min-width: 120px;"))
                    .WithView(Controls.Switch(a.IsActive)
                        .WithCheckedMessage("Allow")
                        .WithUncheckedMessage("Deny"))
            )
        );

        // Add Assignment button (only for admins)
        if (isAdmin)
        {
            stack = stack.WithView(BuildAddButton(host, securityService, nodePath));
        }

        return stack;
    }

    private static async void LoadAssignmentsAsync(
        ISecurityService securityService,
        string nodePath,
        BehaviorSubject<IEnumerable<AccessAssignment>> inheritedSubject,
        BehaviorSubject<IEnumerable<AccessAssignment>> localSubject,
        IServiceProvider serviceProvider)
    {
        try
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
        catch (Exception ex)
        {
            var logger = serviceProvider.GetService<ILogger<LayoutAreaHost>>();
            logger?.LogError(ex, "Failed to load access assignments for node {NodePath}", nodePath);
        }
    }

    internal static async Task RefreshAssignmentsAsync(
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
        string nodePath)
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
        // Prepare form data with nodePath pre-populated
        var formId = $"add_assignment_{Guid.NewGuid().AsString()}";
        ctx.Host.UpdateData(formId, new Dictionary<string, object?>
        {
            ["userId"] = "",
            ["roleId"] = "",
            ["nodePath"] = nodePath
        });

        // Load users for autocomplete
        var userOptionsId = $"user_options_{Guid.NewGuid().AsString()}";
        var users = new List<Option>();
        await foreach (var userAccess in securityService.GetAllUserAccessAsync())
        {
            var label = string.IsNullOrEmpty(userAccess.DisplayName)
                ? userAccess.UserId
                : $"{userAccess.DisplayName} ({userAccess.UserId})";
            users.Add(new Option<string>(userAccess.UserId, label));
        }
        ctx.Host.UpdateData(userOptionsId, users.ToArray());

        // Load roles with permission descriptions
        var rolesOptionsId = $"roles_options_{Guid.NewGuid().AsString()}";
        var roles = new List<Option>();
        await foreach (var role in securityService.GetRolesAsync())
        {
            var perms = role.Permissions.ToString();
            var label = $"{role.DisplayName ?? role.Id} ({perms})";
            roles.Add(new Option<string>(role.Id, label));
        }
        ctx.Host.UpdateData(rolesOptionsId, roles.ToArray());

        // Load node options for node path selector
        var nodeOptionsId = $"node_options_{Guid.NewGuid().AsString()}";
        var nodeOptions = new List<Option>();
        nodeOptions.Add(new Option<string>(nodePath, nodePath));
        // Add ancestor nodes
        var segments = nodePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = segments.Length - 1; i > 0; i--)
        {
            var ancestorPath = string.Join("/", segments.Take(i));
            nodeOptions.Add(new Option<string>(ancestorPath, ancestorPath));
        }
        nodeOptions.Add(new Option<string>("", "(Global)"));
        // Add child nodes via IMeshQuery if available
        var meshQuery = ctx.Hub.ServiceProvider.GetService<IMeshQuery>();
        if (meshQuery != null)
        {
            await foreach (var suggestion in meshQuery.AutocompleteAsync(nodePath, "", limit: 20))
            {
                if (nodeOptions.All(o => ((Option<string>)o).Item != suggestion.Path))
                    nodeOptions.Add(new Option<string>(suggestion.Path, $"{suggestion.Name} ({suggestion.Path})"));
            }
        }
        ctx.Host.UpdateData(nodeOptionsId, nodeOptions.ToArray());

        // Build dialog content
        var formContent = Controls.Stack.WithStyle("gap: 16px; padding: 16px;")
            .WithView(new ComboboxControl(
                new JsonPointerReference("userId"),
                new JsonPointerReference(LayoutAreaReference.GetDataPointer(userOptionsId)))
            {
                Label = "User",
                Required = true,
                Autocomplete = ComboboxAutocomplete.Both,
                Placeholder = "Search users...",
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
            .WithView(new ComboboxControl(
                new JsonPointerReference("nodePath"),
                new JsonPointerReference(LayoutAreaReference.GetDataPointer(nodeOptionsId)))
            {
                Label = "Node Path",
                Autocomplete = ComboboxAutocomplete.Both,
                Placeholder = "Select or type node path...",
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
                                Controls.Markdown("Please fill in both **User** and **Role**."),
                                "Validation Error"
                            ).WithSize("S").WithClosable(true);
                            saveCtx.Host.UpdateArea(DialogControl.DialogArea, errorDialog);
                            return;
                        }

                        var targetPath = formValues.GetValueOrDefault("nodePath")?.ToString()?.Trim();
                        if (string.IsNullOrEmpty(targetPath))
                            targetPath = nodePath;

                        var svc = saveCtx.Hub.ServiceProvider.GetRequiredService<ISecurityService>();
                        await svc.AddUserRoleAsync(userId, roleId, targetPath);

                        // Close dialog and refresh data reactively
                        saveCtx.Host.UpdateArea(DialogControl.DialogArea, null!);
                        await RefreshAssignmentsAsync(svc, targetPath, saveCtx.Host);
                    })));

        var dialog = Controls.Dialog(formContent, "Add Role Assignment")
            .WithSize("M")
            .WithClosable(true);

        ctx.Host.UpdateArea(DialogControl.DialogArea, dialog);
    }
}
