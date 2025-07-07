using MeshWeaver.Layout;
using Microsoft.FluentUI.AspNetCore.Components;

namespace MeshWeaver.Blazor.Components;

public partial class DialogView : BlazorView<DialogControl, DialogView>
{
    private bool isHidden;
    private object? Title;
    private bool? IsClosable;
    private object? Size;

    protected override void BindData()
    {
        base.BindData();
        if (ViewModel.Title != null)
            DataBind(ViewModel.Title, x => x.Title);
        if (ViewModel.IsClosable != null)
            DataBind(ViewModel.IsClosable, x => x.IsClosable);
        if (ViewModel.Size != null)
            DataBind(ViewModel.Size, x => x.Size);
    }

    protected override void OnInitialized()
    {
        base.OnInitialized();
        isHidden = false; // Show dialog when component is initialized
    }

    private string GetDialogStyle()
    {
        var size = Size?.ToString() ?? "M";
        var width = GetDialogWidth(size);
        var height = GetDialogHeight(size);

        var style = $"--dialog-width: {width};";
        if (!string.IsNullOrEmpty(height))
        {
            style += $" --dialog-height: {height};";
        }

        return style;
    }

    private string GetDialogWidth(string size) => size switch
    {
        "S" => "400px",
        "M" => "600px",
        "L" => "800px",
        _ => "600px"
    };

    private string GetDialogHeight(string size) => size switch
    {
        "S" => "300px",
        "M" => "auto",
        "L" => "600px",
        _ => "auto"
    };

    private void HandleClose()
    {
        isHidden = true;
        // Send CloseDialogEvent with OK state
        Stream!.Hub.Post(new CloseDialogEvent(Area, Stream.StreamId, DialogCloseState.OK),
            o => o.WithTarget(Stream.Owner));
    }

    private void HandleDialogResult(DialogResult result)
    {
        if (IsClosable == true)
        {
            isHidden = true;

            // Determine the close state based on the dialog result
            var closeState = result.Cancelled ? DialogCloseState.Cancel : DialogCloseState.OK;

            // Send CloseDialogEvent
            Stream!.Hub.Post(new CloseDialogEvent(Area, Stream.StreamId, closeState),
                o => o.WithTarget(Stream.Owner));
        }
    }
}
