using MeshWeaver.Articles;

namespace MeshWeaver.Blazor
{
    public class CreateFolderModel(ContentCollection collection, IReadOnlyCollection<CollectionItem> items)
    {
        public string Name { get; set; }
        public bool IsValid()
            => items.All(i => !string.Equals(i.Name, Name, StringComparison.OrdinalIgnoreCase));

        public Task CreateAsync()
            => collection.CreateFolderAsync(Name);
    }
}
