using System.ComponentModel;
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
/// Layout areas for Group nodes.
/// - Overview: Shows group name/description and lists members (read-only).
/// - Edit: Same member list with delete buttons, plus an Add Member button.
/// </summary>
public static class GroupLayoutAreas
{
    public const string OverviewArea = "Overview";
    public const string EditArea = "Edit";
    public const string MembershipsArea = "Memberships";

    /// <summary>
    /// Adds the Group views to the hub's layout.
    /// </summary>
    public static MessageHubConfiguration AddGroupViews(this MessageHubConfiguration configuration)
        => configuration.AddLayout(layout => layout
            .WithDefaultArea(OverviewArea)
            .WithView(OverviewArea, Overview)
            .WithView(EditArea, Edit)
            .WithView(MembershipsArea, Memberships)
            .WithView(MeshNodeLayoutAreas.SettingsArea, SettingsLayoutArea.Settings)
            .WithView(MeshNodeLayoutAreas.CreateNodeArea, CreateLayoutArea.Create)
            .WithView(MeshNodeLayoutAreas.DeleteArea, DeleteLayoutArea.Delete));

    /// <summary>
    /// Renders the Memberships area for a Group node.
    /// Delegates to GroupsLayoutArea.Groups() to show inherited + local memberships.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> Memberships(LayoutAreaHost host, RenderingContext ctx)
        => GroupsLayoutArea.Groups(host, ctx);

