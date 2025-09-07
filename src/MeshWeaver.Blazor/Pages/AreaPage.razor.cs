using System.Collections.Immutable;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using Microsoft.AspNetCore.Components;

namespace MeshWeaver.Blazor.Pages;

public partial class AreaPage
{
    private LayoutAreaControl ViewModel { get; set; } = null!;
    private bool IsContentReady { get; set; } = false;

    [Inject]
    private NavigationManager Navigation { get; set; } = null!;

    [Parameter]
    public string? AddressId { get; set; } = "";
    [Parameter]
    public string? AddressType { get; set; } = "";
    [Parameter]
    public string? Environment { get; set; } = "";

    [Parameter]
    public string? Area { get; set; } = "";

    [Parameter]
    public string Id
    {
        get;
        set;
    } = "";

    private string? PageTitle { get; set; } = "";

    [Parameter(CaptureUnmatchedValues = true)]
    public IReadOnlyDictionary<string, object>? Options { get; set; } = ImmutableDictionary<string, object>.Empty;
    private object? Address => MeshExtensions.MapAddress(AddressType!, AddressId!);

    private LayoutAreaReference Reference { get; set; } = null!;
    protected override async Task OnParametersSetAsync()
    {
        await base.OnParametersSetAsync();

        var id = (string)WorkspaceReference.Decode(Id);
        var query = Navigation.ToAbsoluteUri(Navigation.Uri).Query;
        if (!string.IsNullOrEmpty(query))
            id += "?" + query;

        Reference = new((string)WorkspaceReference.Decode(Area!))
        {
            Id = id,
        };



        ViewModel = Controls.LayoutArea(Address!, Reference)
            with {
                ShowProgress = false, // Disable progress indicator for cleaner screenshots
            };

        // Reset content ready state when parameters change
        IsContentReady = false;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await base.OnAfterRenderAsync(firstRender);
        
        // Use a short delay to allow content to stabilize, then mark as ready
        if (!IsContentReady)
        {
            await Task.Delay(1000); // Allow content to load
            IsContentReady = true;
            StateHasChanged();
        }
    }


}
