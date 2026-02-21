namespace MeshWeaver.Mesh.Security;

/// <summary>
/// View model representing a single role assignment at a specific level in the hierarchy.
/// Used by the Access Control management UI to show inherited vs. local assignments.
/// All properties are serializable for client-side data binding in ItemTemplateControl.
/// </summary>
public record AccessAssignment
{
    /// <summary>User identifier for this assignment.</summary>
    public string UserId { get; init; } = "";
    /// <summary>Optional display name for the user.</summary>
    public string? DisplayName { get; init; }
    /// <summary>Role identifier assigned to the user.</summary>
    public string RoleId { get; init; } = "";
    /// <summary>Path from which this assignment originates.</summary>
    public string SourcePath { get; init; } = "";
    /// <summary>True if the assignment denies rather than grants access.</summary>
    public bool Denied { get; init; }
    /// <summary>True if the assignment is local (not inherited).</summary>
    public bool IsLocal { get; init; }

    /// <summary>
    /// Display label for the user (DisplayName or UserId fallback).
    /// Must be set at creation time for JSON serialization.
    /// </summary>
    public string DisplayLabel { get; init; } = "";

    /// <summary>
    /// Human-readable source ("Global" for empty path).
    /// Must be set at creation time for JSON serialization.
    /// </summary>
    public string SourceDisplay { get; init; } = "";

    /// <summary>
    /// True when the role is granted (not denied). Bindable for Switch controls.
    /// Must be set at creation time for JSON serialization.
    /// </summary>
    public bool IsActive { get; init; }
}
