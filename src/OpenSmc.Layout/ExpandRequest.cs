using OpenSmc.Data;
using OpenSmc.Messaging;

namespace OpenSmc.Layout.Views;

public record ClickedEvent(string Area) : IWorkspaceMessage
{
    public object Address { get; init; }
    public object Reference { get; init; }
    public object Payload { get; init; }
};
