namespace MeshWeaver.Layout
{
    /// <summary>
    /// Control for exporting MeshNodes as a downloadable ZIP file.
    /// Rendered by NodeExportView in the Blazor layer.
    /// </summary>
    public record NodeExportControl()
        : UiControl<NodeExportControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
    {
        /// <summary>
        /// Root path of the node subtree to export.
        /// </summary>
        public string? SourcePath { get; init; }

        /// <summary>
        /// Display name of the node being exported.
        /// </summary>
        public string? NodeName { get; init; }

        /// <summary>
        /// Satellite node types that exist under this subtree (for checkbox display).
        /// </summary>
        public IReadOnlyList<string>? AvailableSatelliteTypes { get; init; }
    }
}
