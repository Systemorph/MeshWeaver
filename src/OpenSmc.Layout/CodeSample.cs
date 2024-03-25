

// TODO V10: this is temporary solution, because we dont want to change name in contract, either move or change namespace or change name in future (2023.08.11, Armen Sirotenko)
namespace OpenSmc.Layout;

public record CodeSample(string Code, string Language = "csharp") : IObjectWithUiControl
{
    public UiControl GetUiControl(UiControlsManager uiControlsManager)
    {
        return Controls.CodeSample(this);
    }
}