using MeshWeaver.Articles;

namespace MeshWeaver.Blazor
{
    public class CreateFolderModel(ContentCollection collection, string currentPath, IReadOnlyCollection<CollectionItem> items)
    {

        public string Name { get; set; }
        public bool IsValid()
            => items.All(i => !string.Equals(i.Name, Name, StringComparison.OrdinalIgnoreCase));

        public Task CreateAsync()
            => collection.CreateFolderAsync(Path.Combine(currentPath,Name));
    }
}
