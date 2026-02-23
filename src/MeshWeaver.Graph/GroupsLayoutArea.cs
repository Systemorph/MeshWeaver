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

namespace MeshWeaver.Graph;

/// <summary>
/// Layout area for managing group memberships on a mesh node.
/// Inherited memberships are loaded via IMeshQuery from ancestor nodes (merged per member).
/// Local memberships are rendered via GroupMembershipControlBuilder (reactive).
/// </summary>
public static class GroupsLayoutArea
{
    /// <summary>
    /// Entry point for the Groups layout area.
    /// </summary>
    public static IObservable<UiControl?> Groups(LayoutAreaHost host, RenderingContext _)
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

        var meshQuery = host.Hub.ServiceProvider.GetService<IMeshQuery>();
        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? [])
            ?? Observable.Return<MeshNode[]>([]);

        return nodeStream
            .SelectMany(async nodes =>
            {
                var node = nodes.FirstOrDefault(n => n.Namespace == hubPath || n.Path == hubPath);
                var isAdmin = await CheckAdminPermission(host.Hub, hubPath);

                // Load inherited memberships from ancestor nodes via IMeshQuery (one-shot)
                var inherited = new List<(GroupMembership Membership, string SourcePath)>();
                if (meshQuery != null)
                {
                    try
                    {
                        var ancestorMemberships = await meshQuery
                            .QueryAsync<MeshNode>($"path:{hubPath} nodeType:GroupMembership scope:ancestors")
                            .ToListAsync();

                        foreach (var membershipNode in ancestorMemberships)
                        {
                            var membership = DeserializeMembership(membershipNode);
                            if (membership != null)
                                inherited.Add((membership, membershipNode.Namespace ?? ""));
                        }
                    }
                    catch
                    {
                        // Query may fail if index not ready
                    }
                }

                return BuildGroupsPage(host, node, hubPath, isAdmin, inherited);
            });
    }

    internal static GroupMembership? DeserializeMembership(MeshNode node)
    {
        if (node.Content is GroupMembership gm)
            return gm;
        if (node.Content is System.Text.Json.JsonElement je)
            return System.Text.Json.JsonSerializer.Deserialize<GroupMembership>(je.GetRawText());
        return null;
    }

    private static async Task<bool> CheckAdminPermission(IMessageHub hub, string nodePath)
    {
        var permissions = await PermissionHelper.GetEffectivePermissionsAsync(hub, nodePath);
        return permissions.HasFlag(Permission.Delete);
    }

    private static UiControl? BuildGroupsPage(
        LayoutAreaHost host,
        MeshNode? node,
        string nodePath,
        bool isAdmin,
        IReadOnlyList<(GroupMembership Membership, string SourcePath)> inherited)
    {
        var stack = Controls.Stack.WithStyle("padding: 24px; gap: 24px;");

        // Header
        var headerText = node?.Name ?? nodePath.Split('/').LastOrDefault() ?? nodePath;
        stack = stack.WithView(Controls.H2($"Groups - {headerText}"));

        // Section 1: Inherited Memberships (merged per member, using builder)
        stack = stack.WithView(Controls.H3("Inherited Memberships").WithStyle("margin: 0;"));

        if (inherited.Count == 0)
        {
            stack = stack.WithView(Controls.Html("<p style=\"color: var(--neutral-foreground-hint);\">No inherited memberships.</p>"));
        }
        else
        {
            stack = stack.WithView(BuildInheritedSection(inherited));
        }

        // Section 2: Local Memberships (reactive via workspace stream)
        stack = stack.WithView(Controls.H3("Local Memberships").WithStyle("margin: 0;"));

        stack = stack.WithView((h, _) => BuildLocalMemberships(h, nodePath, isAdmin));

        // + button if admin
        if (isAdmin)
        {
            stack = stack.WithView(Controls.Button("+ Add Membership")
                .WithAppearance(Appearance.Accent)
                .WithStyle("align-self: flex-start; margin-top: 8px;")
                .WithClickAction(async ctx => await ShowAddMembershipDialog(ctx, nodePath)));
        }

        return stack;
    }

    /// <summary>
    /// Builds the inherited section by merging memberships per member.
    /// </summary>
    private static UiControl BuildInheritedSection(
        IReadOnlyList<(GroupMembership Membership, string SourcePath)> inherited)
    {
        var merged = inherited
            .GroupBy(x => x.Membership.Member)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var first = g.First();
                var mergedGroups = g
                    .SelectMany(x =>
                    {
                        var source = string.IsNullOrEmpty(x.SourcePath) ? "Global" : x.SourcePath.Split('/').LastOrDefault() ?? x.SourcePath;
                        return x.Membership.Groups.Select(e => new MembershipEntry
                        {
                            Group = string.IsNullOrEmpty(e.Group) ? $"(no group) [{source}]" : $"{e.Group} [{source}]"
                        });
                    })
                    .ToList();

                return first.Membership with { Groups = mergedGroups };
            })
            .ToList();

        var container = Controls.Stack.WithStyle("gap: 6px;");
        foreach (var membership in merged)
        {
            container = container.WithView(GroupMembershipControlBuilder.Build(
                membership, isEditable: false));
        }
        return container;
    }

    /// <summary>
    /// Builds the local memberships section using reactive workspace stream.
    /// </summary>
    private static IObservable<UiControl?> BuildLocalMemberships(
        LayoutAreaHost host, string nodePath, bool isAdmin)
    {
        var meshQuery = host.Hub.ServiceProvider.GetService<IMeshQuery>();
        if (meshQuery == null)
            return Observable.Return<UiControl?>(Controls.Html("<p style=\"color: var(--neutral-foreground-hint);\">No local memberships.</p>"));

        return meshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery($"path:{nodePath} nodeType:GroupMembership scope:children"))
            .Select(change =>
            {
                var nodes = change.Items;
                if (nodes == null || !nodes.Any())
                    return (UiControl?)Controls.Html("<p style=\"color: var(--neutral-foreground-hint);\">No local memberships.</p>");

                var container = Controls.Stack.WithStyle("gap: 6px;");
                foreach (var membershipNode in nodes.OrderBy(n => n.Name))
                {
                    var membership = DeserializeMembership(membershipNode);
                    if (membership == null) continue;

                    container = container.WithView(GroupMembershipControlBuilder.Build(
                        membership,
                        node: membershipNode,
                        isEditable: isAdmin,
                        navigateTo: $"/{membershipNode.Path}"));
                }
                return (UiControl?)container;
            });
    }

    /// <summary>
    /// Shows a dialog to add a new group membership.
    /// Uses MeshNodePickerControl driven by the [MeshNode] attribute on GroupMembership.Member.
    /// </summary>
    private static Task ShowAddMembershipDialog(UiActionContext ctx, string nodePath)
    {
        var formId = $"add_membership_{Guid.NewGuid().AsString()}";
        ctx.Host.UpdateData(formId, new Dictionary<string, object?>
        {
            ["memberId"] = ""
        });

        // Resolve queries from the [MeshNode] attribute on GroupMembership.Member
        var meshNodeAttr = typeof(GroupMembership).GetProperty(nameof(GroupMembership.Member))!
            .GetCustomAttributes(typeof(MeshNodeAttribute), false).OfType<MeshNodeAttribute>().First();
        var queries = MeshNodeAttribute.ResolveQueries(meshNodeAttr.Queries, nodePath, nodePath);

        var formContent = Controls.Stack.WithStyle("gap: 16px; padding: 16px;")
            .WithView(new MeshNodePickerControl(new JsonPointerReference("memberId"))
            {
                Queries = queries,
                Label = "Member (User or Group)",
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
            .WithView(Controls.Button("Create")
                .WithAppearance(Appearance.Accent)
                .WithClickAction(async saveCtx =>
                {
                    var formValues = await saveCtx.Host.Stream
                        .GetDataStream<Dictionary<string, object?>>(formId).FirstAsync();

                    var selectedMember = formValues.GetValueOrDefault("memberId")?.ToString()?.Trim();
                    if (string.IsNullOrEmpty(selectedMember))
                    {
                        var errorDialog = Controls.Dialog(
                            Controls.Markdown("Please select a **Member**."),
                            "Validation Error"
                        ).WithSize("S").WithClosable(true);
                        saveCtx.Host.UpdateArea(DialogControl.DialogArea, errorDialog);
                        return;
                    }

                    var memberName = selectedMember.Split('/').Last();
                    var nodeId = $"{memberName}_Membership";
                    var path = $"{nodePath}/{nodeId}";

                    // Check if a membership already exists for this member
                    MeshNode? existing = null;
                    var query = saveCtx.Hub.ServiceProvider.GetService<IMeshQuery>();
                    if (query != null)
                    {
                        try
                        {
                            existing = await query.QueryAsync<MeshNode>($"path:{path} scope:exact")
                                .FirstOrDefaultAsync();
                        }
                        catch { }
                    }

                    // Close dialog
                    saveCtx.Host.UpdateArea(DialogControl.DialogArea, null!);

                    if (existing != null)
                    {
                        // Navigate to existing membership
                        saveCtx.NavigateTo($"/{existing.Path}");
                    }
                    else
                    {
                        // Create transient node and navigate to Create view
                        var catalog = saveCtx.Hub.ServiceProvider.GetService<IMeshCatalog>();
                        if (catalog != null)
                        {
                            // Look up the member node to copy their icon
                            string? memberIcon = null;
                            if (query != null)
                            {
                                try
                                {
                                    var memberNode = await query.QueryAsync<MeshNode>($"path:{selectedMember} scope:exact")
                                        .FirstOrDefaultAsync();
                                    memberIcon = memberNode?.Icon;
                                }
                                catch { }
                            }

                            var newNode = new MeshNode(nodeId, nodePath)
                            {
                                NodeType = Configuration.GroupMembershipNodeType.NodeType,
                                Name = $"{memberName} Membership",
                                Icon = memberIcon,
                                Content = new GroupMembership
                                {
                                    Member = selectedMember,
                                    DisplayName = memberName,
                                    Groups = [new MembershipEntry { Group = "" }]
                                }
                            };
                            await catalog.CreateTransientAsync(newNode);
                            saveCtx.NavigateTo($"/{path}/{MeshNodeLayoutAreas.CreateNodeArea}");
                        }
                    }
                }));

        var dialog = Controls.Dialog(formContent, "Add Membership")
            .WithSize("M")
            .WithActions(actions);

        ctx.Host.UpdateArea(DialogControl.DialogArea, dialog);
        return Task.CompletedTask;
    }
}
