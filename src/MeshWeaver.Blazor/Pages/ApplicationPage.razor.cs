using System.Collections.Immutable;
using System.Linq;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;

namespace MeshWeaver.Blazor.Pages;

public partial class ApplicationPage : ComponentBase
{
    private DesignThemeModes Mode { get; set; }
    private LayoutAreaControl ViewModel { get; set; } = null!;

    [Inject]
    private NavigationManager Navigation { get; set; } = null!;

    [Inject]
    private IMessageHub Hub { get; set; } = null!;

    [Inject]
    private IMeshCatalog MeshCatalog { get; set; } = null!;

    /// <summary>
    /// Catch-all path parameter - the entire URL path is matched against registered namespace patterns.
    /// </summary>
    [Parameter]
    public string? Path { get; set; } = "";

    private string? PageTitle { get; set; } = "";

    [Parameter(CaptureUnmatchedValues = true)]
    public IReadOnlyDictionary<string, object>? Options { get; set; } = ImmutableDictionary<string, object>.Empty;

    private AddressResolution? Resolution { get; set; }
    private Address? Address => Resolution != null ? (Address)Resolution.Prefix : null;

    private LayoutAreaReference Reference { get; set; } = null!;
    protected override Task OnParametersSetAsync()
    {
        // Resolve the entire path using pattern matching
        Resolution = MeshCatalog.ResolvePath(Path ?? "");

        if (Resolution is null)
        {
            PageTitle = $"Page Not Found";
            return Task.CompletedTask;
        }

        // Parse remainder into area and id
        var (area, id) = ParseRemainder(Resolution.Remainder);

        // Decode area and id, append query string
        area = area != null ? (string)WorkspaceReference.Decode(area) : null;
        id = id != null ? (string)WorkspaceReference.Decode(id) : "";

        var query = Navigation.ToAbsoluteUri(Navigation.Uri).Query;
        if (!string.IsNullOrEmpty(query))
            id = id + query;

        Reference = new(area)
        {
            Id = id,
        };

        ViewModel = Controls.LayoutArea(Address!, Reference)
            with
        {
            ShowProgress = true
        };

        // Use the last segment of the address for the page title
        var titleSegment = Address!.Segments.LastOrDefault() ?? Address.Type;
        PageTitle = titleSegment;

        return Task.CompletedTask;
    }

    private static (string? Area, string? Id) ParseRemainder(string? remainder)
    {
        if (string.IsNullOrEmpty(remainder))
            return (null, null);

        var slashIndex = remainder.IndexOf('/');
        if (slashIndex >= 0)
            return (remainder.Substring(0, slashIndex), remainder.Substring(slashIndex + 1));

        return (remainder, null);
    }


    private string? GetDisplayNameFromId()
    {
        // TODO V10: This is very hand woven.
        // We need some configurability for how to create DisplayArea, PageTitle, etc.  (14.08.2024, Roland Bürgi)

        if (Reference.Id is null)
            return null!;

        return Reference.Id.ToString()!.Split("?").First().Split("/").Last();
    }


}
