using MeshWeaver.Layout;
using MeshWeaver.Messaging;

namespace MeshWeaver.Blazor.Monaco;

/// <summary>
/// Extensions for adding Monaco editor views to the application.
/// </summary>
public static class BlazorMonacoExtensions
{
    /// <summary>
    /// Adds the Monaco editor views (CodeEditorView) to the configuration.
    /// </summary>
    public static MessageHubConfiguration AddMonacoViews(this MessageHubConfiguration configuration)
    {
        return configuration
            .WithTypes(typeof(CodeEditorControl), typeof(MarkdownEditorControl))
            .AddViews(registry => registry
                .WithView<CodeEditorControl, CodeEditorView>()
                .WithView<MarkdownEditorControl, MarkdownEditorView>());
    }
}
