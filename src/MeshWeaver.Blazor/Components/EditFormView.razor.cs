using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Layout.Client;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.FluentUI.AspNetCore.Components;

namespace MeshWeaver.Blazor.Components;

public partial class EditFormView
{
    private ModelParameter<JsonElement>? model;
    private ActivityLog? Log { get; set; }

    protected override void BindData()
    {
        DataBind(
            new JsonPointerReference(ViewModel.DataContext ?? "/"), 
            x => x.model,
            (jsonObject,_) => jsonObject == null ? null!: Convert((JsonElement)jsonObject)
            );
    }

    private void Submit(EditContext context)
    {
        if (Stream is null)
            throw new InvalidOperationException("Stream must be set before submitting the form.");

        // Subscribe — do NOT bridge to Task / await. The hub round-trip stays
        // observable end-to-end (see Doc/Architecture/AsynchronousCalls.md).
        Stream.SubmitModel(model!).Subscribe(log =>
        {
            InvokeAsync(() =>
            {
                if (log.Status == ActivityStatus.Succeeded)
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
                StateHasChanged();
            });
        });
    }

    private ModelParameter<JsonElement> Convert(JsonElement jsonObject)
    {
        var ret = new ModelParameter<JsonElement>(jsonObject, (m, r) => LayoutClientExtensions.GetValueFromModel(m,r));
        ret.ElementChanged += OnModelChanged;
        return ret;
    }

    private void Reset()
    {
        model?.Reset();
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
        if (model is not null)
            model.ElementChanged -= OnModelChanged;
        return base.DisposeAsync();
    }

    private void OnModelChanged(object? sender, JsonElement e)
    {
        InvokeAsync(StateHasChanged);
    }
}
