using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;

namespace MeshWeaver.Graph;

/// <summary>
/// Builds a compact row control for an AccessAssignment.
/// Uses MeshNodeThumbnailControl as the card with optional × delete button.
/// </summary>
public static class AccessAssignmentControlBuilder
{
    /// <summary>
    /// Builds a row for an AccessAssignment using MeshNodeThumbnailControl.
    /// Description shows role names inline.
    /// </summary>
    public static UiControl Build(
        AccessAssignment assignment,
        MeshNode? node = null,
        string? source = null,
        bool isEditable = false,
        string? navigateTo = null,
        Func<UiActionContext, Task>? onDelete = null)
    {
        var name = assignment.DisplayName ?? assignment.AccessObject;
        var rolesText = string.Join(", ", assignment.Roles.Select(r =>
        {
            var label = string.IsNullOrEmpty(r.Role) ? "(no role)" : r.Role;
            return r.Denied ? $"~{label}~" : label;
        }));

        var imageUrl = node != null ? MeshNodeThumbnailControl.GetImageUrlForNode(node) : null;
        var thumbnail = new MeshNodeThumbnailControl(
            navigateTo?.TrimStart('/') ?? node?.Path ?? "",
            name ?? "?",
            rolesText,
            imageUrl);

        if (onDelete == null)
            return thumbnail;

        // Wrap in horizontal row with × button
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
