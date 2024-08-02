using OpenSmc.Layout;

namespace OpenSmc.Charting;

public record ChartControl(object Data)
    : UiControl<ChartControl>(typeof(ChartControl).Namespace, "1.0", Data);
