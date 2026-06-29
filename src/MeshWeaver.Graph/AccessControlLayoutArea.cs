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
    /// Entry point for the Access Control layout area.
    /// </summary>
    public static IObservable<UiControl?> AccessControl(LayoutAreaHost host, RenderingContext _)
    {
        var nodePath = host.Hub.Address.ToString();
        var rlsEnabled = host.Hub.Configuration.Get<EffectivePermissionsDelegate>() != null;

        if (!rlsEnabled)
        {
            return Observable.Return<UiControl?>(Controls.Stack
                .WithStyle("padding: 24px;")
                .WithView(Controls.Markdown(
                    "> **Row-Level Security is not enabled.** Add `.AddRowLevelSecurity()` " +
                    "to your mesh configuration to manage access here.")));
        }

        // Admin check — reactive Delete-permission probe (the manage-access gate). Sourcing
        // from AccessService.Context.Roles is unreliable on the per-node hub (CircuitContext
        // lives on a different AccessService instance), so probe effective permissions.
        var isAdminStream = host.Hub.CheckPermission(nodePath, Permission.Delete);

        // The node stream supplies only the section header name. If the reducer isn't registered
        // (minimal hub configs) fall through to a name derived from the path.
        IObservable<ChangeItem<MeshNode>>? nodeStream = null;
        try
        {
            nodeStream = host.Workspace.GetStream(new MeshNodeReference());
        }
        catch (Exception)
        {
            // MeshNodeReference reducer not available on this hub — render without node.
        }

        if (nodeStream is null)
            return isAdminStream.Select(isAdmin => (UiControl?)BuildPage(host, node: null, nodePath, isAdmin));

        return nodeStream.CombineLatest(isAdminStream, (change, isAdmin)
                => (UiControl?)BuildPage(host, change?.Value, nodePath, isAdmin))
            .Catch<UiControl?, Exception>(_ => isAdminStream.Select(isAdmin =>
                (UiControl?)BuildPage(host, node: null, nodePath, isAdmin)));
    }

    internal static AccessAssignment? DeserializeAssignment(MeshNode node)
    {
        if (node.Content is AccessAssignment aa)
            return aa;
        if (node.Content is System.Text.Json.JsonElement je)
            return System.Text.Json.JsonSerializer.Deserialize<AccessAssignment>(je.GetRawText());
        return null;
    }

    private static UiControl BuildPage(LayoutAreaHost host, MeshNode? node, string nodePath, bool isAdmin)
    {
        var stack = Controls.Stack.WithStyle("padding: 24px; gap: 20px; width: 100%;");

        var currentName = node?.Name ?? nodePath.Split('/').LastOrDefault() ?? nodePath;
        if (string.IsNullOrEmpty(currentName)) currentName = "Root";

        stack = stack.WithView(Controls.H2($"Access Control — {currentName}"));

        // Parent section — the direct assignments at the parent scope. These rows render read-only
        // unless the caller also manages the parent (their per-row effective permissions decide).
        var parentPath = GetParentPath(nodePath);
        if (parentPath != null)
        {
            var parentName = string.IsNullOrEmpty(parentPath)
                ? "Root"
                : parentPath.Split('/').LastOrDefault() ?? parentPath;
            stack = stack
                .WithView(Controls.H3($"Parent — {parentName}").WithStyle("margin: 8px 0 0 0;"))
                .WithView(AccessList(parentPath));
        }

        // Current scope — the editable section.
        stack = stack
            .WithView(Controls.H3(currentName).WithStyle("margin: 8px 0 0 0;"))
            .WithView(AccessList(nodePath));

        if (isAdmin)
            stack = stack
                .WithView(BuildAddRow(host, nodePath))
                .WithView(BuildAdvancedSection(host, nodePath));

        return stack;
    }

    /// <summary>The parent scope of a node path, or null when the node is the root.</summary>
    private static string? GetParentPath(string nodePath)
    {
        if (string.IsNullOrEmpty(nodePath)) return null;       // root: no parent
        var idx = nodePath.LastIndexOf('/');
        return idx < 0 ? string.Empty : nodePath[..idx];        // top-level node → parent is root ("")
    }

    /// <summary>The <c>_Access</c> satellite namespace for a scope path.</summary>
    private static string AccessNamespace(string path)
        => string.IsNullOrEmpty(path) ? "_Access" : $"{path}/_Access";

    /// <summary>
    /// A live, one-per-row list of the AccessAssignment nodes at <paramref name="path"/>, rendered via
    /// the AccessAssignment Thumbnail area (person/group + node-bound role editors).
    /// </summary>
    private static UiControl AccessList(string path)
        => Controls.MeshSearch
            .WithHiddenQuery($"namespace:{AccessNamespace(path)} nodeType:AccessAssignment")
            .WithShowSearchBox(false)
            .WithReactiveMode(true)
            .WithItemArea(MeshNodeLayoutAreas.ThumbnailArea)
            .WithMaxColumns(1)
            .WithDisableNavigation()
            .WithStyle("width: 100%;");

    /// <summary>
    /// The inline add row: a user picker (root-namespace users) + a role select (default Editor) + an
    /// Add button that creates the AccessAssignment node. The created assignment appears in the current
    /// section automatically (reactive MeshSearch).
    /// </summary>
    private static UiControl BuildAddRow(LayoutAreaHost host, string nodePath)
    {
        var formId = $"add_access_{Guid.NewGuid().AsString()}";
        host.UpdateData(formId, new Dictionary<string, object?>
        {
            ["accessObject"] = "",
            ["role"] = Role.Editor.Id
        });
        var dataContext = LayoutAreaReference.GetDataPointer(formId);

        var userPicker = new MeshNodePickerControl(new JsonPointerReference("accessObject"))
        {
            Queries = ["namespace:\"\" nodeType:User"],
            Label = "Add user",
            DataContext = dataContext
        }.WithStyle("flex: 1; min-width: 220px;");

        var roleSelect = (new SelectControl(new JsonPointerReference("role"), Array.Empty<object>())
                .WithOptions(new[] { Role.Admin.Id, Role.Editor.Id, Role.Viewer.Id, Role.Commenter.Id }))
            with { Label = "Role", DataContext = dataContext };

        var addButton = Controls.Button("+ Add")
            .WithAppearance(Appearance.Accent)
            .WithClickAction((Action<UiActionContext>)(ctx =>
            {
                ctx.Host.Stream.GetDataStream<Dictionary<string, object?>>(formId)
                    .Take(1)
                    .Subscribe(form =>
                    {
                        var subject = form.GetValueOrDefault("accessObject")?.ToString()?.Trim();
                        var role = form.GetValueOrDefault("role")?.ToString()?.Trim();
                        if (string.IsNullOrEmpty(subject))
                        {
                            ShowValidationError(ctx, "Please select a **user**.");
                            return;
                        }
                        if (string.IsNullOrEmpty(role)) role = Role.Editor.Id;
                        CreateAssignment(ctx, nodePath, subject, role);
                    });
            }));

        return Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("align-items: flex-end; gap: 12px; margin-top: 8px;")
            .WithView(userPicker)
            .WithView(roleSelect)
            .WithView(addButton);
    }

    /// <summary>
    /// Creates an AccessAssignment node for <paramref name="subject"/> with a single
    /// <paramref name="role"/> (the bare role id — the value the permission evaluator resolves).
    /// </summary>
    private static void CreateAssignment(UiActionContext ctx, string nodePath, string subject, string role)
    {
        var subjectName = subject.Split('/').Last();
        var newNode = new MeshNode($"{subjectName}_Access", AccessNamespace(nodePath))
        {
            NodeType = Configuration.AccessAssignmentNodeType.NodeType,
            Name = $"{subjectName} Access",
            MainNode = nodePath,
            Content = new AccessAssignment
            {
                AccessObject = subject,
                DisplayName = subjectName,
                Roles = [new RoleAssignment { Role = role, Denied = false }]
            }
        };

        var meshService = ctx.Host.Hub.ServiceProvider.GetRequiredService<IMeshService>();
        meshService.CreateNode(newNode).Subscribe(
            _ => { },
            ex =>
            {
                var dialog = Controls.Dialog(
                    Controls.Markdown($"Failed to add: {ex.Message}"), "Error")
                    .WithSize("S").WithClosable(true);
                ctx.Host.UpdateArea(DialogControl.DialogArea, dialog);
            });
    }

    /// <summary>
    /// The collapsed-by-design Advanced section: the partition policy that caps the permissions
    /// available to everyone at this scope and below.
    /// </summary>
    private static UiControl BuildAdvancedSection(LayoutAreaHost host, string nodePath)
    {
        var workspace = host.Hub.GetWorkspace();
        var meshService = host.Hub.ServiceProvider.GetRequiredService<IMeshService>();
        var policyPath = string.IsNullOrEmpty(nodePath) ? "_Policy" : $"{nodePath}/_Policy";

        return Controls.Stack
            .WithStyle("gap: 8px; margin-top: 24px; padding-top: 16px; " +
                       "border-top: 1px solid var(--neutral-stroke-divider-rest);")
            .WithView(Controls.H3("Advanced").WithStyle("margin: 0;"))
            .WithView(Controls.Markdown(
                "Partition policy caps the permissions available to **everyone** at this scope and below."))
            .WithView(Controls.Button("Set partition policy…")
                .WithAppearance(Appearance.Neutral)
                .WithStyle("align-self: flex-start;")
                .WithClickAction((Action<UiActionContext>)(ctx =>
                    ShowSetPolicyDialog(ctx, nodePath, policyPath, policyExists: false, workspace, meshService, existing: null))));
    }

    /// <summary>
    /// Deletes an AccessAssignment node.
    /// </summary>
    internal static void DeleteAssignment(UiActionContext ctx, LayoutAreaHost host, string nodePath)
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
    internal static void ShowAddAssignmentDialog(UiActionContext ctx, string nodePath)
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
            .WithView(Controls.Button("Cancel")
                .WithAppearance(Appearance.Neutral)
                .WithClickAction((Action<UiActionContext>)(cancelCtx =>
                    cancelCtx.Host.UpdateArea(DialogControl.DialogArea, null!))))
            .WithView(Controls.Button("Create")
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

    private static void ShowValidationError(UiActionContext ctx, string message)
    {
        var errorDialog = Controls.Dialog(
            Controls.Markdown(message),
            "Validation Error"
        ).WithSize("S").WithClosable(true);
        ctx.Host.UpdateArea(DialogControl.DialogArea, errorDialog);
    }

    private static void ShowSetPolicyDialog(
        UiActionContext ctx, string nodePath, string policyPath, bool policyExists,
        IWorkspace workspace, IMeshService meshService, PartitionAccessPolicy? existing)
    {
        var formId = $"set_policy_{Guid.NewGuid().AsString()}";
        ctx.Host.UpdateData(formId, new Dictionary<string, object?>
        {
            ["allowRead"] = existing?.Read != false,
            ["allowCreate"] = existing?.Create != false,
            ["allowUpdate"] = existing?.Update != false,
            ["allowDelete"] = existing?.Delete != false,
            ["allowComment"] = existing?.Comment != false,
            ["breaksInheritance"] = existing?.BreaksInheritance ?? false
        });

        var formContent = Controls.Stack.WithStyle("gap: 16px; padding: 16px;")
            .WithView(Controls.Markdown("Set a partition access policy to restrict permissions for **all users** at this namespace. Turn off permissions to deny them."))
            .WithView(new SwitchControl(new JsonPointerReference("allowRead"))
            {
                Label = "Read",
                DataContext = LayoutAreaReference.GetDataPointer(formId)
            })
            .WithView(new SwitchControl(new JsonPointerReference("allowCreate"))
            {
                Label = "Create",
                DataContext = LayoutAreaReference.GetDataPointer(formId)
            })
            .WithView(new SwitchControl(new JsonPointerReference("allowUpdate"))
            {
                Label = "Update",
                DataContext = LayoutAreaReference.GetDataPointer(formId)
            })
            .WithView(new SwitchControl(new JsonPointerReference("allowDelete"))
            {
                Label = "Delete",
                DataContext = LayoutAreaReference.GetDataPointer(formId)
            })
            .WithView(new SwitchControl(new JsonPointerReference("allowComment"))
            {
                Label = "Comment",
                DataContext = LayoutAreaReference.GetDataPointer(formId)
            })
            .WithView(new SwitchControl(new JsonPointerReference("breaksInheritance"))
            {
                Label = "Break inheritance (discard roles from parent scopes)",
                DataContext = LayoutAreaReference.GetDataPointer(formId)
            });

        var actions = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 8px;")
            .WithView(Controls.Button("Cancel")
                .WithAppearance(Appearance.Neutral)
                .WithClickAction((Action<UiActionContext>)(cancelCtx =>
                    cancelCtx.Host.UpdateArea(DialogControl.DialogArea, null!))))
            .WithView(Controls.Button("Save")
                .WithAppearance(Appearance.Accent)
                .WithClickAction((Action<UiActionContext>)(saveCtx =>
                {
                    // Read form values via Subscribe (sync emission of the BehaviorSubject).
                    // Pure reactive — no await, no FirstAsync, no Task bridging.
                    saveCtx.Host.Stream.GetDataStream<Dictionary<string, object?>>(formId)
                        .Take(1)
                        .Subscribe(formValues =>
                        {
                            var policy = new PartitionAccessPolicy
                            {
                                Read = formValues.GetValueOrDefault("allowRead") is true ? null : false,
                                Create = formValues.GetValueOrDefault("allowCreate") is true ? null : false,
                                Update = formValues.GetValueOrDefault("allowUpdate") is true ? null : false,
                                Delete = formValues.GetValueOrDefault("allowDelete") is true ? null : false,
                                Comment = formValues.GetValueOrDefault("allowComment") is true ? null : false,
                                BreaksInheritance = formValues.GetValueOrDefault("breaksInheritance") is true
                            };

                            // Caller (the UI) knows whether this is a create or an
                            // update — branch directly instead of asking SecurityService.
                            if (policyExists)
                            {
                                // In-place update through the canonical per-path mesh-node
                                // handle. (Cold observable: must Subscribe.)
                                workspace.GetMeshNodeStream(policyPath)
                                    .Update(current => current with { Content = policy })
                                    .Subscribe(_ => { }, _ => { /* surface via standard data-layer error path */ });
                            }
                            else
                            {
                                // First-time create — only IMeshService.CreateNode
                                // brings the per-node hub into existence.
                                var policyNode = new MeshNode("_Policy", nodePath ?? "")
                                {
                                    NodeType = "PartitionAccessPolicy",
                                    Name = "Access Policy",
                                    Content = policy,
                                };
                                meshService.CreateNode(policyNode).Subscribe(
                                    _ => { },
                                    _ => { /* surface via standard data-layer error path */ });
                            }
                            saveCtx.Host.UpdateArea(DialogControl.DialogArea, null!);
                        });
                })));

        var dialog = Controls.Dialog(formContent, "Partition Access Policy")
            .WithSize("M")
            .WithActions(actions);

        ctx.Host.UpdateArea(DialogControl.DialogArea, dialog);
    }
}
