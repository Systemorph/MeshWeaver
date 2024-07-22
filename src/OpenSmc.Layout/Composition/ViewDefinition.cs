namespace OpenSmc.Layout.Composition;

public delegate Task<object> ViewDefinition(LayoutAreaHost area, RenderingContext context, CancellationToken cancellationToken);

public record RenderingContext(string Area, LayoutAreaProperties Properties)
{
    public string DataContext { get; init; }
};

public delegate IObservable<object> ViewStream(LayoutAreaHost area, RenderingContext context);

public abstract record ViewElement(string Area, LayoutAreaProperties Properties);

public record ViewElementWithViewDefinition(string Area, IObservable<ViewDefinition> ViewDefinition, LayoutAreaProperties Options)
    : ViewElement(Area, Options);

public record ViewElementWithView(string Area, object View, LayoutAreaProperties Properties) : ViewElement(Area, Properties);

public record ViewElementWithViewStream(string Area, ViewStream Stream, LayoutAreaProperties Properties) : ViewElement(Area, Properties);
