namespace OpenSmc.Layout;

public delegate object ViewDefinition(LayoutAreaReference reference);

public abstract record ViewElement(LayoutAreaReference Reference);

public record ViewElementWithViewDefinition(LayoutAreaReference Reference, ViewDefinition ViewDefinition) : ViewElement(Reference);
public record ViewElementWithView(LayoutAreaReference Reference, object View) : ViewElement(Reference);


