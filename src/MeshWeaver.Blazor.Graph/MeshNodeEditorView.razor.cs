using MeshWeaver.Blazor.Components.Monaco;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Blazor.Graph;

public partial class MeshNodeEditorView
{
    private MonacoEditorView? _monacoEditor;
    private MeshNode? _node;
    private bool _isLoading = true;
    private bool _isSaving;

    // Metadata fields
    private string _parentPath = string.Empty;
    private string _lastSegment = string.Empty;
    private string _originalPath = string.Empty;
    private string _name = string.Empty;
    private string? _nodeType;

    // Content field
    private string _contentText = string.Empty;

    // Messages
    private string? _metadataMessage;
    private bool _metadataSuccess;
    private string? _contentMessage;
    private bool _contentSuccess;

    protected override void BindData()
    {
        base.BindData();
        _ = LoadNodeAsync();
    }

    private async Task LoadNodeAsync()
    {
        _isLoading = true;
        StateHasChanged();

        try
        {
            var meshQuery = Hub.ServiceProvider.GetService<IMeshService>();
            if (meshQuery == null)
            {
                Logger.LogError("IMeshService not available");
                return;
            }

            var path = ViewModel.NodePath;
            _originalPath = path;
            _node = await meshQuery.QueryAsync<MeshNode>($"path:{path} scope:self").FirstOrDefaultAsync();

            if (_node != null)
            {
                // Parse path into parent and last segment
                var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length > 1)
                {
                    _parentPath = string.Join("/", segments.Take(segments.Length - 1));
                    _lastSegment = segments[^1];
                }
                else
                {
                    _parentPath = "";
                    _lastSegment = path;
                }

                _name = _node.Name ?? string.Empty;
                _nodeType = _node.NodeType?.ToLowerInvariant();

                // Load content based on node type
                LoadContent();
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading node at path {Path}", ViewModel.NodePath);
        }
        finally
        {
            _isLoading = false;
            StateHasChanged();
        }
    }

    private void LoadContent()
    {
        if (_node?.Content == null)
        {
            _contentText = string.Empty;
            return;
        }

        // Handle Story content using reflection (to avoid circular dependency with Graph.Domain)
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

    private void OnContentChanged(string value)
    {
        _contentText = value;
    }

    private async Task SaveMetadataAsync()
    {
        if (_node == null) return;

        _isSaving = true;
        _metadataMessage = null;
        StateHasChanged();

        try
        {
            // Calculate new path
            var newPath = string.IsNullOrEmpty(_parentPath)
                ? _lastSegment
                : $"{_parentPath}/{_lastSegment}";

            // Check if path changed
            var pathChanged = !newPath.Equals(_originalPath, StringComparison.OrdinalIgnoreCase);

            if (pathChanged)
            {
                // Move the node to new path
                Logger.LogInformation("Moving node from {OldPath} to {NewPath}", _originalPath, newPath);
                var moveResponse = await Hub.AwaitResponse(
                    new MoveNodeRequest(_originalPath, newPath),
                    o => o.WithTarget(Hub.Address));
                if (!moveResponse.Message.Success)
                    throw new InvalidOperationException(moveResponse.Message.Error);
                _node = moveResponse.Message.Node
                    ?? throw new InvalidOperationException("Move succeeded but returned no node");
                _originalPath = newPath;
            }

            // Update metadata
            var updatedNode = MeshNode.FromPath(_node.Path) with
            {
                Name = _name,
                NodeType = _node.NodeType,
                Icon = _node.Icon,
                Order = _node.Order,
                Content = _node.Content,
                AssemblyLocation = _node.AssemblyLocation,
                HubConfiguration = _node.HubConfiguration,
                GlobalServiceConfigurations = _node.GlobalServiceConfigurations
            };

            var updateResponse = await Hub.AwaitResponse(
                new UpdateNodeRequest(updatedNode),
                o => o.WithTarget(Hub.Address));
            if (!updateResponse.Message.Success)
                throw new InvalidOperationException(updateResponse.Message.Error);
            _node = updateResponse.Message.Node;

            _metadataMessage = pathChanged
                ? $"Metadata saved. Node moved to {newPath}"
                : "Metadata saved successfully";
            _metadataSuccess = true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error saving metadata");
            _metadataMessage = $"Error: {ex.Message}";
            _metadataSuccess = false;
        }
        finally
        {
            _isSaving = false;
            StateHasChanged();
        }
    }

    private async Task SaveContentAsync()
    {
        if (_node == null) return;

        _isSaving = true;
        _contentMessage = null;
        StateHasChanged();

        try
        {
            // Update content based on node type
            object? newContent = _node.Content;

            if (_nodeType == "story" && _node.Content != null)
            {
                // Use reflection to update Story.Text (avoid circular dependency with Graph.Domain)
                var textProperty = _node.Content.GetType().GetProperty("Text");
                if (textProperty != null)
                {
                    // Create a new instance with updated Text using record's with expression via reflection
                    // Since Story is a record, we can use the <Clone>$ method
                    var cloneMethod = _node.Content.GetType().GetMethod("<Clone>$");
                    if (cloneMethod != null)
                    {
                        var cloned = cloneMethod.Invoke(_node.Content, null);
                        if (cloned != null)
                        {
                            textProperty.SetValue(cloned, _contentText);
                            newContent = cloned;
                        }
                    }
                }
            }
            var updatedNode = MeshNode.FromPath(_node.Path) with
            {
                Name = _node.Name,
                NodeType = _node.NodeType,
                Icon = _node.Icon,
                Order = _node.Order,
                Content = newContent,
                AssemblyLocation = _node.AssemblyLocation,
                HubConfiguration = _node.HubConfiguration,
                GlobalServiceConfigurations = _node.GlobalServiceConfigurations
            };

            var updateResponse = await Hub.AwaitResponse(
                new UpdateNodeRequest(updatedNode),
                o => o.WithTarget(Hub.Address));
            if (!updateResponse.Message.Success)
                throw new InvalidOperationException(updateResponse.Message.Error);
            _node = updateResponse.Message.Node;

            _contentMessage = "Content saved successfully";
            _contentSuccess = true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error saving content");
            _contentMessage = $"Error: {ex.Message}";
            _contentSuccess = false;
        }
        finally
        {
            _isSaving = false;
            StateHasChanged();
        }
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
}
