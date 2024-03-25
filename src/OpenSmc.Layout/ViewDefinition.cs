namespace OpenSmc.Layout;

public delegate object ViewDefinition(AreaReference request);

public abstract record ViewElement(string Area);

public record ViewElementWithViewDefinition(string Area, ViewDefinition ViewDefinition) : ViewElement(Area);
public record ViewElementWithView(string Area, object View) : ViewElement(Area);


