using MeshWeaver.Layout;
using Microsoft.AspNetCore.Components;
using System.Collections.Immutable;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.FluentUI.AspNetCore.Components;

namespace MeshWeaver.Blazor.Pages;

public partial class ApplicationPage
{
    private DesignThemeModes Mode;
    private LayoutAreaControl ViewModel { get; set; } = null!;

    [Inject]
    private NavigationManager Navigation { get; set; } = null!;

    [Inject]
    private IMessageHub Hub { get; set; } = null!;

    [Parameter]
    public string? AddressType { get; set; } = "";
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
    private object? Address => IsValidAddressType ? MeshExtensions.MapAddress(AddressType!, Application!) : null;
    private bool IsValidAddressType => GetValidAddressTypes().Contains(AddressType!);
    
    private HashSet<string> GetValidAddressTypes() => 
        Hub.TypeRegistry.Types
            .Where(kvp => kvp.Value.Type.IsAssignableTo(typeof(Address)))
            .Select(kvp => kvp.Key)
            .ToHashSet();

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



        if (!IsValidAddressType)
        {
            // Handle invalid address type
            PageTitle = $"Invalid Address Type: {AddressType}";
            return;
        }

        ViewModel = Controls.LayoutArea(Address!, Reference)
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
