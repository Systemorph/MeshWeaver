using System.Runtime.CompilerServices;
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
    public async IAsyncEnumerable<AutocompleteItem> GetItemsAsync(
        string query,
        string? contextPath = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var address = hub.Address;

        await foreach (var collection in contentService.GetCollectionsAsync().WithCancellation(ct))
        {
            IReadOnlyCollection<FileItem>? files = null;
            try
            {
                files = await collection.GetFilesAsync("/");
            }
            catch
            {
                // Skip collections that fail to enumerate
            }

            if (files != null)
            {
                foreach (var file in files)
                {
                    var pathWithoutLeadingSlash = file.Path.TrimStart('/');
                    var fullPath = $"{collection.Collection}/{pathWithoutLeadingSlash}";

                    // Format: addressType/addressId/content/collection/path
                    yield return new AutocompleteItem(
                        Label: file.Name,
                        InsertText: $"@{address}/content/{fullPath} ",
                        Description: fullPath,
                        Category: collection.DisplayName,
                        Priority: 0,
                        Kind: AutocompleteKind.File
                    );
                }
            }
        }
    }
}
