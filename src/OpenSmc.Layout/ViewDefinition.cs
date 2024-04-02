using OpenSmc.Data;

namespace OpenSmc.Layout;

public delegate IObservable<object> ViewDefinition(IObservable<WorkspaceState> stateStream, LayoutAreaReference reference);

public abstract record ViewElement(string Area);

public record ViewElementWithViewDefinition(string Area, ViewDefinition ViewDefinition) : ViewElement(Area);
public record ViewElementWithView(string Area, object View) : ViewElement(Area);


