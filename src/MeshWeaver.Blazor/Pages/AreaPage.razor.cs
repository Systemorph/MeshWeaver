using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Components;

namespace MeshWeaver.Blazor.Pages;

public partial class AreaPage : ComponentBase
{
    private LayoutAreaControl ViewModel { get; set; } = null!;
    private bool IsContentReady { get; set; } = false;

    [Inject]
    private NavigationManager Navigation { get; set; } = null!;
    [Inject] private IMessageHub Hub { get; set; } = null!;
    [Inject] private IPathResolver PathResolver { get; set; } = null!;

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
    protected override void OnParametersSet()
    {
        // Reactive — Subscribe, never await on PathResolver chain (deadlock surface;
        // see Doc/Architecture/AsynchronousCalls.md).
        PathResolver.ResolvePath(Path ?? "").Subscribe(resolution =>
        {
            Resolution = resolution;

            if (Resolution is null)
            {
                PageTitle = "Page Not Found";
                InvokeAsync(StateHasChanged);
                return;
            }

            var (area, id) = ParseRemainder(Resolution.Remainder);
            area = area != null ? (string)WorkspaceReference.Decode(area) : null;
            id = id != null ? (string)WorkspaceReference.Decode(id) : "";

            var query = Navigation.ToAbsoluteUri(Navigation.Uri).Query;
            if (!string.IsNullOrEmpty(query))
                id = id + query;

            Reference = new(area) { Id = id };

            ViewModel = Controls.LayoutArea(Address!, Reference)
                with
            { ShowProgress = false };

            IsContentReady = false;
            InvokeAsync(StateHasChanged);
        });
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

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {

        // Use a short delay to allow content to stabilize, then mark as ready
        if (!IsContentReady)
        {
            await Task.Delay(1000); // Allow content to load
            IsContentReady = true;
            StateHasChanged();
        }
    }


}

