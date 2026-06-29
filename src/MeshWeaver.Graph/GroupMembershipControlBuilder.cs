using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;

namespace MeshWeaver.Graph;

/// <summary>
/// Builds a card control for a GroupMembership using MeshNodeThumbnailControl.
/// Description shows group names inline.
/// </summary>
public static class GroupMembershipControlBuilder
{
    /// <summary>
    /// Builds a card control for a group membership, optionally with an inline delete button.
    /// </summary>
    /// <param name="membership">The group membership being rendered.</param>
    /// <param name="node">The related mesh node, used for the thumbnail image and path.</param>
    /// <param name="source">Optional source identifier for the membership card.</param>
    /// <param name="isEditable">Whether the rendered card is editable.</param>
    /// <param name="navigateTo">Optional path the card navigates to when clicked.</param>
    /// <param name="onDelete">Optional callback invoked when the delete button is clicked; when null, no delete button is shown.</param>
    /// <returns>The card view for the group membership.</returns>
    public static UiControl Build(
        GroupMembership membership,
        MeshNode? node = null,
        string? source = null,
        bool isEditable = false,
        string? navigateTo = null,
        Func<UiActionContext, Task>? onDelete = null)
    {
        var name = membership.DisplayName ?? membership.Member;
        var groupsText = string.Join(", ", membership.Groups.Select(g =>
            string.IsNullOrEmpty(g.Group) ? "(no group)" : g.Group));

        var imageUrl = node != null ? MeshNodeThumbnailControl.GetImageUrlForNode(node) : null;
        var thumbnail = new MeshNodeThumbnailControl(
            navigateTo?.TrimStart('/') ?? node?.Path ?? "",
            name ?? "?",
            groupsText,
            imageUrl);

        if (onDelete == null)
            return thumbnail;

        var row = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("align-items: center; gap: 4px;")
            .WithView(thumbnail.WithStyle("flex: 1;"))
            .WithView(Controls.Button("×")
                .WithAppearance(Appearance.Stealth)
                .WithStyle("min-width:28px;padding:0 4px;height:28px;font-size:16px;")
                .WithClickAction(onDelete));

        return row;
    }
}
