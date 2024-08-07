using MeshWeaver.Data;

namespace MeshWeaver.Layout;

public record ClickedEvent(string Area) : WorkspaceMessage
{
    public object Payload { get; init; }
};
