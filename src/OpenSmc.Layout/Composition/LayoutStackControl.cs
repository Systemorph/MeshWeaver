using System.Collections.Immutable;

namespace OpenSmc.Layout.Composition;


public record LayoutStackControl() : UiControl<LayoutStackControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, null)
{
    internal const string Root = "";

    internal ImmutableList<ViewElement> ViewElements { get; init; } = ImmutableList<ViewElement>.Empty;

    public IReadOnlyCollection<object> Areas { get; init; }





    public LayoutStackControl WithView(object value) => WithView(GetAutoName(), value);
    public LayoutStackControl WithView(string area, object value) => 
        this with { ViewElements = ViewElements.Add(new ViewElementWithView(area, value)) };

    private string GetAutoName()
    {
        return $"Area{ViewElements.Count + 1}";
    }



    public LayoutStackControl WithView(ViewDefinition viewDefinition)
        => WithView(GetAutoName(), viewDefinition);

    public LayoutStackControl WithView(string area, ViewDefinition viewDefinition)
    {
        return this with { ViewElements = ViewElements.Add(new ViewElementWithViewDefinition(area, viewDefinition)) };
    }

   
    public bool HighlightNewAreas { get; init; }
    public LayoutStackControl WithHighlightNewAreas()
    {
        return this with { HighlightNewAreas = true };
    }

}

