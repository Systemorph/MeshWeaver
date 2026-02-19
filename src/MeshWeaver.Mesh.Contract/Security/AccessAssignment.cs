namespace MeshWeaver.Mesh.Security;

/// <summary>
/// View model representing a single role assignment at a specific level in the hierarchy.
/// Used by the Access Control management UI to show inherited vs. local assignments.
/// All properties are serializable for client-side data binding in ItemTemplateControl.
/// </summary>
public record AccessAssignment
{
    public string UserId { get; init; } = "";
    public string? DisplayName { get; init; }
    public string RoleId { get; init; } = "";
    public string SourcePath { get; init; } = "";
    public bool Denied { get; init; }
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
