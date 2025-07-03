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

        public FileBrowserControl CreatePath()
            => this with { PathCreation = true };

        public FileBrowserControl WithTopLevel(string path)
            => this with { TopLevelPath = path };

        public string? TopLevelPath { get; init; }
    }
}
