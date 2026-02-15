namespace MeshWeaver.Blazor.Portal.Components;

/// <summary>
/// Data class for the ImportNodeDialog component.
/// Contains the initial parameters and result data.
/// </summary>
public class ImportNodeDialogData
{
    /// <summary>
    /// The initial namespace to pre-select in the dialog.
    /// Typically the current navigation context.
    /// </summary>
    public string? InitialNamespace { get; set; }

    /// <summary>
    /// Whether to force re-import (overwrite existing data).
    /// </summary>
    public bool Force { get; set; }

    /// <summary>
    /// Whether the import was successful.
    /// </summary>
    public bool ResultSuccess { get; set; }
}
