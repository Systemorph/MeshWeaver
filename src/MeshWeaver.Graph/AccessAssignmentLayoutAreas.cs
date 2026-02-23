using System.Reactive.Linq;
using System.Reflection;
using System.Text.Json;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataBinding;
using MeshWeaver.Layout.Domain;
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
    /// Custom thumbnail — uses MeshNodeThumbnailControl with role names as description.
    /// </summary>
    public static IObservable<UiControl?> Thumbnail(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();

        return host.StreamView<MeshNode>(
            (nodes, h) =>
            {
                var node = nodes.FirstOrDefault(n => n.Path == hubPath);
                var assignment = AccessControlLayoutArea.DeserializeAssignment(node!);
                if (assignment == null)
                    return MeshNodeThumbnailControl.FromNode(node, hubPath);

                var rolesText = string.Join(", ", assignment.Roles.Select(r =>
                    string.IsNullOrEmpty(r.Role) ? "(no role)" : GetRoleDisplayName(r.Role)));
                var imageUrl = MeshNodeThumbnailControl.GetImageUrlForNode(node);

                return new MeshNodeThumbnailControl(
                    hubPath,
                    assignment.DisplayName ?? assignment.AccessObject,
                    rolesText,
                    imageUrl);
            },
            hubPath);
    }

    /// <summary>
    /// Custom overview — user thumbnail + roles in LayoutGrid with switch/remove.
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
            return (UiControl?)BuildOverviewContent(host, node, hubPath, canEdit);
        });
    }

    private static UiControl BuildOverviewContent(LayoutAreaHost host, MeshNode? node, string hubPath, bool canEdit)
    {
        var stack = Controls.Stack.WithWidth("100%").WithStyle(MeshNodeLayoutAreas.GetContainerStyle(host));

        // Header
        stack = stack.WithView(MeshNodeLayoutAreas.BuildHeader(host, node, canEdit));

        if (node?.Content == null)
            return stack;

        // Data binding + auto-save
        var instance = node.Content;
        if (instance is JsonElement je)
            instance = JsonSerializer.Deserialize<object>(je.GetRawText(), host.Hub.JsonSerializerOptions)!;

        var dataId = EditLayoutArea.GetDataId(hubPath);
        host.UpdateData(dataId, instance);

        if (canEdit)
            OverviewLayoutArea.SetupAutoSave(host, dataId, instance, node);

        // User thumbnail card
        var assignment = instance as AccessAssignment
            ?? AccessControlLayoutArea.DeserializeAssignment(node);
        if (assignment != null)
        {
            var imageUrl = MeshNodeThumbnailControl.GetImageUrlForNode(node);
            stack = stack.WithView(new MeshNodeThumbnailControl(
                assignment.AccessObject,
                assignment.DisplayName ?? assignment.AccessObject,
                assignment.AccessObject,
                imageUrl));
        }

        // Roles section
        stack = stack.WithView(BuildRolesSection(host, dataId, canEdit, hubPath));

        return stack;
    }

    private static UiControl BuildRolesSection(LayoutAreaHost host, string dataId, bool canEdit, string nodePath)
    {
        var section = Controls.Stack.WithWidth("100%").WithStyle("margin-top: 32px;");
        section = section.WithView(Controls.H3("Roles").WithStyle("margin: 0 0 16px 0;"));

        // Reactive role cards
        section = section.WithView((h, _) =>
            h.Stream.GetDataStream<JsonElement>(dataId)
                .Select(data => BuildRoleCards(h, data, dataId, canEdit, nodePath)));

        if (canEdit)
        {
            section = section.WithView(Controls.Button("+ Add")
                .WithAppearance(Appearance.Accent)
                .WithStyle("align-self: flex-start; margin-top: 20px;")
                .WithClickAction(ctx =>
                {
                    ShowAddRoleDialog(ctx, nodePath);
                    return Task.CompletedTask;
                }));
        }

        return section;
    }

    private static UiControl BuildRoleCards(LayoutAreaHost host, JsonElement data, string dataId, bool canEdit, string nodePath)
    {
        if (!data.TryGetProperty("roles", out var rolesArr) || rolesArr.ValueKind != JsonValueKind.Array)
            return Controls.Html("<p style=\"color: var(--neutral-foreground-hint);\">No roles assigned.</p>");

        var grid = Controls.LayoutGrid
            .WithStyle(s => s.WithWidth("100%"))
            .WithSkin(s => s.WithSpacing(2));
        var index = 0;
        var hasItems = false;

        foreach (var roleItem in rolesArr.EnumerateArray())
        {
            hasItems = true;
            var capturedIndex = index;

            var rolePath = roleItem.TryGetProperty("role", out var roleVal) && roleVal.ValueKind == JsonValueKind.String
                ? roleVal.GetString() ?? ""
                : "";
            var roleDisplayName = string.IsNullOrEmpty(rolePath) ? "(no role)" : GetRoleDisplayName(rolePath);
            var shieldUrl = MeshNodeImageHelper.GetIconAsImageUrl("Shield");

            // Row: [Role thumbnail] [Denied switch] [× remove]
            var row = Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithStyle("align-items: center; gap: 8px;");

            row = row.WithView(
                new MeshNodeThumbnailControl(rolePath, roleDisplayName, "Role", shieldUrl)
                    .WithStyle("flex: 1;"));

            if (canEdit)
            {
                row = row.WithView(new SwitchControl(new JsonPointerReference($"roles/{capturedIndex}/denied"))
                {
                    DataContext = LayoutAreaReference.GetDataPointer(dataId),
                    CheckedMessage = "Denied",
                    UncheckedMessage = "Allowed"
                });

                var capturedNodePath = nodePath;
                row = row.WithView(Controls.Button("×")
                    .WithAppearance(Appearance.Stealth)
                    .WithStyle("min-width:28px;padding:0 4px;height:28px;font-size:16px;")
                    .WithClickAction(ctx =>
                    {
                        RemoveRole(ctx.Host, dataId, capturedIndex, capturedNodePath);
                        return Task.CompletedTask;
                    }));
            }

            grid = grid.WithView(row, s => s.WithXs(12));
            index++;
        }

        if (!hasItems)
            return Controls.Html("<p style=\"color: var(--neutral-foreground-hint);\">No roles assigned.</p>");

        return grid;
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
                    var dataId = EditLayoutArea.GetDataId(nodePath);
                    AddRole(addCtx.Host, dataId, selectedRole);
                }));

        ctx.Host.UpdateArea(DialogControl.DialogArea,
            Controls.Dialog(formContent, "Add").WithSize("M").WithActions(actions));
    }

    private static void AddRole(LayoutAreaHost host, string dataId, string selectedRole)
    {
        host.Stream.GetDataStream<JsonElement>(dataId).Take(1).Subscribe(data =>
        {
            var jsonObj = System.Text.Json.Nodes.JsonNode.Parse(data.GetRawText())!.AsObject();
            if (jsonObj["roles"] is not System.Text.Json.Nodes.JsonArray rolesArr)
            {
                rolesArr = [];
                jsonObj["roles"] = rolesArr;
            }

            rolesArr.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["$type"] = "RoleAssignment",
                ["role"] = selectedRole,
                ["denied"] = false
            });

            host.UpdateData(dataId, JsonSerializer.Deserialize<JsonElement>(jsonObj.ToJsonString()));
        });
    }

    private static void RemoveRole(LayoutAreaHost host, string dataId, int indexToRemove, string nodePath)
    {
        host.Stream.GetDataStream<JsonElement>(dataId).Take(1).Subscribe(async data =>
        {
            if (!data.TryGetProperty("roles", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return;

            var jsonObj = System.Text.Json.Nodes.JsonNode.Parse(data.GetRawText())!.AsObject();
            var jsonArr = jsonObj["roles"]!.AsArray();
            if (indexToRemove < 0 || indexToRemove >= jsonArr.Count)
                return;

            jsonArr.RemoveAt(indexToRemove);

            if (jsonArr.Count == 0)
            {
                // No roles left — delete the entire AccessAssignment node
                var meshCatalog = host.Hub.ServiceProvider.GetService<IMeshCatalog>();
                if (meshCatalog != null)
                {
                    try { await meshCatalog.DeleteNodeAsync(nodePath); }
                    catch { /* keep the node if deletion fails */ }
                }
            }
            else
            {
                host.UpdateData(dataId, JsonSerializer.Deserialize<JsonElement>(jsonObj.ToJsonString()));
            }
        });
    }
}
