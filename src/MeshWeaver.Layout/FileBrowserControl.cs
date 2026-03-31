namespace MeshWeaver.Layout
{
    /// <summary>
    /// Provides the file browser for the given colleciton
    /// </summary>
    /// <param name="Collection"></param>
    public record FileBrowserControl(object Collection)
        : UiControl<FileBrowserControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
    {
        /// <summary>
        /// Path to initialize the file browser.
        /// </summary>
        public object? Path { get; init; }

        public object? PathCreation { get; init; }

        /// <summary>
        /// Configuration to initialize the collection if it doesn't exist.
        /// </summary>
        public object? CollectionConfiguration { get; init; }

        /// <summary>
        /// Source type for the collection (e.g., "FileSystem", "AzureBlob").
        /// Used to reconstruct the config on the client side when CollectionConfiguration
        /// doesn't survive serialization through the layout area streaming system.
        /// </summary>
        public string? SourceType { get; init; }

        /// <summary>
        /// Base path / blob prefix for the collection.
        /// </summary>
        public string? CollectionBasePath { get; init; }

        /// <summary>
        /// Additional settings serialized as key=value pairs (e.g., "ContainerName=content;ClientName=storage").
        /// </summary>
        public string? CollectionSettings { get; init; }

        public FileBrowserControl CreatePath()
            => this with { PathCreation = true };

        public FileBrowserControl WithTopLevel(string path)
            => this with { TopLevelPath = path };

        public FileBrowserControl WithCollectionConfiguration(object config)
            => this with { CollectionConfiguration = config };

        /// <summary>
        /// Sets the collection metadata from a ContentCollectionConfig so it survives serialization.
        /// </summary>
        public FileBrowserControl WithCollectionInfo(string sourceType, string? basePath, Dictionary<string, string>? settings)
            => this with
            {
                SourceType = sourceType,
                CollectionBasePath = basePath,
                CollectionSettings = settings != null
                    ? string.Join(";", settings.Select(kv => $"{kv.Key}={kv.Value}"))
                    : null
            };

        public string? TopLevelPath { get; init; }

        /// <summary>
        /// When true, hides upload, create folder, and delete buttons.
        /// Set based on user permissions (no Update permission on the node).
        /// </summary>
        public bool ReadOnly { get; init; }

        public FileBrowserControl WithReadOnly(bool readOnly = true)
            => this with { ReadOnly = readOnly };
    }
}
