﻿using MeshWeaver.Application;
using MeshWeaver.Layout;
using Microsoft.AspNetCore.Components;
using System.Collections.Immutable;
using MeshWeaver.Domain.Layout;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Utils;

namespace MeshWeaver.Blazor;

public partial class LayoutAreaPage
{
    private LayoutAreaControl ViewModel { get; set; }

    [Inject]
    private NavigationManager Navigation { get; set; }

    [Parameter]
    public string Application { get; set; }
    [Parameter]
    public string Environment { get; set; }

    [Parameter]
    public string Area { get; set; }

    [Parameter]
    public string Id
    {
        get;
        set;
    }

    private string PageTitle { get; set; }

    [Parameter(CaptureUnmatchedValues = true)]
    public IReadOnlyDictionary<string, object> Options { get; set; } = ImmutableDictionary<string, object>.Empty;
    private object Address => new ApplicationAddress(Application, Environment);

    private LayoutAreaReference Reference { get; set; }
    protected override async Task OnParametersSetAsync()
    {
        await base.OnParametersSetAsync();

        var id = (string)LayoutExtensions.Decode(Id);
        var query = Navigation.ToAbsoluteUri(Navigation.Uri).Query;
        if (!string.IsNullOrEmpty(query))
            id += "?" + query;
        
        Reference = new((string)LayoutExtensions.Decode(Area))
        {
            Id = id,
            Layout = StandardPageLayout.Page,
        };



        ViewModel = new(Address, Reference)
        {
            ShowProgress = true,
            DisplayArea = (GetDisplayNameFromId() ?? Reference.Area).Wordify()
        };
        PageTitle = $"{ViewModel.DisplayArea} - {Application}";

    }


    private string GetDisplayNameFromId()
    {
        // TODO V10: This is very hand woven.
        // We need some configurability for how to create DisplayArea, PageTitle, etc.  (14.08.2024, Roland Bürgi)

        if (Reference.Id is null)
            return null;

        return Reference.Id.ToString()!.Split("?").First().Split("/").Last();
    }


}
