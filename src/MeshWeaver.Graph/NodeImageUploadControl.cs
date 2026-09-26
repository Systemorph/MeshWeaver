using MeshWeaver.Layout;

namespace MeshWeaver.Graph;

/// <summary>
/// A data-bound PICTURE editor for a mesh node — shows the node's current picture
/// (<c>MeshNode.Icon</c>) and offers Upload/Replace and Remove. The profile page uses it for the
/// user's avatar; any node whose icon is an image can use it the same way.
///
/// <para>Same binding shape as <see cref="MeshNodeContentEditorControl"/>: the backend only DECLARES
/// the control with a <see cref="NodePath"/>; the GUI view reads the picture straight off
/// <c>GetMeshNodeStream(NodePath)</c> and writes through
/// <c>MeshWeaver.ContentCollections.NodeImageUpload</c>, which stores the file in the node's own
/// <c>content</c> collection and updates <c>Icon</c> through the one mutation API. There is no
/// <c>/data</c> replica, no file-name form field and no Save button.</para>
///
/// <para>Every visible string is resolved on the backend (<see cref="UploadLabel"/> and friends)
/// in the viewer's language, so the view needs no catalog of its own.</para>
/// </summary>
/// <param name="NodePath">The node whose picture this edits.</param>
public record NodeImageUploadControl(string NodePath)
    : UiControl<NodeImageUploadControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    /// <summary>Whether the viewer may change the picture. False renders the picture read-only.</summary>
    public bool CanEdit { get; init; } = true;

    /// <summary>The name shown as initials when the node has no picture.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Label of the upload button when there is no picture yet.</summary>
    public string? UploadLabel { get; init; }

    /// <summary>Label of the upload button when a picture exists.</summary>
    public string? ReplaceLabel { get; init; }

    /// <summary>Label of the remove button.</summary>
    public string? RemoveLabel { get; init; }

    /// <summary>The hint under the buttons (accepted formats and size).</summary>
    public string? HintText { get; init; }

    /// <summary>The picture's accessible name (the <c>alt</c> text).</summary>
    public string? AltText { get; init; }
}
