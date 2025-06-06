﻿using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Activities;
using MeshWeaver.Data;
using MeshWeaver.Layout.Client;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.FluentUI.AspNetCore.Components;

namespace MeshWeaver.Blazor.Components;

public partial class EditFormView
{
    private ModelParameter<JsonElement> data;
    private ActivityLog Log { get; set; }

    protected override void BindData()
    {
        DataBind(
            new JsonPointerReference(ViewModel.DataContext), 
            x => x.data,
            jsonObject => Convert((JsonObject)jsonObject)
            );
    }

    private async void Submit(EditContext context)
    {
        var log = await Stream.SubmitModel(Model);
        if(log.Status == ActivityStatus.Succeeded)
        {
            Log = null;
            ShowSuccess();
            Reset();
        }
        else
        {
            Log = log;
            ShowError();
        }
    }

    private ModelParameter Convert(JsonObject jsonObject)
    {
        if (jsonObject == null)
            return null;
        var jsonModel = jsonObject.ToJsonString();
        var ret = new ModelParameter<JsonElement>(JsonDocument.Parse(jsonModel).RootElement, (m, r) => LayoutClientExtensions.GetValueFromModel(m,r));
        ret.ElementChanged += OnModelChanged;
        return ret;
    }

    private void Reset()
    {
        data.Reset();
        InvokeAsync(StateHasChanged);
    }

    private void ShowSuccess()
    {
        var message = "Saved successfully";
        ToastService.ShowToast(ToastIntent.Success, message);
    }

    private void ShowError()
    {
        var message = "Saving failed";
        ToastService.ShowToast(ToastIntent.Error, message);
    }

    public override ValueTask DisposeAsync()
    {
        if (data != null)
            data.ElementChanged -= OnModelChanged;
        return base.DisposeAsync();
    }

    private void OnModelChanged(object sender, JsonElement e)
    {
        InvokeAsync(StateHasChanged);
    }
}
