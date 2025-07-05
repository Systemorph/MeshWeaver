using MeshWeaver.Layout;
using Microsoft.AspNetCore.Components;
using System.Collections.Immutable;
using MeshWeaver.Data;
using MeshWeaver.Mesh;

namespace MeshWeaver.Blazor.Pages;

public partial class ApplicationPage
{
    private LayoutAreaControl ViewModel { get; set; } = null!;

    [Inject]
    private NavigationManager Navigation { get; set; } = null!;

    [Parameter]
    public string? Application { get; set; } = "";
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
    private object Address => new ApplicationAddress(Application!);

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



        ViewModel = Controls.LayoutArea(Address, Reference)
            with
        {
            ShowProgress = true
        };
        PageTitle = $"{ViewModel.ProgressMessage} - {Application!}";

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
