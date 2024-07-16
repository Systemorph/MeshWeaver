using OpenSmc.Layout.Composition;

namespace OpenSmc.Layout;

public record BodyContentControl(object Data)
    : UiControl<BodyContentControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, Data), IContainerControl
{
    public bool Collapsible { get; init; } = true;

    public int? Width { get; init; } = 250;
    public IEnumerable<ViewElement> ChildControls => [new ViewElementWithView("Body", Data)];
    public IReadOnlyCollection<string> Areas { get; init; }

    public IContainerControl SetAreas(IReadOnlyCollection<string> areas)
        => this with { Areas = areas };
}
