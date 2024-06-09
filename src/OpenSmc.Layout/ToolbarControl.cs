using System.Collections.Immutable;

namespace OpenSmc.Layout.Views;

public record ToolbarControl()
    : UiControl<ToolbarControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, null)
{
    public ImmutableList<IUiControl> Controls { get; init; } =
        ImmutableList<IUiControl>.Empty;

    public ToolbarControl WithControl(IUiControl control) =>
        this with
        {
            Controls = Controls.Add(control)
        };
}
