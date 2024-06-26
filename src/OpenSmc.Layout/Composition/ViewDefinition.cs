namespace OpenSmc.Layout.Composition;

public delegate Task<object> ViewDefinition(LayoutAreaHost area);

public delegate IObservable<object> ViewStream(LayoutAreaHost area);

public abstract record ViewElement(string Area);

public record ViewElementWithViewDefinition(string Area, IObservable<ViewDefinition> ViewDefinition)
    : ViewElement(Area);

public record ViewElementWithView(string Area, object View) : ViewElement(Area);

public record ViewElementWithViewStream(string Area, ViewStream Stream) : ViewElement(Area);
