using MeshWeaver.Mesh;

namespace MeshWeaver.Blazor.Portal.Components;

/// <summary>
/// Data class for the CreateNodeDialog component.
/// Contains the initial parameters and result data.
/// </summary>
public class CreateNodeDialogData
{
    /// <summary>
    /// The initial namespace to pre-select in the dialog.
    /// Typically the current navigation context.
    /// </summary>
    public string? InitialNamespace { get; set; }

    /// <summary>
    /// The initial type to pre-select in the dialog.
    /// Set when user selects a type from the + menu.
    /// </summary>
    public CreatableTypeInfo? InitialType { get; set; }

    /// <summary>
    /// The path of the created transient node (set after successful creation).
    /// </summary>
    public string? ResultPath { get; set; }

    /// <summary>
    /// Whether the creation was successful.
    /// </summary>
    public bool ResultSuccess { get; set; }
}
