using System.Reactive.Linq;
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
/// Inherited assignments are loaded directly from ISecurityService (read-only display).
/// Local assignments flow through the workspace data pipeline via AccessAssignmentTypeSource.
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

        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? [])
            ?? Observable.Return<MeshNode[]>([]);

        // Use SelectMany to await async operations (inherited load + permission check),
        // then return the synchronous UI.
        return nodeStream.SelectMany(async nodes =>
        {
            var node = nodes.FirstOrDefault(n => n.Namespace == hubPath || n.Path == hubPath);
            var isAdmin = await CheckAdminPermission(host.Hub, hubPath);

            // Load inherited assignments from ISecurityService (read-only)
            var inherited = new List<AccessAssignment>();
            await foreach (var a in securityService.GetAccessAssignmentsAsync(hubPath))
            {
                if (!a.IsLocal)
                    inherited.Add(a);
            }

            return BuildAccessControlPage(host, securityService, node, hubPath, isAdmin,
                inherited.OrderBy(a => a.UserId).ThenBy(a => a.RoleId).ToList());
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
        bool isAdmin,
        IReadOnlyList<AccessAssignment> inherited)
    {
        var stack = Controls.Stack.WithStyle("padding: 24px; gap: 24px;");

        // Header
        var headerText = node?.Name ?? nodePath.Split('/').LastOrDefault() ?? nodePath;
        stack = stack.WithView(Controls.H2($"Access Control - {headerText}"));

        // Section 1: Inherited Permissions (read-only, from ISecurityService)
        stack = stack.WithView(Controls.H3("Inherited Permissions").WithStyle("margin: 0;"));
        stack = stack.WithView(
            inherited.BindMany(a =>
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

        // Section 2: Local Assignments (workspace-bound, editable)
        var localStream = host.Workspace.GetStream<AccessAssignment>()
            ?.Select(items => items?.AsEnumerable() ?? Enumerable.Empty<AccessAssignment>())
            ?? Observable.Return(Enumerable.Empty<AccessAssignment>());

        stack = stack.WithView(Controls.H3("Local Assignments").WithStyle("margin: 0;"));
        stack = stack.WithView(
            localStream.BindMany("acl_local", a =>
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

                        // Create the new assignment and submit via workspace DataChangeRequest
                        var newAssignment = new AccessAssignment
                        {
                            UserId = userId,
                            RoleId = roleId,
                            SourcePath = targetPath,
                            IsLocal = true,
                            IsActive = true,
                            DisplayLabel = userId,
                            SourceDisplay = string.IsNullOrEmpty(targetPath) ? "Global" : targetPath
                        };

                        saveCtx.Host.Workspace.RequestChange(
                            new DataChangeRequest().WithCreations([newAssignment]),
                            null, null);

                        // Close dialog
                        saveCtx.Host.UpdateArea(DialogControl.DialogArea, null!);
                    })));

        var dialog = Controls.Dialog(formContent, "Add Role Assignment")
            .WithSize("M")
            .WithClosable(true);

        ctx.Host.UpdateArea(DialogControl.DialogArea, dialog);
    }
}
