namespace MeshWeaver.Layout;

/// <summary>
/// Control that surfaces the markdown-to-PDF/DOCX export dialog. Paired with
/// <c>ExportDocumentView</c> in the Blazor layer. Instantiated by
/// <c>ExportDocumentLayoutArea</c> in <c>MeshWeaver.Markdown.Export</c>.
/// </summary>
public record ExportDocumentControl()
    : UiControl<ExportDocumentControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    /// <summary>Path of the source markdown node.</summary>
    public string? SourcePath { get; init; }

    /// <summary>Display name of the source node (pre-fills the Title field).</summary>
    public string? NodeName { get; init; }

    /// <summary>
    /// Which format the dialog should default to — "pdf" or "docx".
    /// Driven by the area name the menu item navigates to.
    /// </summary>
    public string DefaultFormat { get; init; } = "pdf";

    /// <summary>
    /// Whether the node has descendants (drives the "Include children" toggle visibility).
    /// </summary>
    public bool HasDescendants { get; init; }
}
