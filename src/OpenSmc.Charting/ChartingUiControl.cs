using OpenSmc.Layout;

namespace OpenSmc.Charting;

public static class ChartingUiControl
{
    public static string ModuleName = "Systemorph.Charting";
    public static string ApiVersion = "1";
}

public record ChartControl(object Data)
    : UiControl<ChartControl>(ChartingUiControl.ModuleName, ChartingUiControl.ApiVersion, Data);
