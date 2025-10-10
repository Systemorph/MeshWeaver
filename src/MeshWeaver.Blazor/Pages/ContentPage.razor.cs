using MeshWeaver.Blazor.Infrastructure;
using MeshWeaver.ContentCollections;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Blazor.Pages;

public partial class ContentPage : ComponentBase, IDisposable
{
    [Parameter] public string? Collection { get; set; }
    [Parameter] public string? Path { get; set; }

    public Stream? Content { get; set; }
    public string? ContentType { get; set; }
    public string? ErrorMessage { get; set; }

    [Inject] public PortalApplication PortalApplication { get; set; } = null!;
    private IContentService ContentService => PortalApplication.Hub.ServiceProvider.GetRequiredService<IContentService>();

    protected override async Task OnInitializedAsync()
    {
        await base.OnInitializedAsync();

        var collection = await ContentService.GetCollectionAsync(Collection!);
        if (collection is null)
        {
            ErrorMessage = $"Collection '{Collection}' does not exist.";
            return;
        }

        if (string.IsNullOrEmpty(Path))
            return;

        ContentType = collection.GetContentType(Path!);
        if (ContentType != "text/markdown")
        {
            Content = await collection.GetContentAsync(Path!);
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
