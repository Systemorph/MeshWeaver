namespace OpenSmc.Layout.Composition;

public delegate Task<T> ViewDefinition<T>(LayoutAreaHost area, RenderingContext context, CancellationToken cancellationToken);
public delegate Task<object> ViewDefinition(LayoutAreaHost area, RenderingContext context, CancellationToken cancellationToken);

public record RenderingContext(string Area)
{
    public string Layout { get; init; }
    public string DataContext { get; init; }
    public RenderingContext Parent { get; init; }
};

public delegate IObservable<T> ViewStream<out T>(LayoutAreaHost area, RenderingContext context);


