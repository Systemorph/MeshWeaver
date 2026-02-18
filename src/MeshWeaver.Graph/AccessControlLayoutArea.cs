using System.Reactive.Linq;
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
/// Shows inherited and local role assignments with toggle switches,
/// and a dialog for adding new assignments.
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

        return nodeStream.SelectMany(async nodes =>
        {
            var node = nodes.FirstOrDefault(n => n.Namespace == hubPath || n.Path == hubPath);
            return await BuildAccessControlPageAsync(host, securityService, node, hubPath);
        });
    }

    private static async Task<UiControl?> BuildAccessControlPageAsync(
        LayoutAreaHost host,
        ISecurityService securityService,
        MeshNode? node,
        string nodePath)
    {
        var stack = Controls.Stack.WithStyle("padding: 24px; gap: 24px;");

        // Header
        var headerText = node?.Name ?? nodePath.Split('/').LastOrDefault() ?? nodePath;
        stack = stack.WithView(Controls.H2($"Access Control - {headerText}"));

        // Load all assignments
        var assignments = new List<AccessAssignment>();
        await foreach (var assignment in securityService.GetAccessAssignmentsAsync(nodePath))
        {
            assignments.Add(assignment);
        }

        if (assignments.Count == 0)
        {
            stack = stack.WithView(Controls.Html(
                "<p style=\"color: var(--neutral-foreground-hint);\">No role assignments found for this namespace. " +
                "Use the button below to add role assignments.</p>"));
        }
        else
        {
            // Split into inherited and local
            var inherited = assignments.Where(a => !a.IsLocal).ToList();
            var local = assignments.Where(a => a.IsLocal).ToList();

            // Inherited Permissions section
            stack = stack.WithView(BuildSection(host, "Inherited Permissions", inherited, nodePath, showSource: true));

            // Local Assignments section
            stack = stack.WithView(BuildSection(host, "Local Assignments", local, nodePath, showSource: false));
        }

        // Add Assignment button
        stack = stack.WithView(BuildAddButton(host, securityService, nodePath));

        return stack;
    }

    private static UiControl BuildSection(
        LayoutAreaHost host,
        string title,
        List<AccessAssignment> assignments,
        string nodePath,
        bool showSource)
    {
        var section = Controls.Stack.WithStyle("gap: 8px;");
        section = section.WithView(Controls.H3(title).WithStyle("margin: 0;"));

        if (assignments.Count == 0)
        {
            section = section.WithView(Controls.Html(
                $"<p style=\"color: var(--neutral-foreground-hint); font-style: italic;\">No {title.ToLowerInvariant()}.</p>"));
            return section;
        }

        // Table header
        var headerRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("padding: 8px 16px; background: var(--neutral-layer-2); border-radius: 4px 4px 0 0; font-weight: 600; font-size: 0.85rem;")
            .WithView(Controls.Html("<span style=\"flex: 1; min-width: 120px;\">User</span>"))
            .WithView(Controls.Html("<span style=\"flex: 1; min-width: 120px;\">Role</span>"));

        if (showSource)
            headerRow = headerRow.WithView(Controls.Html("<span style=\"flex: 1; min-width: 100px;\">Source</span>"));

        headerRow = headerRow.WithView(Controls.Html("<span style=\"width: 80px; text-align: center;\">Active</span>"));
        section = section.WithView(headerRow);

        // Data rows
        foreach (var assignment in assignments.OrderBy(a => a.UserId).ThenBy(a => a.RoleId))
        {
            section = section.WithView(BuildAssignmentRow(host, assignment, nodePath, showSource));
        }

        return section;
    }

    private static UiControl BuildAssignmentRow(
        LayoutAreaHost host,
        AccessAssignment assignment,
        string nodePath,
        bool showSource)
    {
        var isActive = !assignment.Denied;
        var toggleId = $"toggle_{assignment.UserId}_{assignment.RoleId}_{nodePath.Replace("/", "_")}".Replace(" ", "_");

        // Store toggle state
        host.UpdateData(toggleId, isActive);

        var row = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("padding: 8px 16px; border-bottom: 1px solid var(--neutral-stroke-rest); align-items: center;")
            .WithView(Controls.Html($"<span style=\"flex: 1; min-width: 120px;\">{System.Web.HttpUtility.HtmlEncode(assignment.DisplayName ?? assignment.UserId)}</span>"))
            .WithView(Controls.Html($"<span style=\"flex: 1; min-width: 120px;\">{System.Web.HttpUtility.HtmlEncode(assignment.RoleId)}</span>"));

        if (showSource)
        {
            var sourceDisplay = string.IsNullOrEmpty(assignment.SourcePath) ? "Global" : assignment.SourcePath;
            row = row.WithView(Controls.Html($"<span style=\"flex: 1; min-width: 100px;\">{System.Web.HttpUtility.HtmlEncode(sourceDisplay)}</span>"));
        }

        // Toggle button (styled as active/inactive)
        var toggleButton = isActive
            ? Controls.Button("Active")
                .WithAppearance(Appearance.Accent)
                .WithStyle("width: 80px; font-size: 0.8rem;")
            : Controls.Button("Denied")
                .WithAppearance(Appearance.Neutral)
                .WithStyle("width: 80px; font-size: 0.8rem; opacity: 0.6;");

        toggleButton = toggleButton.WithClickAction(async ctx =>
        {
            var securityService = ctx.Hub.ServiceProvider.GetRequiredService<ISecurityService>();
            var newDenied = !assignment.Denied;
            await securityService.ToggleRoleAssignmentAsync(
                nodePath, assignment.UserId, assignment.RoleId, newDenied);

            // Refresh the page by navigating to the same URL
            var hubAddress = ctx.Hub.Address;
            var href = new LayoutAreaReference(MeshNodeLayoutAreas.SettingsArea)
                { Id = SettingsLayoutArea.AccessControlTab }.ToHref(hubAddress);
            ctx.NavigateTo(href, forceLoad: true);
        });

        row = row.WithView(Controls.Stack
            .WithStyle("width: 80px; display: flex; justify-content: center;")
            .WithView(toggleButton));

        return row;
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

                        // Close dialog and refresh
                        saveCtx.Host.UpdateArea(DialogControl.DialogArea, null!);

                        var hubAddress = saveCtx.Hub.Address;
                        var href = new LayoutAreaReference(MeshNodeLayoutAreas.SettingsArea)
                            { Id = SettingsLayoutArea.AccessControlTab }.ToHref(hubAddress);
                        saveCtx.NavigateTo(href, forceLoad: true);
                    })));

        var dialog = Controls.Dialog(formContent, "Add Role Assignment")
            .WithSize("M")
            .WithClosable(true);

        ctx.Host.UpdateArea(DialogControl.DialogArea, dialog);
    }
}
