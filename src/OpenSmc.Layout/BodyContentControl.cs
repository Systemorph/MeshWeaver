using OpenSmc.Layout.Composition;

namespace OpenSmc.Layout;

public record BodyContentControl()
    : UiControl<BodyContentControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, null), IContainerControl
{
    public bool Collapsible { get; init; } = true;

    public int? Width { get; init; } = 250;
    IEnumerable<ViewElement> IContainerControl.ChildControls => [View as ViewElement ?? new ViewElementWithView("Body", View)];
    public IReadOnlyCollection<string> Areas { get; init; }
    internal object View { get; init; }

    IContainerControl IContainerControl.SetAreas(IReadOnlyCollection<string> areas)
        => this with { Areas = areas };
}
