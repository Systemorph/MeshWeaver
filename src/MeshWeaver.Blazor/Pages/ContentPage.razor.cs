using MeshWeaver.Blazor.Infrastructure;
using MeshWeaver.Layout;
using Microsoft.AspNetCore.Components;

namespace MeshWeaver.Blazor.Pages;

public partial class ContentPage : IDisposable
{
    private Stream? Content { get; set; }
    private string? ContentType { get; set; }
    IDisposable? ArticleStreamSubscription { get; set; }
    [Inject] private PortalApplication PortalApplication { get; set; } = null!;
    protected override async Task OnInitializedAsync()
    {
        await base.OnInitializedAsync();
        var collection = ArticleService.GetCollection(Collection);
        if (collection is null || string.IsNullOrEmpty(Path))
            return;

        ContentType = collection.GetContentType(Path!);
        if(ContentType != "text/markdown")
        {
            Content = await collection.GetContentAsync(Path!);
        }

    }
    private byte[] ReadStream(Stream stream)
    {
        using var memoryStream = new MemoryStream();
        stream.CopyTo(memoryStream);
        return memoryStream.ToArray();
    }

    private string ReadStreamAsString(Stream stream)
    {
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public void Dispose()
    {
        Content?.Dispose();
        ArticleStreamSubscription?.Dispose();
    }
}
