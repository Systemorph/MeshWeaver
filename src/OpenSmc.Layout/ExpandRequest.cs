using OpenSmc.Data;
using OpenSmc.Messaging;

namespace OpenSmc.Layout.Views;

public record ClickedEvent(string Area) : WorkspaceMessage
{
    public object Payload { get; init; }
};
