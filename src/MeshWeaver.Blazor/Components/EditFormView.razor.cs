using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Activities;
using MeshWeaver.Data;
using MeshWeaver.Layout.Client;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.FluentUI.AspNetCore.Components;

namespace MeshWeaver.Blazor.Components;

public partial class EditFormView
{
    private ModelParameter<JsonElement> model;
    private ActivityLog Log { get; set; }

    protected override void BindData()
    {
        DataBind(
            new JsonPointerReference(ViewModel.DataContext), 
            x => x.model,
            (jsonObject,_) => jsonObject == null ? null: Convert((JsonElement)jsonObject)
            );
    }

    private async void Submit(EditContext context)
    {
        var log = await Stream.SubmitModel(model);
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

    private ModelParameter<JsonElement> Convert(JsonElement jsonObject)
    {
        var ret = new ModelParameter<JsonElement>(jsonObject, (m, r) => LayoutClientExtensions.GetValueFromModel(m,r));
        ret.ElementChanged += OnModelChanged;
        return ret;
    }

    private void Reset()
    {
        model.Reset();
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
        if (model != null)
            model.ElementChanged -= OnModelChanged;
        return base.DisposeAsync();
    }

    private void OnModelChanged(object sender, JsonElement e)
    {
        InvokeAsync(StateHasChanged);
    }
}
