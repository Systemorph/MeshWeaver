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
    /// <summary>Area name for the Overview layout area.</summary>
    public const string OverviewArea = "Overview";
    /// <summary>Area name for the Edit layout area.</summary>
    public const string EditArea = "Edit";
    /// <summary>Area name for the Memberships layout area.</summary>
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

        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? [])
            ?? Observable.Return<MeshNode[]>([]);

        var membersStream = host.Workspace.GetQuery(
            $"group-members:{hubPath}",
            $"namespace:{hubPath} nodeType:GroupMembership");

        return nodeStream.CombineLatest(membersStream, (nodes, members) =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);
            var stack = Controls.Stack.WithStyle("padding: 24px; gap: 16px;");

            var groupName = node?.Name ?? hubPath;
            stack = stack.WithView(Controls.H2(groupName));

            var accessObject = node.ContentAs<AccessObject>(host.Hub.JsonSerializerOptions)
                ?? (node?.Content is System.Text.Json.JsonElement je
                    ? System.Text.Json.JsonSerializer.Deserialize<AccessObject>(je.GetRawText())
                    : null);

            if (!string.IsNullOrEmpty(accessObject?.Description))
                stack = stack.WithView(Controls.Html($"<p>{System.Web.HttpUtility.HtmlEncode(accessObject.Description)}</p>"));

            stack = stack.WithView(Controls.H3("Members").WithStyle("margin: 0;"));

            var memberList = members as IReadOnlyList<MeshNode> ?? members.ToList();
            if (memberList.Count == 0)
            {
                stack = stack.WithView(Controls.Html("<p style=\"color: var(--neutral-foreground-hint);\">No members.</p>"));
            }
            else
            {
                foreach (var member in memberList)
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

            return (UiControl?)stack;
        });
    }

    /// <summary>
    /// Renders the Edit area for a Group node.
    /// Shows members with delete buttons and an Add Member button.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> Edit(LayoutAreaHost host, RenderingContext context)
    {
        var hubPath = host.Hub.Address.ToString();

        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? [])
            ?? Observable.Return<MeshNode[]>([]);

        var membersStream = host.Workspace.GetQuery(
            $"group-members-edit:{hubPath}",
            $"namespace:{hubPath} nodeType:GroupMembership");

        return nodeStream.CombineLatest(membersStream, (nodes, members) =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);
            var stack = Controls.Stack.WithStyle("padding: 24px; gap: 16px;");

            var groupName = node?.Name ?? hubPath;
            stack = stack.WithView(Controls.H2($"Edit Group: {groupName}"));

            stack = stack.WithView(Controls.H3("Members").WithStyle("margin: 0;"));

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
                        .WithClickAction(ctx =>
                        {
                            var nodeFactory = ctx.Hub.ServiceProvider.GetRequiredService<IMeshService>();
                            nodeFactory.DeleteNode(member.Path).Subscribe(
                                _ => { },
                                _ => { });
                            return Task.CompletedTask;
                        })));
            }

            stack = stack.WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithStyle("gap: 8px;")
                .WithView(Controls.Button("Add Member")
                    .WithAppearance(Appearance.Accent)
                    .WithIconStart(FluentIcons.PersonAdd())
                    .WithClickAction(ctx =>
                    {
                        ShowAddMemberDialog(ctx, hubPath);
                        return Task.CompletedTask;
                    }))
                .WithView(Controls.Button("Invite by Email")
                    .WithIconStart(FluentIcons.Mail())
                    .WithClickAction(ctx =>
                    {
                        ShowBulkInviteDialog(ctx, hubPath);
                        return Task.CompletedTask;
                    })));

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

    private static void ShowAddMemberDialog(UiActionContext ctx, string groupPath)
    {
        var formId = $"add_member_{Guid.NewGuid().AsString()}";
        ctx.Host.UpdateData(formId, new Dictionary<string, object?>
        {
            ["memberId"] = ""
        });

        // Load user/group options for autocomplete — REACTIVELY. Never await / await-foreach
        // a hub query on the action-block thread: that is the layout-area deadlock (the click
        // runs on the hub, and the query response is queued behind the blocked await). The
        // Autocomplete observable's first snapshot populates the combobox options; the combobox
        // databinds to optionsId and refreshes when they arrive.
        var optionsId = $"member_options_{Guid.NewGuid().AsString()}";
        ctx.Host.UpdateData(optionsId, Array.Empty<Option>());
        var meshQuery = ctx.Hub.ServiceProvider.GetService<IMeshService>();
        meshQuery?.Autocomplete(groupPath, "", limit: 50)
            .Take(1)
            .Subscribe(suggestions =>
            {
                var options = new List<Option>();
                foreach (var s in suggestions)
                    if (s.NodeType is "User" or "Group")
                        options.Add(new Option<string>(s.Path, $"{s.Name} ({s.Path})"));
                ctx.Host.UpdateData(optionsId, options.ToArray());
            });

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
                    .WithClickAction(saveCtx =>
                    {
                        saveCtx.Host.Stream.GetDataStream<Dictionary<string, object?>>(formId)
                            .Take(1)
                            .Subscribe(formValues =>
                            {
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

                                var nodeFactory = saveCtx.Hub.ServiceProvider.GetRequiredService<IMeshService>();
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
                                nodeFactory.CreateNode(memberNode).Subscribe(
                                    _ => saveCtx.Host.UpdateArea(DialogControl.DialogArea, null!),
                                    _ => { });
                            });
                        return Task.CompletedTask;
                    })));

        var dialog = Controls.Dialog(formContent, "Add Member")
            .WithSize("M")
            .WithClosable(true);

        ctx.Host.UpdateArea(DialogControl.DialogArea, dialog);
    }

    /// <summary>
    /// The bulk "Invite by Email" dialog: paste a list of emails (newline / comma / semicolon separated),
    /// pick the role every invitee gets on the group, and invite. Existing accounts become members (with
    /// the role granted on the group) immediately; unknown emails receive an invitation email and land the
    /// identical membership + role the moment they register — via <see cref="GroupInviteExtensions.InviteAllToGroup"/>.
    /// </summary>
    private static void ShowBulkInviteDialog(UiActionContext ctx, string groupPath)
    {
        var formId = $"bulk_invite_{Guid.NewGuid().AsString()}";
        ctx.Host.UpdateData(formId, new Dictionary<string, object?>
        {
            ["emails"] = "",
            ["role"] = Role.Viewer.Id,
        });
        var dataContext = LayoutAreaReference.GetDataPointer(formId);

        var formContent = Controls.Stack.WithStyle("gap: 16px; padding: 16px;")
            .WithView((new TextAreaControl(new JsonPointerReference("emails"))
                {
                    Label = "Email addresses",
                    Placeholder = "anna@example.com, ben@example.com — one per line or comma-separated",
                    DataContext = dataContext,
                }).WithRows(8))
            .WithView(new SelectControl(new JsonPointerReference("role"), Array.Empty<object>())
                    .WithOptions(new[] { Role.Admin.Id, Role.Editor.Id, Role.Viewer.Id, Role.Commenter.Id })
                with { Label = "Role", DataContext = dataContext })
            .WithView(Controls.Markdown(
                    "Everyone on the list becomes a member with the selected role on this group. "
                    + "Existing accounts are added right away; everyone else receives an invitation email "
                    + "and is added automatically the moment they register.")
                .WithStyle("font-size: 12px; opacity: .75;"))
            .WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithStyle("justify-content: flex-end; gap: 8px;")
                .WithView(Controls.Button("Invite")
                    .WithAppearance(Appearance.Accent)
                    .WithIconStart(FluentIcons.Mail())
                    .WithClickAction(inviteCtx =>
                    {
                        inviteCtx.Host.Stream.GetDataStream<Dictionary<string, object?>>(formId)
                            .Take(1)
                            .Subscribe(form =>
                            {
                                var emailList = form.GetValueOrDefault("emails")?.ToString() ?? "";
                                var role = form.GetValueOrDefault("role")?.ToString()?.Trim();
                                if (string.IsNullOrEmpty(role))
                                    role = Role.Viewer.Id;

                                var (valid, invalid) = GroupInviteExtensions.ParseEmails(emailList);
                                if (valid.Count == 0)
                                {
                                    ShowDialogMessage(inviteCtx, invalid.Count == 0
                                            ? "Please enter at least one **email address**."
                                            : "No valid email address found. Not email-shaped: "
                                              + string.Join(", ", invalid.Select(t => $"`{t}`")),
                                        "Validation Error");
                                    return;
                                }

                                var accessService = inviteCtx.Hub.ServiceProvider.GetRequiredService<AccessService>();
                                var invitedBy = accessService.Context?.ObjectId;
                                inviteCtx.Hub.InviteAllToGroup(groupPath, emailList, invitedBy, role)
                                    .Subscribe(
                                        result => ShowDialogMessage(inviteCtx, BuildInviteSummary(result, role),
                                            "Invitations"),
                                        ex => ShowDialogMessage(inviteCtx, $"Inviting failed: {ex.Message}", "Error"));
                            });
                        return Task.CompletedTask;
                    })));

        var dialog = Controls.Dialog(formContent, "Invite by Email")
            .WithSize("M")
            .WithClosable(true);

        ctx.Host.UpdateArea(DialogControl.DialogArea, dialog);
    }

    private static string BuildInviteSummary(GroupBulkInviteResult result, string role)
    {
        var summary = $"**{result.AddedCount}** added now · **{result.InvitedCount}** invited "
                      + $"(added when they register) · role **{role}**.";
        if (result.InvalidCount > 0)
            summary += "\n\nSkipped (not email-shaped): "
                       + string.Join(", ", result.InvalidEmails.Select(t => $"`{t}`"));
        return summary;
    }

    private static void ShowDialogMessage(UiActionContext ctx, string markdown, string title) =>
        ctx.Host.UpdateArea(DialogControl.DialogArea,
            Controls.Dialog(Controls.Markdown(markdown), title).WithSize("S").WithClosable(true));
}
