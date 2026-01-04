using System.Diagnostics;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.FluentUI.AspNetCore.Components;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace MeshWeaver.Blazor.Portal.Components;

public partial class SearchBar : IAsyncDisposable
{
    private const string SearchPlaceholder = "Type / to search, @ for references...";

    [Inject]
    public required NavigationManager NavigationManager { get; set; }

    [Inject]
    public required IKeyCodeService KeyCodeService { get; set; }

    [Inject]
    public IMeshQuery? MeshQuery { get; set; }

    private FluentAutocomplete<MeshNode>? searchAutocomplete;
    private string? searchTerm;
    private IEnumerable<MeshNode> selectedOptions = [];
    private bool isReferenceMode;

    protected override void OnInitialized()
    {
        KeyCodeService.RegisterListener(OnKeyDownAsync);
    }

    public Task OnKeyDownAsync(FluentKeyCodeEventArgs? args)
    {
        if (args is not null && args.Key == KeyCode.Slash)
        {
            searchAutocomplete?.Element?.FocusAsync();
        }
        return Task.CompletedTask;
    }

    private async Task HandleSearchInputAsync(OptionsSearchEventArgs<MeshNode> e)
    {
        searchTerm = e.Text;

        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            e.Items = null;
            isReferenceMode = false;
            return;
        }

        if (MeshQuery == null)
        {
            e.Items = null;
            return;
        }

        // @ Reference autocomplete mode
        if (searchTerm.StartsWith("@"))
        {
            isReferenceMode = true;
            await HandleReferenceAutocompleteAsync(e, searchTerm.Substring(1));
            return;
        }

        isReferenceMode = false;

        // Standard search mode - case-insensitive substring search with wildcards
        // Use wildcards for substring matching and scope:descendants to search all nodes
        var request = new MeshQueryRequest
        {
            Query = $"*{searchTerm}* scope:descendants",
            Limit = 10
        };

