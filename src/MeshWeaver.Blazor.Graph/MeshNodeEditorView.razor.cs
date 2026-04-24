using System.Reactive.Linq;
using MeshWeaver.Blazor.Components.Monaco;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Blazor.Graph;

public partial class MeshNodeEditorView : IDisposable
{
    private MonacoEditorView? _monacoEditor;
    private MeshNode? _node;
    private bool _isLoading = true;

    // Bound fields — refreshed from the live stream and pushed back through it.
    private string _name = string.Empty;
    private string? _nodeType;
    private string _contentText = string.Empty;

    // Suppress stream-echo refresh while the user is mid-typing in the content area.
    private bool _userIsEditingContent;

    // Backend editor: owns one long-lived subscription to the node's MeshNode stream
    // and writes back through the same stream. No save buttons, no AwaitResponse —
    // every change streams immediately.
    private IMeshNodeEditor? _editor;
    private IDisposable? _streamSub;

    protected override void BindData()
    {
        base.BindData();
        _editor = new MeshNodeEditor(Hub, ViewModel.NodePath);
        _streamSub = _editor.Node.Subscribe(node =>
        {
            _node = node;
            ApplyNodeToFields(node);
            _isLoading = false;
            InvokeAsync(StateHasChanged);
        }, ex =>
        {
            Logger.LogError(ex, "Error streaming node at path {Path}", ViewModel.NodePath);
            _isLoading = false;
            InvokeAsync(StateHasChanged);
        });
    }

    private void ApplyNodeToFields(MeshNode node)
    {
        _name = node.Name ?? string.Empty;
        _nodeType = node.NodeType?.ToLowerInvariant();

        // Don't clobber the user's in-flight edits with the round-trip echo from the
        // stream — only refresh content text from the stream when the user isn't typing.
        if (!_userIsEditingContent)
            LoadContent();
    }

    private void LoadContent()
    {
        if (_node?.Content == null)
        {
            _contentText = string.Empty;
            return;
        }

        // Reflect into Story.Text to avoid circular dependency with Graph.Domain.
        if (_nodeType == "story")
        {
            var textProperty = _node.Content.GetType().GetProperty("Text");
            if (textProperty != null)
            {
                _contentText = textProperty.GetValue(_node.Content) as string ?? string.Empty;
                return;
            }
        }

        _contentText = string.Empty;
    }

    private void OnNameChanged(string newName)
    {
        if (_name == newName) return;
        _name = newName;
        // Push name change through the stream — echo refreshes _node.
        _editor?.Update(node => node with { Name = newName });
    }

    private void OnContentChanged(string value)
    {
        if (_contentText == value) return;
        _userIsEditingContent = true;
        _contentText = value;
        _editor?.Update(node => node with { Content = WithText(node.Content, value) });
    }

    private object? WithText(object? currentContent, string newText)
    {
        if (_nodeType != "story" || currentContent == null)
            return currentContent;

        var textProperty = currentContent.GetType().GetProperty("Text");
        if (textProperty == null)
            return currentContent;

        // Records: use the compiler-generated <Clone>$ method so we don't lose any
        // fields the editor doesn't surface.
        var cloneMethod = currentContent.GetType().GetMethod("<Clone>$");
        if (cloneMethod == null)
            return currentContent;

        var cloned = cloneMethod.Invoke(currentContent, null);
        if (cloned == null) return currentContent;
        textProperty.SetValue(cloned, newText);
        return cloned;
    }

    private CompletionProviderConfig GetArticleCompletionProvider()
    {
        return new CompletionProviderConfig
        {
            TriggerCharacters = ["@", "/"],
            Items =
            [
                new CompletionItem
                {
                    Label = "@author",
                    InsertText = "@author: ",
                    Description = "Set the article author",
                    Category = "Frontmatter"
                },
                new CompletionItem
                {
                    Label = "@tags",
                    InsertText = "@tags: []",
                    Description = "Set article tags",
                    Category = "Frontmatter"
                },
                new CompletionItem
                {
                    Label = "@title",
                    InsertText = "@title: ",
                    Description = "Set the article title",
                    Category = "Frontmatter"
                },
                new CompletionItem
                {
                    Label = "@abstract",
                    InsertText = "@abstract: ",
                    Description = "Set the article abstract",
                    Category = "Frontmatter"
                },
                new CompletionItem
                {
                    Label = "@published",
                    InsertText = "@published: " + DateTime.UtcNow.ToString("yyyy-MM-dd"),
                    Description = "Set the publication date",
                    Category = "Frontmatter"
                },
                new CompletionItem
                {
                    Label = "/frontmatter",
                    InsertText = "---\ntitle: \nauthor: \ntags: []\npublished: " + DateTime.UtcNow.ToString("yyyy-MM-dd") + "\n---\n\n",
                    Description = "Insert YAML frontmatter template",
                    Category = "Templates"
                }
            ]
        };
    }

    public void Dispose()
    {
        _streamSub?.Dispose();
        _editor?.Dispose();
    }
}
