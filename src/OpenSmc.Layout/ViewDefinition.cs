using OpenSmc.Layout.Composition;

namespace OpenSmc.Layout;

public delegate IObservable<Func<LayoutArea, Task<UiControl>>> ViewDefinition(LayoutAreaReference reference);

public abstract record ViewElement(string Area);

public record ViewElementWithViewDefinition(string Area, ViewDefinition ViewDefinition) : ViewElement(Area);
public record ViewElementWithView(string Area, object View) : ViewElement(Area);


