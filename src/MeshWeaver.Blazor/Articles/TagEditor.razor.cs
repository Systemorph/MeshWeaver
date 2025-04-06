using Azure;
using Microsoft.AspNetCore.Components;

namespace MeshWeaver.Blazor.Articles;

public partial class TagEditor
{
    [Parameter]
    public List<string> Tags { get; set; } = new();

    [Parameter]
    public EventCallback<List<string>> TagsChanged { get; set; }

    private string newTag = string.Empty;

    private async Task AddTag()
    {
        if (!string.IsNullOrWhiteSpace(newTag) && !Tags.Contains(newTag))
        {
            var updatedTags = new List<string>(Tags) { newTag };
            Tags = updatedTags;
            await TagsChanged.InvokeAsync(Tags);
            newTag = string.Empty;
        }
    }

    private async Task RemoveTag(string tag)
    {
        var updatedTags = new List<string>(Tags);
        updatedTags.Remove(tag);
        Tags = updatedTags;
        await TagsChanged.InvokeAsync(Tags);
    }

}
