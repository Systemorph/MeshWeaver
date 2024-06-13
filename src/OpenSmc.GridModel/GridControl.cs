using OpenSmc.Layout;

namespace OpenSmc.GridModel;

public record GridControl(object Data) : UiControl<GridControl>(typeof(GridControl).Namespace, "1.0", Data);
