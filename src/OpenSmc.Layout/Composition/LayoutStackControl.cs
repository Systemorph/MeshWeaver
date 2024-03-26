using System.Collections.Immutable;

namespace OpenSmc.Layout.Composition;


public record LayoutStackControl() : UiControl<LayoutStackControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, null)
{
    public record AreaChangedOptions(string InsertAfter = null);
    internal const string Root = "";

    internal ImmutableList<ViewElement> ViewElements { get; init; } = ImmutableList<ViewElement>.Empty; //definition of views

    public IReadOnlyCollection<object> Areas
    {
        get;
        init;
    }






    public LayoutStackControl WithView(object value) => WithView(GetAutoName(), value);
    public LayoutStackControl WithView(string area, object value) => WithView(new LayoutAreaReference(area), value);

    private string GetAutoName()
    {
        return $"Area{ViewElements.Count + 1}";
    }

    public LayoutStackControl WithView(LayoutAreaReference reference, object value)
    {
        return this with { ViewElements = ViewElements.Add(new ViewElementWithView(reference, value)) };
    }


    public LayoutStackControl WithView(ViewDefinition viewDefinition)
        => WithView(new LayoutAreaReference(GetAutoName()), viewDefinition);
    public LayoutStackControl WithView(string area, ViewDefinition viewDefinition)
        => WithView(new LayoutAreaReference(area), viewDefinition);

    public LayoutStackControl WithView(LayoutAreaReference reference, ViewDefinition viewDefinition)
    {
        return this with { ViewElements = ViewElements.Add(new ViewElementWithViewDefinition(reference, viewDefinition)) };
    }

    private string GetAreaName()
    {
        return $"{ViewElements.Count + 1}";
    }


    public LayoutStackControl WithColumns(int columns)
    {
        return this with { ColumnCount = columns };
    }

    public int ColumnCount { get; init; }

    public bool HighlightNewAreas { get; init; }
    public LayoutStackControl WithHighlightNewAreas()
    {
        return this with { HighlightNewAreas = true };
    }

}

