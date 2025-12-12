using MeshWeaver.Blazor.Infrastructure;
using MeshWeaver.ContentCollections;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Blazor.Pages;

/// <summary>
/// Content page that handles both global content URLs (/content/{collection}/{path})
/// and address-scoped content URLs (/content/{addressType}/{addressId}/{collection}/{path}).
/// </summary>
public partial class ContentPage : ComponentBase, IDisposable
{
    /// <summary>
    /// Address type parameter from the URL route (optional, for address-scoped content).
    /// </summary>
    [Parameter] public string? AddressType { get; set; }

    /// <summary>
    /// Address ID parameter from the URL route (optional, for address-scoped content).
    /// </summary>
    [Parameter] public string? AddressId { get; set; }

    /// <summary>
    /// Collection parameter from the URL route.
    /// </summary>
    [Parameter] public string? Collection { get; set; }

    /// <summary>
    /// Path parameter from the URL route (catch-all).
    /// </summary>
    [Parameter] public string? Path { get; set; }

    /// <summary>
    /// The resolved collection name (same as Collection for global content).
    /// </summary>
    public string? ResolvedCollection { get; set; }

    /// <summary>
    /// The resolved path within the collection.
    /// </summary>
    public string? ResolvedPath { get; set; }

    /// <summary>
    /// The target address for the content (portal hub address for global content).
    /// </summary>
    public Address? TargetAddress { get; set; }

    public Stream? Content { get; set; }
    public string? ContentType { get; set; }
    public string? ErrorMessage { get; set; }

    [Inject] public PortalApplication PortalApplication { get; set; } = null!;
    private IContentService ContentService => PortalApplication.Hub.ServiceProvider.GetRequiredService<IContentService>();

    protected override async Task OnInitializedAsync()
    {
        await base.OnInitializedAsync();

        ResolvedCollection = Collection;
        ResolvedPath = Path;

        // Determine target address based on route
        if (!string.IsNullOrEmpty(AddressType) && !string.IsNullOrEmpty(AddressId))
        {
            // Address-scoped content: /content/{AddressType}/{AddressId}/{Collection}/{Path}
            TargetAddress = new Address(AddressType, AddressId);
        }
        else
        {
            // Global content: /content/{Collection}/{Path} - use portal hub address
            TargetAddress = PortalApplication.Hub.Address;
        }

        // Add configuration for address-scoped content collections
        if (TargetAddress != null && !string.IsNullOrEmpty(ResolvedCollection))
        {
            ContentService.AddConfiguration(new ContentCollectionConfig
            {
                Name = ResolvedCollection,
                SourceType = HubStreamProviderFactory.SourceType,
                Address = TargetAddress
            });
        }

        var collection = await ContentService.GetCollectionAsync(ResolvedCollection!);
        if (collection is null)
        {
            ErrorMessage = $"Collection '{ResolvedCollection}' does not exist.";
            return;
        }

        if (string.IsNullOrEmpty(ResolvedPath))
            return;

        ContentType = collection.GetContentType(ResolvedPath!);
        if (ContentType != "text/markdown")
        {
            Content = await collection.GetContentAsync(ResolvedPath!);
        }
    }

    public byte[] ReadStream(Stream stream)
    {
        using var memoryStream = new MemoryStream();
        stream.CopyTo(memoryStream);
        return memoryStream.ToArray();
    }

    public string ReadStreamAsString(Stream stream)
    {
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public void Dispose()
    {
        Content?.Dispose();
    }
}
