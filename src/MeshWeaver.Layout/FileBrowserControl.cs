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

        /// <summary>
        /// When set to <c>true</c>, instructs the browser to create the path if it does not
        /// already exist. Set via <see cref="CreatePath"/>.
        /// </summary>
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

        /// <summary>Returns a copy that instructs the browser to create the path on the collection if it does not exist.</summary>
        public FileBrowserControl CreatePath()
            => this with { PathCreation = true };

        /// <summary>Returns a copy with <paramref name="path"/> as the top-level path the browser is restricted to.</summary>
        /// <param name="path">The top-level mesh or file-system path; the browser cannot navigate above this.</param>
        public FileBrowserControl WithTopLevel(string path)
            => this with { TopLevelPath = path };

        /// <summary>Returns a copy with <paramref name="config"/> as the collection configuration used to initialise the collection when it does not exist.</summary>
        /// <param name="config">The collection configuration object (e.g. a blob or file-system config).</param>
        public FileBrowserControl WithCollectionConfiguration(object config)
            => this with { CollectionConfiguration = config };

        /// <summary>
        /// Sets the collection metadata from a ContentCollectionConfig so it survives serialization.
        /// </summary>
        public FileBrowserControl WithCollectionInfo(string sourceType, string? basePath, IReadOnlyDictionary<string, string>? settings)
            => this with
            {
                SourceType = sourceType,
                CollectionBasePath = basePath,
                CollectionSettings = settings != null
                    ? string.Join(";", settings.Select(kv => $"{kv.Key}={kv.Value}"))
                    : null
            };

        /// <summary>The top-level path the browser is anchored to; navigation cannot go above this path.</summary>
        public string? TopLevelPath { get; init; }

        /// <summary>
        /// When true, hides upload, create folder, and delete buttons.
        /// Set based on user permissions (no Update permission on the node).
        /// </summary>
        public bool ReadOnly { get; init; }

        /// <summary>Returns a copy with <paramref name="readOnly"/> controlling whether mutating operations (upload, create, delete) are hidden.</summary>
        /// <param name="readOnly">When <c>true</c>, only read operations are available in the browser UI.</param>
        public FileBrowserControl WithReadOnly(bool readOnly = true)
            => this with { ReadOnly = readOnly };
    }
}
