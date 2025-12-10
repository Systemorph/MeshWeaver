using MeshWeaver.Data.Completion;
using MeshWeaver.Messaging;

namespace MeshWeaver.ContentCollections.Completion;

/// <summary>
/// Provides autocomplete items for content collections.
/// Returns files from all registered content collections.
/// </summary>
public class ContentAutocompleteProvider(IContentService contentService, IMessageHub hub) : IAutocompleteProvider
{
    /// <inheritdoc />
    public async Task<IEnumerable<AutocompleteItem>> GetItemsAsync(string query, CancellationToken ct = default)
    {
        var items = new List<AutocompleteItem>();
        var address = hub.Address;

        await foreach (var collection in contentService.GetCollectionsAsync().WithCancellation(ct))
        {
            try
            {
                var files = await collection.GetFilesAsync("/");
                foreach (var file in files)
                {
                    var pathWithoutLeadingSlash = file.Path.TrimStart('/');
                    var fullPath = $"{collection.Collection}/{pathWithoutLeadingSlash}";

                    // Format: addressType/addressId/content/collection/path
                    items.Add(new AutocompleteItem(
                        Label: file.Name,
                        InsertText: $"@{address}/content/{fullPath} ",
                        Description: fullPath,
                        Category: collection.DisplayName,
                        Priority: 0,
                        Kind: AutocompleteKind.File
                    ));
                }
            }
            catch
            {
                // Skip collections that fail to enumerate
            }
        }

        return items;
    }
}
