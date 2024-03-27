using OpenSmc.Data;

namespace OpenSmc.Layout;

public delegate IObservable<object> ViewDefinition(IObservable<WorkspaceState> stateStream, LayoutAreaReference reference);

public abstract record ViewElement(LayoutAreaReference Reference);

public record ViewElementWithViewDefinition(LayoutAreaReference Reference, ViewDefinition ViewDefinition) : ViewElement(Reference);
public record ViewElementWithView(LayoutAreaReference Reference, object View) : ViewElement(Reference);


