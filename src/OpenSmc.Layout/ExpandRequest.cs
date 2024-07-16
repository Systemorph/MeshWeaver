using OpenSmc.Data;

namespace OpenSmc.Layout;

public record ClickedEvent(string Area) : WorkspaceMessage
{
    public object Payload { get; init; }
};
