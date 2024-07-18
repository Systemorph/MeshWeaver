namespace OpenSmc.Layout.Composition;

public delegate Task<object> ViewDefinition(LayoutAreaHost area, RenderingContext context, CancellationToken cancellationToken);

public record RenderingContext(string Area, ViewOptions Options)
{
    public string DataContext { get; init; }
    public bool IsTopLevel { get; init; }
};

public delegate IObservable<object> ViewStream(LayoutAreaHost area, RenderingContext context);

public abstract record ViewElement(string Area, ViewOptions Options);

public record ViewElementWithViewDefinition(string Area, IObservable<ViewDefinition> ViewDefinition, ViewOptions Options)
    : ViewElement(Area, Options);

public record ViewElementWithView(string Area, object View, ViewOptions Options) : ViewElement(Area, Options);

public record ViewElementWithViewStream(string Area, ViewStream Stream, ViewOptions Options) : ViewElement(Area, Options);
