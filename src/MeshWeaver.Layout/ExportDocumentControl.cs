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

    /// <summary>
    /// Whether this export can offer PIXEL fidelity — true only for a Deck on a deployment that
    /// actually has a headless browser. The capability is resolved server-side and travels on the
    /// control, so the dialog never shows a choice that would fail: a portal without a browser
    /// simply doesn't render the fidelity picker.
    /// </summary>
    public bool PixelFidelityAvailable { get; init; }
}