        var results = await MeshQuery.QueryAsync<MeshNode>(request).ToArrayAsync();
        e.Items = results;
    }

    private async Task HandleReferenceAutocompleteAsync(OptionsSearchEventArgs<MeshNode> e, string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            // Just "@" - show all nodes
            var request = new MeshQueryRequest { Query = "scope:descendants", Limit = 10 };
            e.Items = await MeshQuery!.QueryAsync<MeshNode>(request).ToArrayAsync();
            return;
        }

        // Parse reference for scope pattern (e.g., "data:MyType/Id1")
        var colonIndex = reference.IndexOf(':');
        if (colonIndex > 0)
        {
            var scope = reference.Substring(0, colonIndex);
            var remainder = reference.Substring(colonIndex + 1);
            await HandleScopedAutocompleteAsync(e, scope, remainder);
            return;
        }

        // Check if we have a path with trailing slash for sub-completions
        var lastSlashIndex = reference.LastIndexOf('/');
        if (lastSlashIndex > 0 && reference.EndsWith("/"))
        {
            var basePath = reference.TrimEnd('/');
            // Use AutocompleteAsync to get sub-completions at this address
            var suggestions = await MeshQuery!.AutocompleteAsync(basePath, "", 10).ToArrayAsync();
            e.Items = suggestions.Select(s => MeshNode.FromPath(s.Path) with
            {
                Name = s.Name,
                NodeType = s.NodeType,
                Description = $"Score: {s.Score:F2}"
            }).ToArray();
            return;
        }

        // Standard node search with wildcard and scope
        var searchRequest = new MeshQueryRequest
        {
            Query = $"*{reference}* scope:descendants",
            Limit = 10
        };
        e.Items = await MeshQuery!.QueryAsync<MeshNode>(searchRequest).ToArrayAsync();
    }

    private async Task HandleScopedAutocompleteAsync(OptionsSearchEventArgs<MeshNode> e, string scope, string remainder)
    {
        // Handle scope-based completion (e.g., "data:", "layout:")
        // For now, search nodes with the scope as a filter
        var query = string.IsNullOrWhiteSpace(remainder)
            ? $"nodeType:*{scope}* scope:descendants"
            : $"nodeType:*{scope}* *{remainder}* scope:descendants";

        var request = new MeshQueryRequest { Query = query, Limit = 10 };
        e.Items = await MeshQuery!.QueryAsync<MeshNode>(request).ToArrayAsync();
    }

    private void HandleOptionSelected()
    {
        var selectedNode = selectedOptions.SingleOrDefault();

        if (selectedNode is null)
        {
            // Selection was cleared
            searchTerm = null;
            selectedOptions = [];
            InvokeAsync(StateHasChanged);
            return;
        }

        if (isReferenceMode)
        {
            // In reference mode, insert the reference text instead of navigating
            searchTerm = $"@{selectedNode.Path}/";
            selectedOptions = [];
            InvokeAsync(StateHasChanged);
            // Trigger autocomplete refresh
            searchAutocomplete?.Element?.FocusAsync();
            return;
        }

        // Standard mode - navigate to the selected node
        var targetHref = selectedNode.Path;
        searchTerm = null;
        selectedOptions = [];
        InvokeAsync(StateHasChanged);

        NavigationManager.NavigateTo(targetHref ?? throw new UnreachableException("Item has no href"));
    }

    private void HandleKeyDown(KeyboardEventArgs e)
    {
        // Navigate to search page when Enter is pressed (and no autocomplete item is selected)
        if (e.Key == "Enter" && !string.IsNullOrWhiteSpace(searchTerm) && !selectedOptions.Any())
        {
            var encodedQuery = Uri.EscapeDataString(searchTerm);
            NavigationManager.NavigateTo($"/search?q={encodedQuery}");
        }
    }

    private static string TruncateDescription(string? description, int maxLength = 60)
    {
        if (string.IsNullOrEmpty(description))
            return string.Empty;

        if (description.Length <= maxLength)
            return description;

        return description.Substring(0, maxLength - 3) + "...";
    }

    private static string GetNodeTypeDisplay(string? nodeType)
    {
        if (string.IsNullOrEmpty(nodeType))
            return string.Empty;

        // Extract the last segment of the path for display (e.g., "type/org" -> "org")
        var lastSlash = nodeType.LastIndexOf('/');
        return lastSlash >= 0 ? nodeType.Substring(lastSlash + 1) : nodeType;
    }

    private static Icon GetIconForName(string? iconName)
    {
        // Map common icon names to FluentUI icons
        return iconName?.ToLowerInvariant() switch
        {
            "building" => new Icons.Regular.Size20.Building(),
            "person" => new Icons.Regular.Size20.Person(),
            "people" => new Icons.Regular.Size20.People(),
            "folder" => new Icons.Regular.Size20.Folder(),
            "document" => new Icons.Regular.Size20.Document(),
            "code" => new Icons.Regular.Size20.Code(),
            "settings" => new Icons.Regular.Size20.Settings(),
            "home" => new Icons.Regular.Size20.Home(),
            "search" => new Icons.Regular.Size20.Search(),
            "star" => new Icons.Regular.Size20.Star(),
            "heart" => new Icons.Regular.Size20.Heart(),
            "chat" => new Icons.Regular.Size20.Chat(),
            "mail" => new Icons.Regular.Size20.Mail(),
            "calendar" => new Icons.Regular.Size20.Calendar(),
            "checkmark" => new Icons.Regular.Size20.Checkmark(),
            "add" => new Icons.Regular.Size20.Add(),
            "delete" => new Icons.Regular.Size20.Delete(),
            "edit" => new Icons.Regular.Size20.Edit(),
            "info" => new Icons.Regular.Size20.Info(),
            "warning" => new Icons.Regular.Size20.Warning(),
            "error" => new Icons.Regular.Size20.ErrorCircle(),
            "bot" => new Icons.Regular.Size20.Bot(),
            "robot" => new Icons.Regular.Size20.Bot(),
            "agent" => new Icons.Regular.Size20.Bot(),
            "project" => new Icons.Regular.Size20.Briefcase(),
            "briefcase" => new Icons.Regular.Size20.Briefcase(),
            "task" => new Icons.Regular.Size20.TaskListSquareLtr(),
            "organization" => new Icons.Regular.Size20.Building(),
            "nodetype" => new Icons.Regular.Size20.Box(),
            "box" => new Icons.Regular.Size20.Box(),
            "markdown" => new Icons.Regular.Size20.DocumentText(),
            "text" => new Icons.Regular.Size20.DocumentText(),
            _ => new Icons.Regular.Size20.Document()
        };
    }

    private static Icon GetIconForNodeType(string? nodeType)
    {
        if (string.IsNullOrEmpty(nodeType))
            return new Icons.Regular.Size20.Document();

        var typeName = GetNodeTypeDisplay(nodeType).ToLowerInvariant();
        return GetIconForName(typeName);
    }

    public ValueTask DisposeAsync()
    {
        KeyCodeService.UnregisterListener(OnKeyDownAsync, OnKeyDownAsync);
        return ValueTask.CompletedTask;
    }
}
