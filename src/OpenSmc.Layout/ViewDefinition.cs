using OpenSmc.Layout.Composition;

namespace OpenSmc.Layout;

public delegate Task<UiControl> ViewDefinition(LayoutArea area);

public abstract record ViewElement(string Area);

public record ViewElementWithViewDefinition(string Area, IObservable<ViewDefinition> ViewDefinition) : ViewElement(Area);
public record ViewElementWithView(string Area, object View) : ViewElement(Area);


