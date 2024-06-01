using System.Collections.Immutable;
using System.Reactive.Linq;
using OpenSmc.Layout.Composition;

namespace OpenSmc.Layout;

public record LayoutStackControl()
    : UiControl<LayoutStackControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, null)
{
    internal const string Root = "";

    internal ImmutableList<ViewElement> ViewElements { get; init; } =
        ImmutableList<ViewElement>.Empty;

    public IReadOnlyCollection<string> Areas { get; init; }

    public LayoutStackControl WithView(object value) => WithView(GetAutoName(), value);

    public LayoutStackControl WithView(string area, object value) =>
        this with
        {
            ViewElements = ViewElements.Add(new ViewElementWithView(area, value))
        };

    private string GetAutoName()
    {
        return $"Area{ViewElements.Count + 1}";
    }

    public LayoutStackControl WithView(ViewDefinition viewDefinition) =>
        WithView(GetAutoName(), Observable.Return(viewDefinition));

    public LayoutStackControl WithView(string area, IObservable<ViewDefinition> viewDefinition) =>
        this with
        {
            ViewElements = ViewElements.Add(new ViewElementWithViewDefinition(area, viewDefinition))
        };

    public LayoutStackControl WithView(IObservable<ViewDefinition> viewDefinition)
        => WithView(GetAutoName(), viewDefinition);
    public LayoutStackControl WithView(Func<LayoutArea, IObservable<object>> viewDefinition)
        => WithView(GetAutoName(), viewDefinition);
    public LayoutStackControl WithView(string area, Func<LayoutArea,IObservable<object>> viewDefinition)
        => this with {ViewElements = ViewElements.Add(new ViewElementWithViewStream(area, viewDefinition.Invoke))};

    public bool HighlightNewAreas { get; init; }

    public LayoutStackControl WithHighlightNewAreas()
    {
        return this with { HighlightNewAreas = true };
    }
}
