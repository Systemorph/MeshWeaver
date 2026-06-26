using MeshWeaver.ContentCollections;

namespace MeshWeaver.Blazor
{
    /// <summary>
    /// Temporary model used by the new-folder dialog in the file browser. Holds the candidate folder
    /// name and validates it against existing items in the current content collection.
    /// </summary>
    /// <param name="collection">The content collection in which the new folder will be created.</param>
    /// <param name="currentPath">The current directory path; the new folder is created relative to this.</param>
    /// <param name="items">Existing items in the current directory, used for name-uniqueness validation.</param>
    public class CreateFolderModel(ContentCollection collection, string currentPath, IReadOnlyCollection<CollectionItem> items)
    {

        /// <summary>The proposed folder name entered by the user.</summary>
        public string Name { get; set; } = "";
        /// <summary>Returns <c>true</c> when <c>Name</c> does not duplicate any existing item name (case-insensitive).</summary>
        public bool IsValid()
            => items.All(i => !string.Equals(i.Name, Name, StringComparison.OrdinalIgnoreCase));

        /// <summary>Creates the new folder at the path formed by combining <c>currentPath</c> and <c>Name</c> in the content collection.</summary>
        public Task CreateAsync()
            => collection.CreateFolderAsync(Path.Combine(currentPath,Name));
    }
}
