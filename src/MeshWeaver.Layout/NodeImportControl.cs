namespace MeshWeaver.Layout
{
    /// <summary>
    /// Control for importing MeshNodes from a ZIP file uploaded via the browser.
    /// Rendered by NodeImportView in the Blazor layer.
    /// </summary>
    public record NodeImportControl()
        : UiControl<NodeImportControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
    {
        /// <summary>
        /// Target root path in the mesh for imported nodes.
        /// </summary>
        public string? TargetPath { get; init; }

        /// <summary>
        /// Whether to force re-import (overwrite existing data).
        /// </summary>
        public bool Force { get; init; }
    }
}
