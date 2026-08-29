using System.Net;
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
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// Layout area for managing access control on a mesh node. The page shows two sections — direct
/// assignments at the PARENT scope, and the editable assignments at the CURRENT scope — each driven
/// by a live mesh query (<c>namespace:{path}/_Access nodeType:AccessAssignment</c>) rendered as clean
/// rows via the AccessAssignment Thumbnail area. An inline add row (user picker + role select) creates
/// new assignments; the partition policy lives in a collapsed Advanced section.
/// </summary>
public static class AccessControlLayoutArea
{

    /// <summary>
    /// Deserializes an <see cref="AccessAssignment"/> from a node's content, tolerating the
    /// untyped-JSON shape a cross-hub read produces. Public because the view that consumes it
    /// ships in the MeshWeaver.Graph.Views MODULE while this content helper stays platform-side —
    /// the deserialization is contract, the rendering is not.
    /// </summary>
    /// <param name="node">The assignment node.</param>
    /// <returns>The assignment, or null when the content is absent or unreadable.</returns>
    public static AccessAssignment? DeserializeAssignment(MeshNode node)
    {
        if (node.Content is AccessAssignment aa)
            return aa;
        if (node.Content is System.Text.Json.JsonElement je)
            return System.Text.Json.JsonSerializer.Deserialize<AccessAssignment>(je.GetRawText());
        return null;
    }

    /// <summary>
    /// Deletes an AccessAssignment node.
    /// </summary>
    public static void DeleteAssignment(UiActionContext ctx, LayoutAreaHost host, string nodePath)
    {
        var nodeFactory = host.Hub.ServiceProvider.GetRequiredService<IMeshService>();
        nodeFactory.DeleteNode(nodePath).Subscribe(
            _ => { },
            ex =>
            {
                var dialog = Controls.Dialog(
                    Controls.Markdown($"Failed to delete: {ex.Message}"),
                    "Error"
                ).WithSize("S").WithClosable(true);
                ctx.Host.UpdateArea(DialogControl.DialogArea, dialog);
            });
    }

    /// <summary>
    /// Shows a dialog to add a new access assignment.
    /// Captures both Subject (user/group) AND Role in one dialog.
    /// </summary>
    public static void ShowAddAssignmentDialog(UiActionContext ctx, string nodePath)
    {
        var formId = $"add_assignment_{Guid.NewGuid().AsString()}";
        ctx.Host.UpdateData(formId, new Dictionary<string, object?>
        {
            ["accessObject"] = "",
            ["role"] = ""
        });

        // Canonical subject queries (users at root via the auth mirror + groups in the
        // partition subtree) — never resolve the attribute template with a PATH here: the
        // previous ResolveQueries(queries, nodePath, nodePath) substituted the node's path
        // into {node.namespace}, scoping the group search to the node instead of its partition.
        var subjectQueries = AccessSubjectQueries.ForScope(nodePath);

        // Resolve queries for Role from [MeshNodeCollection] attribute
        var rolesAttr = typeof(AccessAssignment).GetProperty(nameof(AccessAssignment.Roles))!
            .GetCustomAttributes(typeof(MeshNodeCollectionAttribute), false)
            .OfType<MeshNodeCollectionAttribute>().First();
        var roleQueries = MeshNodeCollectionAttribute.ResolveQueries(rolesAttr.Queries, nodePath, nodePath);

        var formContent = Controls.Stack.WithStyle("gap: 16px; padding: 16px;")
            .WithView(new MeshNodePickerControl(new JsonPointerReference("accessObject"))
            {
                Queries = subjectQueries,
                FilterInMemory = true,
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
            .WithView(Controls.Button(ctx.Host.Localize("common.cancel"))
                .WithAppearance(Appearance.Neutral)
                .WithClickAction((Action<UiActionContext>)(cancelCtx =>
                    cancelCtx.Host.UpdateArea(DialogControl.DialogArea, null!))))
            .WithView(Controls.Button(ctx.Host.Localize("menu.create"))
                .WithAppearance(Appearance.Accent)
                .WithClickAction((Action<UiActionContext>)(saveCtx =>
                {
                    // Subscribe to the form data stream (synchronous emission via Take(1) —
                    // one-shot read for a click action, per DataBinding doc rule).
                    saveCtx.Host.Stream.GetDataStream<Dictionary<string, object?>>(formId)
                        .Take(1)
                        .Subscribe(formValues =>
                        {
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
                            var accessNs = $"{nodePath}/_Access";

                            // Close dialog immediately. No backend existence check, no icon
                            // lookup — both belong on the GUI (the path-bound thumbnail
                            // subscribes to the subject's node stream). A duplicate path is
                            // harmless (create handler rejects with NodeAlreadyExists).
                            saveCtx.Host.UpdateArea(DialogControl.DialogArea, null!);

                            var newNode = new MeshNode(nodeId, accessNs)
                            {
                                NodeType = Configuration.AccessAssignmentNodeType.NodeType,
                                Name = $"{subjectName} Access",
                                MainNode = nodePath,
                                Content = new AccessAssignment
                                {
                                    AccessObject = selectedSubject,
                                    DisplayName = subjectName,
                                    Roles = [new RoleAssignment { Role = selectedRole, Denied = false }]
                                }
                            };

                            // CREATE flow (not update) — DataChangeRequest is the framework
                            // primitive for create-or-update; UpdateMeshNode requires the
                            // node to already exist on the owning hub. The owning hub's
                            // data layer (registered by AddData) processes the create
                            // natively. See Doc/Architecture/AsynchronousCalls.md.
                            saveCtx.Hub.Post(
                                new DataChangeRequest { ChangedBy = saveCtx.Host.Stream.ClientId }.WithUpdates(newNode),
                                o => o.WithTarget(saveCtx.Hub.Address));
                        });
                })));

        var dialog = Controls.Dialog(formContent, "Add Assignment")
            .WithSize("M")
            .WithActions(actions);

        ctx.Host.UpdateArea(DialogControl.DialogArea, dialog);
    }

    /// <summary>
    /// Shows a validation error dialog. Public because BOTH halves call it: the add-assignment
    /// dialog kept here, and the moved views in the MeshWeaver.Graph.Views module.
    /// </summary>
    public static void ShowValidationError(UiActionContext ctx, string message)
    {
        var errorDialog = Controls.Dialog(
            Controls.Markdown(message),
            "Validation Error"
        ).WithSize("S").WithClosable(true);
        ctx.Host.UpdateArea(DialogControl.DialogArea, errorDialog);
    }
}
