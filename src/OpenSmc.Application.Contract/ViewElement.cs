using System.Threading.Tasks;

namespace OpenSmc.Application;

public delegate Task<ViewElementWithView> ViewDefinition(SetAreaOptions options);
public delegate object SyncViewDefinition();

public abstract record ViewElement(SetAreaOptions Options)
{
    public string Area => Options.Area;
}

public record ViewElementWithViewDefinition(ViewDefinition ViewDefinition, SetAreaOptions Options) : ViewElement(Options);
public record ViewElementWithView(object View, SetAreaOptions Options) : ViewElement(Options);
public record ViewElementWithPath(string Path, SetAreaOptions Options) : ViewElement(Options);