    /// <summary>
    /// Renders the Overview area for a Group node.
    /// Shows group info and lists members from GroupMembership nodes where this group appears in their Groups list.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> Overview(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var meshQuery = host.Hub.ServiceProvider.GetService<IMeshQuery>();

        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? [])
            ?? Observable.Return<MeshNode[]>([]);

        return nodeStream.SelectMany(async nodes =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);
            var stack = Controls.Stack.WithStyle("padding: 24px; gap: 16px;");

            // Group header
            var groupName = node?.Name ?? hubPath;
            stack = stack.WithView(Controls.H2(groupName));

            var accessObject = node?.Content as AccessObject
                ?? (node?.Content is System.Text.Json.JsonElement je
                    ? System.Text.Json.JsonSerializer.Deserialize<AccessObject>(je.GetRawText())
                    : null);

            if (!string.IsNullOrEmpty(accessObject?.Description))
                stack = stack.WithView(Controls.Html($"<p>{System.Web.HttpUtility.HtmlEncode(accessObject.Description)}</p>"));

            // Members section
            stack = stack.WithView(Controls.H3("Members").WithStyle("margin: 0;"));

            if (meshQuery != null)
            {
                var members = await meshQuery
                    .QueryAsync<MeshNode>($"path:{hubPath} nodeType:GroupMembership scope:children")
                    .ToListAsync();

                if (members.Count == 0)
                {
                    stack = stack.WithView(Controls.Html("<p style=\"color: var(--neutral-foreground-hint);\">No members.</p>"));
                }
                else
                {
                    foreach (var member in members)
                    {
                        var membership = DeserializeMembership(member);
                        var memberDisplay = membership?.DisplayName ?? membership?.Member ?? member.Name ?? member.Id;

                        stack = stack.WithView(Controls.Stack
                            .WithOrientation(Orientation.Horizontal)
                            .WithStyle("padding: 8px 16px; border-bottom: 1px solid var(--neutral-stroke-rest); align-items: center; gap: 12px;")
                            .WithView(Controls.Icon(FluentIcons.Person()).WithStyle("font-size: 20px;"))
                            .WithView(Controls.Label(memberDisplay)));
                    }
                }
            }

            return (UiControl?)stack;
        });
    }

    /// <summary>
    /// Renders the Edit area for a Group node.
    /// Shows members with delete buttons and an Add Member button.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> Edit(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var meshQuery = host.Hub.ServiceProvider.GetService<IMeshQuery>();

        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? [])
            ?? Observable.Return<MeshNode[]>([]);

        return nodeStream.SelectMany(async nodes =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);
            var stack = Controls.Stack.WithStyle("padding: 24px; gap: 16px;");

            var groupName = node?.Name ?? hubPath;
            stack = stack.WithView(Controls.H2($"Edit Group: {groupName}"));

            // Members section
            stack = stack.WithView(Controls.H3("Members").WithStyle("margin: 0;"));

            if (meshQuery != null)
            {
                var members = await meshQuery
                    .QueryAsync<MeshNode>($"path:{hubPath} nodeType:GroupMembership scope:children")
                    .ToListAsync();

                foreach (var member in members)
                {
                    var membership = DeserializeMembership(member);
                    var memberDisplay = membership?.DisplayName ?? membership?.Member ?? member.Name ?? member.Id;

                    stack = stack.WithView(Controls.Stack
                        .WithOrientation(Orientation.Horizontal)
                        .WithStyle("padding: 8px 16px; border-bottom: 1px solid var(--neutral-stroke-rest); align-items: center; gap: 12px;")
                        .WithView(Controls.Icon(FluentIcons.Person()).WithStyle("font-size: 20px;"))
                        .WithView(Controls.Label(memberDisplay).WithStyle("flex: 1;"))
                        .WithView(Controls.Button("")
                            .WithIconStart(FluentIcons.Delete())
                            .WithAppearance(Appearance.Stealth)
                            .WithClickAction(async ctx =>
                            {
                                var nodeFactory = ctx.Hub.ServiceProvider.GetRequiredService<IMeshNodePersistence>();
                                await nodeFactory.DeleteNodeAsync(member.Path);
                            })));
                }
            }

            // Add Member button
            stack = stack.WithView(Controls.Button("Add Member")
                .WithAppearance(Appearance.Accent)
                .WithIconStart(FluentIcons.PersonAdd())
                .WithClickAction(async ctx => await ShowAddMemberDialog(ctx, hubPath)));

            return (UiControl?)stack;
        });
    }

    private static GroupMembership? DeserializeMembership(MeshNode node)
    {
        if (node.Content is GroupMembership gm)
            return gm;
        if (node.Content is System.Text.Json.JsonElement je)
            return System.Text.Json.JsonSerializer.Deserialize<GroupMembership>(je.GetRawText());
        return null;
    }

    private static async Task ShowAddMemberDialog(UiActionContext ctx, string groupPath)
    {
        var formId = $"add_member_{Guid.NewGuid().AsString()}";
        ctx.Host.UpdateData(formId, new Dictionary<string, object?>
        {
            ["memberId"] = ""
        });

        // Load user/group options for autocomplete
        var optionsId = $"member_options_{Guid.NewGuid().AsString()}";
        var options = new List<Option>();
        var meshQuery = ctx.Hub.ServiceProvider.GetService<IMeshQuery>();
        if (meshQuery != null)
        {
            await foreach (var suggestion in meshQuery.AutocompleteAsync(groupPath, "", limit: 50))
            {
                if (suggestion.NodeType is "User" or "Group")
                    options.Add(new Option<string>(suggestion.Path, $"{suggestion.Name} ({suggestion.Path})"));
            }
        }
        ctx.Host.UpdateData(optionsId, options.ToArray());

        var formContent = Controls.Stack.WithStyle("gap: 16px; padding: 16px;")
            .WithView(new ComboboxControl(
                new JsonPointerReference("memberId"),
                new JsonPointerReference(LayoutAreaReference.GetDataPointer(optionsId)))
            {
                Label = "Member (User or Group)",
                Required = true,
                Autocomplete = ComboboxAutocomplete.Both,
                Placeholder = "Search users or groups...",
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

                        var memberId = formValues.GetValueOrDefault("memberId")?.ToString()?.Trim();
                        if (string.IsNullOrEmpty(memberId))
                        {
                            var errorDialog = Controls.Dialog(
                                Controls.Markdown("Please select a **Member**."),
                                "Validation Error"
                            ).WithSize("S").WithClosable(true);
                            saveCtx.Host.UpdateArea(DialogControl.DialogArea, errorDialog);
                            return;
                        }

                        var nodeFactory = saveCtx.Hub.ServiceProvider.GetRequiredService<IMeshNodePersistence>();
                        {
                            var memberName = memberId.Split('/').Last();
                            var memberNode = new MeshNode($"{memberName}_Membership", groupPath)
                            {
                                NodeType = Configuration.GroupMembershipNodeType.NodeType,
                                Name = $"{memberName} Membership",
                                Content = new GroupMembership
                                {
                                    Member = memberId,
                                    Groups = [new MembershipEntry { Group = groupPath }]
                                }
                            };
                            await nodeFactory.CreateNodeAsync(memberNode);
                        }

                        saveCtx.Host.UpdateArea(DialogControl.DialogArea, null!);
                    })));

        var dialog = Controls.Dialog(formContent, "Add Member")
            .WithSize("M")
            .WithClosable(true);

        ctx.Host.UpdateArea(DialogControl.DialogArea, dialog);
    }
}
